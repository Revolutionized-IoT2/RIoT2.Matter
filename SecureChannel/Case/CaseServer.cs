using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using RIoT2.Matter.DataModel;
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
    private readonly ConcurrentDictionary<ExchangeContext, CaseHandshakeState> _handshakes = new();

    /// <param name="crypto">The CASE crypto engine factory.</param>
    /// <param name="fabrics">The store of commissioned fabrics used to match Sigma1.</param>
    /// <param name="allocateSessionId">
    /// Allocates a unique responder session id. TODO: source this from the session manager so the id
    /// is reserved for the installed secure session.
    /// </param>
    public CaseServer(ICaseCryptoProvider crypto, IFabricStore fabrics, Func<ushort> allocateSessionId)
    {
        _crypto = crypto ?? throw new ArgumentNullException(nameof(crypto));
        _fabrics = fabrics ?? throw new ArgumentNullException(nameof(fabrics));
        _allocateSessionId = allocateSessionId ?? throw new ArgumentNullException(nameof(allocateSessionId));
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
            SecureChannelOpcode.CaseSigma2Resume => HandleResumeAsync(exchange, cancellationToken),
            SecureChannelOpcode.StatusReport => HandlePeerStatusReport(exchange),
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

        if (sigma1.HasResumption)
        {
            // TODO: attempt session resumption (spec section 4.14.2.6). Falling through performs a full handshake.
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

    private async ValueTask HandleSigma3Async(ExchangeContext exchange, MatterMessage message, CancellationToken cancellationToken)
    {
        if (!_handshakes.TryGetValue(exchange, out var state) || state.Phase != CaseSessionPhase.AwaitingSigma3)
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
        try
        {
            if (!state.Context.TryProcessSigma3(encrypted3))
            {
                await FailAsync(exchange, SecureChannelStatusCode.InvalidParameter, cancellationToken).ConfigureAwait(false);
                return;
            }

            state.Context.AppendToTranscript(message.ApplicationPayload.Span);
            keys = state.Context.DeriveSessionKeys();
            peerNodeId = state.Context.PeerNodeId;
        }
        catch (CryptographicException)
        {
            await FailAsync(exchange, SecureChannelStatusCode.InvalidParameter, cancellationToken).ConfigureAwait(false);
            return;
        }

        await SecureChannelHandler.SendStatusReportAsync(
            exchange, GeneralStatusCode.Success, SecureChannelStatusCode.SessionEstablishmentSuccess, cancellationToken: cancellationToken).ConfigureAwait(false);

        state.Phase = CaseSessionPhase.Established;

        // TODO: install the operational secure session (local/peer session ids + keys + peer node id)
        // via the session manager before the exchange closes.
        SessionEstablished?.Invoke(this, new CaseSessionEstablishedEventArgs(
            state.LocalSessionId, state.PeerSessionId, state.FabricIndex, peerNodeId, keys, state.PeerSessionParameters));
    }

    private ValueTask HandleResumeAsync(ExchangeContext exchange, CancellationToken cancellationToken)
    {
        // TODO: implement CASE session resumption (the Sigma2_Resume path, spec section 4.14.2.6).
        return FailAsync(exchange, SecureChannelStatusCode.InvalidParameter, cancellationToken);
    }

    private ValueTask HandlePeerStatusReport(ExchangeContext exchange)
    {
        // A StatusReport from the initiator here signals an aborted handshake; drop our state.
        OnExchangeClosed(exchange);
        return ValueTask.CompletedTask;
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

    private static bool TryParseSigma1(ReadOnlySpan<byte> payload, out Sigma1Fields fields)
    {
        byte[]? initiatorRandom = null;
        ushort initiatorSessionId = 0;
        byte[]? destinationId = null;
        byte[]? initiatorEphPubKey = null;
        var hasResumption = false;
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
                case 6: case 7: hasResumption = true; break; // resumptionID / initiatorResumeMIC
            }
        }

        if (initiatorRandom is not { Length: RandomLength } ||
            destinationId is not { Length: DestinationIdLength } ||
            initiatorEphPubKey is not { Length: PublicKeyLength })
        {
            fields = default;
            return false;
        }

        fields = new Sigma1Fields(initiatorRandom, initiatorSessionId, destinationId, initiatorEphPubKey, hasResumption, initiatorSessionParams);
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
        bool HasResumption,
        ReliableMessageProtocolConfig InitiatorSessionParams);

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

        public ICaseResponderContext Context { get; }
        public FabricIndex FabricIndex { get; }
        public ushort PeerSessionId { get; }
        public ushort LocalSessionId { get; }
        public ReliableMessageProtocolConfig PeerSessionParameters { get; }
        public CaseSessionPhase Phase { get; set; }

        public void Dispose() => Context.Dispose();
    }
}