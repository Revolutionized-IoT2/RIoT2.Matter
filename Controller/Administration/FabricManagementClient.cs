using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RIoT2.Matter.Controller.InteractionModel;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.InteractionModel;
using RIoT2.Matter.Tlv;
using InteractionModelException = RIoT2.Matter.Controller.InteractionModel.InteractionModelException;

namespace RIoT2.Matter.Controller.Administration;

/// <summary>
/// Implements <see cref="IFabricManagementClient"/> over the Interaction Model
/// <see cref="IInteractionClient"/>, reading the Operational Credentials cluster (0x003E) fabric
/// table and issuing UpdateFabricLabel / RemoveFabric. TLV layouts mirror those the node-side
/// <c>OperationalCredentialsCluster</c> produces and parses. See the Matter Core Specification,
/// section 11.18.
/// </summary>
public sealed class FabricManagementClient : IFabricManagementClient
{
    private static readonly EndpointId RootEndpoint = new(0);
    private static readonly ClusterId OperationalCredentials = new(0x003E);

    // Attribute ids (spec 11.18.5).
    private const uint FabricsId = 0x0001;
    private const uint SupportedFabricsId = 0x0002;
    private const uint CommissionedFabricsId = 0x0003;
    private const uint CurrentFabricIndexId = 0x0005;

    // Command ids (spec 11.18.6).
    private static readonly CommandId UpdateFabricLabel = new(0x09);
    private static readonly CommandId RemoveFabric = new(0x0A);
    private const uint NocResponseId = 0x08;

    // The FabricIndex field tag shared by every fabric-scoped struct (spec 7.13.2).
    private const int FabricIndexFieldTag = 254;
    private const int MaxLabelLength = 32;

    private readonly IInteractionClient _client;

    /// <param name="client">The Interaction Model client bound to the node's operational (CASE) session.</param>
    public FabricManagementClient(IInteractionClient client)
        => _client = client ?? throw new ArgumentNullException(nameof(client));

    public async Task<IReadOnlyList<NodeFabricDescriptor>> ReadFabricsAsync(bool fabricFiltered = false, CancellationToken cancellationToken = default)
    {
        var reports = await _client.ReadAttributesAsync([Path(FabricsId)], cancellationToken).ConfigureAwait(false);

        var fabrics = new List<NodeFabricDescriptor>();
        foreach (var report in reports)
        {
            if (report.AttributeData is not { } data || data.Path.Attribute?.Value != FabricsId)
            {
                continue;
            }

            DecodeFabrics(data.Data.Span, fabrics);
        }

        return fabrics;
    }

    public async Task<NodeFabricSummary> ReadFabricSummaryAsync(CancellationToken cancellationToken = default)
    {
        var reports = await _client.ReadAttributesAsync(
            [
                Path(SupportedFabricsId),
                Path(CommissionedFabricsId),
                Path(CurrentFabricIndexId),
            ],
            cancellationToken).ConfigureAwait(false);

        byte supported = 0;
        byte commissioned = 0;
        byte current = 0;

        foreach (var report in reports)
        {
            if (report.AttributeData is not { } data)
            {
                continue;
            }

            switch (data.Path.Attribute?.Value)
            {
                case SupportedFabricsId: supported = (byte)ReadUInt(data.Data.Span); break;
                case CommissionedFabricsId: commissioned = (byte)ReadUInt(data.Data.Span); break;
                case CurrentFabricIndexId: current = (byte)ReadUInt(data.Data.Span); break;
            }
        }

        return new NodeFabricSummary
        {
            SupportedFabrics = supported,
            CommissionedFabrics = commissioned,
            CurrentFabricIndex = new FabricIndex(current),
        };
    }

    public async Task UpdateFabricLabelAsync(string label, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(label);
        if (label.Length > MaxLabelLength)
        {
            throw new ArgumentException($"Label must be <= {MaxLabelLength} characters.", nameof(label));
        }

        // UpdateFabricLabel (0x09): Label [0 : string].
        var fields = EncodeFields(w => w.WriteUtf8String(TlvTag.ContextSpecific(0), label));
        var result = await InvokeAsync(UpdateFabricLabel, fields, cancellationToken).ConfigureAwait(false);
        ThrowOnNocFailure(result, nameof(UpdateFabricLabelAsync));
    }

    public async Task RemoveFabricAsync(FabricIndex fabricIndex, CancellationToken cancellationToken = default)
    {
        if (fabricIndex.Value == 0)
        {
            throw new ArgumentException("FabricIndex must not be 0 (NoFabric).", nameof(fabricIndex));
        }

        // RemoveFabric (0x0A): FabricIndex [0 : uint8].
        var fields = EncodeFields(w => w.WriteUnsignedInteger(TlvTag.ContextSpecific(0), fabricIndex.Value));
        var result = await InvokeAsync(RemoveFabric, fields, cancellationToken).ConfigureAwait(false);
        ThrowOnNocFailure(result, nameof(RemoveFabricAsync));
    }

    private async Task<InvokeResult> InvokeAsync(CommandId command, ReadOnlyMemory<byte> fields, CancellationToken cancellationToken)
    {
        var result = await _client.InvokeAsync(
            new ClusterCommand
            {
                Endpoint = RootEndpoint,
                Cluster = OperationalCredentials,
                Command = command,
                Fields = fields,
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            throw new InteractionModelException(
                $"OperationalCredentials command 0x{command.Value:X2} failed.", result.Status?.Status);
        }

        return result;
    }

    /// <summary>Validates the NOCResponse StatusCode [0]; only 0 (Ok) is a success.</summary>
    private static void ThrowOnNocFailure(InvokeResult result, string operation)
    {
        var statusCode = 0xFF;
        var reader = new TlvReader(result.ResponseData.Span);
        var depth = 0;
        while (reader.Read())
        {
            if (reader.IsContainer) { depth++; continue; }
            if (reader.IsEndOfContainer) { depth--; continue; }
            if (depth == 1 && reader.Tag.TagNumber == 0)
            {
                statusCode = (byte)reader.GetUnsignedInteger();
                break;
            }
        }

        if (statusCode != 0)
        {
            throw new InteractionModelException($"{operation} failed with NodeOperationalCertStatus {statusCode}.");
        }
    }

    private static void DecodeFabrics(ReadOnlySpan<byte> payload, List<NodeFabricDescriptor> fabrics)
    {
        // The value is an array of FabricDescriptorStruct: RootPublicKey [1], VendorID [2],
        // FabricID [3], NodeID [4], Label [5], FabricIndex [254].
        var reader = new TlvReader(payload);

        // Advance to the array element.
        if (!reader.Read() || !reader.IsContainer)
        {
            return;
        }

        while (reader.Read() && !reader.IsEndOfContainer)
        {
            if (!reader.IsContainer)
            {
                continue;
            }

            byte[] rootPublicKey = [];
            ushort vendorId = 0;
            ulong fabricId = 0;
            ulong nodeId = 0;
            var label = string.Empty;
            byte fabricIndex = 0;

            while (reader.Read() && !reader.IsEndOfContainer)
            {
                switch (reader.Tag.TagNumber)
                {
                    case 1: rootPublicKey = reader.GetByteString().ToArray(); break;
                    case 2: vendorId = (ushort)reader.GetUnsignedInteger(); break;
                    case 3: fabricId = reader.GetUnsignedInteger(); break;
                    case 4: nodeId = reader.GetUnsignedInteger(); break;
                    case 5: label = reader.GetUtf8String(); break;
                    case FabricIndexFieldTag: fabricIndex = (byte)reader.GetUnsignedInteger(); break;
                }
            }

            fabrics.Add(new NodeFabricDescriptor
            {
                FabricIndex = new FabricIndex(fabricIndex),
                RootPublicKey = rootPublicKey,
                VendorId = new VendorId(vendorId),
                FabricId = new FabricId(fabricId),
                NodeId = new NodeId(nodeId),
                Label = label,
            });
        }
    }

    private static AttributePathIB Path(uint attributeId) => new()
    {
        Endpoint = RootEndpoint,
        Cluster = OperationalCredentials,
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

    private static ulong ReadUInt(ReadOnlySpan<byte> payload)
    {
        var reader = new TlvReader(payload);
        while (reader.Read())
        {
            if (reader.IsContainer || reader.IsEndOfContainer)
            {
                continue;
            }

            return reader.GetUnsignedInteger();
        }

        return 0;
    }
}