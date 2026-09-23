using RIoT2.Matter.DataModel;
using RIoT2.Matter.Device;
using RIoT2.Matter.InteractionModel;
using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.Clusters;

/// <summary>
/// The Relative Humidity Measurement cluster (0x0405) on a sensor endpoint: exposes the device-driven,
/// read-only MeasuredValue in hundredths of a percent, along with the MinMeasuredValue /
/// MaxMeasuredValue range the sensor can report and its Tolerance. Every attribute is device-driven;
/// there are no commands. See the Matter Cluster Specification, section 2.6.
/// </summary>
/// <remarks>
/// Add to a Humidity Sensor (0x0307) endpoint and push readings in from the host:
/// <code>
/// var humidity = new RelativeHumidityMeasurementCluster();
/// endpoint.AddCluster(humidity);
/// humidity.MeasuredValue = (ushort)Math.Round(percent * 100);   // 43.5 % -> 4350
/// </code>
/// A null MeasuredValue means "unknown"; assign <see langword="null"/> when the sensor is unreachable.
/// </remarks>
public sealed class RelativeHumidityMeasurementCluster : Cluster
{
    /// <summary>The Relative Humidity Measurement cluster identifier (0x0405).</summary>
    public static readonly ClusterId ClusterId = new(0x0405);

    // Attribute ids (spec section 2.6.4).
    private const uint MeasuredValueId = 0x0000;
    private const uint MinMeasuredValueId = 0x0001;
    private const uint MaxMeasuredValueId = 0x0002;
    private const uint ToleranceId = 0x0003;

    private static readonly TlvCodec<ushort?> NullableUInt16 = TlvCodec.Nullable(TlvCodec.UInt16);

    private readonly AttributeStore _attributes;
    private readonly Attribute<ushort?> _measuredValue;

    /// <param name="initialValue">The initial MeasuredValue in 0.01 %; <see langword="null"/> means unknown.</param>
    /// <param name="minMeasuredValue">The lowest value the sensor can report, in 0.01 %.</param>
    /// <param name="maxMeasuredValue">The highest value the sensor can report, in 0.01 %.</param>
    /// <param name="tolerance">The measurement tolerance in 0.01 %.</param>
    public RelativeHumidityMeasurementCluster(
        ushort? initialValue = null,
        ushort? minMeasuredValue = 0,
        ushort? maxMeasuredValue = 10000, // 100.00 %
        ushort tolerance = 0)
    {
        _attributes = new AttributeStore(IncrementDataVersion);
        _measuredValue = _attributes.Add(new AttributeId(MeasuredValueId), NullableUInt16, initialValue);
        _attributes.Add(new AttributeId(MinMeasuredValueId), NullableUInt16, minMeasuredValue);
        _attributes.Add(new AttributeId(MaxMeasuredValueId), NullableUInt16, maxMeasuredValue);
        _attributes.Add(new AttributeId(ToleranceId), TlvCodec.UInt16, tolerance);
    }

    /// <inheritdoc />
    public override ClusterId Id => ClusterId;

    /// <inheritdoc />
    /// <remarks>Revision 3 (Matter 1.2).</remarks>
    public override ushort ClusterRevision => 3;

    /// <inheritdoc />
    public override IReadOnlyCollection<AttributeId> AttributeIds => _attributes.Ids;

    /// <summary>
    /// The current relative humidity in hundredths of a percent, or <see langword="null"/> when
    /// unknown. Assigning from device logic notifies subscriptions only when the value changes.
    /// </summary>
    public ushort? MeasuredValue
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
