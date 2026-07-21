using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.Hosting;
using RIoT2.Matter.Messaging;
using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.SecureChannel.Case;

/// <summary>
/// The CASE responder (device) endpoint, implemented as an <see cref="ISessionEstablishmentDelegate"/>.
/// Establishes operational (CASE) sessions with commissioned controllers via
/// Sigma1 → Sigma2 → Sigma3 → StatusReport, deriving per-session keys on success. This is the
/// operational counterpart to the PASE responder. See the Matter Core Specification, section 4.14.
/// </summary>
/// <remarks>
/// Unlike PASE, a device may establish many CASE sessions concurrently, so per-handshake state is
/// keyed by exchange. Register with a <see cref="SecureChannelHandler"/> as its CASE delegate.
/// </remarks>
public sealed class CaseServer : ISessionEstablishmentDelegate
{
    private const int RandomLength = 32;
    private const int DestinationIdLength = 32;
    private const int PublicKeyLength = 65;

    private readonly ICaseCryptoProvider _crypto;
    private readonly IFabricStore _fabrics;
    private readonly Func<ushort> _allocateSessionId;
    private readonly ICaseResumptionStore? _resumptionStore;
    private readonly ConcurrentDictionary<ExchangeContext, CaseHandshakeState> _handshakes = new();

    /// <param name="crypto">The CASE crypto engine factory.</param>
    /// <param name="fabrics">The store of commissioned fabrics used to match Sigma1.</param>
    /// <param name="allocateSessionId">
    /// Allocates a unique responder session id. TODO: source this from the session manager so the id
    /// is reserved for the installed secure session.
    /// </param>
    /// <param name="resumptionStore">
    /// Optional store of prior-session resumption records. When supplied, a Sigma1 that offers a valid
    /// resumption completes via Sigma2_Resume (spec §4.14.2.6); when <see langword="null"/> or the offer
    /// cannot be honoured, a full handshake runs instead.
    /// </param>
    public CaseServer(
        ICaseCryptoProvider crypto,
        IFabricStore fabrics,
        Func<ushort> allocateSessionId,
        ICaseResumptionStore? resumptionStore = null)
    {
        _crypto = crypto ?? throw new ArgumentNullException(nameof(crypto));
        _fabrics = fabrics ?? throw new ArgumentNullException(nameof(fabrics));
        _allocateSessionId = allocateSessionId ?? throw new ArgumentNullException(nameof(allocateSessionId));
        _resumptionStore = resumptionStore;
    }

    /// <summary>Raised once per successful handshake with the material needed to install the session.</summary>
    public event EventHandler<CaseSessionEstablishedEventArgs>? SessionEstablished;

    /// <inheritdoc />
    public ValueTask OnMessageAsync(ExchangeContext exchange, SecureChannelOpcode opcode, MatterMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        ArgumentNullException.ThrowIfNull(message);

        return opcode switch
        {
            SecureChannelOpcode.CaseSigma1 => HandleSigma1Async(exchange, message, cancellationToken),
            SecureChannelOpcode.CaseSigma3 => HandleSigma3Async(exchange, message, cancellationToken),
            SecureChannelOpcode.StatusReport => HandlePeerStatusReportAsync(exchange, message, cancellationToken),
            _ => FailAsync(exchange, SecureChannelStatusCode.InvalidParameter, cancellationToken),
        };
    }

    /// <inheritdoc />
    public void OnExchangeClosed(ExchangeContext exchange)
    {
        if (_handshakes.TryRemove(exchange, out var state))
        {
            state.Dispose();
        }
    }

    private async ValueTask HandleSigma1Async(ExchangeContext exchange, MatterMessage message, CancellationToken cancellationToken)
    {
        // A duplicate Sigma1 on an in-flight exchange restarts the handshake cleanly.
        OnExchangeClosed(exchange);

        if (!TryParseSigma1(message.ApplicationPayload.Span, out var sigma1))
        {
            await FailAsync(exchange, SecureChannelStatusCode.InvalidParameter, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Type 1 resumption: honour a valid resumption offer with Sigma2_Resume (spec §4.14.2.6). A
        // missing store, unknown resumptionID, or failed MIC falls through to a full handshake (Type 2).
        if (sigma1.HasResumption &&
            TryBuildSigma2Resume(sigma1, out byte[] resumePayload, out CaseHandshakeState? resumeState))
        {
            _handshakes[exchange] = resumeState;
            await exchange.SendAsync((byte)SecureChannelOpcode.CaseSigma2Resume, resumePayload, reliable: true, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!TryResolveFabric(sigma1.DestinationId, sigma1.InitiatorRandom, out var fabric))
        {
            await FailAsync(exchange, SecureChannelStatusCode.NoSharedTrustRoots, cancellationToken).ConfigureAwait(false);
            return;
        }

        ICaseResponderContext? context = null;
        byte[] sigma2Payload;
        ushort localSessionId;
        try
        {
            context = _crypto.CreateResponder(fabric);
            context.AppendToTranscript(message.ApplicationPayload.Span);

            var encrypted2 = context.BuildSigma2Encrypted(sigma1.InitiatorEphemeralPublicKey);
            localSessionId = _allocateSessionId();
            sigma2Payload = BuildSigma2(context.ResponderRandom.Span, localSessionId, context.ResponderEphemeralPublicKey.Span, encrypted2);
            context.AppendToTranscript(sigma2Payload);
        }
        catch (CryptographicException)
        {
            // A malformed initiator ephemeral key or credential failure aborts the handshake.
            context?.Dispose();
            await FailAsync(exchange, SecureChannelStatusCode.InvalidParameter, cancellationToken).ConfigureAwait(false);
            return;
        }

        _handshakes[exchange] = new CaseHandshakeState(context, fabric.FabricIndex, sigma1.InitiatorSessionId, localSessionId, sigma1.InitiatorSessionParams);
        await exchange.SendAsync((byte)SecureChannelOpcode.CaseSigma2, sigma2Payload, reliable: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Attempts to satisfy a resumption offer from Sigma1: looks up the stored record by resumptionID,
    /// verifies the initiatorResumeMIC, then derives the resumed session keys, issues a fresh
    /// resumptionID, and builds the Sigma2_Resume payload. Returns <see langword="false"/> (so the
    /// caller runs a full handshake) when resumption is unavailable, unknown, or fails verification.
    /// See the Matter Core Specification, section 4.14.2.6.
    /// </summary>
    private bool TryBuildSigma2Resume(in Sigma1Fields sigma1, out byte[] payload, out CaseHandshakeState? state)
    {
        payload = [];
        state = null;

        if (_resumptionStore is null ||
            !_resumptionStore.TryGetByResumptionId(sigma1.ResumptionId!, out var record) ||
            !_crypto.VerifySigma1ResumeMic(record.SharedSecret, sigma1.InitiatorRandom, sigma1.ResumptionId!, sigma1.InitiatorResumeMic!))
        {
            return false;
        }

        // A fresh resumptionID is issued for the resumed session and salts the new key schedule.
        byte[] newResumptionId = _crypto.GenerateResumptionId();
        byte[] sigma2ResumeMic = _crypto.ComputeSigma2ResumeMic(record.SharedSecret, sigma1.InitiatorRandom, newResumptionId);
        CaseSessionKeys keys = _crypto.DeriveResumedSessionKeys(record.SharedSecret, sigma1.InitiatorRandom, newResumptionId);

        ushort localSessionId = _allocateSessionId();
        payload = BuildSigma2Resume(newResumptionId, sigma2ResumeMic, localSessionId);

        state = CaseHandshakeState.ForResumption(
            record.Peer.FabricIndex,
            sigma1.InitiatorSessionId,
            localSessionId,
            sigma1.InitiatorSessionParams,
            record.Peer.NodeId,
            keys,
            newResumptionId,
            record.SharedSecret);
        return true;
    }

    private async ValueTask HandleSigma3Async(ExchangeContext exchange, MatterMessage message, CancellationToken cancellationToken)
    {
        if (!_handshakes.TryGetValue(exchange, out var state) ||
            state.Phase != CaseSessionPhase.AwaitingSigma3 ||
            state.Context is not { } context)
        {
            await FailAsync(exchange, SecureChannelStatusCode.InvalidParameter, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!TryParseSingleByteString(message.ApplicationPayload.Span, tagNumber: 1, out var encrypted3))
        {
            await FailAsync(exchange, SecureChannelStatusCode.InvalidParameter, cancellationToken).ConfigureAwait(false);
            return;
        }

        CaseSessionKeys keys;
        NodeId peerNodeId;
        byte[] sharedSecret;
        try
        {
            if (!context.TryProcessSigma3(encrypted3))
            {
                await FailAsync(exchange, SecureChannelStatusCode.InvalidParameter, cancellationToken).ConfigureAwait(false);
                return;
            }

            context.AppendToTranscript(message.ApplicationPayload.Span);
            keys = context.DeriveSessionKeys();
            peerNodeId = context.PeerNodeId;
            sharedSecret = context.SharedSecret.ToArray();
        }
        catch (CryptographicException)
        {
            await FailAsync(exchange, SecureChannelStatusCode.InvalidParameter, cancellationToken).ConfigureAwait(false);
            return;
        }

        await SecureChannelHandler.SendStatusReportAsync(
            exchange, GeneralStatusCode.Success, SecureChannelStatusCode.SessionEstablishmentSuccess, cancellationToken: cancellationToken).ConfigureAwait(false);

        state.Phase = CaseSessionPhase.Established;

        // Persist a resumption record so a future Sigma1 from this peer can resume (spec §4.14.2.6).
        // The resumptionID MUST be the one we advertised in Sigma2's TBEData2, so the peer's later
        // Sigma1 resume offer keys against the same record.
        SaveResumptionRecord(state.FabricIndex, peerNodeId, sharedSecret, state.PeerSessionParameters, context.ResumptionId.ToArray());

        // TODO: install the operational secure session (local/peer session ids + keys + peer node id)
        // via the session manager before the exchange closes.
        SessionEstablished?.Invoke(this, new CaseSessionEstablishedEventArgs(
            state.LocalSessionId, state.PeerSessionId, state.FabricIndex, peerNodeId, keys, state.PeerSessionParameters,
            context.PeerCaseAuthenticatedTags));
    }

    private ValueTask HandlePeerStatusReportAsync(ExchangeContext exchange, MatterMessage message, CancellationToken cancellationToken)
    {
        // A success StatusReport completes a resumption we offered via Sigma2_Resume; anything else
        // (or a StatusReport during a full handshake) signals an abort and drops our state.
        if (_handshakes.TryGetValue(exchange, out var state) &&
            state.Phase == CaseSessionPhase.AwaitingResumeStatusReport &&
            SecureChannelStatusReport.TryParse(message.ApplicationPayload.Span, out var report) &&
            report.IsSuccess &&
            report.SecureChannelStatus == SecureChannelStatusCode.SessionEstablishmentSuccess)
        {
            CompleteResumption(exchange, state);
            return ValueTask.CompletedTask;
        }

        OnExchangeClosed(exchange);
        return ValueTask.CompletedTask;
    }

    private void CompleteResumption(ExchangeContext exchange, CaseHandshakeState state)
    {
        state.Phase = CaseSessionPhase.Established;

        // Persist the freshly issued resumptionID + shared secret for the next resumption.
        SaveResumptionRecord(state.FabricIndex, state.PeerNodeId, state.ResumptionSharedSecret, state.PeerSessionParameters, state.NewResumptionId);

        // TODO: install the operational secure session via the session manager before the exchange closes.
        SessionEstablished?.Invoke(this, new CaseSessionEstablishedEventArgs(
            state.LocalSessionId, state.PeerSessionId, state.FabricIndex, state.PeerNodeId, state.ResumedKeys!, state.PeerSessionParameters));
    }

    private void SaveResumptionRecord(
        FabricIndex fabricIndex,
        NodeId peerNodeId,
        ReadOnlySpan<byte> sharedSecret,
        ReliableMessageProtocolConfig peerSessionParameters,
        byte[]? resumptionId = null)
    {
        if (_resumptionStore is null || sharedSecret.IsEmpty)
        {
            return;
        }

        _resumptionStore.Save(new CaseResumptionRecord(
            resumptionId ?? _crypto.GenerateResumptionId(),
            sharedSecret.ToArray(),
            new OperationalPeer(fabricIndex, peerNodeId),
            peerSessionParameters));
    }

    private async ValueTask FailAsync(ExchangeContext exchange, SecureChannelStatusCode statusCode, CancellationToken cancellationToken)
    {
        if (_handshakes.TryGetValue(exchange, out var state))
        {
            state.Phase = CaseSessionPhase.Failed;
        }

        await SecureChannelHandler.SendStatusReportAsync(
            exchange, GeneralStatusCode.Failure, statusCode, cancellationToken: cancellationToken).ConfigureAwait(false);

        OnExchangeClosed(exchange);
    }

    private bool TryResolveFabric(ReadOnlySpan<byte> destinationId, ReadOnlySpan<byte> initiatorRandom, out ResolvedFabric resolved)
    {
        foreach (var fabric in _fabrics.Fabrics)
        {
            var candidate = _crypto.ComputeDestinationIdentifier(
                fabric.IdentityProtectionKey, initiatorRandom, fabric.RootPublicKey, fabric.FabricId, fabric.NodeId);

            // TODO(diagnostic): temporary - remove once CASE DestinationId resolution is confirmed.
            Console.Error.WriteLine(
                $"[case] resolve fabric idx={fabric.FabricIndex.Value} nodeId=0x{fabric.NodeId.Value:X16} " +
                $"fabricId=0x{fabric.FabricId.Value:X16} rootPubLen={fabric.RootPublicKey.Length} ipkLen={fabric.IdentityProtectionKey.Length} " +
                $"candidate={Convert.ToHexString(candidate)} wanted={Convert.ToHexString(destinationId)} " +
                $"match={CryptographicOperations.FixedTimeEquals(candidate, destinationId)}");

            if (CryptographicOperations.FixedTimeEquals(candidate, destinationId))
            {
                resolved = fabric;
                return true;
            }
        }

        resolved = null!;
        return false;
    }

    // --- TLV wire format (spec section 4.14.1) --------------------------------------------------

    private static byte[] BuildSigma2(ReadOnlySpan<byte> responderRandom, ushort responderSessionId, ReadOnlySpan<byte> responderEphPubKey, ReadOnlySpan<byte> encrypted2)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new TlvWriter(buffer);

        writer.StartStructure(TlvTag.Anonymous);
        writer.WriteByteString(TlvTag.ContextSpecific(1), responderRandom);
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(2), responderSessionId);
        writer.WriteByteString(TlvTag.ContextSpecific(3), responderEphPubKey);
        writer.WriteByteString(TlvTag.ContextSpecific(4), encrypted2);

        // responderSessionParams (field 5): advertise this node's MRP config so the initiator uses the
        // correct retransmit timing for messages sent to the responder (spec §4.11.2, §4.14.1).
        SessionParametersCodec.Write(writer, TlvTag.ContextSpecific(5), ReliableMessageProtocolConfig.Default);

        writer.EndContainer();

        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] BuildSigma2Resume(
        ReadOnlySpan<byte> resumptionId, ReadOnlySpan<byte> sigma2ResumeMic, ushort responderSessionId)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new TlvWriter(buffer);

        writer.StartStructure(TlvTag.Anonymous);
        writer.WriteByteString(TlvTag.ContextSpecific(1), resumptionId);      // resumptionID
        writer.WriteByteString(TlvTag.ContextSpecific(2), sigma2ResumeMic);   // sigma2ResumeMIC
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(3), responderSessionId);

        // responderSessionParams (field 4): advertise this node's MRP config for the resumed session.
        SessionParametersCodec.Write(writer, TlvTag.ContextSpecific(4), ReliableMessageProtocolConfig.Default);

        writer.EndContainer();

        return buffer.WrittenSpan.ToArray();
    }

    private static bool TryParseSigma1(ReadOnlySpan<byte> payload, out Sigma1Fields fields)
    {
        byte[]? initiatorRandom = null;
        ushort initiatorSessionId = 0;
        byte[]? destinationId = null;
        byte[]? initiatorEphPubKey = null;
        byte[]? resumptionId = null;
        byte[]? initiatorResumeMic = null;
        var initiatorSessionParams = ReliableMessageProtocolConfig.Default;

        var reader = new TlvReader(payload);
        var depth = 0;
        while (reader.Read())
        {
            // initiatorSessionParams (field 5) is a structure nested one level inside the outer
            // Sigma1 structure. Parse it in place so the outer depth tracking never descends into it.
            if (depth == 1 && reader.IsContainer && reader.Tag.TagNumber == 5)
            {
                initiatorSessionParams = SessionParametersCodec.ReadStructure(ref reader);
                continue;
            }

            if (reader.IsContainer) { depth++; continue; }
            if (reader.IsEndOfContainer) { depth--; continue; }
            if (depth != 1) { continue; } // skip any other nested container's scalar members

            switch (reader.Tag.TagNumber)
            {
                case 1: initiatorRandom = reader.GetByteString().ToArray(); break;
                case 2: initiatorSessionId = (ushort)reader.GetUnsignedInteger(); break;
                case 3: destinationId = reader.GetByteString().ToArray(); break;
                case 4: initiatorEphPubKey = reader.GetByteString().ToArray(); break;
                case 6: resumptionId = reader.GetByteString().ToArray(); break;         // resumptionID
                case 7: initiatorResumeMic = reader.GetByteString().ToArray(); break;   // initiatorResumeMIC
            }
        }

        if (initiatorRandom is not { Length: RandomLength } ||
            destinationId is not { Length: DestinationIdLength } ||
            initiatorEphPubKey is not { Length: PublicKeyLength })
        {
            fields = default;
            return false;
        }

        fields = new Sigma1Fields(
            initiatorRandom, initiatorSessionId, destinationId, initiatorEphPubKey, resumptionId, initiatorResumeMic, initiatorSessionParams);
        return true;
    }

    private static bool TryParseSingleByteString(ReadOnlySpan<byte> payload, uint tagNumber, out byte[] value)
    {
        byte[]? result = null;

        var reader = new TlvReader(payload);
        var depth = 0;
        while (reader.Read())
        {
            if (reader.IsContainer) { depth++; continue; }
            if (reader.IsEndOfContainer) { depth--; continue; }
            if (depth == 1 && reader.Tag.TagNumber == tagNumber)
            {
                result = reader.GetByteString().ToArray();
            }
        }

        value = result ?? [];
        return result is not null;
    }

    private readonly record struct Sigma1Fields(
        byte[] InitiatorRandom,
        ushort InitiatorSessionId,
        byte[] DestinationId,
        byte[] InitiatorEphemeralPublicKey,
        byte[]? ResumptionId,
        byte[]? InitiatorResumeMic,
        ReliableMessageProtocolConfig InitiatorSessionParams)
    {
        /// <summary>True when the initiator offered a resumption (both fields 6 and 7 are present).</summary>
        public bool HasResumption => ResumptionId is { Length: > 0 } && InitiatorResumeMic is { Length: > 0 };
    }

    private sealed class CaseHandshakeState : IDisposable
    {
        public CaseHandshakeState(
            ICaseResponderContext context,
            FabricIndex fabricIndex,
            ushort peerSessionId,
            ushort localSessionId,
            ReliableMessageProtocolConfig peerSessionParameters)
        {
            Context = context;
            FabricIndex = fabricIndex;
            PeerSessionId = peerSessionId;
            LocalSessionId = localSessionId;
            PeerSessionParameters = peerSessionParameters;
            Phase = CaseSessionPhase.AwaitingSigma3;
        }

        private CaseHandshakeState(
            FabricIndex fabricIndex,
            ushort peerSessionId,
            ushort localSessionId,
            ReliableMessageProtocolConfig peerSessionParameters,
            NodeId peerNodeId,
            CaseSessionKeys resumedKeys,
            byte[] newResumptionId,
            byte[] resumptionSharedSecret)
        {
            Context = null;
            FabricIndex = fabricIndex;
            PeerSessionId = peerSessionId;
            LocalSessionId = localSessionId;
            PeerSessionParameters = peerSessionParameters;
            PeerNodeId = peerNodeId;
            ResumedKeys = resumedKeys;
            NewResumptionId = newResumptionId;
            ResumptionSharedSecret = resumptionSharedSecret;
            Phase = CaseSessionPhase.AwaitingResumeStatusReport;
        }

        /// <summary>Creates the state for a resumption awaiting the initiator's success StatusReport.</summary>
        public static CaseHandshakeState ForResumption(
            FabricIndex fabricIndex,
            ushort peerSessionId,
            ushort localSessionId,
            ReliableMessageProtocolConfig peerSessionParameters,
            NodeId peerNodeId,
            CaseSessionKeys resumedKeys,
            byte[] newResumptionId,
            byte[] resumptionSharedSecret) =>
            new(fabricIndex, peerSessionId, localSessionId, peerSessionParameters,
                peerNodeId, resumedKeys, newResumptionId, resumptionSharedSecret);

        /// <summary>The per-handshake responder crypto context; <see langword="null"/> on the resumption path.</summary>
        public ICaseResponderContext? Context { get; }
        public FabricIndex FabricIndex { get; }
        public ushort PeerSessionId { get; }
        public ushort LocalSessionId { get; }
        public ReliableMessageProtocolConfig PeerSessionParameters { get; }
        public CaseSessionPhase Phase { get; set; }

        // Resumption-only state (populated by ForResumption).
        public NodeId PeerNodeId { get; }
        public CaseSessionKeys? ResumedKeys { get; }
        public byte[] NewResumptionId { get; } = [];
        public byte[] ResumptionSharedSecret { get; } = [];

        public void Dispose() => Context?.Dispose();
    }
}