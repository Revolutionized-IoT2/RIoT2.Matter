using System;
using System.Threading;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.Device;
using RIoT2.Matter.InteractionModel;
using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.Clusters;

/// <summary>
/// The Color Control cluster (0x0300) on an application endpoint, implementing the Hue/Saturation (HS)
/// and Color Temperature (CT) features: it exposes the device-driven CurrentHue, CurrentSaturation and
/// ColorTemperatureMireds together with the ColorMode/ColorCapabilities projection, and implements the
/// MoveToHue/MoveHue/StepHue, MoveToSaturation/MoveSaturation/StepSaturation,
/// MoveToHueAndSaturation, MoveToColorTemperature/MoveColorTemperature/StepColorTemperature and
/// StopMoveStep commands. Timed transitions are driven one tick at a time from an injected
/// <see cref="TimeProvider"/> (the node time source). See the Matter Core Specification, section 3.2.
/// </summary>
/// <remarks>
/// Add to a colour lighting endpoint alongside On/Off and Level Control:
/// <code>
/// var onOff = new OnOffCluster();
/// var level = new LevelControlCluster(coupling: new OnOffCouplingAdapter(onOff));
/// var color = new ColorControlCluster(coupling: new OnOffCouplingAdapter(onOff));
/// endpoint.AddCluster(onOff).AddCluster(level).AddCluster(color);
/// color.CurrentHueChanged += (_, _) => lamp.SetHue(color.CurrentHue);
/// </code>
/// The XY, Enhanced Hue (EHUE) and Colour Loop (CL) features are deferred, so <see cref="FeatureMap"/>
/// and ColorCapabilities are both <c>0x0011</c> (HS + CT) and the XY attributes are absent. Only one
/// transition is active at a time: starting a hue/saturation transition cancels a colour-temperature
/// transition and vice versa, which also flips ColorMode. StartUpColorTemperatureMireds is stored and
/// readable but not applied on start-up, because the cluster has no persistence of its own. Dispose the
/// cluster to release the transition timer.
/// </remarks>
public sealed class ColorControlCluster : Cluster, IDisposable
{
    /// <summary>The Color Control cluster identifier (0x0300).</summary>
    public static readonly ClusterId ClusterId = new(0x0300);

    // Attribute ids (spec 3.2.7). The XY, EHUE and colour-loop attributes are deferred.
    private const uint CurrentHueId = 0x0000;
    private const uint CurrentSaturationId = 0x0001;
    private const uint RemainingTimeId = 0x0002;
    private const uint ColorTemperatureMiredsId = 0x0007;
    private const uint ColorModeId = 0x0008;
    private const uint OptionsId = 0x000F;
    private const uint EnhancedColorModeId = 0x4001;
    private const uint ColorCapabilitiesId = 0x400A;
    private const uint ColorTempPhysicalMinMiredsId = 0x400B;
    private const uint ColorTempPhysicalMaxMiredsId = 0x400C;
    private const uint CoupleColorTempToLevelMinMiredsId = 0x400D;
    private const uint StartUpColorTemperatureMiredsId = 0x4010;

    // Command ids (spec 3.2.11).
    private const uint MoveToHueId = 0x00;
    private const uint MoveHueId = 0x01;
    private const uint StepHueId = 0x02;
    private const uint MoveToSaturationId = 0x03;
    private const uint MoveSaturationId = 0x04;
    private const uint StepSaturationId = 0x05;
    private const uint MoveToHueAndSaturationId = 0x06;
    private const uint MoveToColorTemperatureId = 0x0A;
    private const uint StopMoveStepId = 0x47;
    private const uint MoveColorTemperatureId = 0x4B;
    private const uint StepColorTemperatureId = 0x4C;

    /// <summary>The highest CurrentHue / CurrentSaturation value; 255 is reserved (spec 3.2.7.1).</summary>
    private const byte MaxHueSaturation = 254;

    /// <summary>The number of distinct hue values, i.e. the modulus the colour wheel wraps on.</summary>
    private const int HueWheel = MaxHueSaturation + 1;

    // RemainingTime and the transition step run in tenths of a second (spec 3.2.7.3).
    private static readonly TimeSpan TransitionTick = TimeSpan.FromMilliseconds(100);
    private static readonly TlvCodec<ushort?> NullableUInt16 = TlvCodec.Nullable(TlvCodec.UInt16);

    private static readonly CommandId[] AcceptedCommands =
    [
        new(MoveToHueId), new(MoveHueId), new(StepHueId),
        new(MoveToSaturationId), new(MoveSaturationId), new(StepSaturationId),
        new(MoveToHueAndSaturationId),
        new(MoveToColorTemperatureId), new(MoveColorTemperatureId), new(StepColorTemperatureId),
        new(StopMoveStepId),
    ];

    private readonly ushort _physicalMinMireds;
    private readonly ushort _physicalMaxMireds;
    private readonly IOnOffCoupling? _coupling;
    private readonly TimeProvider _timeProvider;
    private readonly AttributeStore _attributes;
    private readonly Attribute<byte> _currentHue;
    private readonly Attribute<byte> _currentSaturation;
    private readonly Attribute<ushort> _remainingTime;
    private readonly Attribute<ushort> _colorTemperatureMireds;
    private readonly Attribute<byte> _colorMode;
    private readonly Attribute<byte> _enhancedColorMode;
    private readonly Attribute<byte> _options;
    private readonly object _gate = new();

    private ITimer? _timer;
    private bool _transitionActive;
    private int _transDuration; // tenths of a second
    private int _transElapsed;  // tenths of a second
    private bool _hueMoving;
    private int _hueStart;
    private int _hueDelta;      // signed; may travel more than once around the wheel
    private bool _hueLoop;      // a continuous MoveHue that only ends on StopMoveStep
    private bool _saturationMoving;
    private int _saturationStart;
    private int _saturationEnd;
    private bool _colorTemperatureMoving;
    private int _colorTemperatureStart;
    private int _colorTemperatureEnd;
    private bool _disposed;

    /// <param name="initialHue">The initial CurrentHue (0..254).</param>
    /// <param name="initialSaturation">The initial CurrentSaturation (0..254).</param>
    /// <param name="initialColorTemperatureMireds">The initial ColorTemperatureMireds (clamped into the physical bounds).</param>
    /// <param name="physicalMinMireds">The ColorTempPhysicalMinMireds bound; defaults to 153 mireds (about 6500 K).</param>
    /// <param name="physicalMaxMireds">The ColorTempPhysicalMaxMireds bound; defaults to 500 mireds (2000 K).</param>
    /// <param name="initialColorMode">The initial ColorMode; XY is not supported and falls back to hue/saturation.</param>
    /// <param name="initialOptions">The initial Options bitmap.</param>
    /// <param name="coupling">The On/Off coupling backing the ExecuteIfOff option; <see langword="null"/> executes always.</param>
    /// <param name="timeProvider">The clock driving transitions; defaults to <see cref="TimeProvider.System"/>.</param>
    public ColorControlCluster(
        byte initialHue = 0,
        byte initialSaturation = 0,
        ushort initialColorTemperatureMireds = 250,
        ushort physicalMinMireds = 153,
        ushort physicalMaxMireds = 500,
        ColorMode initialColorMode = ColorMode.CurrentHueAndCurrentSaturation,
        ColorControlOptions initialOptions = ColorControlOptions.None,
        IOnOffCoupling? coupling = null,
        TimeProvider? timeProvider = null)
    {
        if (physicalMinMireds == 0 || physicalMinMireds > physicalMaxMireds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(physicalMinMireds), physicalMinMireds, "ColorTempPhysicalMinMireds must be non-zero and at most ColorTempPhysicalMaxMireds.");
        }

        _physicalMinMireds = physicalMinMireds;
        _physicalMaxMireds = physicalMaxMireds;
        _coupling = coupling;
        _timeProvider = timeProvider ?? TimeProvider.System;

        // XY is not implemented, so a caller asking for it gets the hue/saturation mode instead.
        var startMode = initialColorMode == ColorMode.CurrentXAndCurrentY
            ? ColorMode.CurrentHueAndCurrentSaturation
            : initialColorMode;

        _attributes = new AttributeStore(IncrementDataVersion);
        _currentHue = _attributes.Add(new AttributeId(CurrentHueId), TlvCodec.UInt8, ClampHueSaturation(initialHue));                        // R V, device-driven
        _currentSaturation = _attributes.Add(new AttributeId(CurrentSaturationId), TlvCodec.UInt8, ClampHueSaturation(initialSaturation));  // R V, device-driven
        _remainingTime = _attributes.Add(new AttributeId(RemainingTimeId), TlvCodec.UInt16, initialValue: (ushort)0);
        _colorTemperatureMireds = _attributes.Add(
            new AttributeId(ColorTemperatureMiredsId), TlvCodec.UInt16, (ushort)ClampMireds(initialColorTemperatureMireds));
        _colorMode = _attributes.Add(new AttributeId(ColorModeId), TlvCodec.UInt8, (byte)startMode);
        _options = _attributes.Add(new AttributeId(OptionsId), TlvCodec.UInt8, (byte)initialOptions, writable: true);
        _enhancedColorMode = _attributes.Add(new AttributeId(EnhancedColorModeId), TlvCodec.UInt8, (byte)startMode);

        // Fixed capability/bound attributes: registered read-only so they are served (and enumerated)
        // by the same store as the live ones.
        _attributes.Add(new AttributeId(ColorCapabilitiesId), TlvCodec.UInt16, (ushort)SupportedCapabilities);
        _attributes.Add(new AttributeId(ColorTempPhysicalMinMiredsId), TlvCodec.UInt16, _physicalMinMireds);
        _attributes.Add(new AttributeId(ColorTempPhysicalMaxMiredsId), TlvCodec.UInt16, _physicalMaxMireds);
        _attributes.Add(new AttributeId(CoupleColorTempToLevelMinMiredsId), TlvCodec.UInt16, _physicalMinMireds);
        _attributes.Add(new AttributeId(StartUpColorTemperatureMiredsId), NullableUInt16, initialValue: (ushort?)null, writable: true,
            validate: v => v is null || (v.Value >= _physicalMinMireds && v.Value <= _physicalMaxMireds));
    }

    /// <summary>The capability set this implementation supports: hue/saturation plus colour temperature.</summary>
    public const ColorCapabilities SupportedCapabilities = ColorCapabilities.HueSaturation | ColorCapabilities.ColorTemperature;

    /// <inheritdoc />
    public override ClusterId Id => ClusterId;

    /// <inheritdoc />
    /// <remarks>Revision 5 (Matter 1.0/1.1) definition; the XY, EHUE and colour-loop features are deferred.</remarks>
    public override ushort ClusterRevision => 5;

    /// <inheritdoc />
    /// <remarks>HS (bit 0) + CT (bit 4), matching <see cref="SupportedCapabilities"/>.</remarks>
    public override uint FeatureMap => (uint)SupportedCapabilities;

    /// <inheritdoc />
    public override IReadOnlyCollection<AttributeId> AttributeIds => _attributes.Ids;

    /// <inheritdoc />
    public override IReadOnlyCollection<CommandId> AcceptedCommandIds => AcceptedCommands;

    /// <summary>Raised whenever CurrentHue changes, so the host can drive the physical output. Raised outside the internal lock.</summary>
    public event EventHandler? CurrentHueChanged;

    /// <summary>Raised whenever CurrentSaturation changes. Raised outside the internal lock.</summary>
    public event EventHandler? CurrentSaturationChanged;

    /// <summary>Raised whenever ColorTemperatureMireds changes. Raised outside the internal lock.</summary>
    public event EventHandler? ColorTemperatureMiredsChanged;

    /// <summary>The current hue (0..254 mapping onto the full colour wheel).</summary>
    public byte CurrentHue => _currentHue.Value;

    /// <summary>The current saturation (0..254).</summary>
    public byte CurrentSaturation => _currentSaturation.Value;

    /// <summary>The current colour temperature in mireds.</summary>
    public ushort ColorTemperatureMireds => _colorTemperatureMireds.Value;

    /// <summary>The colour representation currently in effect.</summary>
    public ColorMode Mode => (ColorMode)_colorMode.Value;

    /// <summary>The time remaining in the active transition, in tenths of a second; 0 when idle.</summary>
    public ushort RemainingTime => _remainingTime.Value;

    /// <summary>The ColorTempPhysicalMinMireds bound.</summary>
    public ushort ColorTempPhysicalMinMireds => _physicalMinMireds;

    /// <summary>The ColorTempPhysicalMaxMireds bound.</summary>
    public ushort ColorTempPhysicalMaxMireds => _physicalMaxMireds;

    /// <summary>The current Options bitmap.</summary>
    public ColorControlOptions Options => (ColorControlOptions)_options.Value;

    /// <summary>Sets CurrentHue from device logic (e.g. the real lamp reporting its colour), cancelling any active transition.</summary>
    public void SetCurrentHue(byte hue) => SetHueAndSaturation(hue, _currentSaturation.Value);

    /// <summary>Sets CurrentSaturation from device logic, cancelling any active transition.</summary>
    public void SetCurrentSaturation(byte saturation) => SetHueAndSaturation(_currentHue.Value, saturation);

    /// <summary>
    /// Sets CurrentHue and CurrentSaturation from device logic in one step, cancelling any active
    /// transition and switching ColorMode to hue/saturation.
    /// </summary>
    public void SetHueAndSaturation(byte hue, byte saturation)
    {
        bool hueChanged, saturationChanged;
        lock (_gate)
        {
            CancelTransitionLocked();
            hueChanged = ApplyHueLocked(ClampHueSaturation(hue));
            saturationChanged = ApplySaturationLocked(ClampHueSaturation(saturation));
            SetColorModeLocked(ColorMode.CurrentHueAndCurrentSaturation);
            _remainingTime.Value = 0;
        }

        RaiseChanges(hueChanged, saturationChanged, colorTemperatureChanged: false);
    }

    /// <summary>
    /// Sets ColorTemperatureMireds from device logic, cancelling any active transition and switching
    /// ColorMode to colour temperature. The value is clamped into the physical bounds.
    /// </summary>
    public void SetColorTemperatureMireds(ushort mireds)
    {
        bool changed;
        lock (_gate)
        {
            CancelTransitionLocked();
            changed = ApplyColorTemperatureLocked(ClampMireds(mireds));
            SetColorModeLocked(ColorMode.ColorTemperatureMireds);
            _remainingTime.Value = 0;
        }

        RaiseChanges(hueChanged: false, saturationChanged: false, colorTemperatureChanged: changed);
    }

    /// <inheritdoc />
    protected override ValueTask<InteractionModelStatusCode> ReadAttributeCoreAsync(
        AttributeId attributeId, TlvWriter writer, TlvTag tag, InteractionContext context, CancellationToken cancellationToken)
        => new(_attributes.TryRead(attributeId, writer, tag)
            ? InteractionModelStatusCode.Success
            : InteractionModelStatusCode.UnsupportedAttribute);

    /// <inheritdoc />
    protected override ValueTask<InteractionModelStatusCode> WriteAttributeCoreAsync(
        AttributeId attributeId, ReadOnlyMemory<byte> value, InteractionContext context, CancellationToken cancellationToken)
        => new(_attributes.Write(attributeId, value)); // Options/StartUpColorTemperatureMireds writable, the rest device-driven

    /// <inheritdoc />
    protected override ValueTask<CommandResponse> InvokeCommandCoreAsync(
        CommandId commandId, ReadOnlyMemory<byte> fields, InteractionContext context, CancellationToken cancellationToken)
        => commandId.Value switch
        {
            MoveToHueId => CommandCodec.Invoke(fields, ExecuteMoveToHue),
            MoveHueId => CommandCodec.Invoke(fields, ExecuteMoveHue),
            StepHueId => CommandCodec.Invoke(fields, ExecuteStepHue),
            MoveToSaturationId => CommandCodec.Invoke(fields, ExecuteMoveToSaturation),
            MoveSaturationId => CommandCodec.Invoke(fields, ExecuteMoveSaturation),
            StepSaturationId => CommandCodec.Invoke(fields, ExecuteStepSaturation),
            MoveToHueAndSaturationId => CommandCodec.Invoke(fields, ExecuteMoveToHueAndSaturation),
            MoveToColorTemperatureId => CommandCodec.Invoke(fields, ExecuteMoveToColorTemperature),
            MoveColorTemperatureId => CommandCodec.Invoke(fields, ExecuteMoveColorTemperature),
            StepColorTemperatureId => CommandCodec.Invoke(fields, ExecuteStepColorTemperature),
            StopMoveStepId => CommandCodec.Invoke(fields, ExecuteStopMoveStep),
            _ => new ValueTask<CommandResponse>(CommandResponse.FromStatus(InteractionModelStatusCode.UnsupportedCommand)),
        };

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }

    private CommandResponse ExecuteMoveToHue(CommandFields fields)
    {
        var hue = fields.GetRequired(0, TlvCodec.UInt8, v => v <= MaxHueSaturation);
        var direction = (HueDirection)fields.GetRequired(1, TlvCodec.UInt8, v => v <= 3);
        var transitionTime = fields.GetOptional(2, TlvCodec.UInt16, fallback: (ushort)0);
        var (mask, over) = ReadOptions(fields, maskField: 3, overrideField: 4);

        if (!ShouldExecuteWhenOff(mask, over))
        {
            return CommandResponse.Success();
        }

        BeginTransition(HueDeltaTo(hue, direction), saturationEnd: null, colorTemperatureEnd: null, transitionTime);
        return CommandResponse.Success();
    }

    private CommandResponse ExecuteMoveHue(CommandFields fields)
    {
        var mode = (ColorMoveMode)fields.GetRequired(0, TlvCodec.UInt8, v => v is 0 or 1 or 3);
        var rate = fields.GetOptional(1, TlvCodec.UInt8, fallback: (byte)0);
        var (mask, over) = ReadOptions(fields, maskField: 2, overrideField: 3);

        if (!ShouldExecuteWhenOff(mask, over))
        {
            return CommandResponse.Success();
        }

        if (mode == ColorMoveMode.Stop || rate == 0)
        {
            StopTransition();
            return CommandResponse.Success();
        }

        // A hue move runs around the wheel until it is stopped; one lap is HueWheel units at Rate/second.
        var up = mode == ColorMoveMode.Up;
        BeginTransition(up ? HueWheel : -HueWheel, null, null, HueWheel * 10 / rate, hueLoop: true);
        return CommandResponse.Success();
    }

    private CommandResponse ExecuteStepHue(CommandFields fields)
    {
        var mode = (ColorStepMode)fields.GetRequired(0, TlvCodec.UInt8, v => v is 1 or 3);
        var stepSize = fields.GetRequired(1, TlvCodec.UInt8);
        var transitionTime = fields.GetOptional(2, TlvCodec.UInt8, fallback: (byte)0); // uint8 for the hue/saturation steps
        var (mask, over) = ReadOptions(fields, maskField: 3, overrideField: 4);

        if (!ShouldExecuteWhenOff(mask, over))
        {
            return CommandResponse.Success();
        }

        BeginTransition(mode == ColorStepMode.Up ? stepSize : -stepSize, null, null, transitionTime);
        return CommandResponse.Success();
    }

    private CommandResponse ExecuteMoveToSaturation(CommandFields fields)
    {
        var saturation = fields.GetRequired(0, TlvCodec.UInt8, v => v <= MaxHueSaturation);
        var transitionTime = fields.GetOptional(1, TlvCodec.UInt16, fallback: (ushort)0);
        var (mask, over) = ReadOptions(fields, maskField: 2, overrideField: 3);

        if (!ShouldExecuteWhenOff(mask, over))
        {
            return CommandResponse.Success();
        }

        BeginTransition(null, saturation, null, transitionTime);
        return CommandResponse.Success();
    }

    private CommandResponse ExecuteMoveSaturation(CommandFields fields)
    {
        var mode = (ColorMoveMode)fields.GetRequired(0, TlvCodec.UInt8, v => v is 0 or 1 or 3);
        var rate = fields.GetOptional(1, TlvCodec.UInt8, fallback: (byte)0);
        var (mask, over) = ReadOptions(fields, maskField: 2, overrideField: 3);

        if (!ShouldExecuteWhenOff(mask, over))
        {
            return CommandResponse.Success();
        }

        if (mode == ColorMoveMode.Stop || rate == 0)
        {
            StopTransition();
            return CommandResponse.Success();
        }

        var end = mode == ColorMoveMode.Up ? MaxHueSaturation : 0;
        var duration = Math.Abs(end - _currentSaturation.Value) * 10 / rate;
        BeginTransition(null, end, null, duration);
        return CommandResponse.Success();
    }

    private CommandResponse ExecuteStepSaturation(CommandFields fields)
    {
        var mode = (ColorStepMode)fields.GetRequired(0, TlvCodec.UInt8, v => v is 1 or 3);
        var stepSize = fields.GetRequired(1, TlvCodec.UInt8);
        var transitionTime = fields.GetOptional(2, TlvCodec.UInt8, fallback: (byte)0);
        var (mask, over) = ReadOptions(fields, maskField: 3, overrideField: 4);

        if (!ShouldExecuteWhenOff(mask, over))
        {
            return CommandResponse.Success();
        }

        var current = _currentSaturation.Value;
        var end = ClampHueSaturation(mode == ColorStepMode.Up ? current + stepSize : current - stepSize);
        BeginTransition(null, end, null, transitionTime);
        return CommandResponse.Success();
    }

    private CommandResponse ExecuteMoveToHueAndSaturation(CommandFields fields)
    {
        var hue = fields.GetRequired(0, TlvCodec.UInt8, v => v <= MaxHueSaturation);
        var saturation = fields.GetRequired(1, TlvCodec.UInt8, v => v <= MaxHueSaturation);
        var transitionTime = fields.GetOptional(2, TlvCodec.UInt16, fallback: (ushort)0);
        var (mask, over) = ReadOptions(fields, maskField: 3, overrideField: 4);

        if (!ShouldExecuteWhenOff(mask, over))
        {
            return CommandResponse.Success();
        }

        BeginTransition(HueDeltaTo(hue, HueDirection.ShortestDistance), saturation, null, transitionTime);
        return CommandResponse.Success();
    }

    private CommandResponse ExecuteMoveToColorTemperature(CommandFields fields)
    {
        var mireds = fields.GetRequired(0, TlvCodec.UInt16);
        var transitionTime = fields.GetOptional(1, TlvCodec.UInt16, fallback: (ushort)0);
        var (mask, over) = ReadOptions(fields, maskField: 2, overrideField: 3);

        if (!ShouldExecuteWhenOff(mask, over))
        {
            return CommandResponse.Success();
        }

        BeginTransition(null, null, ClampMireds(mireds), transitionTime);
        return CommandResponse.Success();
    }

    private CommandResponse ExecuteMoveColorTemperature(CommandFields fields)
    {
        var mode = (ColorMoveMode)fields.GetRequired(0, TlvCodec.UInt8, v => v is 0 or 1 or 3);
        var rate = fields.GetOptional(1, TlvCodec.UInt16, fallback: (ushort)0);
        var (min, max) = ReadMiredsBounds(fields, minField: 2, maxField: 3);
        var (mask, over) = ReadOptions(fields, maskField: 4, overrideField: 5);

        if (!ShouldExecuteWhenOff(mask, over))
        {
            return CommandResponse.Success();
        }

        if (mode == ColorMoveMode.Stop || rate == 0)
        {
            StopTransition();
            return CommandResponse.Success();
        }

        var end = mode == ColorMoveMode.Up ? max : min;
        var duration = Math.Abs(end - _colorTemperatureMireds.Value) * 10 / rate;
        BeginTransition(null, null, end, duration);
        return CommandResponse.Success();
    }

    private CommandResponse ExecuteStepColorTemperature(CommandFields fields)
    {
        var mode = (ColorStepMode)fields.GetRequired(0, TlvCodec.UInt8, v => v is 1 or 3);
        var stepSize = fields.GetRequired(1, TlvCodec.UInt16);
        var transitionTime = fields.GetOptional(2, TlvCodec.UInt16, fallback: (ushort)0);
        var (min, max) = ReadMiredsBounds(fields, minField: 3, maxField: 4);
        var (mask, over) = ReadOptions(fields, maskField: 5, overrideField: 6);

        if (!ShouldExecuteWhenOff(mask, over))
        {
            return CommandResponse.Success();
        }

        var current = _colorTemperatureMireds.Value;
        var stepped = mode == ColorStepMode.Up ? current + stepSize : current - stepSize;
        BeginTransition(null, null, Math.Clamp(stepped, min, max), transitionTime);
        return CommandResponse.Success();
    }

    private CommandResponse ExecuteStopMoveStep(CommandFields fields)
    {
        var (mask, over) = ReadOptions(fields, maskField: 0, overrideField: 1);
        if (ShouldExecuteWhenOff(mask, over))
        {
            StopTransition();
        }

        return CommandResponse.Success();
    }

    /// <summary>
    /// Starts a transition toward the supplied channel targets over <paramref name="durationTenths"/>.
    /// A null target leaves that channel untouched; supplying a colour temperature switches ColorMode to
    /// CT, supplying hue or saturation switches it to HS. Any transition already running is cancelled.
    /// </summary>
    private void BeginTransition(int? hueDelta, int? saturationEnd, int? colorTemperatureEnd, int durationTenths, bool hueLoop = false)
    {
        bool hueChanged = false, saturationChanged = false, colorTemperatureChanged = false;
        lock (_gate)
        {
            CancelTransitionLocked();
            SetColorModeLocked(colorTemperatureEnd.HasValue
                ? ColorMode.ColorTemperatureMireds
                : ColorMode.CurrentHueAndCurrentSaturation);

            if (durationTenths <= 0)
            {
                if (hueDelta is { } delta) { hueChanged = ApplyHueLocked(WrapHue(_currentHue.Value + delta)); }
                if (saturationEnd is { } saturation) { saturationChanged = ApplySaturationLocked(saturation); }
                if (colorTemperatureEnd is { } mireds) { colorTemperatureChanged = ApplyColorTemperatureLocked(mireds); }
                _remainingTime.Value = 0;
            }
            else
            {
                _hueMoving = hueDelta.HasValue;
                _hueStart = _currentHue.Value;
                _hueDelta = hueDelta ?? 0;
                _hueLoop = hueLoop;
                _saturationMoving = saturationEnd.HasValue;
                _saturationStart = _currentSaturation.Value;
                _saturationEnd = saturationEnd ?? 0;
                _colorTemperatureMoving = colorTemperatureEnd.HasValue;
                _colorTemperatureStart = _colorTemperatureMireds.Value;
                _colorTemperatureEnd = colorTemperatureEnd ?? 0;
                _transDuration = durationTenths;
                _transElapsed = 0;
                _transitionActive = true;
                _remainingTime.Value = (ushort)Math.Min(durationTenths, ushort.MaxValue);
                ScheduleTickLocked();
            }
        }

        RaiseChanges(hueChanged, saturationChanged, colorTemperatureChanged);
    }

    private void StopTransition()
    {
        lock (_gate)
        {
            CancelTransitionLocked();
            _remainingTime.Value = 0;
        }
    }

    private void OnTransitionTick(object? state)
    {
        bool hueChanged = false, saturationChanged = false, colorTemperatureChanged = false;
        lock (_gate)
        {
            if (_disposed || !_transitionActive)
            {
                return;
            }

            _transElapsed++;
            var done = _transElapsed >= _transDuration;

            if (_hueMoving)
            {
                var value = done
                    ? _hueStart + _hueDelta
                    : _hueStart + (_hueDelta * _transElapsed / _transDuration);
                hueChanged = ApplyHueLocked(WrapHue(value));
            }

            if (_saturationMoving)
            {
                saturationChanged = ApplySaturationLocked(Interpolate(_saturationStart, _saturationEnd, done));
            }

            if (_colorTemperatureMoving)
            {
                colorTemperatureChanged = ApplyColorTemperatureLocked(Interpolate(_colorTemperatureStart, _colorTemperatureEnd, done));
            }

            if (!done)
            {
                _remainingTime.Value = (ushort)Math.Min(_transDuration - _transElapsed, ushort.MaxValue);
                ScheduleTickLocked();
            }
            else if (_hueLoop)
            {
                // A continuous MoveHue: start the next lap from where this one ended.
                _hueStart = _currentHue.Value;
                _saturationMoving = false;
                _colorTemperatureMoving = false;
                _transElapsed = 0;
                _remainingTime.Value = (ushort)Math.Min(_transDuration, ushort.MaxValue);
                ScheduleTickLocked();
            }
            else
            {
                _transitionActive = false;
                _remainingTime.Value = 0;
                StopTimerLocked();
            }
        }

        RaiseChanges(hueChanged, saturationChanged, colorTemperatureChanged);
    }

    private int Interpolate(int start, int end, bool done)
        => done ? end : start + ((end - start) * _transElapsed / _transDuration);

    private bool ApplyHueLocked(int hue)
    {
        var value = (byte)hue;
        var changed = _currentHue.Value != value;
        _currentHue.Value = value;
        return changed;
    }

    private bool ApplySaturationLocked(int saturation)
    {
        var value = ClampHueSaturation(saturation);
        var changed = _currentSaturation.Value != value;
        _currentSaturation.Value = value;
        return changed;
    }

    private bool ApplyColorTemperatureLocked(int mireds)
    {
        var value = (ushort)ClampMireds(mireds);
        var changed = _colorTemperatureMireds.Value != value;
        _colorTemperatureMireds.Value = value;
        return changed;
    }

    private void SetColorModeLocked(ColorMode mode)
    {
        _colorMode.Value = (byte)mode;
        _enhancedColorMode.Value = (byte)mode; // EHUE is deferred, so the enhanced mode mirrors ColorMode.
    }

    private void RaiseChanges(bool hueChanged, bool saturationChanged, bool colorTemperatureChanged)
    {
        if (hueChanged) { CurrentHueChanged?.Invoke(this, EventArgs.Empty); }
        if (saturationChanged) { CurrentSaturationChanged?.Invoke(this, EventArgs.Empty); }
        if (colorTemperatureChanged) { ColorTemperatureMiredsChanged?.Invoke(this, EventArgs.Empty); }
    }

    /// <summary>The signed distance from CurrentHue to <paramref name="hue"/> for the requested direction.</summary>
    private int HueDeltaTo(byte hue, HueDirection direction)
    {
        var up = ((hue - _currentHue.Value) % HueWheel + HueWheel) % HueWheel;
        var down = up == 0 ? 0 : up - HueWheel; // negative distance the other way around the wheel

        return direction switch
        {
            HueDirection.Up => up,
            HueDirection.Down => down,
            HueDirection.LongestDistance => up >= -down ? up : down,
            _ => up <= -down ? up : down, // ShortestDistance
        };
    }

    // The effective ExecuteIfOff gate shared by every command (spec 3.2.7.10).
    private bool ShouldExecuteWhenOff(byte optionsMask, byte optionsOverride)
    {
        if (_coupling is not { } coupling || coupling.IsOn)
        {
            return true;
        }

        var effective = (byte)((_options.Value & ~optionsMask) | (optionsOverride & optionsMask));
        return (effective & (byte)ColorControlOptions.ExecuteIfOff) != 0;
    }

    private void ScheduleTickLocked()
    {
        _timer ??= _timeProvider.CreateTimer(OnTransitionTick, state: null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _timer.Change(TransitionTick, Timeout.InfiniteTimeSpan);
    }

    private void CancelTransitionLocked()
    {
        _transitionActive = false;
        _hueMoving = false;
        _hueLoop = false;
        _saturationMoving = false;
        _colorTemperatureMoving = false;
        StopTimerLocked();
    }

    private void StopTimerLocked() => _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

    /// <summary>Reads a colour-temperature command's min/max bounds, defaulting to the physical bounds when absent or zero.</summary>
    private (int Min, int Max) ReadMiredsBounds(CommandFields fields, byte minField, byte maxField)
    {
        var min = fields.GetOptional(minField, TlvCodec.UInt16, fallback: (ushort)0);
        var max = fields.GetOptional(maxField, TlvCodec.UInt16, fallback: (ushort)0);

        // 0 means "no client-supplied limit" (spec 3.2.11.19.3), so fall back to the physical bound.
        var lower = min == 0 ? _physicalMinMireds : ClampMireds(min);
        var upper = max == 0 ? _physicalMaxMireds : ClampMireds(max);
        return upper < lower ? (lower, lower) : (lower, upper);
    }

    private int ClampMireds(int mireds) => Math.Clamp(mireds, _physicalMinMireds, _physicalMaxMireds);

    private static byte ClampHueSaturation(int value)
        => value < 0 ? (byte)0 : value > MaxHueSaturation ? MaxHueSaturation : (byte)value;

    private static int WrapHue(int hue) => ((hue % HueWheel) + HueWheel) % HueWheel;

    private static (byte Mask, byte Override) ReadOptions(CommandFields fields, byte maskField, byte overrideField) =>
        (fields.GetOptional(maskField, TlvCodec.UInt8, fallback: (byte)0),
         fields.GetOptional(overrideField, TlvCodec.UInt8, fallback: (byte)0));
}
