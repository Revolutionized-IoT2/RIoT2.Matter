using System;
using System.Threading;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.Device;
using RIoT2.Matter.InteractionModel;
using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.Clusters;

/// <summary>
/// The Level Control cluster (0x0008) on an application endpoint: exposes the device-driven
/// CurrentLevel, the live RemainingTime, the fixed MinLevel/MaxLevel bounds, and the writable
/// OnLevel/Options, and implements MoveToLevel/Move/Step/Stop plus their WithOnOff variants. Timed
/// transitions are driven one tick at a time from an injected <see cref="TimeProvider"/> (the node
/// time source); an optional <see cref="IOnOffCoupling"/> couples the level to the same endpoint's
/// On/Off (0x0006) cluster. See the Matter Core Specification, section 1.6.
/// </summary>
/// <remarks>
/// Add to a lighting endpoint, coupling it to the On/Off cluster in both directions:
/// <code>
/// var onOff = new OnOffCluster();
/// var level = new LevelControlCluster(coupling: new OnOffCouplingAdapter(onOff));
/// onOff.OnOffChanged += (_, _) => level.NotifyOnOffChanged();     // On/Off -> Level (OnLevel restore)
/// endpoint.AddCluster(onOff).AddCluster(level);
/// level.CurrentLevelChanged += (_, _) => dimmer.Set(level.CurrentLevel);
/// </code>
/// The Lighting (LT) and Frequency (FQ) features, StartUpCurrentLevel, and the *TransitionTime
/// attributes are deferred, so <see cref="FeatureMap"/> is 0 and a null command TransitionTime/Rate
/// resolves to an instantaneous change. Dispose the cluster to release the transition timer.
/// </remarks>
public sealed class LevelControlCluster : Cluster, IDisposable
{
    /// <summary>The Level Control cluster identifier (0x0008).</summary>
    public static readonly ClusterId ClusterId = new(0x0008);

    // Attribute ids (spec �1.6.5). The LT/FQ-feature and *TransitionTime attributes are deferred.
    private const uint CurrentLevelId = 0x0000;
    private const uint RemainingTimeId = 0x0001;
    private const uint MinLevelId = 0x0002;
    private const uint MaxLevelId = 0x0003;
    private const uint OptionsId = 0x000F;
    private const uint OnLevelId = 0x0011;

    // Command ids (spec �1.6.6).
    private const uint MoveToLevelId = 0x00;
    private const uint MoveId = 0x01;
    private const uint StepId = 0x02;
    private const uint StopId = 0x03;
    private const uint MoveToLevelWithOnOffId = 0x04;
    private const uint MoveWithOnOffId = 0x05;
    private const uint StepWithOnOffId = 0x06;
    private const uint StopWithOnOffId = 0x07;

    private const byte MaxLevelCeiling = 254; // 255 is reserved / undefined for level (spec �1.6.5.1).

    // RemainingTime and the transition step run in tenths of a second (spec �1.6.5.2).
    private static readonly TimeSpan TransitionTick = TimeSpan.FromMilliseconds(100);
    private static readonly TlvCodec<ushort?> NullableUInt16 = TlvCodec.Nullable(TlvCodec.UInt16);
    private static readonly TlvCodec<byte?> NullableUInt8 = TlvCodec.Nullable(TlvCodec.UInt8);

    private static readonly AttributeId[] AttributeIdList =
    [
        new(CurrentLevelId), new(RemainingTimeId), new(MinLevelId),
        new(MaxLevelId), new(OptionsId), new(OnLevelId),
    ];

    private static readonly CommandId[] AcceptedCommands =
    [
        new(MoveToLevelId), new(MoveId), new(StepId), new(StopId),
        new(MoveToLevelWithOnOffId), new(MoveWithOnOffId), new(StepWithOnOffId), new(StopWithOnOffId),
    ];

    private readonly byte _minLevel;
    private readonly byte _maxLevel;
    private readonly byte? _defaultMoveRate;
    private readonly IOnOffCoupling? _coupling;
    private readonly TimeProvider _timeProvider;
    private readonly AttributeStore _attributes;
    private readonly Attribute<byte> _currentLevel;
    private readonly Attribute<ushort> _remainingTime;
    private readonly Attribute<byte?> _onLevel;
    private readonly Attribute<byte> _options;
    private readonly object _gate = new();

    private ITimer? _timer;
    private bool _transitionActive;
    private int _transStart;
    private int _transEnd;
    private int _transDuration; // tenths of a second
    private int _transElapsed;  // tenths of a second
    private bool _transTurnOff; // set OnOff false when this transition completes
    private bool _couplingLastOn;
    private volatile bool _selfDrivingOnOff;
    private bool _disposed;

    /// <param name="minLevel">The MinLevel bound (0..254).</param>
    /// <param name="maxLevel">The MaxLevel bound (<paramref name="minLevel"/>..254).</param>
    /// <param name="initialLevel">The initial CurrentLevel (clamped into the bounds).</param>
    /// <param name="initialOnLevel">The initial OnLevel restored on turn-on; <see langword="null"/> disables the restore.</param>
    /// <param name="initialOptions">The initial Options bitmap.</param>
    /// <param name="defaultMoveRate">The DefaultMoveRate (units/second) used when a Move Rate is null; <see langword="null"/> means instantaneous.</param>
    /// <param name="coupling">The On/Off coupling for ExecuteIfOff and the WithOnOff variants; <see langword="null"/> for a standalone dimmer.</param>
    /// <param name="timeProvider">The clock driving transitions; defaults to <see cref="TimeProvider.System"/>.</param>
    public LevelControlCluster(
        byte minLevel = 1,
        byte maxLevel = MaxLevelCeiling,
        byte initialLevel = MaxLevelCeiling,
        byte? initialOnLevel = null,
        LevelControlOptions initialOptions = LevelControlOptions.None,
        byte? defaultMoveRate = null,
        IOnOffCoupling? coupling = null,
        TimeProvider? timeProvider = null)
    {
        if (maxLevel > MaxLevelCeiling)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLevel), maxLevel, $"MaxLevel must be at most {MaxLevelCeiling}.");
        }

        if (minLevel > maxLevel)
        {
            throw new ArgumentOutOfRangeException(nameof(minLevel), minLevel, "MinLevel must not exceed MaxLevel.");
        }

        _minLevel = minLevel;
        _maxLevel = maxLevel;
        _defaultMoveRate = defaultMoveRate;
        _coupling = coupling;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _couplingLastOn = coupling?.IsOn ?? false;

        var startLevel = Clamp(initialLevel);
        _attributes = new AttributeStore(IncrementDataVersion);
        _currentLevel = _attributes.Add(new AttributeId(CurrentLevelId), TlvCodec.UInt8, (byte)startLevel);           // R V, device-driven
        _remainingTime = _attributes.Add(new AttributeId(RemainingTimeId), TlvCodec.UInt16, initialValue: (ushort)0); // R V, device-driven
        _options = _attributes.Add(new AttributeId(OptionsId), TlvCodec.UInt8, (byte)initialOptions, writable: true);
        _onLevel = _attributes.Add(
            new AttributeId(OnLevelId), NullableUInt8, initialOnLevel, writable: true,
            validate: v => v is null || (v.Value >= _minLevel && v.Value <= _maxLevel));
    }

    /// <inheritdoc />
    public override ClusterId Id => ClusterId;

    /// <inheritdoc />
    /// <remarks>Revision 5 (Matter 1.2) command/attribute subset; the LT/FQ features and StartUpCurrentLevel are deferred (FeatureMap 0).</remarks>
    public override ushort ClusterRevision => 5;

    /// <inheritdoc />
    public override IReadOnlyCollection<AttributeId> AttributeIds => AttributeIdList;

    /// <inheritdoc />
    public override IReadOnlyCollection<CommandId> AcceptedCommandIds => AcceptedCommands;

    /// <summary>Raised whenever CurrentLevel changes (by command, transition tick, or device logic), so the host can drive the physical output. Raised outside the internal lock.</summary>
    public event EventHandler? CurrentLevelChanged;

    /// <summary>The current level (MinLevel..MaxLevel).</summary>
    public byte CurrentLevel => _currentLevel.Value;

    /// <summary>The time remaining in the active transition, in tenths of a second; 0 when idle.</summary>
    public ushort RemainingTime => _remainingTime.Value;

    /// <summary>The MinLevel bound.</summary>
    public byte MinLevel => _minLevel;

    /// <summary>The MaxLevel bound.</summary>
    public byte MaxLevel => _maxLevel;

    /// <summary>The level restored on turn-on; <see langword="null"/> disables the restore.</summary>
    public byte? OnLevel => _onLevel.Value;

    /// <summary>The current Options bitmap.</summary>
    public LevelControlOptions Options => (LevelControlOptions)_options.Value;

    /// <summary>Sets CurrentLevel from device logic (e.g. a physical dimmer), cancelling any active transition.</summary>
    public void SetCurrentLevel(byte level)
    {
        var clamped = Clamp(level);
        bool changed;
        lock (_gate)
        {
            CancelTransitionLocked();
            changed = _currentLevel.Value != clamped;
            _currentLevel.Value = (byte)clamped;
            _remainingTime.Value = 0;
        }

        if (changed)
        {
            CurrentLevelChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Notifies the cluster that the coupled On/Off state changed. Wire this to the On/Off cluster's
    /// change notification: on an off-to-on edge that the cluster did not drive itself, CurrentLevel is
    /// restored to OnLevel (when set). See the Matter Core Specification, section 1.6.4.1.1.
    /// </summary>
    public void NotifyOnOffChanged()
    {
        if (_coupling is not { } coupling || _selfDrivingOnOff)
        {
            return; // no coupling, or our own WithOnOff side effect (which sets its own target level).
        }

        bool rising;
        var isOn = coupling.IsOn;
        lock (_gate)
        {
            rising = isOn && !_couplingLastOn;
            _couplingLastOn = isOn;
        }

        // On turn-on, restore to OnLevel when configured; the null-OnLevel restore-to-previous behavior
        // (StartUpCurrentLevel / stored level) is deferred.
        if (rising && _onLevel.Value is byte onLevel)
        {
            SetCurrentLevel(onLevel);
        }
    }

    /// <inheritdoc />
    protected override ValueTask<InteractionModelStatusCode> ReadAttributeCoreAsync(
        AttributeId attributeId, TlvWriter writer, TlvTag tag, InteractionContext context, CancellationToken cancellationToken)
    {
        if (_attributes.TryRead(attributeId, writer, tag))
        {
            return new ValueTask<InteractionModelStatusCode>(InteractionModelStatusCode.Success);
        }

        switch (attributeId.Value)
        {
            case MinLevelId:
                writer.WriteUnsignedInteger(tag, _minLevel);
                break;
            case MaxLevelId:
                writer.WriteUnsignedInteger(tag, _maxLevel);
                break;
            default:
                return new ValueTask<InteractionModelStatusCode>(InteractionModelStatusCode.UnsupportedAttribute);
        }

        return new ValueTask<InteractionModelStatusCode>(InteractionModelStatusCode.Success);
    }

    /// <inheritdoc />
    protected override ValueTask<InteractionModelStatusCode> WriteAttributeCoreAsync(
        AttributeId attributeId, ReadOnlyMemory<byte> value, InteractionContext context, CancellationToken cancellationToken)
        => new(attributeId.Value switch
        {
            MinLevelId or MaxLevelId => InteractionModelStatusCode.UnsupportedWrite, // fixed bounds
            _ => _attributes.Write(attributeId, value),                              // Options/OnLevel writable; CurrentLevel/RemainingTime read-only
        });

    /// <inheritdoc />
    protected override ValueTask<CommandResponse> InvokeCommandCoreAsync(
        CommandId commandId, ReadOnlyMemory<byte> fields, InteractionContext context, CancellationToken cancellationToken)
        => commandId.Value switch
        {
            MoveToLevelId => CommandCodec.Invoke(fields, f => ExecuteMoveToLevel(f, withOnOff: false)),
            MoveToLevelWithOnOffId => CommandCodec.Invoke(fields, f => ExecuteMoveToLevel(f, withOnOff: true)),
            MoveId => CommandCodec.Invoke(fields, f => ExecuteMove(f, withOnOff: false)),
            MoveWithOnOffId => CommandCodec.Invoke(fields, f => ExecuteMove(f, withOnOff: true)),
            StepId => CommandCodec.Invoke(fields, f => ExecuteStep(f, withOnOff: false)),
            StepWithOnOffId => CommandCodec.Invoke(fields, f => ExecuteStep(f, withOnOff: true)),
            StopId => CommandCodec.Invoke(fields, f => ExecuteStop(f, withOnOff: false)),
            StopWithOnOffId => CommandCodec.Invoke(fields, f => ExecuteStop(f, withOnOff: true)),
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

    private CommandResponse ExecuteMoveToLevel(CommandFields fields, bool withOnOff)
    {
        var level = fields.GetRequired(0, TlvCodec.UInt8);
        var transitionTime = fields.GetOptional(1, NullableUInt16, fallback: (ushort?)null);
        var (mask, over) = ReadOptions(fields, maskField: 2, overrideField: 3);

        if (!withOnOff && !ShouldExecuteWhenOff(mask, over))
        {
            return CommandResponse.Success(); // gated off: no effect (spec �1.6.6.1.2).
        }

        var end = Clamp(level);
        if (withOnOff && end > _minLevel)
        {
            DriveOnOff(true); // turn on before ramping to a non-minimum level (spec �1.6.6.1.5).
        }

        ApplyTransition(end, transitionTime ?? 0, turnOffAtEnd: withOnOff && end <= _minLevel);
        return CommandResponse.Success();
    }

    private CommandResponse ExecuteMove(CommandFields fields, bool withOnOff)
    {
        var mode = (LevelMoveMode)fields.GetRequired(0, TlvCodec.UInt8, v => v is 0 or 1);
        var rate = fields.GetOptional(1, NullableUInt8, fallback: (byte?)null);
        var (mask, over) = ReadOptions(fields, maskField: 2, overrideField: 3);

        if (!withOnOff && !ShouldExecuteWhenOff(mask, over))
        {
            return CommandResponse.Success();
        }

        var up = mode == LevelMoveMode.Up;
        if (withOnOff && up)
        {
            DriveOnOff(true);
        }

        var end = up ? _maxLevel : _minLevel;
        ApplyMove(end, rate, turnOffAtEnd: withOnOff && !up);
        return CommandResponse.Success();
    }

    private CommandResponse ExecuteStep(CommandFields fields, bool withOnOff)
    {
        var mode = (LevelMoveMode)fields.GetRequired(0, TlvCodec.UInt8, v => v is 0 or 1);
        var stepSize = fields.GetRequired(1, TlvCodec.UInt8);
        var transitionTime = fields.GetOptional(2, NullableUInt16, fallback: (ushort?)null);
        var (mask, over) = ReadOptions(fields, maskField: 3, overrideField: 4);

        if (!withOnOff && !ShouldExecuteWhenOff(mask, over))
        {
            return CommandResponse.Success();
        }

        var up = mode == LevelMoveMode.Up;
        if (withOnOff && up)
        {
            DriveOnOff(true);
        }

        ApplyStep(up, stepSize, transitionTime ?? 0, withOnOff);
        return CommandResponse.Success();
    }

    private CommandResponse ExecuteStop(CommandFields fields, bool withOnOff)
    {
        var (mask, over) = ReadOptions(fields, maskField: 0, overrideField: 1);
        if (!withOnOff && !ShouldExecuteWhenOff(mask, over))
        {
            return CommandResponse.Success();
        }

        lock (_gate)
        {
            CancelTransitionLocked();
            _remainingTime.Value = 0;
        }

        return CommandResponse.Success();
    }

    private void ApplyTransition(int end, int durationTenths, bool turnOffAtEnd)
    {
        bool levelChanged, turnOffNow;
        lock (_gate)
        {
            (levelChanged, turnOffNow) = BeginTransitionLocked(end, durationTenths, turnOffAtEnd);
        }

        RunTransitionSideEffects(levelChanged, turnOffNow);
    }

    private void ApplyMove(int end, byte? rate, bool turnOffAtEnd)
    {
        bool levelChanged, turnOffNow;
        lock (_gate)
        {
            var current = _currentLevel.Value;
            var effectiveRate = rate ?? _defaultMoveRate;
            // Rate is units/second; RemainingTime is tenths of a second. A null/zero rate is instantaneous.
            var duration = effectiveRate is byte r && r > 0 ? Math.Abs(end - current) * 10 / r : 0;
            (levelChanged, turnOffNow) = BeginTransitionLocked(end, duration, turnOffAtEnd);
        }

        RunTransitionSideEffects(levelChanged, turnOffNow);
    }

    private void ApplyStep(bool up, byte stepSize, int durationTenths, bool withOnOff)
    {
        bool levelChanged, turnOffNow;
        lock (_gate)
        {
            var current = _currentLevel.Value;
            var end = Clamp(up ? current + stepSize : current - stepSize);
            var turnOffAtEnd = withOnOff && !up && end <= _minLevel;
            (levelChanged, turnOffNow) = BeginTransitionLocked(end, durationTenths, turnOffAtEnd);
        }

        RunTransitionSideEffects(levelChanged, turnOffNow);
    }

    // Starts a transition toward end over durationTenths; an instantaneous transition applies at once.
    // Returns whether the level changed synchronously and whether an immediate turn-off is required.
    private (bool LevelChanged, bool TurnOffNow) BeginTransitionLocked(int end, int durationTenths, bool turnOffAtEnd)
    {
        var start = _currentLevel.Value;
        CancelTransitionLocked();

        if (durationTenths <= 0)
        {
            var changed = start != end;
            _currentLevel.Value = (byte)end;
            _remainingTime.Value = 0;
            return (changed, turnOffAtEnd);
        }

        _transStart = start;
        _transEnd = end;
        _transDuration = durationTenths;
        _transElapsed = 0;
        _transTurnOff = turnOffAtEnd;
        _transitionActive = true;
        _remainingTime.Value = (ushort)Math.Min(durationTenths, ushort.MaxValue);
        ScheduleTickLocked();
        return (false, false); // start == current: the first tick makes the first visible change.
    }

    private void OnTransitionTick(object? state)
    {
        bool levelChanged, turnOff = false;
        lock (_gate)
        {
            if (_disposed || !_transitionActive)
            {
                return;
            }

            _transElapsed++;
            var done = _transElapsed >= _transDuration;
            var newLevel = Clamp(done
                ? _transEnd
                : _transStart + (_transEnd - _transStart) * _transElapsed / _transDuration);

            levelChanged = newLevel != _currentLevel.Value;
            _currentLevel.Value = (byte)newLevel;

            if (done)
            {
                _transitionActive = false;
                _remainingTime.Value = 0;
                StopTimerLocked();
                turnOff = _transTurnOff;
            }
            else
            {
                _remainingTime.Value = (ushort)(_transDuration - _transElapsed);
                ScheduleTickLocked();
            }
        }

        RunTransitionSideEffects(levelChanged, turnOff);
    }

    private void RunTransitionSideEffects(bool levelChanged, bool turnOffNow)
    {
        if (levelChanged)
        {
            CurrentLevelChanged?.Invoke(this, EventArgs.Empty);
        }

        if (turnOffNow)
        {
            DriveOnOff(false); // turn off after reaching the minimum level (spec �1.6.6.1.5).
        }
    }

    // Drives the coupled On/Off state, suppressing the reverse OnLevel-restore for our own change.
    private void DriveOnOff(bool on)
    {
        if (_coupling is not { } coupling)
        {
            return;
        }

        _selfDrivingOnOff = true;
        try
        {
            coupling.SetOnOff(on);
        }
        finally
        {
            _selfDrivingOnOff = false;
        }

        lock (_gate)
        {
            _couplingLastOn = on;
        }
    }

    // The effective ExecuteIfOff gate for the non-WithOnOff commands (spec �1.6.6.1.2).
    private bool ShouldExecuteWhenOff(byte optionsMask, byte optionsOverride)
    {
        if (_coupling is not { } coupling || coupling.IsOn)
        {
            return true;
        }

        var effective = (byte)((_options.Value & ~optionsMask) | (optionsOverride & optionsMask));
        return (effective & (byte)LevelControlOptions.ExecuteIfOff) != 0;
    }

    private void ScheduleTickLocked()
    {
        _timer ??= _timeProvider.CreateTimer(OnTransitionTick, state: null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _timer.Change(TransitionTick, Timeout.InfiniteTimeSpan);
    }

    private void CancelTransitionLocked()
    {
        _transitionActive = false;
        StopTimerLocked();
    }

    private void StopTimerLocked() => _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

    private int Clamp(int level) => level < _minLevel ? _minLevel : level > _maxLevel ? _maxLevel : level;

    private static (byte Mask, byte Override) ReadOptions(CommandFields fields, byte maskField, byte overrideField) =>
        (fields.GetOptional(maskField, TlvCodec.UInt8, fallback: (byte)0),
         fields.GetOptional(overrideField, TlvCodec.UInt8, fallback: (byte)0));
}