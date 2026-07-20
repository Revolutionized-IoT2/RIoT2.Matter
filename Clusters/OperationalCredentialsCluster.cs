using System.Linq;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.Device;
using RIoT2.Matter.InteractionModel;
using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.Clusters;

/// <summary>
/// The Operational Credentials cluster (0x003E) on the root endpoint: exposes the fabric table
/// (NOCs/Fabrics/TrustedRootCertificates), the SupportedFabrics/CommissionedFabrics counts, and the
/// accessing CurrentFabricIndex, and implements device attestation
/// (AttestationRequest/CertificateChainRequest/CSRRequest) plus fabric management
/// (AddNOC/UpdateNOC/UpdateFabricLabel/RemoveFabric/AddTrustedRootCertificate). The cluster owns the
/// Interaction Model surface; the fabric table, attestation, and key material are delegated to an
/// injected <see cref="IOperationalCredentialsManager"/>. Mandatory on endpoint 0. See the Matter
/// Core Specification, section 11.18.
/// </summary>
/// <remarks>
/// Add to the root endpoint, wiring the fabric-table/attestation backend:
/// <code>node.Root.AddCluster(new OperationalCredentialsCluster(manager));</code>
/// </remarks>
public sealed class OperationalCredentialsCluster : Cluster
{
    /// <summary>The Operational Credentials cluster identifier (0x003E).</summary>
    public static readonly ClusterId ClusterId = new(0x003E);

    // Attribute ids (spec §11.18.5).
    private const uint NocsId = 0x0000;
    private const uint FabricsId = 0x0001;
    private const uint SupportedFabricsId = 0x0002;
    private const uint CommissionedFabricsId = 0x0003;
    private const uint TrustedRootCertificatesId = 0x0004;
    private const uint CurrentFabricIndexId = 0x0005;

    // Command ids (spec §11.18.6).
    private const uint AttestationRequestId = 0x00;
    private const uint AttestationResponseId = 0x01;
    private const uint CertificateChainRequestId = 0x02;
    private const uint CertificateChainResponseId = 0x03;
    private const uint CsrRequestId = 0x04;
    private const uint CsrResponseId = 0x05;
    private const uint AddNocId = 0x06;
    private const uint UpdateNocId = 0x07;
    private const uint NocResponseId = 0x08;
    private const uint UpdateFabricLabelId = 0x09;
    private const uint RemoveFabricId = 0x0A;
    private const uint AddTrustedRootCertificateId = 0x0B;

    // The FabricIndex field tag shared by every fabric-scoped struct (spec §7.13.2).
    private const byte FabricIndexFieldTag = 254;

    private const int NonceLength = 32;
    private const int IpkLength = 16;
    private const int MaxLabelLength = 32;

    private static readonly AttributeId[] AttributeIdList =
    [
        new(NocsId), new(FabricsId), new(SupportedFabricsId),
        new(CommissionedFabricsId), new(TrustedRootCertificatesId), new(CurrentFabricIndexId),
    ];

    private static readonly CommandId[] AcceptedCommands =
    [
        new(AttestationRequestId), new(CertificateChainRequestId), new(CsrRequestId),
        new(AddNocId), new(UpdateNocId), new(UpdateFabricLabelId), new(RemoveFabricId),
        new(AddTrustedRootCertificateId),
    ];

    private static readonly CommandId[] GeneratedCommands =
    [
        new(AttestationResponseId), new(CertificateChainResponseId), new(CsrResponseId), new(NocResponseId),
    ];

    private readonly IOperationalCredentialsManager _manager;

    /// <param name="manager">The fabric-table / attestation / operational-key backend this cluster drives.</param>
    public OperationalCredentialsCluster(IOperationalCredentialsManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        _manager = manager;
        _manager.Changed += OnManagerChanged;
    }

    /// <inheritdoc />
    public override ClusterId Id => ClusterId;

    /// <inheritdoc />
    /// <remarks>Revision 1 attribute/command set.</remarks>
    public override ushort ClusterRevision => 1;

    /// <inheritdoc />
    public override IReadOnlyCollection<AttributeId> AttributeIds => AttributeIdList;

    /// <inheritdoc />
    public override IReadOnlyCollection<CommandId> AcceptedCommandIds => AcceptedCommands;

    /// <inheritdoc />
    public override IReadOnlyCollection<CommandId> GeneratedCommandIds => GeneratedCommands;

    /// <inheritdoc />
    protected override ValueTask<InteractionModelStatusCode> ReadAttributeCoreAsync(
        AttributeId attributeId, TlvWriter writer, TlvTag tag, InteractionContext context, CancellationToken cancellationToken)
    {
        switch (attributeId.Value)
        {
            case NocsId:
                WriteNocs(writer, tag, context);
                break;
            case FabricsId:
                WriteFabrics(writer, tag, context);
                break;
            case SupportedFabricsId:
                writer.WriteUnsignedInteger(tag, _manager.SupportedFabrics);
                break;
            case CommissionedFabricsId:
                writer.WriteUnsignedInteger(tag, (byte)_manager.Fabrics.Count);
                break;
            case TrustedRootCertificatesId:
                WriteTrustedRoots(writer, tag);
                break;
            case CurrentFabricIndexId:
                writer.WriteUnsignedInteger(tag, context.AccessingFabricIndex.Value);
                break;
            default:
                return new ValueTask<InteractionModelStatusCode>(InteractionModelStatusCode.UnsupportedAttribute);
        }

        return new ValueTask<InteractionModelStatusCode>(InteractionModelStatusCode.Success);
    }

    /// <inheritdoc />
    protected override ValueTask<CommandResponse> InvokeCommandCoreAsync(
        CommandId commandId, ReadOnlyMemory<byte> fields, InteractionContext context, CancellationToken cancellationToken)
        => commandId.Value switch
        {
            AttestationRequestId => CommandCodec.Invoke(fields, f => HandleAttestationRequest(f, context)),
            CertificateChainRequestId => CommandCodec.Invoke(fields, HandleCertificateChainRequest),
            CsrRequestId => CommandCodec.Invoke(fields, f => HandleCsrRequest(f, context)),
            AddNocId => CommandCodec.Invoke(fields, HandleAddNoc),
            UpdateNocId => CommandCodec.Invoke(fields, f => HandleUpdateNoc(f, context)),
            UpdateFabricLabelId => CommandCodec.Invoke(fields, f => HandleUpdateFabricLabel(f, context)),
            RemoveFabricId => CommandCodec.Invoke(fields, HandleRemoveFabric),
            AddTrustedRootCertificateId => CommandCodec.Invoke(fields, HandleAddTrustedRoot),
            _ => new ValueTask<CommandResponse>(CommandResponse.FromStatus(InteractionModelStatusCode.UnsupportedCommand)),
        };

    private CommandResponse HandleAttestationRequest(CommandFields fields, InteractionContext context)
    {
        if (!context.IsSecure)
        {
            return CommandResponse.FromStatus(InteractionModelStatusCode.Failure);
        }

        var nonce = fields.GetRequired(0, TlvCodec.OctetString, v => v.Length == NonceLength);

        var result = _manager.CreateAttestation(nonce, context.AttestationChallenge.Span);
        if (result is not { } attestation)
        {
            return CommandResponse.FromStatus(InteractionModelStatusCode.Failure);
        }

        return CommandCodec.Respond(new CommandId(AttestationResponseId), w => w
            .Write(0, TlvCodec.OctetString, attestation.AttestationElements)   // AttestationElements
            .Write(1, TlvCodec.OctetString, attestation.AttestationSignature)); // AttestationSignature
    }

    private CommandResponse HandleCertificateChainRequest(CommandFields fields)
    {
        var type = (CertificateChainType)fields.GetRequired(0, TlvCodec.UInt8, v => v is 1 or 2);

        var certificate = _manager.GetCertificateChain(type);
        return certificate is null
            ? CommandResponse.FromStatus(InteractionModelStatusCode.Failure)
            : CommandCodec.Respond(new CommandId(CertificateChainResponseId), w => w.Write(0, TlvCodec.OctetString, certificate));
    }

    private CommandResponse HandleCsrRequest(CommandFields fields, InteractionContext context)
    {
        if (!context.IsSecure)
        {
            return CommandResponse.FromStatus(InteractionModelStatusCode.Failure);
        }

        var csrNonce = fields.GetRequired(0, TlvCodec.OctetString, v => v.Length == NonceLength);
        var isForUpdateNoc = fields.GetOptional(1, TlvCodec.Bool, fallback: false);

        var result = _manager.CreateCsr(csrNonce, isForUpdateNoc, context.AttestationChallenge.Span);
        if (result is not { } csr)
        {
            return CommandResponse.FromStatus(InteractionModelStatusCode.Failure);
        }

        return CommandCodec.Respond(new CommandId(CsrResponseId), w => w
            .Write(0, TlvCodec.OctetString, csr.NocsrElements)         // NOCSRElements
            .Write(1, TlvCodec.OctetString, csr.AttestationSignature)); // AttestationSignature
    }

    private CommandResponse HandleAddNoc(CommandFields fields)
    {
        var noc = fields.GetRequired(0, TlvCodec.OctetString);
        fields.TryGet(1, TlvCodec.OctetString, out var icac); // optional ICAC
        var ipk = fields.GetRequired(2, TlvCodec.OctetString, v => v.Length == IpkLength);
        var caseAdminSubject = fields.GetRequired(3, TlvCodec.UInt64);
        var adminVendorId = new VendorId(fields.GetRequired(4, TlvCodec.UInt16));

        return NocResponse(_manager.AddNoc(noc, icac, ipk, caseAdminSubject, adminVendorId));
    }

    private CommandResponse HandleUpdateNoc(CommandFields fields, InteractionContext context)
    {
        var noc = fields.GetRequired(0, TlvCodec.OctetString);
        fields.TryGet(1, TlvCodec.OctetString, out var icac); // optional ICAC

        return NocResponse(_manager.UpdateNoc(noc, icac, context.AccessingFabricIndex));
    }

    private CommandResponse HandleUpdateFabricLabel(CommandFields fields, InteractionContext context)
    {
        var label = fields.GetRequired(0, TlvCodec.Utf8String, v => v.Length <= MaxLabelLength);
        return NocResponse(_manager.UpdateFabricLabel(label, context.AccessingFabricIndex));
    }

    private CommandResponse HandleRemoveFabric(CommandFields fields)
    {
        var fabricIndex = new FabricIndex(fields.GetRequired(0, TlvCodec.UInt8, v => v != 0));
        return NocResponse(_manager.RemoveFabric(fabricIndex));
    }

    private CommandResponse HandleAddTrustedRoot(CommandFields fields)
    {
        var rootCertificate = fields.GetRequired(0, TlvCodec.OctetString);

        // AddTrustedRootCertificate returns a bare StatusResponse rather than a response command.
        return CommandResponse.FromStatus(_manager.AddTrustedRoot(rootCertificate) switch
        {
            NodeOperationalCertStatus.Ok => InteractionModelStatusCode.Success,
            NodeOperationalCertStatus.TableFull => InteractionModelStatusCode.ResourceExhausted,
            _ => InteractionModelStatusCode.Failure,
        });
    }

    private void WriteNocs(TlvWriter writer, TlvTag tag, InteractionContext context)
    {
        writer.StartArray(tag);
        foreach (var noc in _manager.Nocs)
        {
            // Fabric-filtered reads return only the accessing fabric's entry (spec §7.13.2).
            if (context.IsFabricFiltered && noc.FabricIndex != context.AccessingFabricIndex)
            {
                continue;
            }

            writer.StartStructure(TlvTag.Anonymous);
            writer.WriteByteString(TlvTag.ContextSpecific(1), noc.Noc); // NOC
            if (noc.Icac is { } icac)
            {
                writer.WriteByteString(TlvTag.ContextSpecific(2), icac); // ICAC
            }
            else
            {
                writer.WriteNull(TlvTag.ContextSpecific(2)); // ICAC is nullable
            }

            writer.WriteUnsignedInteger(TlvTag.ContextSpecific(FabricIndexFieldTag), noc.FabricIndex.Value);
            writer.EndContainer();
        }

        writer.EndContainer();
    }

    private void WriteFabrics(TlvWriter writer, TlvTag tag, InteractionContext context)
    {
        writer.StartArray(tag);
        foreach (var fabric in _manager.Fabrics)
        {
            if (context.IsFabricFiltered && fabric.FabricIndex != context.AccessingFabricIndex)
            {
                continue;
            }

            writer.StartStructure(TlvTag.Anonymous);
            writer.WriteByteString(TlvTag.ContextSpecific(1), fabric.RootPublicKey); // RootPublicKey
            writer.WriteUnsignedInteger(TlvTag.ContextSpecific(2), fabric.VendorId.Value); // VendorID
            writer.WriteUnsignedInteger(TlvTag.ContextSpecific(3), fabric.FabricId.Value); // FabricID
            writer.WriteUnsignedInteger(TlvTag.ContextSpecific(4), fabric.NodeId.Value); // NodeID
            writer.WriteUtf8String(TlvTag.ContextSpecific(5), fabric.Label); // Label
            writer.WriteUnsignedInteger(TlvTag.ContextSpecific(FabricIndexFieldTag), fabric.FabricIndex.Value);
            writer.EndContainer();
        }

        writer.EndContainer();
    }

    private void WriteTrustedRoots(TlvWriter writer, TlvTag tag)
    {
        writer.StartArray(tag);
        foreach (var root in _manager.TrustedRootCertificates)
        {
            writer.WriteByteString(TlvTag.Anonymous, root);
        }

        writer.EndContainer();
    }

    private void OnManagerChanged(object? sender, EventArgs e) => IncrementDataVersion();

    private static CommandResponse NocResponse(NocOperationResult result) =>
        CommandCodec.Respond(new CommandId(NocResponseId), w =>
        {
            w.Write(0, TlvCodec.UInt8, (byte)result.Status); // StatusCode
            if (result.Status == NodeOperationalCertStatus.Ok)
            {
                w.Write(1, TlvCodec.UInt8, result.FabricIndex.Value); // FabricIndex (present on success)
            }

            if (!string.IsNullOrEmpty(result.DebugText))
            {
                w.Write(2, TlvCodec.Utf8String, result.DebugText); // DebugText
            }
        });
}