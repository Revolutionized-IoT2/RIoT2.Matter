using RIoT2.Matter.DataModel;
using RIoT2.Matter.Device;
using RIoT2.Matter.InteractionModel;
using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.Clusters;

/// <summary>
/// The Network Commissioning cluster (0x0031) for the Ethernet feature: exposes the node's single
/// on-network Ethernet interface as the Networks list, the administrative InterfaceEnabled flag, and
/// the MaxNetworks and Last* (<see cref="NetworkCommissioningStatus"/>) status attributes. The
/// Ethernet feature has no commands — the interface is already on the network — so the cluster is
/// attribute-only. The Wi-Fi (WI) and Thread (TH) features, with their Scan/Add/Connect commands, are
/// deferred. See the Matter Core Specification, section 11.8.
/// </summary>
/// <remarks>
/// Add to the root endpoint, supplying the Ethernet interface's NetworkID (its name or MAC, 1..32 octets):
/// <code>node.Root.AddCluster(new NetworkCommissioningCluster(interfaceId));</code>
/// </remarks>
public sealed class NetworkCommissioningCluster : Cluster
{
    /// <summary>The Network Commissioning cluster identifier (0x0031).</summary>
    public static readonly ClusterId ClusterId = new(0x0031);

    // Attribute ids (spec §11.8.6). The WI/TH-only attributes (Scan/Connect timeouts, Wi-Fi bands,
    // Thread version/features) are omitted for the Ethernet feature.
    private const uint MaxNetworksId = 0x0000;
    private const uint NetworksId = 0x0001;
    private const uint InterfaceEnabledId = 0x0004;
    private const uint LastNetworkingStatusId = 0x0005;
    private const uint LastNetworkIdId = 0x0006;
    private const uint LastConnectErrorValueId = 0x0007;

    // NetworkInfoStruct field tags (spec §11.8.5.3).
    private const byte NetworkIdFieldTag = 0;
    private const byte ConnectedFieldTag = 1;

    private const int MaxNetworkIdLength = 32;

    private static readonly AttributeId[] AttributeIdList =
    [
        new(MaxNetworksId), new(NetworksId), new(InterfaceEnabledId),
        new(LastNetworkingStatusId), new(LastNetworkIdId), new(LastConnectErrorValueId),
    ];

    private readonly byte[] _networkId;
    private readonly byte _maxNetworks;
    private readonly AttributeStore _attributes;
    private readonly Attribute<bool> _interfaceEnabled;

    /// <param name="networkId">The Ethernet interface's NetworkID (its name or MAC), 1..32 octets.</param>
    /// <param name="maxNetworks">The MaxNetworks attribute value (at least 1; a single Ethernet interface).</param>
    /// <param name="interfaceEnabled">The initial InterfaceEnabled value (whether the interface is administratively up).</param>
    public NetworkCommissioningCluster(byte[] networkId, byte maxNetworks = 1, bool interfaceEnabled = true)
    {
        ArgumentNullException.ThrowIfNull(networkId);
        if (networkId.Length is 0 or > MaxNetworkIdLength)
        {
            throw new ArgumentException($"NetworkID must be 1..{MaxNetworkIdLength} octets.", nameof(networkId));
        }

        if (maxNetworks == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxNetworks), maxNetworks, "MaxNetworks must be at least 1.");
        }

        _networkId = (byte[])networkId.Clone();
        _maxNetworks = maxNetworks;

        _attributes = new AttributeStore(IncrementDataVersion);
        _interfaceEnabled = _attributes.Add(new AttributeId(InterfaceEnabledId), TlvCodec.Bool, interfaceEnabled, writable: true);
    }

    /// <inheritdoc />
    public override ClusterId Id => ClusterId;

    /// <inheritdoc />
    /// <remarks>Revision 1 attribute set for the Ethernet feature.</remarks>
    public override ushort ClusterRevision => 1;

    /// <inheritdoc />
    public override uint FeatureMap => (uint)NetworkCommissioningFeature.EthernetNetworkInterface;

    /// <inheritdoc />
    public override IReadOnlyCollection<AttributeId> AttributeIds => AttributeIdList;

    /// <inheritdoc />
    /// <remarks>
    /// MaxNetworks, Networks, and the Last* status attributes expose commissioning detail and require
    /// Administer to read; InterfaceEnabled stays View-readable (spec §11.8.6).
    /// </remarks>
    public override AccessPrivilege RequiredReadPrivilege(AttributeId attributeId) => attributeId.Value switch
    {
        MaxNetworksId or NetworksId or LastNetworkingStatusId or LastNetworkIdId or LastConnectErrorValueId
            => AccessPrivilege.Administer,
        _ => base.RequiredReadPrivilege(attributeId),
    };

    /// <inheritdoc />
    /// <remarks>InterfaceEnabled is administratively controlled, so writing it requires Administer (spec §11.8.6).</remarks>
    public override AccessPrivilege RequiredWritePrivilege(AttributeId attributeId) =>
        attributeId.Value == InterfaceEnabledId ? AccessPrivilege.Administer : base.RequiredWritePrivilege(attributeId);

    /// <summary>
    /// Whether the Ethernet interface is administratively enabled (InterfaceEnabled). Setting it from
    /// device logic notifies subscriptions and flips the interface's Connected state in Networks.
    /// </summary>
    public bool InterfaceEnabled
    {
        get => _interfaceEnabled.Value;
        set => _interfaceEnabled.Value = value;
    }

    /// <inheritdoc />
    protected override ValueTask<InteractionModelStatusCode> ReadAttributeCoreAsync(
        AttributeId attributeId, TlvWriter writer, TlvTag tag, InteractionContext context, CancellationToken cancellationToken)
    {
        // The writable InterfaceEnabled lives in the store; the rest are fixed or device-driven projections.
        // Read privileges (Administer for MaxNetworks/Networks/Last*) are enforced by the IM Read engine
        // via RequiredReadPrivilege before this method runs.
        if (_attributes.TryRead(attributeId, writer, tag))
        {
            return new ValueTask<InteractionModelStatusCode>(InteractionModelStatusCode.Success);
        }

        switch (attributeId.Value)
        {
            case MaxNetworksId:
                writer.WriteUnsignedInteger(tag, _maxNetworks);
                break;
            case NetworksId:
                WriteNetworks(writer, tag);
                break;
            case LastNetworkingStatusId:
            case LastNetworkIdId:
            case LastConnectErrorValueId:
                // No networking commands exist for the Ethernet feature, so these stay Null until the
                // Wi-Fi/Thread Scan/Connect handlers (deferred) populate them. See spec §11.8.6.
                writer.WriteNull(tag);
                break;
            default:
                return new ValueTask<InteractionModelStatusCode>(InteractionModelStatusCode.UnsupportedAttribute);
        }

        return new ValueTask<InteractionModelStatusCode>(InteractionModelStatusCode.Success);
    }

    /// <inheritdoc />
    protected override ValueTask<InteractionModelStatusCode> WriteAttributeCoreAsync(
        AttributeId attributeId, ReadOnlyMemory<byte> value, InteractionContext context, CancellationToken cancellationToken)
        // The Administer requirement on InterfaceEnabled is enforced by the IM Write engine via
        // RequiredWritePrivilege before this method runs; only InterfaceEnabled is writable here.
        => new(_attributes.Write(attributeId, value));

    private void WriteNetworks(TlvWriter writer, TlvTag tag)
    {
        // The Ethernet feature models exactly one interface; its Connected state follows InterfaceEnabled.
        writer.StartArray(tag);
        writer.StartStructure(TlvTag.Anonymous);
        writer.WriteByteString(TlvTag.ContextSpecific(NetworkIdFieldTag), _networkId);              // NetworkID
        writer.WriteBoolean(TlvTag.ContextSpecific(ConnectedFieldTag), _interfaceEnabled.Value);    // Connected
        writer.EndContainer();
        writer.EndContainer();
    }
}