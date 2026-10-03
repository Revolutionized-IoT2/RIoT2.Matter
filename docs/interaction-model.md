Applies to: onboarding payloads and direct Interaction Model cluster access in `RIoT2.Matter`.

# Onboarding and Interaction Model examples

## QR code and passcode/verifier pairing

The setup passcode is the SPAKE2+ password. Generate the passcode and the on-device verifier from
one `PaseProvisioning` bundle, and use that same passcode as the source of truth for the QR payload.

```csharp
using RIoT2.Matter.Onboarding;
using RIoT2.Matter.SecureChannel.Pase;

// One provisioning step yields the passcode, PBKDF parameters and matching SPAKE2+ verifier.
PaseProvisioning provisioning = PaseVerifierGenerator.Provision();

// Provision the verifier onto the device. This is what PASE authenticates against.
byte[] verifierBlob = PaseVerifierGenerator.SerializeVerifier(provisioning.Verifier);
// Store verifierBlob + provisioning.Parameters with the device or commissioning window state.

// Build the onboarding payload from the SAME passcode that produced the verifier.
var payload = new SetupPayload
{
    VendorId = options.Information.VendorId,
    ProductId = options.Information.ProductId,
    DiscoveryCapabilities = DiscoveryCapabilities.OnNetwork, // | Ble | SoftAccessPoint
    Discriminator = 0x0F00,                                  // pairs with DNS-SD _L/_S subtypes
    Passcode = provisioning.Passcode,                        // source of truth
};

string qr = QrCodePayload.Encode(payload); // e.g. "MT:Y.K90..." (MT: prefix + Base38)
```

`SetupPayload.ToString()` redacts the passcode, so it is not written to logs in plaintext. Rendering
the QR string to an image is deliberately outside the core; supply an `IQrCodeRenderer` if a host
needs PNG/SVG output:

```csharp
byte[] image = qrRenderer.Render(qr); // your IQrCodeRenderer implementation
```

Controller-side decode round-trips the same payload:

```csharp
if (QrCodePayload.TryDecode(qr, out SetupPayload decoded))
{
    // decoded.Discriminator, decoded.VendorId, decoded.Passcode, ...
}
```

The 12-bit `Discriminator` in the payload must match the DNS-SD `_L` and `_S` subtypes advertised by
the commissionable node, so a controller can find the device it scanned.

## Interaction Model: read, write, invoke

Every `Cluster` exposes the read/write/invoke surface the Interaction Model engine binds against.
Direct calls are useful in tests and local device logic. Reads and writes carry TLV payloads;
commands take opaque TLV fields and return a `CommandResponse`.

```csharp
using System.Buffers;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.Tlv;

// Invoke On/Off Toggle (cluster 0x0006, command 0x02) with no fields.
var response = await device.OnOff.InvokeCommandAsync(
    commandId: new CommandId(0x02),
    fields: ReadOnlyMemory<byte>.Empty,
    cancellationToken: cancellationToken);

// Read the OnOff attribute (0x0000) into a TLV writer.
var buffer = new ArrayBufferWriter<byte>();
var status = await device.OnOff.ReadAttributeAsync(
    attributeId: new AttributeId(0x0000),
    writer: new TlvWriter(buffer),
    tag: TlvTag.Anonymous,
    cancellationToken: cancellationToken);
```

Global attributes (`ClusterRevision`, `FeatureMap`, `AttributeList`, `AcceptedCommandList`,
`GeneratedCommandList`, `EventList`) are served by the base class automatically. Every successful
attribute mutation bumps `DataVersion` and notifies the node change broker so live subscriptions
report promptly.

## Authoring a custom cluster

Derive from `Cluster`, back attributes with `AttributeStore`, and override only the core hooks your
cluster needs. Add the cluster to an endpoint with `Endpoint.AddCluster`, which binds it to the
node's event and change sinks.

```csharp
using RIoT2.Matter.DataModel;
using RIoT2.Matter.Device;
using RIoT2.Matter.InteractionModel;
using RIoT2.Matter.Tlv;

public sealed class SampleCluster : Cluster
{
    public static readonly ClusterId ClusterId = new(0xFC00); // manufacturer-specific range
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
        set => _measured.Value = value; // Attribute<T>.Set -> change broker -> IncrementDataVersion
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

var node = new MatterNode();
var endpoint = node.AddEndpoint(new EndpointId(1));
endpoint.AddCluster(new SampleCluster());
```

To generate events, call the protected `EmitEvent`. To expose commands, override
`InvokeCommandCoreAsync` and declare `AcceptedCommandIds`.
