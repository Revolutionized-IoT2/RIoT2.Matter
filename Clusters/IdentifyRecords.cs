namespace RIoT2.Matter.Clusters;

/// <summary>
/// How a device makes itself identifiable, reported by the Identify cluster's IdentifyType attribute
/// and transmitted as <c>enum8</c>. Values match the Matter Core Specification, section 1.2.5.2
/// (IdentifyTypeEnum).
/// </summary>
public enum IdentifyType : byte
{
    /// <summary>The device is not able to identify itself in any of the defined ways.</summary>
    None = 0,

    /// <summary>The device identifies by modulating its light output (e.g. a bulb).</summary>
    LightOutput = 1,

    /// <summary>The device identifies with a dedicated visible indicator (e.g. an LED).</summary>
    VisibleIndicator = 2,

    /// <summary>The device identifies with an audible beep.</summary>
    AudibleBeep = 3,

    /// <summary>The device identifies via its display.</summary>
    Display = 4,

    /// <summary>The device identifies by actuating a physical mechanism.</summary>
    Actuator = 5,
}

/// <summary>
/// The visual effect requested by the Identify cluster's TriggerEffect command, transmitted as
/// <c>enum8</c>. Values match the Matter Core Specification, section 1.2.6.2.1 (EffectIdentifierEnum).
/// An unrecognized value defaults to <see cref="Blink"/> per the specification.
/// </summary>
public enum IdentifyEffect : byte
{
    /// <summary>Light is turned on/off once.</summary>
    Blink = 0x00,

    /// <summary>Light is slowly turned on and off over one second, repeated for 15 seconds.</summary>
    Breathe = 0x01,

    /// <summary>Light is turned on for one second then off (an acknowledgement).</summary>
    Okay = 0x02,

    /// <summary>A channel-change effect (colour cycle) for 8 seconds.</summary>
    ChannelChange = 0x0B,

    /// <summary>Finish the current effect sequence, then terminate.</summary>
    FinishEffect = 0xFE,

    /// <summary>Terminate the effect as soon as possible.</summary>
    StopEffect = 0xFF,
}

/// <summary>
/// The variant of the requested <see cref="IdentifyEffect"/>, transmitted as <c>enum8</c>. Values
/// match the Matter Core Specification, section 1.2.6.2.2 (EffectVariantEnum). An unrecognized value
/// defaults to <see cref="Default"/>.
/// </summary>
public enum IdentifyEffectVariant : byte
{
    /// <summary>The default variant of the effect.</summary>
    Default = 0x00,
}

/// <summary>
/// Carries the effect and variant of an Identify cluster TriggerEffect command so the host can render
/// the requested visual effect. See the Matter Core Specification, section 1.2.6.2.
/// </summary>
public sealed class IdentifyEffectEventArgs : EventArgs
{
    /// <param name="effect">The requested effect (already normalized to a defined value).</param>
    /// <param name="variant">The requested effect variant (already normalized to a defined value).</param>
    public IdentifyEffectEventArgs(IdentifyEffect effect, IdentifyEffectVariant variant)
    {
        Effect = effect;
        Variant = variant;
    }

    /// <summary>The requested effect.</summary>
    public IdentifyEffect Effect { get; }

    /// <summary>The requested effect variant.</summary>
    public IdentifyEffectVariant Variant { get; }
}