using RIoT2.Matter.DataModel;
using RIoT2.Matter.Device;
using RIoT2.Matter.InteractionModel;
using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.Clusters;

/// <summary>
/// The Occupancy Sensing cluster (0x0406) on a sensor endpoint: exposes the device-driven, read-only
/// Occupancy bitmap plus the sensing technology behind it (OccupancySensorType and the matching
/// OccupancySensorTypeBitmap). Every attribute is device-driven; there are no commands. See the Matter
/// Cluster Specification, section 2.7.
/// </summary>
/// <remarks>
/// Add to an Occupancy Sensor (0x0107) endpoint and push the sensor state in from the host:
/// <code>
/// var occupancy = new OccupancySensingCluster(OccupancySensorTypes.Pir);
/// endpoint.AddCluster(occupancy);
/// occupancy.Occupied = true;
/// </code>
/// </remarks>
public sealed class OccupancySensingCluster : Cluster
{
    /// <summary>The Occupancy Sensing cluster identifier (0x0406).</summary>
    public static readonly ClusterId ClusterId = new(0x0406);

    // Attribute ids (spec section 2.7.5).
    private const uint OccupancyId = 0x0000;
    private const uint OccupancySensorTypeId = 0x0001;
    private const uint OccupancySensorTypeBitmapId = 0x0002;

    // Bit 0 of the Occupancy bitmap: set when the sensor detects occupancy.
    private const byte OccupiedBit = 0x01;

    private readonly AttributeStore _attributes;
    private readonly Attribute<byte> _occupancy;

    /// <param name="sensorType">The sensing technology; see <see cref="OccupancySensorTypes"/>.</param>
    /// <param name="initiallyOccupied">The initial Occupancy state.</param>
    public OccupancySensingCluster(byte sensorType = OccupancySensorTypes.Pir, bool initiallyOccupied = false)
    {
        _attributes = new AttributeStore(IncrementDataVersion);
        _occupancy = _attributes.Add(new AttributeId(OccupancyId), TlvCodec.UInt8, initiallyOccupied ? OccupiedBit : (byte)0);
        _attributes.Add(new AttributeId(OccupancySensorTypeId), TlvCodec.UInt8, sensorType);
        _attributes.Add(new AttributeId(OccupancySensorTypeBitmapId), TlvCodec.UInt8, ToSensorTypeBitmap(sensorType));
    }

    /// <inheritdoc />
    public override ClusterId Id => ClusterId;

    /// <inheritdoc />
    /// <remarks>Revision 3 (Matter 1.2); the revision 4 feature-flagged attribute set is deferred.</remarks>
    public override ushort ClusterRevision => 3;

    /// <inheritdoc />
    public override IReadOnlyCollection<AttributeId> AttributeIds => _attributes.Ids;

    /// <summary>
    /// Whether the sensor currently detects occupancy (bit 0 of the Occupancy bitmap). Assigning from
    /// device logic notifies subscriptions only when the value changes.
    /// </summary>
    public bool Occupied
    {
        get => (_occupancy.Value & OccupiedBit) != 0;
        set => _occupancy.Value = value ? OccupiedBit : (byte)0;
    }

    /// <inheritdoc />
    protected override ValueTask<InteractionModelStatusCode> ReadAttributeCoreAsync(
        AttributeId attributeId, TlvWriter writer, TlvTag tag, InteractionContext context, CancellationToken cancellationToken)
        => new(_attributes.TryRead(attributeId, writer, tag)
            ? InteractionModelStatusCode.Success
            : InteractionModelStatusCode.UnsupportedAttribute);

    // OccupancySensorTypeBitmap advertises every technology present; for the single-technology types the
    // spec maps PIR -> bit 0, Ultrasonic -> bit 1, PhysicalContact -> bit 2, and PIR+Ultrasonic -> both.
    private static byte ToSensorTypeBitmap(byte sensorType) => sensorType switch
    {
        OccupancySensorTypes.Pir => 0b001,
        OccupancySensorTypes.Ultrasonic => 0b010,
        OccupancySensorTypes.PirAndUltrasonic => 0b011,
        OccupancySensorTypes.PhysicalContact => 0b100,
        _ => 0b001,
    };
}

/// <summary>
/// The OccupancySensorTypeEnum values from the Occupancy Sensing cluster (spec section 2.7.5.2).
/// </summary>
public static class OccupancySensorTypes
{
    /// <summary>Passive infrared.</summary>
    public const byte Pir = 0;

    /// <summary>Ultrasonic.</summary>
    public const byte Ultrasonic = 1;

    /// <summary>Passive infrared and ultrasonic combined.</summary>
    public const byte PirAndUltrasonic = 2;

    /// <summary>Physical contact (e.g. a hotel key-card holder).</summary>
    public const byte PhysicalContact = 3;
}
