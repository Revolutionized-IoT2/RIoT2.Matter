using System.Security.Cryptography;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.Device;
using RIoT2.Matter.InteractionModel;
using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.Clusters;

/// <summary>
/// The General Diagnostics cluster (0x0033) on the root endpoint: exposes the node's
/// NetworkInterfaces, RebootCount, live UpTime, BootReason, and the TestEventTriggersEnabled flag,
/// and implements the TestEventTrigger command controllers (and chip-tool) use to drive test-only
/// behaviors. The runtime facts are host-sourced (supplied at construction); UpTime is computed live
/// from an injected <see cref="TimeProvider"/>. Mandatory on endpoint 0. See the Matter Core
/// Specification, section 11.11.
/// </summary>
/// <remarks>
/// Add to the root endpoint with the host's interface list and boot facts, then emit the BootReason
/// event once the node is operational:
/// <code>
/// var diagnostics = new GeneralDiagnosticsCluster(
///     networkInterfaces, rebootCount: 3, bootReason: BootReason.SoftwareReset);
/// node.Root.AddCluster(diagnostics);
/// diagnostics.EmitBootReason();
/// </code>
/// The optional TotalOperationalHours attribute, the Active*Faults lists with their *FaultChange
/// events, and the (revision 2) TimeSnapshot command are deferred.
/// </remarks>
public sealed class GeneralDiagnosticsCluster : Cluster
{
    /// <summary>The General Diagnostics cluster identifier (0x0033).</summary>
    public static readonly ClusterId ClusterId = new(0x0033);

    // Attribute ids (spec §11.11.6). TotalOperationalHours and the Active*Faults lists are deferred.
    private const uint NetworkInterfacesId = 0x0000;
    private const uint RebootCountId = 0x0001;
    private const uint UpTimeId = 0x0002;
    private const uint BootReasonId = 0x0004;
    private const uint TestEventTriggersEnabledId = 0x0008;

    // Command ids (spec §11.11.7). TimeSnapshot (revision 2) is deferred.
    private const uint TestEventTriggerId = 0x00;

    // Event ids (spec §11.11.8). The *FaultChange events are deferred.
    private static readonly EventId BootReasonEventId = new(0x03);

    // NetworkInterface struct field tags (spec §11.11.5.1).
    private const byte NameTag = 0;
    private const byte IsOperationalTag = 1;
    private const byte OffPremiseIPv4Tag = 2;
    private const byte OffPremiseIPv6Tag = 3;
    private const byte HardwareAddressTag = 4;
    private const byte IPv4AddressesTag = 5;
    private const byte IPv6AddressesTag = 6;
    private const byte InterfaceTypeTag = 7;

    // TestEventTrigger request field ids (spec §11.11.7.1).
    private const byte EnableKeyField = 0;
    private const byte EventTriggerField = 1;

    private const int EnableKeyLength = 16;

    private static readonly AttributeId[] AttributeIdList =
    [
        new(NetworkInterfacesId), new(RebootCountId), new(UpTimeId),
        new(BootReasonId), new(TestEventTriggersEnabledId),
    ];

    private static readonly CommandId[] AcceptedCommands = [new(TestEventTriggerId)];

    private static readonly EventId[] EmittableEvents = [BootReasonEventId];

    private readonly TimeProvider _timeProvider;
    private readonly long _bootTimestamp;
    private readonly ushort _rebootCount;
    private readonly BootReason _bootReason;
    private readonly byte[] _testEventTriggerEnableKey;
    private readonly bool _testEventTriggersEnabled;
    private readonly Func<ulong, bool>? _testEventTriggerHandler;
    private readonly object _gate = new();
    private IReadOnlyList<NetworkInterface> _networkInterfaces;

    /// <param name="networkInterfaces">The node's network interfaces (snapshotted; update later via <see cref="NetworkInterfaces"/>).</param>
    /// <param name="rebootCount">The RebootCount attribute value (host-persisted count of reboots since factory reset).</param>
    /// <param name="bootReason">The reason for the node's most recent boot (BootReason attribute/event).</param>
    /// <param name="testEventTriggerEnableKey">
    /// The 16-octet key a TestEventTrigger command must match; empty (the default) or all-zero leaves
    /// test event triggers disabled (TestEventTriggersEnabled reads false and every trigger is rejected).
    /// </param>
    /// <param name="testEventTriggerHandler">
    /// Invoked with the EventTrigger value once the enable key is verified; returns whether the trigger
    /// was recognized. <see langword="null"/> rejects every (otherwise-authorized) trigger.
    /// </param>
    /// <param name="timeProvider">The monotonic clock backing UpTime; defaults to <see cref="TimeProvider.System"/>.</param>
    public GeneralDiagnosticsCluster(
        IReadOnlyList<NetworkInterface> networkInterfaces,
        ushort rebootCount = 0,
        BootReason bootReason = BootReason.Unspecified,
        ReadOnlyMemory<byte> testEventTriggerEnableKey = default,
        Func<ulong, bool>? testEventTriggerHandler = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(networkInterfaces);
        if (!testEventTriggerEnableKey.IsEmpty && testEventTriggerEnableKey.Length != EnableKeyLength)
        {
            throw new ArgumentException($"The test event trigger enable key must be {EnableKeyLength} octets.", nameof(testEventTriggerEnableKey));
        }

        _networkInterfaces = SnapshotInterfaces(networkInterfaces);
        _rebootCount = rebootCount;
        _bootReason = bootReason;
        _testEventTriggerEnableKey = testEventTriggerEnableKey.ToArray();
        _testEventTriggersEnabled = IsEnableKeyConfigured(_testEventTriggerEnableKey);
        _testEventTriggerHandler = testEventTriggerHandler;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _bootTimestamp = _timeProvider.GetTimestamp();
    }

    /// <inheritdoc />
    public override ClusterId Id => ClusterId;

    /// <inheritdoc />
    /// <remarks>Revision 1 attribute/command set; TimeSnapshot and the fault-diagnostics surface are deferred.</remarks>
    public override ushort ClusterRevision => 1;

    /// <inheritdoc />
    public override IReadOnlyCollection<AttributeId> AttributeIds => AttributeIdList;

    /// <inheritdoc />
    public override IReadOnlyCollection<CommandId> AcceptedCommandIds => AcceptedCommands;

    /// <inheritdoc />
    public override IReadOnlyCollection<EventId> EventIds => EmittableEvents;

    /// <summary>The host-persisted count of reboots since the last factory reset (RebootCount attribute).</summary>
    public ushort RebootCount => _rebootCount;

    /// <summary>The reason for the node's most recent boot.</summary>
    public BootReason BootReason => _bootReason;

    /// <summary>Whether test event triggers are enabled (a non-zero 16-octet enable key was supplied).</summary>
    public bool TestEventTriggersEnabled => _testEventTriggersEnabled;

    /// <summary>The node's uptime in seconds, computed live from the injected time provider.</summary>
    public ulong UpTimeSeconds => (ulong)_timeProvider.GetElapsedTime(_bootTimestamp).TotalSeconds;

    /// <summary>
    /// The node's network interfaces. Setting this from device logic snapshots the new list and
    /// notifies subscriptions (e.g. when an interface goes up/down or changes address).
    /// </summary>
    public IReadOnlyList<NetworkInterface> NetworkInterfaces
    {
        get { lock (_gate) { return _networkInterfaces; } }
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            var snapshot = SnapshotInterfaces(value);
            lock (_gate)
            {
                _networkInterfaces = snapshot;
            }

            IncrementDataVersion();
        }
    }

    /// <inheritdoc />
    /// <remarks>The TestEventTrigger command is a test/manufacturing operation and requires Manage (spec §11.11.7).</remarks>
    public override AccessPrivilege RequiredInvokePrivilege(CommandId commandId) => AccessPrivilege.Manage;

    /// <summary>
    /// Emits the BootReason event carrying the node's <see cref="BootReason"/>. Call once the node is
    /// operational. Returns the allocated event number, or 0 if the cluster is not yet attached to a
    /// node. See the specification, section 11.11.8.4.
    /// </summary>
    public ulong EmitBootReason() => EmitEvent(BootReasonEventId, EventPriority.Critical, writer =>
    {
        writer.StartStructure(TlvTag.Anonymous);
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(0), (byte)_bootReason); // BootReason
        writer.EndContainer();
    });

    /// <inheritdoc />
    protected override ValueTask<InteractionModelStatusCode> ReadAttributeCoreAsync(
        AttributeId attributeId, TlvWriter writer, TlvTag tag, InteractionContext context, CancellationToken cancellationToken)
    {
        switch (attributeId.Value)
        {
            case NetworkInterfacesId:
                WriteNetworkInterfaces(writer, tag);
                break;
            case RebootCountId:
                writer.WriteUnsignedInteger(tag, _rebootCount);
                break;
            case UpTimeId:
                writer.WriteUnsignedInteger(tag, UpTimeSeconds);
                break;
            case BootReasonId:
                writer.WriteUnsignedInteger(tag, (byte)_bootReason);
                break;
            case TestEventTriggersEnabledId:
                writer.WriteBoolean(tag, _testEventTriggersEnabled);
                break;
            default:
                return new ValueTask<InteractionModelStatusCode>(InteractionModelStatusCode.UnsupportedAttribute);
        }

        return new ValueTask<InteractionModelStatusCode>(InteractionModelStatusCode.Success);
    }

    /// <inheritdoc />
    protected override ValueTask<CommandResponse> InvokeCommandCoreAsync(
        CommandId commandId, ReadOnlyMemory<byte> fields, InteractionContext context, CancellationToken cancellationToken)
        => commandId.Value switch
        {
            TestEventTriggerId => CommandCodec.Invoke(fields, HandleTestEventTrigger),
            _ => new ValueTask<CommandResponse>(CommandResponse.FromStatus(InteractionModelStatusCode.UnsupportedCommand)),
        };

    private CommandResponse HandleTestEventTrigger(CommandFields fields)
    {
        // A wrong-length key is a constraint error (mapped by CommandCodec from the validate failure).
        var enableKey = fields.GetRequired(EnableKeyField, TlvCodec.OctetString, v => v.Length == EnableKeyLength);
        var eventTrigger = fields.GetRequired(EventTriggerField, TlvCodec.UInt64);

        // Triggers must be enabled and the key must match, else CONSTRAINT_ERROR (spec §11.11.7.1).
        if (!_testEventTriggersEnabled ||
            !CryptographicOperations.FixedTimeEquals(enableKey, _testEventTriggerEnableKey))
        {
            return CommandResponse.FromStatus(InteractionModelStatusCode.ConstraintError);
        }

        // An unrecognized trigger is an INVALID_COMMAND; a handled one is SUCCESS (spec §11.11.7.1).
        var handled = _testEventTriggerHandler?.Invoke(eventTrigger) ?? false;
        return CommandResponse.FromStatus(handled
            ? InteractionModelStatusCode.Success
            : InteractionModelStatusCode.InvalidCommand);
    }

    private void WriteNetworkInterfaces(TlvWriter writer, TlvTag tag)
    {
        IReadOnlyList<NetworkInterface> snapshot;
        lock (_gate)
        {
            snapshot = _networkInterfaces;
        }

        writer.StartArray(tag);
        foreach (var iface in snapshot)
        {
            WriteInterface(writer, iface);
        }

        writer.EndContainer();
    }

    private static void WriteInterface(TlvWriter writer, NetworkInterface iface)
    {
        writer.StartStructure(TlvTag.Anonymous);
        writer.WriteUtf8String(TlvTag.ContextSpecific(NameTag), iface.Name);
        writer.WriteBoolean(TlvTag.ContextSpecific(IsOperationalTag), iface.IsOperational);
        WriteNullableBool(writer, OffPremiseIPv4Tag, iface.OffPremiseServicesReachableIPv4);
        WriteNullableBool(writer, OffPremiseIPv6Tag, iface.OffPremiseServicesReachableIPv6);
        writer.WriteByteString(TlvTag.ContextSpecific(HardwareAddressTag), iface.HardwareAddress);
        WriteAddressList(writer, IPv4AddressesTag, iface.IPv4Addresses);
        WriteAddressList(writer, IPv6AddressesTag, iface.IPv6Addresses);
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(InterfaceTypeTag), (byte)iface.Type);
        writer.EndContainer();
    }

    private static void WriteNullableBool(TlvWriter writer, byte tag, bool? value)
    {
        if (value is { } present)
        {
            writer.WriteBoolean(TlvTag.ContextSpecific(tag), present);
        }
        else
        {
            writer.WriteNull(TlvTag.ContextSpecific(tag));
        }
    }

    private static void WriteAddressList(TlvWriter writer, byte tag, IReadOnlyList<byte[]>? addresses)
    {
        writer.StartArray(TlvTag.ContextSpecific(tag));
        if (addresses is not null)
        {
            foreach (var address in addresses)
            {
                writer.WriteByteString(TlvTag.Anonymous, address);
            }
        }

        writer.EndContainer();
    }

    private static IReadOnlyList<NetworkInterface> SnapshotInterfaces(IReadOnlyList<NetworkInterface> interfaces)
    {
        // Copy the container so later external mutation of the caller's list is not observed; the
        // NetworkInterface records themselves are immutable, so sharing their references is safe.
        var copy = new NetworkInterface[interfaces.Count];
        for (int i = 0; i < interfaces.Count; i++)
        {
            ArgumentNullException.ThrowIfNull(interfaces[i]);
            copy[i] = interfaces[i];
        }

        return copy;
    }

    private static bool IsEnableKeyConfigured(byte[] enableKey)
    {
        if (enableKey.Length != EnableKeyLength)
        {
            return false;
        }

        foreach (var b in enableKey)
        {
            if (b != 0)
            {
                return true;
            }
        }

        return false; // an all-zero key means test event triggers are disabled (spec §11.11.7.1).
    }
}