using System;
using System.Threading;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.Device;
using RIoT2.Matter.InteractionModel;
using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.Clusters;

/// <summary>
/// The Thermostat cluster (0x0201) on an application endpoint, implementing the Heating, Cooling and
/// (optionally) AutoMode features: it exposes the device-driven LocalTemperature, the writable
/// OccupiedHeatingSetpoint / OccupiedCoolingSetpoint / SystemMode, the setpoint limit set, and the
/// SetpointRaiseLower command. See the Matter Core Specification, section 4.3.
/// </summary>
/// <remarks>
/// All temperatures are signed hundredths of a degree Celsius, so 21.5 C is <c>2150</c>; the exception is
/// MinSetpointDeadBand and the SetpointRaiseLower Amount field, which the spec carries in tenths of a
/// degree. A typical heating-only thermostat endpoint is composed as:
/// <code>
/// var thermostat = new ThermostatCluster(ThermostatFeature.Heating);
/// endpoint.AddCluster(new IdentifyCluster()).AddCluster(thermostat);
/// thermostat.OccupiedHeatingSetpointChanged += (_, _) => valve.SetTarget(thermostat.OccupiedHeatingSetpoint);
/// thermostat.SetLocalTemperature(sensor.Hundredths);
/// </code>
/// The Occupancy, ScheduleConfiguration and Setback features are deferred, so the unoccupied setpoints,
/// weekly schedule commands and setback attributes are absent. The setpoint limit attributes are fixed at
/// construction and served read-only, which the spec permits. ThermostatRunningMode is derived from
/// SystemMode (and, in Auto, from LocalTemperature against the setpoints) rather than reported by a real
/// heating plant.
/// </remarks>
public sealed class ThermostatCluster : Cluster
{
    /// <summary>The Thermostat cluster identifier (0x0201).</summary>
    public static readonly ClusterId ClusterId = new(0x0201);

    // Attribute ids (spec 4.3.7). Unoccupied setpoints, setback and schedule attributes are deferred.
    private const uint LocalTemperatureId = 0x0000;
    private const uint AbsMinHeatSetpointLimitId = 0x0003;
    private const uint AbsMaxHeatSetpointLimitId = 0x0004;
    private const uint AbsMinCoolSetpointLimitId = 0x0005;
    private const uint AbsMaxCoolSetpointLimitId = 0x0006;
    private const uint OccupiedCoolingSetpointId = 0x0011;
    private const uint OccupiedHeatingSetpointId = 0x0012;
    private const uint MinHeatSetpointLimitId = 0x0015;
    private const uint MaxHeatSetpointLimitId = 0x0016;
    private const uint MinCoolSetpointLimitId = 0x0017;
    private const uint MaxCoolSetpointLimitId = 0x0018;
    private const uint MinSetpointDeadBandId = 0x0019;
    private const uint ControlSequenceOfOperationId = 0x001B;
    private const uint SystemModeId = 0x001C;
    private const uint ThermostatRunningModeId = 0x001E;

    // Command ids (spec 4.3.9). The schedule commands come with the SCH feature and are deferred.
    private const uint SetpointRaiseLowerId = 0x00;

    private static readonly TlvCodec<short?> NullableInt16 = TlvCodec.Nullable(TlvCodec.Int16);
    private static readonly CommandId[] AcceptedCommands = [new(SetpointRaiseLowerId)];
    private static readonly CommandId[] NoCommands = [];

    private readonly ThermostatFeature _features;
    private readonly AttributeStore _attributes;
    private readonly Attribute<short?> _localTemperature;
    private readonly Attribute<short>? _occupiedHeatingSetpoint;
    private readonly Attribute<short>? _occupiedCoolingSetpoint;
    private readonly Attribute<byte> _controlSequence;
    private readonly Attribute<byte> _systemMode;
    private readonly Attribute<byte>? _runningMode;
    private readonly short _minHeatSetpoint;
    private readonly short _maxHeatSetpoint;
    private readonly short _minCoolSetpoint;
    private readonly short _maxCoolSetpoint;
    private readonly short _deadBand; // hundredths of a degree, converted from the spec's tenths
    private readonly object _gate = new();

    /// <param name="features">The features to advertise; must include Heating, Cooling or both.</param>
    /// <param name="initialHeatingSetpoint">The initial OccupiedHeatingSetpoint in 0.01 C.</param>
    /// <param name="initialCoolingSetpoint">The initial OccupiedCoolingSetpoint in 0.01 C.</param>
    /// <param name="minHeatSetpoint">The heating setpoint lower bound in 0.01 C.</param>
    /// <param name="maxHeatSetpoint">The heating setpoint upper bound in 0.01 C.</param>
    /// <param name="minCoolSetpoint">The cooling setpoint lower bound in 0.01 C.</param>
    /// <param name="maxCoolSetpoint">The cooling setpoint upper bound in 0.01 C.</param>
    /// <param name="minSetpointDeadBandTenths">The MinSetpointDeadBand in 0.1 C (0..25); used only with AutoMode.</param>
    /// <param name="initialSystemMode">The initial SystemMode; must be supported by <paramref name="features"/>.</param>
    /// <param name="initialLocalTemperature">The initial LocalTemperature in 0.01 C, or <see langword="null"/> when unknown.</param>
    public ThermostatCluster(
        ThermostatFeature features = ThermostatFeature.Heating,
        short initialHeatingSetpoint = 2000,
        short initialCoolingSetpoint = 2600,
        short minHeatSetpoint = 700,
        short maxHeatSetpoint = 3000,
        short minCoolSetpoint = 1600,
        short maxCoolSetpoint = 3200,
        byte minSetpointDeadBandTenths = 25,
        ThermostatSystemMode initialSystemMode = ThermostatSystemMode.Off,
        short? initialLocalTemperature = null)
    {
        if ((features & (ThermostatFeature.Heating | ThermostatFeature.Cooling)) == 0)
        {
            throw new ArgumentException("A thermostat must support heating, cooling or both.", nameof(features));
        }

        if (minHeatSetpoint > maxHeatSetpoint)
        {
            throw new ArgumentOutOfRangeException(nameof(minHeatSetpoint), minHeatSetpoint, "The heating setpoint lower bound exceeds the upper bound.");
        }

        if (minCoolSetpoint > maxCoolSetpoint)
        {
            throw new ArgumentOutOfRangeException(nameof(minCoolSetpoint), minCoolSetpoint, "The cooling setpoint lower bound exceeds the upper bound.");
        }

        if (minSetpointDeadBandTenths > 25)
        {
            throw new ArgumentOutOfRangeException(nameof(minSetpointDeadBandTenths), minSetpointDeadBandTenths, "MinSetpointDeadBand is limited to 2.5 C (25 tenths).");
        }

        // AutoMode only makes sense when both plants are present; the spec conformance is AUTO & HEAT & COOL.
        if (features.HasFlag(ThermostatFeature.AutoMode) &&
            (features & (ThermostatFeature.Heating | ThermostatFeature.Cooling)) != (ThermostatFeature.Heating | ThermostatFeature.Cooling))
        {
            throw new ArgumentException("AutoMode requires both the Heating and Cooling features.", nameof(features));
        }

        _features = features;
        _minHeatSetpoint = minHeatSetpoint;
        _maxHeatSetpoint = maxHeatSetpoint;
        _minCoolSetpoint = minCoolSetpoint;
        _maxCoolSetpoint = maxCoolSetpoint;
        _deadBand = (short)(minSetpointDeadBandTenths * 10);

        if (!IsSupportedSystemMode((byte)initialSystemMode))
        {
            throw new ArgumentException($"System mode {initialSystemMode} is not supported by the requested features.", nameof(initialSystemMode));
        }

        _attributes = new AttributeStore(IncrementDataVersion);
        _localTemperature = _attributes.Add(new AttributeId(LocalTemperatureId), NullableInt16, initialLocalTemperature); // R V, device-driven

        if (SupportsHeating)
        {
            _attributes.Add(new AttributeId(AbsMinHeatSetpointLimitId), TlvCodec.Int16, minHeatSetpoint);
            _attributes.Add(new AttributeId(AbsMaxHeatSetpointLimitId), TlvCodec.Int16, maxHeatSetpoint);
            _attributes.Add(new AttributeId(MinHeatSetpointLimitId), TlvCodec.Int16, minHeatSetpoint);
            _attributes.Add(new AttributeId(MaxHeatSetpointLimitId), TlvCodec.Int16, maxHeatSetpoint);
            _occupiedHeatingSetpoint = _attributes.Add(
                new AttributeId(OccupiedHeatingSetpointId), TlvCodec.Int16, ClampHeating(initialHeatingSetpoint),
                writable: true, validate: v => v >= _minHeatSetpoint && v <= _maxHeatSetpoint);
        }

        if (SupportsCooling)
        {
            _attributes.Add(new AttributeId(AbsMinCoolSetpointLimitId), TlvCodec.Int16, minCoolSetpoint);
            _attributes.Add(new AttributeId(AbsMaxCoolSetpointLimitId), TlvCodec.Int16, maxCoolSetpoint);
            _attributes.Add(new AttributeId(MinCoolSetpointLimitId), TlvCodec.Int16, minCoolSetpoint);
            _attributes.Add(new AttributeId(MaxCoolSetpointLimitId), TlvCodec.Int16, maxCoolSetpoint);
            _occupiedCoolingSetpoint = _attributes.Add(
                new AttributeId(OccupiedCoolingSetpointId), TlvCodec.Int16, ClampCooling(initialCoolingSetpoint),
                writable: true, validate: v => v >= _minCoolSetpoint && v <= _maxCoolSetpoint);
        }

        if (SupportsAuto)
        {
            _attributes.Add(new AttributeId(MinSetpointDeadBandId), TlvCodec.Int8, (sbyte)minSetpointDeadBandTenths);
            _runningMode = _attributes.Add(new AttributeId(ThermostatRunningModeId), TlvCodec.UInt8, (byte)ThermostatRunningMode.Off);
        }

        _controlSequence = _attributes.Add(
            new AttributeId(ControlSequenceOfOperationId), TlvCodec.UInt8, (byte)DefaultControlSequence(features),
            writable: true, validate: v => v <= (byte)ThermostatControlSequence.CoolingAndHeatingWithReheat);
        _systemMode = _attributes.Add(
            new AttributeId(SystemModeId), TlvCodec.UInt8, (byte)initialSystemMode,
            writable: true, validate: IsSupportedSystemMode);

        UpdateRunningModeLocked();
    }

    /// <inheritdoc />
    public override ClusterId Id => ClusterId;

    /// <inheritdoc />
    /// <remarks>Revision 6 (Matter 1.1) definition, restricted to the HEAT/COOL/AUTO features.</remarks>
    public override ushort ClusterRevision => 6;

    /// <inheritdoc />
    public override uint FeatureMap => (uint)_features;

    /// <inheritdoc />
    public override IReadOnlyCollection<AttributeId> AttributeIds => _attributes.Ids;

    /// <inheritdoc />
    public override IReadOnlyCollection<CommandId> AcceptedCommandIds => AcceptedCommands;

    /// <inheritdoc />
    public override IReadOnlyCollection<CommandId> GeneratedCommandIds => NoCommands;

    /// <summary>Raised whenever OccupiedHeatingSetpoint changes, so the host can drive the heating plant. Raised outside the internal lock.</summary>
    public event EventHandler? OccupiedHeatingSetpointChanged;

    /// <summary>Raised whenever OccupiedCoolingSetpoint changes. Raised outside the internal lock.</summary>
    public event EventHandler? OccupiedCoolingSetpointChanged;

    /// <summary>Raised whenever SystemMode changes. Raised outside the internal lock.</summary>
    public event EventHandler? SystemModeChanged;

    /// <summary>Whether the thermostat advertises the Heating feature.</summary>
    public bool SupportsHeating => _features.HasFlag(ThermostatFeature.Heating);

    /// <summary>Whether the thermostat advertises the Cooling feature.</summary>
    public bool SupportsCooling => _features.HasFlag(ThermostatFeature.Cooling);

    /// <summary>Whether the thermostat advertises the AutoMode feature.</summary>
    public bool SupportsAuto => _features.HasFlag(ThermostatFeature.AutoMode);

    /// <summary>The measured temperature in 0.01 C, or <see langword="null"/> when it is unknown.</summary>
    public short? LocalTemperature => _localTemperature.Value;

    /// <summary>The heating setpoint in 0.01 C; 0 when the thermostat does not support heating.</summary>
    public short OccupiedHeatingSetpoint => _occupiedHeatingSetpoint?.Value ?? 0;

    /// <summary>The cooling setpoint in 0.01 C; 0 when the thermostat does not support cooling.</summary>
    public short OccupiedCoolingSetpoint => _occupiedCoolingSetpoint?.Value ?? 0;

    /// <summary>The requested operating mode.</summary>
    public ThermostatSystemMode SystemMode => (ThermostatSystemMode)_systemMode.Value;

    /// <summary>The mode actually running, derived from <see cref="SystemMode"/>; Off unless AutoMode is supported.</summary>
    public ThermostatRunningMode RunningMode => (ThermostatRunningMode)(_runningMode?.Value ?? (byte)ThermostatRunningMode.Off);

    /// <summary>The heating/cooling capability reported through ControlSequenceOfOperation.</summary>
    public ThermostatControlSequence ControlSequenceOfOperation => (ThermostatControlSequence)_controlSequence.Value;

    /// <summary>The MinSetpointDeadBand in 0.01 C, i.e. the gap AutoMode keeps between the two setpoints.</summary>
    public short MinSetpointDeadBand => _deadBand;

    /// <summary>Reports the measured temperature in 0.01 C from device logic; <see langword="null"/> marks it unknown.</summary>
    public void SetLocalTemperature(short? hundredthsCelsius)
    {
        lock (_gate)
        {
            _localTemperature.Value = hundredthsCelsius;
            UpdateRunningModeLocked();
        }
    }

    /// <summary>
    /// Sets OccupiedHeatingSetpoint from device logic (e.g. the physical thermostat being turned by hand).
    /// The value is clamped into the setpoint limits and, under AutoMode, pushes the cooling setpoint up to
    /// preserve the dead band.
    /// </summary>
    /// <exception cref="InvalidOperationException">The thermostat does not support heating.</exception>
    public void SetOccupiedHeatingSetpoint(short hundredthsCelsius)
    {
        if (_occupiedHeatingSetpoint is null)
        {
            throw new InvalidOperationException("This thermostat does not support heating.");
        }

        bool heatingChanged, coolingChanged;
        lock (_gate)
        {
            heatingChanged = ApplyHeatingLocked(hundredthsCelsius);
            coolingChanged = EnforceDeadBandLocked(heatingLeads: true);
            UpdateRunningModeLocked();
        }

        RaiseChanges(heatingChanged, coolingChanged, systemModeChanged: false);
    }

    /// <summary>
    /// Sets OccupiedCoolingSetpoint from device logic. The value is clamped into the setpoint limits and,
    /// under AutoMode, pushes the heating setpoint down to preserve the dead band.
    /// </summary>
    /// <exception cref="InvalidOperationException">The thermostat does not support cooling.</exception>
    public void SetOccupiedCoolingSetpoint(short hundredthsCelsius)
    {
        if (_occupiedCoolingSetpoint is null)
        {
            throw new InvalidOperationException("This thermostat does not support cooling.");
        }

        bool coolingChanged, heatingChanged;
        lock (_gate)
        {
            coolingChanged = ApplyCoolingLocked(hundredthsCelsius);
            heatingChanged = EnforceDeadBandLocked(heatingLeads: false);
            UpdateRunningModeLocked();
        }

        RaiseChanges(heatingChanged, coolingChanged, systemModeChanged: false);
    }

    /// <summary>Sets SystemMode from device logic.</summary>
    /// <exception cref="ArgumentException">The mode is not supported by the advertised features.</exception>
    public void SetSystemMode(ThermostatSystemMode mode)
    {
        if (!IsSupportedSystemMode((byte)mode))
        {
            throw new ArgumentException($"System mode {mode} is not supported by this thermostat.", nameof(mode));
        }

        bool changed;
        lock (_gate)
        {
            changed = _systemMode.Value != (byte)mode;
            _systemMode.Value = (byte)mode;
            UpdateRunningModeLocked();
        }

        RaiseChanges(heatingChanged: false, coolingChanged: false, systemModeChanged: changed);
    }

    /// <inheritdoc />
    protected override ValueTask<InteractionModelStatusCode> ReadAttributeCoreAsync(
        AttributeId attributeId, TlvWriter writer, TlvTag tag, InteractionContext context, CancellationToken cancellationToken)
        => new(_attributes.TryRead(attributeId, writer, tag)
            ? InteractionModelStatusCode.Success
            : InteractionModelStatusCode.UnsupportedAttribute);

    /// <inheritdoc />
    /// <remarks>
    /// Writes land in the attribute store first, so per-attribute range checks run there; the dead band is
    /// then re-established across the two setpoints, which the store cannot see on its own.
    /// </remarks>
    protected override ValueTask<InteractionModelStatusCode> WriteAttributeCoreAsync(
        AttributeId attributeId, ReadOnlyMemory<byte> value, InteractionContext context, CancellationToken cancellationToken)
    {
        bool heatingChanged = false, coolingChanged = false, systemModeChanged = false;
        InteractionModelStatusCode status;

        lock (_gate)
        {
            var previousHeating = _occupiedHeatingSetpoint?.Value;
            var previousCooling = _occupiedCoolingSetpoint?.Value;
            var previousMode = _systemMode.Value;

            status = _attributes.Write(attributeId, value);
            if (status == InteractionModelStatusCode.Success)
            {
                if (attributeId.Value == OccupiedHeatingSetpointId) { EnforceDeadBandLocked(heatingLeads: true); }
                else if (attributeId.Value == OccupiedCoolingSetpointId) { EnforceDeadBandLocked(heatingLeads: false); }

                UpdateRunningModeLocked();
                heatingChanged = _occupiedHeatingSetpoint?.Value != previousHeating;
                coolingChanged = _occupiedCoolingSetpoint?.Value != previousCooling;
                systemModeChanged = _systemMode.Value != previousMode;
            }
        }

        RaiseChanges(heatingChanged, coolingChanged, systemModeChanged);
        return new ValueTask<InteractionModelStatusCode>(status);
    }

    /// <inheritdoc />
    protected override ValueTask<CommandResponse> InvokeCommandCoreAsync(
        CommandId commandId, ReadOnlyMemory<byte> fields, InteractionContext context, CancellationToken cancellationToken)
        => commandId.Value switch
        {
            SetpointRaiseLowerId => CommandCodec.Invoke(fields, ExecuteSetpointRaiseLower),
            _ => new ValueTask<CommandResponse>(CommandResponse.FromStatus(InteractionModelStatusCode.UnsupportedCommand)),
        };

    private CommandResponse ExecuteSetpointRaiseLower(CommandFields fields)
    {
        var mode = (SetpointRaiseLowerMode)fields.GetRequired(0, TlvCodec.UInt8, v => v <= 2);
        var amountTenths = fields.GetRequired(1, TlvCodec.Int8); // signed tenths of a degree (spec 4.3.9.1)

        var adjustsHeating = mode is SetpointRaiseLowerMode.Heat or SetpointRaiseLowerMode.Both;
        var adjustsCooling = mode is SetpointRaiseLowerMode.Cool or SetpointRaiseLowerMode.Both;

        // Asking to move a setpoint the thermostat does not have is a constraint error, not a silent no-op.
        if ((adjustsHeating && _occupiedHeatingSetpoint is null && mode == SetpointRaiseLowerMode.Heat) ||
            (adjustsCooling && _occupiedCoolingSetpoint is null && mode == SetpointRaiseLowerMode.Cool))
        {
            return CommandResponse.FromStatus(InteractionModelStatusCode.InvalidCommand);
        }

        var delta = amountTenths * 10;
        bool heatingChanged = false, coolingChanged = false;
        lock (_gate)
        {
            if (adjustsHeating && _occupiedHeatingSetpoint is { } heating)
            {
                heatingChanged = ApplyHeatingLocked(Saturate(heating.Value + delta));
            }

            if (adjustsCooling && _occupiedCoolingSetpoint is { } cooling)
            {
                coolingChanged = ApplyCoolingLocked(Saturate(cooling.Value + delta));
            }

            // Both setpoints moved together keep their gap, so only a single-sided change can break it.
            if (mode != SetpointRaiseLowerMode.Both)
            {
                if (adjustsHeating) { coolingChanged |= EnforceDeadBandLocked(heatingLeads: true); }
                else { heatingChanged |= EnforceDeadBandLocked(heatingLeads: false); }
            }

            UpdateRunningModeLocked();
        }

        RaiseChanges(heatingChanged, coolingChanged, systemModeChanged: false);
        return CommandResponse.Success();
    }

    private bool ApplyHeatingLocked(short value)
    {
        if (_occupiedHeatingSetpoint is not { } heating)
        {
            return false;
        }

        var clamped = ClampHeating(value);
        var changed = heating.Value != clamped;
        heating.Value = clamped;
        return changed;
    }

    private bool ApplyCoolingLocked(short value)
    {
        if (_occupiedCoolingSetpoint is not { } cooling)
        {
            return false;
        }

        var clamped = ClampCooling(value);
        var changed = cooling.Value != clamped;
        cooling.Value = clamped;
        return changed;
    }

    /// <summary>
    /// Restores the AutoMode dead band by moving the setpoint that did not just change; returns whether that
    /// other setpoint moved. Without AutoMode the two setpoints are independent and this does nothing.
    /// </summary>
    private bool EnforceDeadBandLocked(bool heatingLeads)
    {
        if (!SupportsAuto || _occupiedHeatingSetpoint is not { } heating || _occupiedCoolingSetpoint is not { } cooling)
        {
            return false;
        }

        if (cooling.Value - heating.Value >= _deadBand)
        {
            return false;
        }

        return heatingLeads
            ? ApplyCoolingLocked(Saturate(heating.Value + _deadBand))
            : ApplyHeatingLocked(Saturate(cooling.Value - _deadBand));
    }

    /// <summary>
    /// Derives ThermostatRunningMode from SystemMode: an explicit heat/cool mode maps straight through, and
    /// Auto is resolved by comparing LocalTemperature against the setpoints. Only meaningful with AutoMode,
    /// which is the only configuration where the attribute exists.
    /// </summary>
    private void UpdateRunningModeLocked()
    {
        if (_runningMode is not { } runningMode)
        {
            return;
        }

        var mode = (ThermostatSystemMode)_systemMode.Value;
        var running = mode switch
        {
            ThermostatSystemMode.Heat or ThermostatSystemMode.EmergencyHeat => ThermostatRunningMode.Heat,
            ThermostatSystemMode.Cool or ThermostatSystemMode.Precooling => ThermostatRunningMode.Cool,
            ThermostatSystemMode.Auto => ResolveAutoRunningMode((ThermostatRunningMode)runningMode.Value),
            _ => ThermostatRunningMode.Off,
        };

        runningMode.Value = (byte)running;
    }

    private ThermostatRunningMode ResolveAutoRunningMode(ThermostatRunningMode current)
    {
        if (_localTemperature.Value is not { } temperature ||
            _occupiedHeatingSetpoint is not { } heating ||
            _occupiedCoolingSetpoint is not { } cooling)
        {
            return ThermostatRunningMode.Off;
        }

        if (temperature < heating.Value) { return ThermostatRunningMode.Heat; }
        if (temperature > cooling.Value) { return ThermostatRunningMode.Cool; }

        // Inside the dead band: hold whatever was running rather than chattering between the two plants.
        return current;
    }

    private void RaiseChanges(bool heatingChanged, bool coolingChanged, bool systemModeChanged)
    {
        if (heatingChanged) { OccupiedHeatingSetpointChanged?.Invoke(this, EventArgs.Empty); }
        if (coolingChanged) { OccupiedCoolingSetpointChanged?.Invoke(this, EventArgs.Empty); }
        if (systemModeChanged) { SystemModeChanged?.Invoke(this, EventArgs.Empty); }
    }

    private bool IsSupportedSystemMode(byte mode) => (ThermostatSystemMode)mode switch
    {
        ThermostatSystemMode.Off or ThermostatSystemMode.FanOnly or ThermostatSystemMode.Dry or ThermostatSystemMode.Sleep => true,
        ThermostatSystemMode.Heat or ThermostatSystemMode.EmergencyHeat => SupportsHeating,
        ThermostatSystemMode.Cool or ThermostatSystemMode.Precooling => SupportsCooling,
        ThermostatSystemMode.Auto => SupportsAuto,
        _ => false,
    };

    private short ClampHeating(short value) => Math.Clamp(value, _minHeatSetpoint, _maxHeatSetpoint);

    private short ClampCooling(short value) => Math.Clamp(value, _minCoolSetpoint, _maxCoolSetpoint);

    private static short Saturate(int hundredths) => (short)Math.Clamp(hundredths, short.MinValue, short.MaxValue);

    private static ThermostatControlSequence DefaultControlSequence(ThermostatFeature features)
    {
        var heating = features.HasFlag(ThermostatFeature.Heating);
        var cooling = features.HasFlag(ThermostatFeature.Cooling);
        return heating && cooling
            ? ThermostatControlSequence.CoolingAndHeating
            : heating ? ThermostatControlSequence.HeatingOnly : ThermostatControlSequence.CoolingOnly;
    }
}
