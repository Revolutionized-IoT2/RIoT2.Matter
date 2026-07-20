using RIoT2.Matter.DataModel;
using RIoT2.Matter.Device;
using RIoT2.Matter.Messaging;

namespace RIoT2.Matter.InteractionModel;

/// <summary>Sends a subscription <see cref="ReportDataMessage"/> to the subscriber and awaits delivery.</summary>
public delegate ValueTask SubscriptionReportSender(IMessageSession session, ReportDataMessage report, CancellationToken cancellationToken);

/// <summary>
/// An established subscription: owns the report loop that emits data reports when subscribed
/// attributes or events change, and empty-report keepalives at the maximum interval, honoring the
/// minimum interval floor. See the Matter Core Specification, section 8.5 (Subscribe Interaction).
/// </summary>
/// <remarks>
/// Attribute changes (via <see cref="AttributeChangeBroker"/>) and urgent events wake the loop, so a
/// report is emitted one minimum-interval after a change rather than waiting for the maximum
/// interval. The unconditional per-cycle data-version diff remains the correctness backstop: a
/// missed wake only delays reporting to the next cycle. Non-urgent events are batched to the next
/// scheduled cycle.
/// </remarks>
public sealed class Subscription
{
    private readonly IMessageSession _session;
    private readonly ReadRequestMessage _attributeReadRequest;
    private readonly bool _hasAttributePaths;
    private readonly IReadOnlyList<AttributePathIB> _attributePaths;
    private readonly IReadOnlyList<EventPathIB> _eventPaths;
    private readonly InteractionModelReadEngine _readEngine;
    private readonly EventManager _events;
    private readonly AttributeChangeBroker _changes;
    private readonly SubscriptionReportSender _sender;
    private readonly TimeProvider _timeProvider;
    private readonly Action<Subscription> _onTerminated;
    private readonly CancellationTokenSource _cts = new();

    private Dictionary<(EndpointId Endpoint, ClusterId Cluster), uint> _clusterVersions;
    private ulong _lastEventNumber;
    private TaskCompletionSource _changeSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _loop;
    private bool _subscribedToEvents;
    private bool _subscribedToChanges;
    private readonly InteractionContext _context;

    public Subscription(
        uint id,
        IMessageSession session,
        ReadRequestMessage readRequest,
        InteractionContext context,
        InteractionModelReadEngine readEngine,
        EventManager events,
        AttributeChangeBroker changes,
        TimeSpan minInterval,
        TimeSpan maxInterval,
        IReadOnlyDictionary<(EndpointId, ClusterId), uint> initialVersions,
        ulong initialEventNumber,
        SubscriptionReportSender sender,
        TimeProvider timeProvider,
        Action<Subscription> onTerminated)
    {
        Id = id;
        _session = session;
        _context = context;
        _readEngine = readEngine;
        _events = events;
        _changes = changes;
        MinInterval = minInterval;
        MaxInterval = maxInterval;
        _clusterVersions = new Dictionary<(EndpointId, ClusterId), uint>(initialVersions);
        _lastEventNumber = initialEventNumber;
        _sender = sender;
        _timeProvider = timeProvider;
        _onTerminated = onTerminated;

        _attributePaths = readRequest.AttributeRequests ?? [];
        _eventPaths = readRequest.EventRequests ?? [];
        _hasAttributePaths = _attributePaths.Count > 0;

        // Periodic diffing reads attributes only; events are collected incrementally from the store.
        _attributeReadRequest = readRequest with { EventRequests = null, EventFilters = null };
    }

    /// <summary>The server-allocated subscription identifier.</summary>
    public uint Id { get; }

    /// <summary>The session this subscription reports over.</summary>
    public IMessageSession Session => _session;

    /// <summary>The negotiated minimum interval (report floor).</summary>
    public TimeSpan MinInterval { get; }

    /// <summary>The negotiated maximum interval (keepalive ceiling).</summary>
    public TimeSpan MaxInterval { get; }

    /// <summary>Starts the periodic report loop. Call once, after the SubscribeResponse is sent.</summary>
    public void Start()
    {
        if (_hasAttributePaths)
        {
            _changes.ClusterChanged += OnClusterChanged;
            _subscribedToChanges = true;
        }

        if (_eventPaths.Count > 0)
        {
            _events.EventRecorded += OnEventRecorded;
            _subscribedToEvents = true;
        }

        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>Signals that subscribed data may have changed, waking the loop (subject to the floor).</summary>
    public void NotifyChanged() => Volatile.Read(ref _changeSignal).TrySetResult();

    /// <summary>Stops the report loop.</summary>
    public void Stop() => _cts.Cancel();

    /// <summary>Extracts a per-cluster data-version snapshot from a report's attribute data.</summary>
    public static Dictionary<(EndpointId, ClusterId), uint> ExtractVersions(ReportDataMessage report)
    {
        var versions = new Dictionary<(EndpointId, ClusterId), uint>();
        if (report.AttributeReports is { } reports)
        {
            foreach (var entry in reports)
            {
                if (entry.AttributeData is { } data &&
                    data.Path.Endpoint is { } endpoint &&
                    data.Path.Cluster is { } cluster &&
                    data.DataVersion is { } version)
                {
                    versions[(endpoint, cluster)] = version;
                }
            }
        }

        return versions;
    }

    /// <summary>Returns the highest event number carried by a report's event data, or 0 when none.</summary>
    public static ulong ExtractLatestEventNumber(ReportDataMessage report)
    {
        ulong latest = 0;
        if (report.EventReports is { } reports)
        {
            foreach (var entry in reports)
            {
                if (entry.EventData is { } data && data.EventNumber > latest)
                {
                    latest = data.EventNumber;
                }
            }
        }

        return latest;
    }

    private void OnClusterChanged(object? sender, ClusterChange change)
    {
        // Wake only when the change touches a cluster this subscription selects.
        if (AttributePathMatching.MatchesCluster(_attributePaths, change.Endpoint, change.Cluster))
        {
            NotifyChanged();
        }
    }

    private void OnEventRecorded(object? sender, GeneratedEvent generated)
    {
        // Wake the loop early only for events the subscriber flagged urgent; others are picked up by
        // the cursor-based query on the next scheduled cycle.
        foreach (var path in _eventPaths)
        {
            if (path.IsUrgent == true && EventPathMatching.MatchesPath(generated, path))
            {
                NotifyChanged();
                return;
            }
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // Capture the change signal before the floor wait so changes during it aren't missed.
                var changeTask = Volatile.Read(ref _changeSignal).Task;

                // Floor: never report more often than the minimum interval.
                if (MinInterval > TimeSpan.Zero)
                {
                    await Task.Delay(MinInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
                }

                // Ceiling: wake on the next change, or when the max interval elapses.
                await WaitForChangeOrTimeoutAsync(changeTask, MaxInterval - MinInterval, cancellationToken).ConfigureAwait(false);
                Volatile.Write(ref _changeSignal, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

                await ReportAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch
        {
            // A report send failed (e.g. the session was evicted): terminate the subscription.
        }
        finally
        {
            if (_subscribedToChanges)
            {
                _changes.ClusterChanged -= OnClusterChanged;
            }

            if (_subscribedToEvents)
            {
                _events.EventRecorded -= OnEventRecorded;
            }

            _onTerminated(this);
        }
    }

    private async ValueTask ReportAsync(CancellationToken cancellationToken)
    {
        // Attributes: re-read and diff data versions against the last reported snapshot.
        List<AttributeReportIB>? attributeReports = null;
        Dictionary<(EndpointId, ClusterId), uint>? newVersions = null;
        if (_hasAttributePaths)
        {
            var fresh = await _readEngine.ExecuteAsync(_attributeReadRequest, _context, cancellationToken).ConfigureAwait(false);
            var (changed, versions) = DiffChangedReports(fresh);
            if (changed.Count > 0)
            {
                attributeReports = changed;
                newVersions = versions;
            }
        }

        // Events: pull everything recorded beyond the cursor that matches the subscription's paths.
        IReadOnlyList<GeneratedEvent> newEvents = _eventPaths.Count > 0
            ? _events.Query(e => EventPathMatching.MatchesAny(e, _eventPaths), _lastEventNumber + 1)
            : [];

        if (attributeReports is null && newEvents.Count == 0)
        {
            // Nothing changed within the interval: emit an empty keepalive report.
            await _sender(_session, new ReportDataMessage { SubscriptionId = Id }, cancellationToken).ConfigureAwait(false);
            return;
        }

        List<EventReportIB>? eventReports = null;
        if (newEvents.Count > 0)
        {
            eventReports = new List<EventReportIB>(newEvents.Count);
            foreach (var generated in newEvents)
            {
                eventReports.Add(EventReportIB.ForData(generated.ToEventData()));
            }
        }

        var report = new ReportDataMessage
        {
            SubscriptionId = Id,
            AttributeReports = attributeReports,
            EventReports = eventReports,
        };

        await _sender(_session, report, cancellationToken).ConfigureAwait(false);

        // Advance cursors only after a successful send.
        if (newVersions is not null) { _clusterVersions = newVersions; }
        if (newEvents.Count > 0) { _lastEventNumber = newEvents[newEvents.Count - 1].EventNumber; }
    }

    private (List<AttributeReportIB> Changed, Dictionary<(EndpointId, ClusterId), uint> Versions) DiffChangedReports(ReportDataMessage fresh)
    {
        var versions = new Dictionary<(EndpointId, ClusterId), uint>();
        var changed = new List<AttributeReportIB>();

        if (fresh.AttributeReports is { } reports)
        {
            foreach (var entry in reports)
            {
                if (entry.AttributeData is { } data &&
                    data.Path.Endpoint is { } endpoint &&
                    data.Path.Cluster is { } cluster &&
                    data.DataVersion is { } version)
                {
                    var key = (endpoint, cluster);
                    versions[key] = version;
                    if (!_clusterVersions.TryGetValue(key, out var previous) || previous != version)
                    {
                        changed.Add(entry);
                    }
                }
                else
                {
                    // Status reports carry no data version; forward them each cycle.
                    changed.Add(entry);
                }
            }
        }

        return (changed, versions);
    }

    private async Task WaitForChangeOrTimeoutAsync(Task changeTask, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero || changeTask.IsCompleted)
        {
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delayTask = Task.Delay(timeout, _timeProvider, linked.Token);
        var winner = await Task.WhenAny(changeTask, delayTask).ConfigureAwait(false);

        if (winner == changeTask)
        {
            linked.Cancel(); // stop the timer
            try { await delayTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
            return;
        }

        await delayTask.ConfigureAwait(false); // observe timeout / propagate cancellation
    }
}