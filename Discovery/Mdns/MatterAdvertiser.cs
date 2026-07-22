using RIoT2.Matter.Diagnostics;
using RIoT2.Matter.Discovery.Dns;

namespace RIoT2.Matter.Discovery.Mdns;

/// <summary>
/// The top-level DNS-SD advertiser lifecycle. It starts the mDNS interface, rebuilds the advertised
/// record set whenever the advertising inputs change (a fabric added/removed, the commissioning window
/// opening/closing, or the host's addresses changing), withdraws records that are no longer valid, and
/// runs the unsolicited announcement schedule. On disposal it sends a goodbye so controllers drop the
/// node's services immediately. See RFC 6762 sections 8 and 10.
/// </summary>
/// <remarks>
/// This is the composition root for advertising: it owns and disposes the supplied
/// <see cref="IMdnsInterface"/> and <see cref="MdnsResponder"/>. The commissionable↔operational switch
/// needs no special handling here — each rebuild simply reflects whichever services the current input
/// snapshot contains.
/// </remarks>
public sealed class MatterAdvertiser : IAsyncDisposable
{
    private readonly IMdnsInterface _iface;
    private readonly IMatterAdvertisingInputProvider _inputProvider;
    private readonly AdvertisedRecordStore _store;
    private readonly MdnsResponder _responder;
    private readonly MdnsAnnounceSchedule _schedule;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly object _sync = new();

    private CancellationTokenSource? _lifetime;
    private Task _worker = Task.CompletedTask;
    private int _pending;
    private long _generation;
    private bool _started;
    private bool _disposed;

    public MatterAdvertiser(
        IMdnsInterface iface,
        IMatterAdvertisingInputProvider inputProvider,
        AdvertisedRecordStore store,
        MdnsResponder responder,
        MdnsAnnounceSchedule? schedule = null)
    {
        _iface = iface ?? throw new ArgumentNullException(nameof(iface));
        _inputProvider = inputProvider ?? throw new ArgumentNullException(nameof(inputProvider));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _responder = responder ?? throw new ArgumentNullException(nameof(responder));
        _schedule = schedule ?? MdnsAnnounceSchedule.Default;
    }

    /// <summary>Starts the interface, subscribes to input changes, and publishes the initial advertisement.</summary>
    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_sync)
        {
            if (_started)
            {
                throw new InvalidOperationException("The advertiser has already been started.");
            }

            _started = true;
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _worker = Task.Run(() => RunAsync(_lifetime.Token), CancellationToken.None);
        }

        await _iface.StartAsync(cancellationToken).ConfigureAwait(false);
        _inputProvider.Changed += OnInputsChanged;

        // Publish the initial state (which may advertise nothing until the node is commissioned or opens
        // a commissioning window).
        Signal();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _inputProvider.Changed -= OnInputsChanged;
        _lifetime?.Cancel();

        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }

        // Best-effort goodbye so controllers drop our services immediately rather than at TTL expiry.
        try
        {
            using var goodbye = new CancellationTokenSource(_schedule.GoodbyeTimeout);
            await _responder.SendGoodbyeAsync(goodbye.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A failed shutdown goodbye must never mask disposal.
        }

        await _responder.DisposeAsync().ConfigureAwait(false);
        await _iface.DisposeAsync().ConfigureAwait(false);

        _lifetime?.Dispose();
        _signal.Dispose();
    }

    private void OnInputsChanged(object? sender, EventArgs e) => Signal();

    // Coalesces changes: bumps the generation (so an in-flight burst can detect supersession) and wakes
    // the worker at most once until it drains the pending flag.
    private void Signal()
    {
        Interlocked.Increment(ref _generation);
        if (Interlocked.Exchange(ref _pending, 1) == 0)
        {
            _signal.Release();
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        while (true)
        {
            try
            {
                await _signal.WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            Interlocked.Exchange(ref _pending, 0);

            try
            {
                await PublishAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                // A transient publish failure must not tear down the loop; the next change republishes.
            }
        }
    }

    private async Task PublishAsync(CancellationToken token)
    {
        long generation = Volatile.Read(ref _generation);

        AdvertisedRecordSet previous = _store.Current;
        AdvertisedRecordSet next = AdvertisedRecordSet.FromInputs(_inputProvider.GetCurrent());
        _store.Update(next);

        // TODO(diagnostic): temporary. Trace which DNS-SD instances are advertised on each publish so we
        // can confirm the operational (_matter._udp) service goes out after a fabric is added.
        MatterTrace.Write(() =>
        {
            var srvNames = next.Records
                .Where(r => r.Type == DnsRecordType.Srv)
                .Select(r => r.Name.ToString())
                .ToArray();
            return $"[mdns-advertise] publishing {srvNames.Length} SRV instance(s): {string.Join(", ", srvNames)}";
        });

        // Withdraw records that were advertised before but are gone now (the commissionable service after
        // the window closes, or an operational service after a fabric is removed) so peers flush them.
        IReadOnlyList<DnsResourceRecord> removed = Removals(previous, next);
        if (removed.Count > 0)
        {
            await _responder.SendGoodbyeAsync(removed, token).ConfigureAwait(false);
        }

        TimeSpan interval = _schedule.InitialInterval;
        for (int i = 0; i < _schedule.AnnounceCount; i++)
        {
            await _responder.AnnounceAsync(token).ConfigureAwait(false);

            if (i == _schedule.AnnounceCount - 1)
            {
                break;
            }

            try
            {
                await Task.Delay(interval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            // A newer change arrived mid-burst; abandon this schedule so the loop restarts fresh.
            if (Volatile.Read(ref _generation) != generation)
            {
                break;
            }

            interval = TimeSpan.FromTicks(Math.Min(interval.Ticks * 2, _schedule.MaxInterval.Ticks));
        }
    }

    private static IReadOnlyList<DnsResourceRecord> Removals(AdvertisedRecordSet previous, AdvertisedRecordSet next)
    {
        if (previous.IsEmpty)
        {
            return [];
        }

        var current = new HashSet<string>(StringComparer.Ordinal);
        foreach (DnsResourceRecord record in next.Records)
        {
            current.Add(NormalizedKey(record));
        }

        var removed = new List<DnsResourceRecord>();
        foreach (DnsResourceRecord record in previous.Records)
        {
            if (!current.Contains(NormalizedKey(record)))
            {
                removed.Add(record);
            }
        }

        return removed;
    }

    private static string NormalizedKey(DnsResourceRecord record)
    {
        // Identity independent of TTL and the cache-flush bit: name + type + rdata.
        var writer = new DnsWriter();
        (record with { Ttl = 0, CacheFlush = false }).Encode(writer);
        return Convert.ToHexString(writer.ToArray());
    }
}