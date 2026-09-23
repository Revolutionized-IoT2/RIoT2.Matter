namespace RIoT2.Matter.Clusters;

/// <summary>
/// The colour representation currently in effect, reported by the Color Control cluster's ColorMode
/// attribute as <c>enum8</c>. Values match the Matter Core Specification, section 3.2.7.9
/// (ColorModeEnum).
/// </summary>
public enum ColorMode : byte
{
    /// <summary>CurrentHue and CurrentSaturation carry the colour (the HS feature).</summary>
    CurrentHueAndCurrentSaturation = 0,

    /// <summary>CurrentX and CurrentY carry the colour (the XY feature; not implemented here).</summary>
    CurrentXAndCurrentY = 1,

    /// <summary>ColorTemperatureMireds carries the colour (the CT feature).</summary>
    ColorTemperatureMireds = 2,
}

/// <summary>
/// The Options bitmap of the Color Control cluster (the Options attribute and the per-command
/// OptionsMask/OptionsOverride fields), transmitted as <c>map8</c>. Values match the Matter Core
/// Specification, section 3.2.7.10 (OptionsBitmap).
/// </summary>
[Flags]
public enum ColorControlOptions : byte
{
    /// <summary>No options set.</summary>
    None = 0,

    /// <summary>Execute the command even while the coupled On/Off cluster reports off.</summary>
    ExecuteIfOff = 0x01,
}

/// <summary>
/// The features the Color Control cluster advertises through ColorCapabilities (and the FeatureMap),
/// transmitted as <c>map16</c>. Values match the Matter Core Specification, section 3.2.7.19
/// (ColorCapabilitiesBitmap).
/// </summary>
[Flags]
public enum ColorCapabilities : ushort
{
    /// <summary>No colour capability.</summary>
    None = 0,

    /// <summary>HS: hue and saturation are supported.</summary>
    HueSaturation = 0x0001,

    /// <summary>EHUE: the enhanced 16-bit hue is supported. Deferred by this implementation.</summary>
    EnhancedHue = 0x0002,

    /// <summary>CL: the colour loop is supported. Deferred by this implementation.</summary>
    ColorLoop = 0x0004,

    /// <summary>XY: the CIE xyY colour space is supported. Deferred by this implementation.</summary>
    XY = 0x0008,

    /// <summary>CT: the colour temperature (mireds) is supported.</summary>
    ColorTemperature = 0x0010,
}

/// <summary>
/// The direction a MoveToHue command travels around the colour wheel, transmitted as <c>enum8</c>.
/// Values match the Matter Core Specification, section 3.2.11.4 (DirectionEnum).
/// </summary>
public enum HueDirection : byte
{
    /// <summary>Travel whichever way around the wheel is shorter.</summary>
    ShortestDistance = 0,

    /// <summary>Travel whichever way around the wheel is longer.</summary>
    LongestDistance = 1,

    /// <summary>Travel with increasing hue values, wrapping past the maximum.</summary>
    Up = 2,

    /// <summary>Travel with decreasing hue values, wrapping past zero.</summary>
    Down = 3,
}

/// <summary>
/// The direction of a Color Control Move command (MoveHue, MoveSaturation, MoveColorTemperature),
/// transmitted as <c>enum8</c>. Values match the Matter Core Specification, section 3.2.11.5
/// (MoveModeEnum).
/// </summary>
public enum ColorMoveMode : byte
{
    /// <summary>Stop the movement in progress.</summary>
    Stop = 0,

    /// <summary>Move upward at the given rate.</summary>
    Up = 1,

    /// <summary>Move downward at the given rate.</summary>
    Down = 3,
}

/// <summary>
/// The direction of a Color Control Step command (StepHue, StepSaturation, StepColorTemperature),
/// transmitted as <c>enum8</c>. Values match the Matter Core Specification, section 3.2.11.6
/// (StepModeEnum).
/// </summary>
public enum ColorStepMode : byte
{
    /// <summary>Step upward by the given step size.</summary>
    Up = 1,

    /// <summary>Step downward by the given step size.</summary>
    Down = 3,
}
