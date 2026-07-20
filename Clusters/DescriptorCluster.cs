using System.Linq;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.Device;
using RIoT2.Matter.InteractionModel;
using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.Clusters;

/// <summary>
/// The Descriptor cluster (0x001D): describes an endpoint's composition to controllers. Unlike a
/// stored-value cluster, its four attributes are a <em>live projection</em> of the owning
/// <see cref="Endpoint"/> and its <see cref="MatterNode"/>, so it reads directly from that
/// composition rather than an <see cref="AttributeStore"/>. All attributes are read-only. See the
/// Matter Core Specification, section 9.5.
/// </summary>
/// <remarks>
/// Construct with the owning node and endpoint, then add it to that same endpoint:
/// <code>endpoint.AddCluster(new DescriptorCluster(node, endpoint));</code>
/// Both references are live, so ServerList and PartsList reflect clusters/endpoints added afterwards.
/// </remarks>
public sealed class DescriptorCluster : Cluster
{
    /// <summary>The Descriptor cluster identifier (0x001D).</summary>
    public static readonly ClusterId ClusterId = new(0x001D);

    private const uint DeviceTypeListId = 0x0000;
    private const uint ServerListId = 0x0001;
    private const uint ClientListId = 0x0002;
    private const uint PartsListId = 0x0003;

    private static readonly AttributeId[] AttributeIdList =
    [
        new(DeviceTypeListId), new(ServerListId), new(ClientListId), new(PartsListId),
    ];

    private readonly MatterNode _node;
    private readonly Endpoint _endpoint;

    public DescriptorCluster(MatterNode node, Endpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(endpoint);
        _node = node;
        _endpoint = endpoint;
    }

    /// <inheritdoc />
    public override ClusterId Id => ClusterId;

    /// <inheritdoc />
    /// <remarks>Revision 1 (the base four attributes). The TagList feature added in revision 2 is deferred.</remarks>
    public override ushort ClusterRevision => 1;

    /// <inheritdoc />
    public override IReadOnlyCollection<AttributeId> AttributeIds => AttributeIdList;

    /// <inheritdoc />
    protected override ValueTask<InteractionModelStatusCode> ReadAttributeCoreAsync(
        AttributeId attributeId, TlvWriter writer, TlvTag tag, InteractionContext context, CancellationToken cancellationToken)
    {
        switch (attributeId.Value)
        {
            case DeviceTypeListId:
                WriteDeviceTypeList(writer, tag);
                break;
            case ServerListId:
                // Every server cluster hosted on this endpoint, including Descriptor itself.
                WriteClusterIdArray(writer, tag, _endpoint.Clusters.Keys.OrderBy(id => id.Value));
                break;
            case ClientListId:
                // The client (outgoing binding) clusters this endpoint declares via AddClientCluster.
                WriteClusterIdArray(writer, tag, _endpoint.ClientClusters.OrderBy(id => id.Value));
                break;
            case PartsListId:
                WritePartsList(writer, tag);
                break;
            default:
                return new ValueTask<InteractionModelStatusCode>(InteractionModelStatusCode.UnsupportedAttribute);
        }

        return new ValueTask<InteractionModelStatusCode>(InteractionModelStatusCode.Success);
    }

    private void WriteDeviceTypeList(TlvWriter writer, TlvTag tag)
    {
        writer.StartArray(tag);
        foreach (var deviceType in _endpoint.DeviceTypes)
        {
            writer.StartStructure(TlvTag.Anonymous);
            writer.WriteUnsignedInteger(TlvTag.ContextSpecific(0), deviceType.Id.Value); // DeviceType
            writer.WriteUnsignedInteger(TlvTag.ContextSpecific(1), deviceType.Revision);  // Revision
            writer.EndContainer();
        }

        writer.EndContainer();
    }

    private void WritePartsList(TlvWriter writer, TlvTag tag)
    {
        writer.StartArray(tag);

        // Full-family pattern (spec §9.5.1): the root endpoint (0) enumerates every other endpoint on
        // the node. Non-root endpoints default to an empty PartsList; tree/child composition is deferred.
        if (_endpoint.Id == EndpointId.Root)
        {
            foreach (var endpointId in _node.Endpoints.Keys.Where(id => id != EndpointId.Root).OrderBy(id => id.Value))
            {
                writer.WriteUnsignedInteger(TlvTag.Anonymous, endpointId.Value);
            }
        }

        writer.EndContainer();
    }

    private static void WriteClusterIdArray(TlvWriter writer, TlvTag tag, IEnumerable<ClusterId> clusterIds)
    {
        writer.StartArray(tag);
        foreach (var id in clusterIds)
        {
            writer.WriteUnsignedInteger(TlvTag.Anonymous, id.Value);
        }

        writer.EndContainer();
    }
}