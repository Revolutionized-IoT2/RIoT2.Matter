using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using RIoT2.Matter.Controller.InteractionModel;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.InteractionModel;
using RIoT2.Matter.Tlv;
using InteractionModelException = RIoT2.Matter.Controller.InteractionModel.InteractionModelException;

namespace RIoT2.Matter.Controller.Administration;

/// <summary>
/// Implements <see cref="IAdministratorCommissioningClient"/> over the Interaction Model
/// <see cref="IInteractionClient"/>, issuing Administrator Commissioning cluster (0x003C) commands
/// and attribute reads on the root endpoint. Command field encoding mirrors the TLV layout the
/// node-side <c>AdministratorCommissioningCluster</c> parses. All commands are issued as timed
/// invokes, as the cluster requires. See the Matter Core Specification, section 11.19.
/// </summary>
public sealed class AdministratorCommissioningClient : IAdministratorCommissioningClient
{
    private static readonly EndpointId RootEndpoint = new(0);
    private static readonly ClusterId AdministratorCommissioning = new(0x003C);

    // Attribute ids (spec 11.19.7).
    private const uint WindowStatusId = 0x0000;
    private const uint AdminFabricIndexId = 0x0001;
    private const uint AdminVendorIdId = 0x0002;

    // Command ids (spec 11.19.8).
    private static readonly CommandId OpenCommissioningWindow = new(0x00);
    private static readonly CommandId OpenBasicCommissioningWindow = new(0x01);
    private static readonly CommandId RevokeCommissioning = new(0x02);

    // Field constraints (spec 11.19.8.1) enforced client-side for early rejection.
    private const int VerifierLength = 97;
    private const int MinSaltLength = 16;
    private const int MaxSaltLength = 32;
    private const ushort MaxDiscriminator = 0x0FFF;

    private readonly IInteractionClient _client;
    private readonly ushort _timedInvokeTimeoutMs;

    /// <param name="client">The Interaction Model client bound to the node's operational (CASE) session.</param>
    /// <param name="timedInvokeTimeoutMs">
    /// The Timed Request window (ms) allowed between the timed request and each invoke; defaults to 5000.
    /// </param>
    public AdministratorCommissioningClient(IInteractionClient client, ushort timedInvokeTimeoutMs = 5000)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _timedInvokeTimeoutMs = timedInvokeTimeoutMs;
    }

    public async Task<CommissioningWindowState> ReadWindowStateAsync(CancellationToken cancellationToken = default)
    {
        var reports = await _client.ReadAttributesAsync(
            [
                Path(WindowStatusId),
                Path(AdminFabricIndexId),
                Path(AdminVendorIdId),
            ],
            cancellationToken).ConfigureAwait(false);

        var status = CommissioningWindowStatus.WindowNotOpen;
        FabricIndex? adminFabricIndex = null;
        VendorId? adminVendorId = null;

        foreach (var report in reports)
        {
            if (report.AttributeData is not { } data)
            {
                continue;
            }

            switch (data.Path.Attribute?.Value)
            {
                case WindowStatusId:
                    status = (CommissioningWindowStatus)ReadNullableUInt(data.Data.Span, out _);
                    break;
                case AdminFabricIndexId:
                    if (ReadNullableUInt(data.Data.Span, out var hasFabric) is var fi && hasFabric)
                    {
                        adminFabricIndex = new FabricIndex((byte)fi);
                    }
                    break;
                case AdminVendorIdId:
                    if (ReadNullableUInt(data.Data.Span, out var hasVendor) is var vid && hasVendor)
                    {
                        adminVendorId = new VendorId((ushort)vid);
                    }
                    break;
            }
        }

        return new CommissioningWindowState
        {
            Status = status,
            AdminFabricIndex = adminFabricIndex,
            AdminVendorId = adminVendorId,
        };
    }

    public async Task OpenCommissioningWindowAsync(EnhancedCommissioningWindowParameters parameters, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        Validate(parameters);

        // OpenCommissioningWindow (0x00): CommissioningTimeout [0], PAKEPasscodeVerifier [1],
        // Discriminator [2], Iterations [3], Salt [4].
        var fields = EncodeFields(w =>
        {
            w.WriteUnsignedInteger(TlvTag.ContextSpecific(0), parameters.CommissioningTimeoutSeconds);
            w.WriteByteString(TlvTag.ContextSpecific(1), parameters.PakePasscodeVerifier);
            w.WriteUnsignedInteger(TlvTag.ContextSpecific(2), parameters.Discriminator);
            w.WriteUnsignedInteger(TlvTag.ContextSpecific(3), parameters.Iterations);
            w.WriteByteString(TlvTag.ContextSpecific(4), parameters.Salt);
        });

        await InvokeTimedAsync(OpenCommissioningWindow, fields, cancellationToken).ConfigureAwait(false);
    }

    public async Task OpenBasicCommissioningWindowAsync(ushort commissioningTimeoutSeconds, CancellationToken cancellationToken = default)
    {
        // OpenBasicCommissioningWindow (0x01): CommissioningTimeout [0].
        var fields = EncodeFields(w => w.WriteUnsignedInteger(TlvTag.ContextSpecific(0), commissioningTimeoutSeconds));
        await InvokeTimedAsync(OpenBasicCommissioningWindow, fields, cancellationToken).ConfigureAwait(false);
    }

    public async Task RevokeCommissioningAsync(CancellationToken cancellationToken = default)
    {
        // RevokeCommissioning (0x02: no fields.
        await InvokeTimedAsync(RevokeCommissioning, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
    }

    private static void Validate(EnhancedCommissioningWindowParameters p)
    {
        if (p.PakePasscodeVerifier is not { Length: VerifierLength })
        {
            throw new ArgumentException($"PAKEPasscodeVerifier must be {VerifierLength} bytes.", nameof(p));
        }

        if (p.Salt is not { Length: >= MinSaltLength and <= MaxSaltLength })
        {
            throw new ArgumentException($"Salt must be {MinSaltLength}..{MaxSaltLength} bytes.", nameof(p));
        }

        if (p.Discriminator > MaxDiscriminator)
        {
            throw new ArgumentException($"Discriminator must be <= 0x{MaxDiscriminator:X}.", nameof(p));
        }
    }

    private async Task InvokeTimedAsync(CommandId command, ReadOnlyMemory<byte> fields, CancellationToken cancellationToken)
    {
        var result = await _client.InvokeAsync(
            new ClusterCommand
            {
                Endpoint = RootEndpoint,
                Cluster = AdministratorCommissioning,
                Command = command,
                Fields = fields,
            },
            timed: true,
            timedInvokeTimeoutMs: _timedInvokeTimeoutMs,
            cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            throw new InteractionModelException(
                $"AdministratorCommissioning command 0x{command.Value:X2} failed.", result.Status?.Status);
        }
    }

    private static AttributePathIB Path(uint attributeId) => new()
    {
        Endpoint = RootEndpoint,
        Cluster = AdministratorCommissioning,
        Attribute = new AttributeId(attributeId),
    };

    private static ReadOnlyMemory<byte> EncodeFields(Action<TlvWriter> build)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new TlvWriter(buffer);
        writer.StartStructure(TlvTag.Anonymous);
        build(writer);
        writer.EndContainer();
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Reads the first value in an attribute-data TLV element as an unsigned integer; null becomes 0.</summary>
    private static ulong ReadNullableUInt(ReadOnlySpan<byte> payload, out bool hasValue)
    {
        var reader = new TlvReader(payload);
        while (reader.Read())
        {
            if (reader.IsContainer || reader.IsEndOfContainer)
            {
                continue;
            }

            if (reader.IsNull)
            {
                hasValue = false;
                return 0;
            }

            hasValue = true;
            return reader.GetUnsignedInteger();
        }

        hasValue = false;
        return 0;
    }
}