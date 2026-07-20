using RIoT2.Matter.DataModel;
using RIoT2.Matter.Device;
using RIoT2.Matter.InteractionModel;
using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.Clusters;

/// <summary>
/// The Identify cluster (0x0003) on an application endpoint: exposes the writable IdentifyTime
/// countdown and the fixed IdentifyType, and implements the Identify and TriggerEffect commands.
/// While IdentifyTime is non-zero the cluster decrements it once per second from an injected
/// <see cref="TimeProvider"/> (the node time source), notifying subscriptions and raising
/// <see cref="IdentifyStarted"/> / <see cref="IdentifyStopped"/> so the host can drive the physical
/// identification effect. See the Matter Core Specification, section 1.2.
/// </summary>
/// <remarks>
/// Add to a lighting (or other application) endpoint and wire the host effect:
/// <code>
/// var identify = new IdentifyCluster(IdentifyType.LightOutput);
/// endpoint.AddCluster(identify);
/// identify.IdentifyStarted += (_, _) => led.StartBlinking();
/// identify.IdentifyStopped += (_, _) => led.Stop();
/// identify.EffectTriggered += (_, e) => led.Play(e.Effect, e.Variant);
/// </code>
/// The optional QRY (Query) feature � the deprecated IdentifyQuery/IdentifyQueryResponse multicast
/// probe � is deferred. Dispose the cluster to release the countdown timer.
/// </remarks>
public sealed class IdentifyCluster : Cluster, IDisposable
{
    /// <summary>The Identify cluster identifier (0x0003).</summary>
    public static readonly ClusterId ClusterId = new(0x0003);

    // Attribute ids (spec �1.2.5).
    private const uint IdentifyTimeId = 0x0000;
    private const uint IdentifyTypeId = 0x0001;

    // Command ids (spec �1.2.6). IdentifyQuery (0x01, QRY feature) is deferred.
    private const uint IdentifyCommandId = 0x00;
    private const uint TriggerEffectCommandId = 0x40;

    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

    private static readonly AttributeId[] AttributeIdList =
    [
        new(IdentifyTimeId), new(IdentifyTypeId),
    ];

    private static readonly CommandId[] AcceptedCommands =
    [
        new(IdentifyCommandId), new(TriggerEffectCommandId),
    ];

    private readonly IdentifyType _identifyType;
    private readonly TimeProvider _timeProvider;
    private readonly AttributeStore _attributes;
    private readonly Attribute<ushort> _identifyTime;
    private readonly object _gate = new();

    private ITimer? _timer;
    private bool _disposed;

    /// <param name="identifyType">The way this device identifies itself (IdentifyType attribute).</param>
    /// <param name="initialIdentifyTime">The initial IdentifyTime; when non-zero the countdown starts immediately.</param>
    /// <param name="timeProvider">The clock driving the one-second countdown; defaults to <see cref="TimeProvider.System"/>.</param>
    public IdentifyCluster(
        IdentifyType identifyType = IdentifyType.None,
        ushort initialIdentifyTime = 0,
        TimeProvider? timeProvider = null)
    {
        if (!Enum.IsDefined(identifyType))
        {
            throw new ArgumentOutOfRangeException(nameof(identifyType), identifyType, "The IdentifyType is not a defined value.");
        }

        _identifyType = identifyType;
        _timeProvider = timeProvider ?? TimeProvider.System;

        _attributes = new AttributeStore(IncrementDataVersion);
        _identifyTime = _attributes.Add(new AttributeId(IdentifyTimeId), TlvCodec.UInt16, initialIdentifyTime, writable: true);

        // Single-threaded construction: begin the countdown if the node starts out identifying.
        ScheduleLocked(initialIdentifyTime);
    }

    /// <inheritdoc />
    public override ClusterId Id => ClusterId;

    /// <inheritdoc />
    /// <remarks>Revision 4 (Matter 1.2) attribute/command set; the optional QRY feature is deferred.</remarks>
    public override ushort ClusterRevision => 4;

    /// <inheritdoc />
    public override IReadOnlyCollection<AttributeId> AttributeIds => AttributeIdList;

    /// <inheritdoc />
    public override IReadOnlyCollection<CommandId> AcceptedCommandIds => AcceptedCommands;

    /// <summary>Raised when identification begins (IdentifyTime transitions from 0 to non-zero). Raised outside the internal lock.</summary>
    public event EventHandler? IdentifyStarted;

    /// <summary>Raised when identification ends (IdentifyTime reaches 0, by countdown, write, command, or StopEffect). Raised outside the internal lock.</summary>
    public event EventHandler? IdentifyStopped;

    /// <summary>Raised on a TriggerEffect command so the host can render the requested effect. Raised outside the internal lock.</summary>
    public event EventHandler<IdentifyEffectEventArgs>? EffectTriggered;

    /// <summary>The way this device identifies itself.</summary>
    public IdentifyType IdentifyType => _identifyType;

    /// <summary>The remaining identification time in seconds; 0 when the device is not identifying.</summary>
    public ushort IdentifyTime => _identifyTime.Value;

    /// <summary>Whether the device is currently identifying (IdentifyTime is non-zero).</summary>
    public bool IsIdentifying => _identifyTime.Value > 0;

    /// <summary>Begins (or extends) identification for <paramref name="seconds"/> from device logic; 0 stops it. Mirrors the Identify command.</summary>
    public void StartIdentifying(ushort seconds) => SetIdentifyTime(seconds);

    /// <summary>Stops identification from device logic. Equivalent to <see cref="StartIdentifying"/> with 0.</summary>
    public void StopIdentifying() => SetIdentifyTime(0);

    /// <inheritdoc />
    protected override ValueTask<InteractionModelStatusCode> ReadAttributeCoreAsync(
        AttributeId attributeId, TlvWriter writer, TlvTag tag, InteractionContext context, CancellationToken cancellationToken)
    {
        // IdentifyTime lives in the store; IdentifyType is a fixed projection.
        if (_attributes.TryRead(attributeId, writer, tag))
        {
            return new ValueTask<InteractionModelStatusCode>(InteractionModelStatusCode.Success);
        }

        if (attributeId.Value == IdentifyTypeId)
        {
            writer.WriteUnsignedInteger(tag, (byte)_identifyType);
            return new ValueTask<InteractionModelStatusCode>(InteractionModelStatusCode.Success);
        }

        return new ValueTask<InteractionModelStatusCode>(InteractionModelStatusCode.UnsupportedAttribute);
    }

    /// <inheritdoc />
    protected override ValueTask<InteractionModelStatusCode> WriteAttributeCoreAsync(
        AttributeId attributeId, ReadOnlyMemory<byte> value, InteractionContext context, CancellationToken cancellationToken)
    {
        // Only IdentifyTime is writable; a write (re)starts or cancels the countdown.
        if (attributeId.Value != IdentifyTimeId)
        {
            return new ValueTask<InteractionModelStatusCode>(InteractionModelStatusCode.UnsupportedWrite);
        }

        InteractionModelStatusCode status;
        ushort previous, current;
        lock (_gate)
        {
            previous = _identifyTime.Value;
            status = _attributes.Write(attributeId, value); // decode + set (no notify; the base bumps DataVersion)
            current = _identifyTime.Value;
            if (status == InteractionModelStatusCode.Success)
            {
                ScheduleLocked(current);
            }
        }

        if (status == InteractionModelStatusCode.Success)
        {
            RaiseLifecycle(previous, current);
        }

        return new ValueTask<InteractionModelStatusCode>(status);
    }

    /// <inheritdoc />
    protected override ValueTask<CommandResponse> InvokeCommandCoreAsync(
        CommandId commandId, ReadOnlyMemory<byte> fields, InteractionContext context, CancellationToken cancellationToken)
        => commandId.Value switch
        {
            IdentifyCommandId => CommandCodec.Invoke(fields, HandleIdentify),
            TriggerEffectCommandId => CommandCodec.Invoke(fields, HandleTriggerEffect),
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

    private CommandResponse HandleIdentify(CommandFields fields)
    {
        var seconds = fields.GetRequired(0, TlvCodec.UInt16); // IdentifyTime
        SetIdentifyTime(seconds);
        return CommandResponse.Success();
    }

    private CommandResponse HandleTriggerEffect(CommandFields fields)
    {
        // An unrecognized EffectIdentifier defaults to Blink; an unrecognized variant to Default (spec �1.2.6.2).
        var effect = NormalizeEffect(fields.GetRequired(0, TlvCodec.UInt8));
        var variant = NormalizeVariant(fields.GetOptional(1, TlvCodec.UInt8, fallback: (byte)IdentifyEffectVariant.Default));

        // StopEffect terminates any in-progress identification immediately (spec �1.2.6.2.1).
        if (effect == IdentifyEffect.StopEffect)
        {
            SetIdentifyTime(0);
        }

        EffectTriggered?.Invoke(this, new IdentifyEffectEventArgs(effect, variant));
        return CommandResponse.Success();
    }

    // The single choke point for device/command-driven IdentifyTime changes: sets the value (notifying
    // subscriptions), (re)schedules the countdown, and raises the start/stop lifecycle outside the lock.
    private void SetIdentifyTime(ushort seconds)
    {
        ushort previous;
        lock (_gate)
        {
            previous = _identifyTime.Value;
            _identifyTime.Value = seconds; // device path: notifies -> IncrementDataVersion (only when changed)
            ScheduleLocked(seconds);
        }

        RaiseLifecycle(previous, seconds);
    }

    private void OnCountdownTick(object? state)
    {
        ushort previous, current;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            previous = _identifyTime.Value;
            if (previous == 0)
            {
                return; // already stopped by a concurrent write/command.
            }

            current = (ushort)(previous - 1);
            _identifyTime.Value = current; // notifies subscriptions each second.
            ScheduleLocked(current);
        }

        RaiseLifecycle(previous, current);
    }

    // Schedules the next one-second tick when identifying, or cancels the timer when stopped.
    // The caller must hold the gate (or be the constructor).
    private void ScheduleLocked(ushort seconds)
    {
        if (_disposed)
        {
            return;
        }

        if (seconds > 0)
        {
            _timer ??= _timeProvider.CreateTimer(OnCountdownTick, state: null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _timer.Change(OneSecond, Timeout.InfiniteTimeSpan);
        }
        else
        {
            _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    private void RaiseLifecycle(ushort previous, ushort current)
    {
        if (previous == 0 && current > 0)
        {
            IdentifyStarted?.Invoke(this, EventArgs.Empty);
        }
        else if (previous > 0 && current == 0)
        {
            IdentifyStopped?.Invoke(this, EventArgs.Empty);
        }
    }

    private static IdentifyEffect NormalizeEffect(byte raw) =>
        Enum.IsDefined((IdentifyEffect)raw) ? (IdentifyEffect)raw : IdentifyEffect.Blink;

    private static IdentifyEffectVariant NormalizeVariant(byte raw) =>
        Enum.IsDefined((IdentifyEffectVariant)raw) ? (IdentifyEffectVariant)raw : IdentifyEffectVariant.Default;
}