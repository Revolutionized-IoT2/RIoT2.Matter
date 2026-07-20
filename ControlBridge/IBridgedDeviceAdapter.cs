namespace RIoT2.Matter.ControlBridge;

/// <summary>
/// Drives one non-Matter device behind a <see cref="BridgedDevice"/>: it reacts to Matter commands
/// arriving on the bridged endpoint (the <em>inbound</em> direction) by pushing them out to the real
/// device, and it reflects the real device's own state changes back into the bridged clusters. This is
/// the opposite direction to the Control Bridge, which sends commands <em>out</em> to Matter peers.
/// </summary>
/// <remarks>
/// Attach the device-driven direction directly to the bridged clusters (e.g.
/// <c>device.OnOff.OnOffChanged</c>), and implement <see cref="AttachAsync"/> to subscribe to the
/// underlying device and set the initial <see cref="BridgedDevice"/> state. The framework calls
/// <see cref="DetachAsync"/> when the bridged device is removed so the adapter can unsubscribe.
/// </remarks>
public interface IBridgedDeviceAdapter
{
    /// <summary>
    /// Called once when the adapter is attached to <paramref name="device"/>. Wire the bridged clusters'
    /// change events to the underlying device here, subscribe to the underlying device's own changes,
    /// and seed the initial reachability/state.
    /// </summary>
    ValueTask AttachAsync(BridgedDevice device, CancellationToken cancellationToken = default);

    /// <summary>
    /// Called once when the bridged device is removed. Unsubscribe from the underlying device and
    /// release any resources; the bridged endpoint is torn down after this completes.
    /// </summary>
    ValueTask DetachAsync(BridgedDevice device, CancellationToken cancellationToken = default);
}