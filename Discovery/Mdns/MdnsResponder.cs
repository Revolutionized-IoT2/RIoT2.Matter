using RIoT2.Matter.Discovery.Dns;

namespace RIoT2.Matter.Discovery.Mdns;

/// <summary>
/// Answers inbound mDNS queries from the node's <see cref="AdvertisedRecordStore"/> and emits the
/// unsolicited announcements and goodbye packets that advertise and withdraw the node's services.
/// Query matching itself is delegated to <see cref="MdnsResponseComposer"/>; this type owns only the
/// network wiring and QU/QM reply routing. See RFC 6762 sections 6, 8, and 10.
/// </summary>
public sealed class MdnsResponder : IAsyncDisposable
{
    private readonly IMdnsInterface _iface;
    private readonly AdvertisedRecordStore _store;
    private bool _disposed;

    public MdnsResponder(IMdnsInterface iface, AdvertisedRecordStore store)
    {
        _iface = iface ?? throw new ArgumentNullException(nameof(iface));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _iface.DatagramReceived += OnDatagramReceived;
    }

    /// <summary>
    /// Multicasts an unsolicited response advertising every owned record (RFC 6762 section 8.3). The
    /// caller (the advertiser lifecycle) is responsible for repeating this per the spec's announce schedule.
    /// </summary>
    public async ValueTask AnnounceAsync(CancellationToken cancellationToken = default)
    {
        AdvertisedRecordSet records = _store.Current;
        if (records.IsEmpty)
        {
            return;
        }

        var message = new DnsMessage
        {
            Flags = DnsFlags.MulticastResponse,
            Answers = records.Records,
        };

        await _iface.SendMulticastAsync(message.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Multicasts a goodbye for every owned record (a copy with TTL 0) to flush peer caches (RFC 6762 section 10.1).</summary>
    public ValueTask SendGoodbyeAsync(CancellationToken cancellationToken = default) =>
        SendGoodbyeAsync(_store.Current.Records, cancellationToken);

    /// <summary>Multicasts a goodbye (TTL 0) for a specific set of records, e.g. a service being withdrawn.</summary>
    public async ValueTask SendGoodbyeAsync(IReadOnlyList<DnsResourceRecord> records, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0)
        {
            return;
        }

        var goodbye = new List<DnsResourceRecord>(records.Count);
        foreach (DnsResourceRecord record in records)
        {
            goodbye.Add(record with { Ttl = 0 });
        }

        var message = new DnsMessage
        {
            Flags = DnsFlags.MulticastResponse,
            Answers = goodbye,
        };

        await _iface.SendMulticastAsync(message.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _iface.DatagramReceived -= OnDatagramReceived;
        return ValueTask.CompletedTask;
    }

    // Event handlers must not block, so the response is dispatched as a fire-and-forget task.
    private void OnDatagramReceived(object? sender, MdnsDatagram datagram) => _ = RespondAsync(datagram);

    private async Task RespondAsync(MdnsDatagram datagram)
    {
        try
        {
            if (!DnsMessage.TryParse(datagram.Payload.Span, out DnsMessage query) ||
                query.IsResponse ||
                query.Questions.Count == 0)
            {
                return;
            }

            AdvertisedRecordSet records = _store.Current;
            if (records.IsEmpty)
            {
                return;
            }

            // A querier on a source port other than 5353 is a legacy unicast resolver (RFC 6762 section 6.7):
            // it always expects a unicast reply that echoes the question and transaction id.
            bool legacyUnicast = datagram.RemoteEndPoint.Port != MdnsConstants.Port;

            DnsMessage? response = MdnsResponseComposer.BuildResponse(query, records, includeQuestions: legacyUnicast);
            if (response is null)
            {
                return;
            }

            byte[] payload = response.ToArray();
            if (legacyUnicast || RequestsUnicast(query))
            {
                await _iface.SendUnicastAsync(payload, datagram.RemoteEndPoint).ConfigureAwait(false);
            }
            else
            {
                await _iface.SendMulticastAsync(payload).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Top-level boundary of a fire-and-forget handler: a malformed query or a transient send
            // failure must never surface as an unobserved task exception or stop the responder.
        }
    }

    private static bool RequestsUnicast(DnsMessage query)
    {
        foreach (DnsQuestion question in query.Questions)
        {
            if (question.UnicastResponse)
            {
                return true;
            }
        }

        return false;
    }
}