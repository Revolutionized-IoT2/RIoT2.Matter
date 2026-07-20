using System.Collections.Concurrent;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.Messaging;

namespace RIoT2.Matter.InteractionModel;

/// <summary>
/// Allocates subscription identifiers and tracks the node's active <see cref="Subscription"/>
/// instances. See the Matter Core Specification, section 8.5.
/// </summary>
public sealed class SubscriptionManager
{
    private readonly ConcurrentDictionary<uint, Subscription> _subscriptions = new();

    // The spec recommends a random initial subscription id; it then increments (0 is reserved).
    private int _nextId = Random.Shared.Next(1, int.MaxValue);

    /// <summary>Creates and registers a subscription, allocating its id via <paramref name="factory"/>.</summary>
    public Subscription Add(Func<uint, Subscription> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        var id = AllocateId();
        var subscription = factory(id);
        _subscriptions[id] = subscription;
        return subscription;
    }

    /// <summary>Attempts to look up an active subscription by id.</summary>
    public bool TryGet(uint id, out Subscription? subscription) => _subscriptions.TryGetValue(id, out subscription);

    /// <summary>Removes and stops a subscription.</summary>
    public void Remove(Subscription subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        if (_subscriptions.TryRemove(subscription.Id, out _))
        {
            subscription.Stop();
        }
    }

    /// <summary>Removes and stops every subscription on <paramref name="session"/> (e.g. on eviction).</summary>
    public void RemoveSession(IMessageSession session)
    {
        foreach (var pair in _subscriptions)
        {
            if (ReferenceEquals(pair.Value.Session, session) && _subscriptions.TryRemove(pair.Key, out var subscription))
            {
                subscription.Stop();
            }
        }
    }

    /// <summary>
    /// Removes and stops every subscription belonging to the subscriber identified by
    /// <paramref name="fabricIndex"/> and its <paramref name="subject"/> node id. Used when a new
    /// SubscribeRequest arrives with KeepSubscriptions = false, whose scope is the accessing fabric's
    /// subscriber rather than a single session. See the Matter Core Specification, section 8.5.2.
    /// </summary>
    public void RemoveSubscriber(FabricIndex fabricIndex, NodeId subject)
    {
        foreach (var pair in _subscriptions)
        {
            var security = pair.Value.Session.Security;
            if (security.FabricIndex == fabricIndex && security.PeerNodeId == subject &&
                _subscriptions.TryRemove(pair.Key, out var subscription))
            {
                subscription.Stop();
            }
        }
    }

    private uint AllocateId()
    {
        while (true)
        {
            var id = (uint)Interlocked.Increment(ref _nextId);
            if (id != 0 && !_subscriptions.ContainsKey(id))
            {
                return id;
            }
        }
    }
}