using RIoT2.Matter.DataModel;
using RIoT2.Matter.Device;
using RIoT2.Matter.InteractionModel;
using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.Clusters;

/// <summary>
/// The Illuminance Measurement cluster (0x0400) on a sensor endpoint: exposes the device-driven,
/// read-only MeasuredValue on the spec's logarithmic scale, along with the MinMeasuredValue /
/// MaxMeasuredValue range the sensor can report, its Tolerance, and the LightSensorType. Every
/// attribute is device-driven; there are no commands. See the Matter Cluster Specification, section 2.2.
/// </summary>
/// <remarks>
/// MeasuredValue is <c>10000 * log10(lux) + 1</c> rounded to the nearest integer, so a controller can
/// display the reading over the sensor's full dynamic range in a 16-bit field. Use
/// <see cref="FromLux"/> to convert. Add to a Light Sensor (0x0106) endpoint:
/// <code>
/// var illuminance = new IlluminanceMeasurementCluster();
/// endpoint.AddCluster(illuminance);
/// illuminance.MeasuredValue = IlluminanceMeasurementCluster.FromLux(320);
/// </code>
/// A null MeasuredValue means "unknown"; 0 means "too dark to measure".
/// </remarks>
public sealed class IlluminanceMeasurementCluster : Cluster
{
    /// <summary>The Illuminance Measurement cluster identifier (0x0400).</summary>
    public static readonly ClusterId ClusterId = new(0x0400);

    // Attribute ids (spec section 2.2.5).
    private const uint MeasuredValueId = 0x0000;
    private const uint MinMeasuredValueId = 0x0001;
    private const uint MaxMeasuredValueId = 0x0002;
    private const uint ToleranceId = 0x0003;
    private const uint LightSensorTypeId = 0x0004;

    private static readonly TlvCodec<ushort?> NullableUInt16 = TlvCodec.Nullable(TlvCodec.UInt16);
    private static readonly TlvCodec<byte?> NullableUInt8 = TlvCodec.Nullable(TlvCodec.UInt8);

    private readonly AttributeStore _attributes;
    private readonly Attribute<ushort?> _measuredValue;

    /// <param name="initialValue">The initial MeasuredValue on the logarithmic scale; <see langword="null"/> means unknown.</param>
    /// <param name="minMeasuredValue">The lowest value the sensor can report; the spec reserves 0.</param>
    /// <param name="maxMeasuredValue">The highest value the sensor can report; the spec reserves 0xFFFF.</param>
    /// <param name="tolerance">The measurement tolerance on the same scale.</param>
    /// <param name="lightSensorType">0 = photodiode, 1 = CMOS, 0x40-0xFE = manufacturer specific, null = unknown.</param>
    public IlluminanceMeasurementCluster(
        ushort? initialValue = null,
        ushort? minMeasuredValue = 1,
        ushort? maxMeasuredValue = 0xFFFE,
        ushort tolerance = 0,
        byte? lightSensorType = null)
    {
        _attributes = new AttributeStore(IncrementDataVersion);
        _measuredValue = _attributes.Add(new AttributeId(MeasuredValueId), NullableUInt16, initialValue);
        _attributes.Add(new AttributeId(MinMeasuredValueId), NullableUInt16, minMeasuredValue);
        _attributes.Add(new AttributeId(MaxMeasuredValueId), NullableUInt16, maxMeasuredValue);
        _attributes.Add(new AttributeId(ToleranceId), TlvCodec.UInt16, tolerance);
        _attributes.Add(new AttributeId(LightSensorTypeId), NullableUInt8, lightSensorType);
    }

    /// <inheritdoc />
    public override ClusterId Id => ClusterId;

    /// <inheritdoc />
    /// <remarks>Revision 3 (Matter 1.2).</remarks>
    public override ushort ClusterRevision => 3;

    /// <inheritdoc />
    public override IReadOnlyCollection<AttributeId> AttributeIds => _attributes.Ids;

    /// <summary>
    /// The current illuminance on the spec's logarithmic scale, or <see langword="null"/> when unknown.
    /// Assigning from device logic notifies subscriptions only when the value changes.
    /// </summary>
    public ushort? MeasuredValue
    {
        get => _measuredValue.Value;
        set => _measuredValue.Value = value;
    }

    /// <summary>
    /// Converts an illuminance in lux to the MeasuredValue scale: <c>10000 * log10(lux) + 1</c>, clamped
    /// to the reportable range. A non-positive <paramref name="lux"/> maps to 0 ("too dark to measure").
    /// </summary>
    public static ushort FromLux(double lux)
    {
        if (lux <= 0 || double.IsNaN(lux))
        {
            return 0;
        }

        var scaled = Math.Round((10000 * Math.Log10(lux)) + 1);
        return (ushort)Math.Clamp(scaled, 1, 0xFFFE);
    }

    /// <summary>The inverse of <see cref="FromLux"/>; returns 0 for a MeasuredValue of 0.</summary>
    public static double ToLux(ushort measuredValue)
        => measuredValue == 0 ? 0 : Math.Pow(10, (measuredValue - 1) / 10000d);

    /// <inheritdoc />
    protected override ValueTask<InteractionModelStatusCode> ReadAttributeCoreAsync(
        AttributeId attributeId, TlvWriter writer, TlvTag tag, InteractionContext context, CancellationToken cancellationToken)
        => new(_attributes.TryRead(attributeId, writer, tag)
            ? InteractionModelStatusCode.Success
            : InteractionModelStatusCode.UnsupportedAttribute);
}
