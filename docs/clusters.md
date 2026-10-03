Applies to: `RIoT2.Matter` clusters, device-type builders and endpoint composition.

# Clusters and device types

The library exposes Matter behavior through `Cluster` subclasses hosted on `Endpoint`s. Endpoint 0 is
the root node. Application endpoints carry one or more standard device types and the clusters required
by those device types.

## Implemented standard device types

`DataModel/StandardDeviceTypes.cs` defines the standard device types used by the library:

| Device type | Id | Notes |
| --- | --- | --- |
| Root Node | `0x0016` | Endpoint 0 utility device |
| On/Off Light | `0x0100` | Identify + On/Off |
| Dimmable Light | `0x0101` | Identify + On/Off + Level Control |
| Color Temperature Light | `0x010C` | Lighting with Color Control CT feature |
| Extended Color Light | `0x010D` | Lighting with HS and CT Color Control features |
| On/Off Plug-in Unit | `0x010A` | Switchable outlet |
| Dimmable Plug-in Unit | `0x010B` | Dimmable outlet |
| Contact Sensor | `0x0015` | Boolean State backing cluster |
| Light Sensor | `0x0106` | Illuminance Measurement |
| Occupancy Sensor | `0x0107` | Occupancy Sensing |
| Temperature Sensor | `0x0302` | Temperature Measurement |
| Humidity Sensor | `0x0307` | Relative Humidity Measurement |
| Thermostat | `0x0301` | Thermostat cluster |
| Control Bridge | `0x0840` | Outbound controller bridge endpoint |
| Aggregator | `0x000E` | Utility endpoint for bridged devices |
| Bridged Node | `0x0013` | Non-Matter endpoint exposed through an Aggregator |

Future platform ideas for additional device types are tracked in
[features.md](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/features.md#integrations).

## Core cluster groups

Implemented cluster families include:

- Data model: Descriptor, Basic Information, Bridged Device Basic Information.
- Commissioning: General Commissioning, Operational Credentials, Network Commissioning,
  Administrator Commissioning, Access Control, Group Key Management and General Diagnostics.
- Application: Identify, On/Off, Level Control, Groups, Binding, Color Control, Thermostat.
- Sensors: Temperature Measurement, Relative Humidity Measurement, Illuminance Measurement,
  Occupancy Sensing and Boolean State.

Every successful attribute mutation increments `DataVersion` and notifies the node change broker so
active subscriptions report promptly.

## LightingDevice builder

`LightingDevice.Build` composes:

1. Endpoint 0 with Root Node, Descriptor, Basic Information, commissioning-support clusters and
   General Diagnostics.
2. One lighting endpoint with Descriptor, Identify, On/Off and, for `LightingProfile.DimmableLight`,
   Level Control.

The Dimmable profile couples On/Off and Level Control in both directions. Off-to-on restores the
current level, and `*WithOnOff` level commands drive On/Off.

`OnOffSample` currently uses `LightingProfile.DimmableLight`, so it exposes both On/Off and
brightness controls even though the sample name is historical.

## Authoring a cluster

Custom clusters derive from `Cluster`, back attributes with `AttributeStore` when possible, and
override read/write/invoke hooks only for the behavior they own.

```csharp
public sealed class SampleCluster : Cluster
{
    public static readonly ClusterId ClusterId = new(0xFC00);
    private static readonly AttributeId MeasuredValueId = new(0x0000);

    private readonly AttributeStore _attributes;
    private readonly Attribute<ushort> _measured;

    public SampleCluster(ushort initial = 0)
    {
        _attributes = new AttributeStore(IncrementDataVersion);
        _measured = _attributes.Add(MeasuredValueId, TlvCodec.UInt16, initial);
    }

    public override ClusterId Id => ClusterId;
    public override ushort ClusterRevision => 1;
    public override IReadOnlyCollection<AttributeId> AttributeIds => [MeasuredValueId];

    public ushort MeasuredValue
    {
        get => _measured.Value;
        set => _measured.Value = value;
    }

    protected override ValueTask<InteractionModelStatusCode> ReadAttributeCoreAsync(
        AttributeId attributeId,
        TlvWriter writer,
        TlvTag tag,
        InteractionContext context,
        CancellationToken cancellationToken) =>
        new(_attributes.TryRead(attributeId, writer, tag)
            ? InteractionModelStatusCode.Success
            : InteractionModelStatusCode.UnsupportedAttribute);
}
```

Add the cluster to an endpoint with `Endpoint.AddCluster`. For events, call the protected
`EmitEvent`. For commands, override `InvokeCommandCoreAsync` and declare accepted command ids.

## Bridged endpoint composition

ControlBridge's `AggregatorEndpoint` exposes non-Matter devices by dynamically attaching endpoints
that include:

- Bridged Node (`0x0013`)
- The application device type, such as On/Off Light
- Descriptor
- Bridged Device Basic Information
- Application clusters supplied by `BridgedDeviceDefinition.ComposeApplicationClusters`

Use stable endpoint ids when bridging durable RIoT2 devices so commissioners keep references across
restarts and configuration changes.
