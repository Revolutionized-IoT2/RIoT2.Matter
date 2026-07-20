using RIoT2.Matter.DataModel;
using RIoT2.Matter.InteractionModel;

namespace RIoT2.Matter.Device;

/// <summary>
/// The node-wide event store: assigns each generated event a monotonically increasing number,
/// timestamps it, and retains a bounded history per priority for Read and Subscribe reporting.
/// See the Matter Core Specification, section 8.9.
/// </summary>
/// <remarks>
/// Retention is per-priority (Critical retained longest, Debug shortest); the oldest event of a
/// priority is evicted when its cap is exceeded. Numbering is in-memory; a real device must persist
/// the counter across reboots so event numbers never regress.
/// </remarks>
public sealed class EventManager : IEventSink
{
    private static readonly IReadOnlyDictionary<EventPriority, int> DefaultCaps = new Dictionary<EventPriority, int>
    {
        [EventPriority.Debug] = 32,
        [EventPriority.Info] = 64,
        [EventPriority.Critical] = 128,
    };

    private readonly TimeProvider _timeProvider;
    private readonly IReadOnlyDictionary<EventPriority, int> _caps;
    private readonly object _gate = new();
    private readonly LinkedList<GeneratedEvent> _log = new(); // ascending event-number order
    private readonly Dictionary<EventPriority, int> _counts = new();
    private ulong _nextEventNumber = 1; // 0 is reserved to mean "no events".

    public EventManager(TimeProvider? timeProvider = null, IReadOnlyDictionary<EventPriority, int>? priorityCaps = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _caps = priorityCaps ?? DefaultCaps;
    }

    /// <summary>Raised after an event is recorded, so subscriptions can wake for urgent reporting.</summary>
    public event EventHandler<GeneratedEvent>? EventRecorded;

    /// <summary>The number of the most recently recorded event, or 0 when none have been recorded.</summary>
    public ulong LatestEventNumber
    {
        get { lock (_gate) { return _nextEventNumber - 1; } }
    }

    /// <inheritdoc />
    public ulong Record(EndpointId endpoint, ClusterId cluster, EventId eventId, EventPriority priority, ReadOnlyMemory<byte> payload)
    {
        GeneratedEvent recorded;
        lock (_gate)
        {
            recorded = new GeneratedEvent
            {
                EventNumber = _nextEventNumber++,
                Endpoint = endpoint,
                Cluster = cluster,
                Event = eventId,
                Priority = priority,
                EpochTimestampMs = (ulong)_timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                Payload = payload,
            };

            _log.AddLast(recorded);
            _counts[priority] = _counts.GetValueOrDefault(priority) + 1;
            Trim(priority);
        }

        EventRecorded?.Invoke(this, recorded);
        return recorded.EventNumber;
    }

    /// <summary>
    /// Returns the retained events matching <paramref name="match"/> whose number is at or above
    /// <paramref name="minEventNumber"/>, in ascending event-number order.
    /// </summary>
    public IReadOnlyList<GeneratedEvent> Query(Func<GeneratedEvent, bool> match, ulong minEventNumber)
    {
        ArgumentNullException.ThrowIfNull(match);

        var results = new List<GeneratedEvent>();
        lock (_gate)
        {
            foreach (var recorded in _log)
            {
                if (recorded.EventNumber >= minEventNumber && match(recorded))
                {
                    results.Add(recorded);
                }
            }
        }

        return results;
    }

    private void Trim(EventPriority priority)
    {
        var cap = _caps.GetValueOrDefault(priority, int.MaxValue);
        while (_counts.GetValueOrDefault(priority) > cap)
        {
            // Evict the oldest event of this priority, preserving higher-priority history.
            for (var node = _log.First; node is not null; node = node.Next)
            {
                if (node.Value.Priority == priority)
                {
                    _log.Remove(node);
                    _counts[priority]--;
                    break;
                }
            }
        }
    }
}