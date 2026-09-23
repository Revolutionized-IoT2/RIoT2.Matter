using RIoT2.Matter.DataModel;
using RIoT2.Matter.Device;
using RIoT2.Matter.InteractionModel;
using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.Clusters;

/// <summary>
/// The Temperature Measurement cluster (0x0402) on a sensor endpoint: exposes the device-driven,
/// read-only MeasuredValue in hundredths of a degree Celsius, along with the MinMeasuredValue /
/// MaxMeasuredValue range the sensor can report and its Tolerance. Every attribute is device-driven;
/// there are no commands. See the Matter Cluster Specification, section 2.3.
/// </summary>
/// <remarks>
/// Add to a Temperature Sensor (0x0302) endpoint and push readings in from the host:
/// <code>
/// var temperature = new TemperatureMeasurementCluster();
/// endpoint.AddCluster(temperature);
/// temperature.MeasuredValue = (short)Math.Round(celsius * 100);   // 21.5 C -> 2150
/// </code>
/// A null MeasuredValue means "unknown"; assign <see langword="null"/> when the sensor is unreachable
/// so a controller does not display a stale reading.
/// </remarks>
public sealed class TemperatureMeasurementCluster : Cluster
{
    /// <summary>The Temperature Measurement cluster identifier (0x0402).</summary>
    public static readonly ClusterId ClusterId = new(0x0402);

    // Attribute ids (spec section 2.3.4).
    private const uint MeasuredValueId = 0x0000;
    private const uint MinMeasuredValueId = 0x0001;
    private const uint MaxMeasuredValueId = 0x0002;
    private const uint ToleranceId = 0x0003;

    private static readonly TlvCodec<short?> NullableInt16 = TlvCodec.Nullable(TlvCodec.Int16);

    private readonly AttributeStore _attributes;
    private readonly Attribute<short?> _measuredValue;

    /// <param name="initialValue">The initial MeasuredValue in 0.01 Celsius; <see langword="null"/> means unknown.</param>
    /// <param name="minMeasuredValue">The lowest value the sensor can report, in 0.01 Celsius.</param>
    /// <param name="maxMeasuredValue">The highest value the sensor can report, in 0.01 Celsius.</param>
    /// <param name="tolerance">The measurement tolerance in 0.01 Celsius.</param>
    public TemperatureMeasurementCluster(
        short? initialValue = null,
        short? minMeasuredValue = -27315, // absolute zero, the spec's lower bound for the type
        short? maxMeasuredValue = 32767,
        ushort tolerance = 0)
    {
        _attributes = new AttributeStore(IncrementDataVersion);
        _measuredValue = _attributes.Add(new AttributeId(MeasuredValueId), NullableInt16, initialValue);
        _attributes.Add(new AttributeId(MinMeasuredValueId), NullableInt16, minMeasuredValue);
        _attributes.Add(new AttributeId(MaxMeasuredValueId), NullableInt16, maxMeasuredValue);
        _attributes.Add(new AttributeId(ToleranceId), TlvCodec.UInt16, tolerance);
    }

    /// <inheritdoc />
    public override ClusterId Id => ClusterId;

    /// <inheritdoc />
    /// <remarks>Revision 4 (Matter 1.2).</remarks>
    public override ushort ClusterRevision => 4;

    /// <inheritdoc />
    public override IReadOnlyCollection<AttributeId> AttributeIds => _attributes.Ids;

    /// <summary>
    /// The current temperature in hundredths of a degree Celsius, or <see langword="null"/> when
    /// unknown. Assigning from device logic notifies subscriptions only when the value changes.
    /// </summary>
    public short? MeasuredValue
    {
        get => _measuredValue.Value;
        set => _measuredValue.Value = value;
    }

    /// <inheritdoc />
    protected override ValueTask<InteractionModelStatusCode> ReadAttributeCoreAsync(
        AttributeId attributeId, TlvWriter writer, TlvTag tag, InteractionContext context, CancellationToken cancellationToken)
        => new(_attributes.TryRead(attributeId, writer, tag)
            ? InteractionModelStatusCode.Success
            : InteractionModelStatusCode.UnsupportedAttribute);
}
