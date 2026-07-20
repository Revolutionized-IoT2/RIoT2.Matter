using System.Buffers;
using RIoT2.Matter.Clusters;
using RIoT2.Matter.Controller.Commissioning.Attestation;
using RIoT2.Matter.Controller.InteractionModel;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.InteractionModel;
using RIoT2.Matter.Tlv;
using InteractionModelException = RIoT2.Matter.Controller.InteractionModel.InteractionModelException;

namespace RIoT2.Matter.Controller.Commissioning;

/// <summary>
/// Implements <see cref="ICommissioningClusterClient"/> over the Interaction Model
/// <see cref="IInteractionClient"/>, issuing the commissioning-cluster commands as invokes on the
/// root endpoint. Cluster/command ids follow the Matter Core Specification (General Commissioning
/// 0x0030, Operational Credentials 0x003E). Command field encoding uses the same TLV layout the
/// node-side clusters parse.
/// </summary>
public sealed class CommissioningClusterClient : ICommissioningClusterClient
{
    private static readonly EndpointId RootEndpoint = new(0);
    private static readonly ClusterId GeneralCommissioning = new(0x0030);
    private static readonly ClusterId OperationalCredentials = new(0x003E);
    private static readonly ClusterId NetworkCommissioning = new(0x0031);

    private readonly IInteractionClient _client;
    private readonly byte[] _attestationChallenge;

    /// <param name="client">The Interaction Model client bound to the node's secure session.</param>
    /// <param name="attestationChallenge">
    /// The PASE attestation challenge (from <c>PaseClientResult.Keys.AttestationChallenge</c>) the
    /// device binds its attestation and CSR signatures to; threaded into the returned
    /// <see cref="AttestationInformation"/> so the verifier can check the signature.
    /// </param>
    public CommissioningClusterClient(IInteractionClient client, byte[] attestationChallenge)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _attestationChallenge = attestationChallenge ?? throw new ArgumentNullException(nameof(attestationChallenge));
    }

    public async Task ArmFailSafeAsync(ushort expiryLengthSeconds, CancellationToken cancellationToken = default)
    {
        // GeneralCommissioning.ArmFailSafe (0x00): ExpiryLengthSeconds [0], Breadcrumb [1].
        var fields = EncodeFields(w =>
        {
            w.WriteUnsignedInteger(TlvTag.ContextSpecific(0), expiryLengthSeconds);
            w.WriteUnsignedInteger(TlvTag.ContextSpecific(1), 0);
        });
        await InvokeExpectingSuccessAsync(GeneralCommissioning, new CommandId(0x00), fields, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AttestationInformation> RequestAttestationAsync(byte[] attestationNonce, CancellationToken cancellationToken = default)
    {
        // OperationalCredentials.AttestationRequest (0x00): AttestationNonce [0].
        var fields = EncodeFields(w => w.WriteByteString(TlvTag.ContextSpecific(0), attestationNonce));
        var result = await InvokeAsync(OperationalCredentials, new CommandId(0x00), fields, cancellationToken).ConfigureAwait(false);

        // AttestationResponse: AttestationElements [0], AttestationSignature [1].
        var (elements, signature) = DecodeTwoByteStrings(result.ResponseData.Span);

        // Fetch the DAC and PAI so the verifier can validate the chain and the attestation signature.
        var dac = await RequestCertificateChainAsync(CertificateChainType.DeviceAttestation, cancellationToken).ConfigureAwait(false);
        var pai = await RequestCertificateChainAsync(CertificateChainType.ProductAttestationIntermediate, cancellationToken).ConfigureAwait(false);

        return new AttestationInformation
        {
            AttestationElements = elements,
            AttestationSignature = signature,
            AttestationNonce = attestationNonce,
            AttestationChallenge = _attestationChallenge,
            DeviceAttestationCertificate = dac,
            ProductAttestationIntermediateCertificate = pai.Length > 0 ? pai : null,
        };
    }

    public async Task<byte[]> RequestCertificateChainAsync(CertificateChainType certificateType, CancellationToken cancellationToken = default)
    {
        // OperationalCredentials.CertificateChainRequest (0x02): CertificateType [0 : enum8].
        var fields = EncodeFields(w => w.WriteUnsignedInteger(TlvTag.ContextSpecific(0), (byte)certificateType));
        var result = await InvokeAsync(OperationalCredentials, new CommandId(0x02), fields, cancellationToken).ConfigureAwait(false);

        // CertificateChainResponse: Certificate [0 : octstr] (DER X.509).
        return DecodeSingleByteString(result.ResponseData.Span, tagNumber: 0)
            ?? throw new InteractionModelException($"CertificateChainResponse for {certificateType} had no certificate.");
    }

    public async Task<byte[]> RequestCsrAsync(byte[] csrNonce, CancellationToken cancellationToken = default)
    {
        // OperationalCredentials.CSRRequest (0x04): CSRNonce [0].
        var fields = EncodeFields(w => w.WriteByteString(TlvTag.ContextSpecific(0), csrNonce));
        var result = await InvokeAsync(OperationalCredentials, new CommandId(0x04), fields, cancellationToken).ConfigureAwait(false);

        // CSRResponse: NOCSRElements [0], AttestationSignature [1]. The DER CSR is inside NOCSRElements.
        var (nocsrElements, _) = DecodeTwoByteStrings(result.ResponseData.Span);
        return ExtractCsrFromNocsrElements(nocsrElements);
    }

    public async Task AddTrustedRootAsync(byte[] rootCertificate, CancellationToken cancellationToken = default)
    {
        // OperationalCredentials.AddTrustedRootCertificate (0x0B): RootCACertificate [0].
        var fields = EncodeFields(w => w.WriteByteString(TlvTag.ContextSpecific(0), rootCertificate));
        await InvokeExpectingSuccessAsync(OperationalCredentials, new CommandId(0x0B), fields, cancellationToken).ConfigureAwait(false);
    }

    public async Task<FabricIndex> AddNocAsync(
        byte[] noc, byte[]? icac, byte[] ipk, ulong caseAdminSubject, VendorId adminVendorId, CancellationToken cancellationToken = default)
    {
        // OperationalCredentials.AddNOC (0x06): NOCValue [0], ICACValue [1], IPKValue [2],
        // CaseAdminSubject [3], AdminVendorId [4].
        var fields = EncodeFields(w =>
        {
            w.WriteByteString(TlvTag.ContextSpecific(0), noc);
            if (icac is { Length: > 0 }) { w.WriteByteString(TlvTag.ContextSpecific(1), icac); }
            w.WriteByteString(TlvTag.ContextSpecific(2), ipk);
            w.WriteUnsignedInteger(TlvTag.ContextSpecific(3), caseAdminSubject);
            w.WriteUnsignedInteger(TlvTag.ContextSpecific(4), adminVendorId.Value);
        });

        var result = await InvokeAsync(OperationalCredentials, new CommandId(0x06), fields, cancellationToken).ConfigureAwait(false);

        // NOCResponse: StatusCode [0], FabricIndex [1], DebugText [2].
        var (statusCode, fabricIndex) = DecodeNocResponse(result.ResponseData.Span);
        if (statusCode != 0)
        {
            throw new InteractionModelException($"AddNOC failed with NodeOperationalCertStatus {statusCode}.");
        }

        return new FabricIndex(fabricIndex);
    }

    public async Task CommissioningCompleteAsync(CancellationToken cancellationToken = default)
    {
        // GeneralCommissioning.CommissioningComplete (0x04): no fields.
        await InvokeExpectingSuccessAsync(GeneralCommissioning, new CommandId(0x04), ReadOnlyMemory<byte>.Empty, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<NetworkCommissioningStatus> AddOrUpdateWiFiNetworkAsync(
        byte[] ssid, byte[] credentials, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ssid);
        ArgumentNullException.ThrowIfNull(credentials);

        // NetworkCommissioning.AddOrUpdateWiFiNetwork (0x02): SSID [0 : octstr], Credentials [1 : octstr],
        // Breadcrumb [2 : optional]. Responds with NetworkConfigResponse.
        var fields = EncodeFields(w =>
        {
            w.WriteByteString(TlvTag.ContextSpecific(0), ssid);
            w.WriteByteString(TlvTag.ContextSpecific(1), credentials);
        });
        var result = await InvokeAsync(NetworkCommissioning, new CommandId(0x02), fields, cancellationToken).ConfigureAwait(false);
        return DecodeNetworkConfigStatus(result.ResponseData.Span);
    }

    public async Task<NetworkCommissioningStatus> AddOrUpdateThreadNetworkAsync(
        byte[] operationalDataset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operationalDataset);

        // NetworkCommissioning.AddOrUpdateThreadNetwork (0x03): OperationalDataset [0 : octstr],
        // Breadcrumb [1 : optional]. Responds with NetworkConfigResponse.
        var fields = EncodeFields(w => w.WriteByteString(TlvTag.ContextSpecific(0), operationalDataset));
        var result = await InvokeAsync(NetworkCommissioning, new CommandId(0x03), fields, cancellationToken).ConfigureAwait(false);
        return DecodeNetworkConfigStatus(result.ResponseData.Span);
    }

    public async Task<NetworkCommissioningStatus> ConnectNetworkAsync(byte[] networkId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(networkId);

        // NetworkCommissioning.ConnectNetwork (0x06): NetworkID [0 : octstr], Breadcrumb [1 : optional].
        // Responds with ConnectNetworkResponse: NetworkingStatus [0 : enum8], DebugText [1], ErrorValue [2].
        var fields = EncodeFields(w => w.WriteByteString(TlvTag.ContextSpecific(0), networkId));
        var result = await InvokeAsync(NetworkCommissioning, new CommandId(0x06), fields, cancellationToken).ConfigureAwait(false);
        return DecodeNetworkConfigStatus(result.ResponseData.Span);
    }

    private async Task<InvokeResult> InvokeAsync(ClusterId cluster, CommandId command, ReadOnlyMemory<byte> fields, CancellationToken cancellationToken)
    {
        var result = await _client.InvokeAsync(
            new ClusterCommand { Endpoint = RootEndpoint, Cluster = cluster, Command = command, Fields = fields },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            throw new InteractionModelException($"Command 0x{command.Value:X2} on cluster 0x{cluster.Value:X4} failed.", result.Status?.Status);
        }

        return result;
    }

    private async Task InvokeExpectingSuccessAsync(ClusterId cluster, CommandId command, ReadOnlyMemory<byte> fields, CancellationToken cancellationToken)
        => _ = await InvokeAsync(cluster, command, fields, cancellationToken).ConfigureAwait(false);

    private static ReadOnlyMemory<byte> EncodeFields(Action<TlvWriter> build)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new TlvWriter(buffer);
        writer.StartStructure(TlvTag.Anonymous);
        build(writer);
        writer.EndContainer();
        return buffer.WrittenSpan.ToArray();
    }

    private static (byte[] First, byte[] Second) DecodeTwoByteStrings(ReadOnlySpan<byte> payload)
    {
        byte[]? first = null;
        byte[]? second = null;
        var reader = new TlvReader(payload);
        var depth = 0;
        while (reader.Read())
        {
            if (reader.IsContainer) { depth++; continue; }
            if (reader.IsEndOfContainer) { depth--; continue; }
            if (depth != 1) { continue; }
            switch (reader.Tag.TagNumber)
            {
                case 0: first = reader.GetByteString().ToArray(); break;
                case 1: second = reader.GetByteString().ToArray(); break;
            }
        }

        return (first ?? [], second ?? []);
    }

    private static byte[]? DecodeSingleByteString(ReadOnlySpan<byte> payload, int tagNumber)
    {
        var reader = new TlvReader(payload);
        var depth = 0;
        while (reader.Read())
        {
            if (reader.IsContainer) { depth++; continue; }
            if (reader.IsEndOfContainer) { depth--; continue; }
            if (depth == 1 && reader.Tag.TagNumber == tagNumber)
            {
                return reader.GetByteString().ToArray();
            }
        }

        return null;
    }

    private static (byte StatusCode, byte FabricIndex) DecodeNocResponse(ReadOnlySpan<byte> payload)
    {
        byte statusCode = 0xFF;
        byte fabricIndex = 0;
        var reader = new TlvReader(payload);
        var depth = 0;
        while (reader.Read())
        {
            if (reader.IsContainer) { depth++; continue; }
            if (reader.IsEndOfContainer) { depth--; continue; }
            if (depth != 1) { continue; }
            switch (reader.Tag.TagNumber)
            {
                case 0: statusCode = (byte)reader.GetUnsignedInteger(); break;
                case 1: fabricIndex = (byte)reader.GetUnsignedInteger(); break;
            }
        }

        return (statusCode, fabricIndex);
    }

    /// <summary>NOCSRElements: a structure with CSR [1 : octstr] and CSRNonce [2 : octstr]. Returns the DER CSR.</summary>
    private static byte[] ExtractCsrFromNocsrElements(byte[] nocsrElements)
    {
        var reader = new TlvReader(nocsrElements);
        var depth = 0;
        while (reader.Read())
        {
            if (reader.IsContainer) { depth++; continue; }
            if (reader.IsEndOfContainer) { depth--; continue; }
            if (depth == 1 && reader.Tag.TagNumber == 1)
            {
                return reader.GetByteString().ToArray();
            }
        }

        throw new InteractionModelException("The NOCSRElements did not contain a CSR.");
    }

    /// <summary>
    /// Decodes the NetworkingStatus [0 : enum8] field shared by NetworkConfigResponse and
    /// ConnectNetworkResponse (spec �11.8.7.9, �11.8.7.10).
    /// </summary>
    private static NetworkCommissioningStatus DecodeNetworkConfigStatus(ReadOnlySpan<byte> payload)
    {
        byte status = (byte)NetworkCommissioningStatus.UnknownError;
        var reader = new TlvReader(payload);
        var depth = 0;
        while (reader.Read())
        {
            if (reader.IsContainer) { depth++; continue; }
            if (reader.IsEndOfContainer) { depth--; continue; }
            if (depth == 1 && reader.Tag.TagNumber == 0)
            {
                status = (byte)reader.GetUnsignedInteger();
            }
        }

        return (NetworkCommissioningStatus)status;
    }
}