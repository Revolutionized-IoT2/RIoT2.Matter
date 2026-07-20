namespace RIoT2.Matter.Clusters;

/// <summary>
/// The direction of a Level Control Move or Step command, transmitted as <c>enum8</c>. The Move and
/// Step commands share this enumeration (MoveModeEnum / StepModeEnum). Values match the Matter Core
/// Specification, sections 1.6.6.2 and 1.6.6.4.
/// </summary>
public enum LevelMoveMode : byte
{
    /// <summary>Move or step the level upward (toward MaxLevel).</summary>
    Up = 0,

    /// <summary>Move or step the level downward (toward MinLevel).</summary>
    Down = 1,
}

/// <summary>
/// The Options bitmap of the Level Control cluster (Options attribute and the per-command
/// OptionsMask/OptionsOverride fields), transmitted as <c>map8</c>. Values match the Matter Core
/// Specification, section 1.6.5.9 (OptionsBitmap).
/// </summary>
[Flags]
public enum LevelControlOptions : byte
{
    /// <summary>No options set.</summary>
    None = 0,

    /// <summary>Execute a (non-WithOnOff) command even while the device is off.</summary>
    ExecuteIfOff = 0x01,

    /// <summary>Couple the color temperature to the level (Color Control coupling). Deferred.</summary>
    CoupleColorTempToLevel = 0x02,
}

/// <summary>
/// The seam through which the Level Control cluster observes and drives the On/Off state of the same
/// endpoint's On/Off (0x0006) cluster: it reads <see cref="IsOn"/> to honor the ExecuteIfOff option
/// and calls <see cref="SetOnOff"/> from the WithOnOff command variants. Injecting this abstraction
/// keeps Level Control decoupled from the concrete On/Off cluster, mirroring how the commissioning
/// clusters depend on injected backends. See the Matter Core Specification, section 1.6.4.1.
/// </summary>
/// <remarks>
/// The composition root supplies an implementation over the endpoint's On/Off cluster and wires the
/// reverse direction by forwarding the cluster's change notification to
/// <see cref="LevelControlCluster.NotifyOnOffChanged"/>.
/// </remarks>
public interface IOnOffCoupling
{
    /// <summary>Whether the coupled On/Off cluster is currently on.</summary>
    bool IsOn { get; }

    /// <summary>Sets the coupled On/Off cluster's state (called by the WithOnOff command variants).</summary>
    void SetOnOff(bool on);
}