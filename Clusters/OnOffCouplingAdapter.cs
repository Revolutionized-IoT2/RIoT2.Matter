namespace RIoT2.Matter.Clusters;

/// <summary>
/// Couples a <see cref="LevelControlCluster"/> to the same endpoint's <see cref="OnOffCluster"/> by
/// implementing <see cref="IOnOffCoupling"/> over it: it reads OnOff to honor the Level Control
/// ExecuteIfOff option and drives OnOff from the WithOnOff command variants. The composition root wires
/// the reverse direction by forwarding <see cref="OnOffCluster.OnOffChanged"/> to
/// <see cref="LevelControlCluster.NotifyOnOffChanged"/>. See the Matter Core Specification, section 1.6.4.1.
/// </summary>
public sealed class OnOffCouplingAdapter : IOnOffCoupling
{
    private readonly OnOffCluster _onOff;

    /// <param name="onOff">The On/Off cluster on the same endpoint as the coupled Level Control cluster.</param>
    public OnOffCouplingAdapter(OnOffCluster onOff)
    {
        ArgumentNullException.ThrowIfNull(onOff);
        _onOff = onOff;
    }

    /// <inheritdoc />
    public bool IsOn => _onOff.OnOff;

    /// <inheritdoc />
    public void SetOnOff(bool on) => _onOff.OnOff = on;
}