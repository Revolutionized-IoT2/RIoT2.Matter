namespace RIoT2.Matter.Clusters;

/// <summary>
/// Keeps one or more controller <see cref="BindingCluster"/>s consistent with the node's fabric table:
/// when a fabric is removed from the <see cref="OperationalCredentialsManager"/> (by a RemoveFabric
/// command or a fail-safe rollback), it purges that fabric's entries from each Binding list via
/// <see cref="BindingCluster.RemoveFabric"/>. The resulting <see cref="BindingCluster.BindingsChanged"/>
/// then lets the controller runtime (e.g. <c>BindingConnectionManager</c>) tear down any operational
/// sessions the removed fabric's targets held. See the Matter Core Specification, sections 9.6 and
/// 11.18.6.12.
/// </summary>
/// <remarks>
/// Compose one when building a controller device (e.g. a Control Bridge), after adding the Binding
/// cluster(s) to their endpoint(s); dispose it to unhook:
/// <code>
/// var bridge = node.AddEndpoint(new EndpointId(1));
/// var binding = new BindingCluster();
/// bridge.AddCluster(binding).AddClientCluster(OnOffCluster.ClusterId);
/// using var purge = new BindingFabricPurger(support.Manager, binding);
/// </code>
/// Disposal only unsubscribes from the manager; the Binding clusters themselves are left untouched.
/// </remarks>
public sealed class BindingFabricPurger : IDisposable
{
    private readonly OperationalCredentialsManager _manager;
    private readonly BindingCluster[] _bindings;
    private readonly EventHandler<FabricRemovedEventArgs> _onFabricRemoved;

    /// <summary>Subscribes <paramref name="bindings"/> to <paramref name="manager"/>'s fabric removals.</summary>
    /// <param name="manager">The fabric-table backend whose <see cref="OperationalCredentialsManager.FabricRemoved"/> drives the purge.</param>
    /// <param name="bindings">The controller endpoints' Binding clusters to purge; at least one is required.</param>
    /// <exception cref="ArgumentException"><paramref name="bindings"/> is empty or contains a null entry.</exception>
    public BindingFabricPurger(OperationalCredentialsManager manager, params BindingCluster[] bindings)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        ArgumentNullException.ThrowIfNull(bindings);
        if (bindings.Length == 0)
        {
            throw new ArgumentException("At least one Binding cluster is required.", nameof(bindings));
        }

        if (Array.IndexOf(bindings, null) >= 0)
        {
            throw new ArgumentException("A Binding cluster is null.", nameof(bindings));
        }

        // Defensive copy so the caller can't mutate the driven set after subscription.
        _bindings = (BindingCluster[])bindings.Clone();

        // FabricRemoved is raised outside the manager's lock, and BindingCluster.RemoveFabric takes its
        // own, so purging here can't nest the two locks. Each cluster raises BindingsChanged at most once
        // per removal, and only when that fabric actually held entries.
        _onFabricRemoved = (_, e) =>
        {
            foreach (var binding in _bindings)
            {
                binding.RemoveFabric(e.FabricIndex);
            }
        };
        manager.FabricRemoved += _onFabricRemoved;
    }

    /// <inheritdoc />
    public void Dispose() => _manager.FabricRemoved -= _onFabricRemoved;
}

/// <summary>Ergonomic composition helpers that wire a <see cref="BindingCluster"/> to fabric lifecycle events.</summary>
public static class BindingClusterFabricExtensions
{
    /// <summary>
    /// Subscribes this Binding cluster to <paramref name="manager"/> so its entries for a fabric are
    /// purged when that fabric is removed. Dispose the returned handle to unhook.
    /// </summary>
    public static BindingFabricPurger PurgeOnFabricRemoved(this BindingCluster binding, OperationalCredentialsManager manager)
    {
        ArgumentNullException.ThrowIfNull(binding);
        return new BindingFabricPurger(manager, binding);
    }
}