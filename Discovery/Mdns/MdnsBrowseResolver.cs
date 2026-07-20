using System.Net;
using RIoT2.Matter.Discovery.Dns;

namespace RIoT2.Matter.Discovery.Mdns;

/// <summary>
/// The controller-side DNS-SD browse resolver. It issues PTR queries for Matter service types and
/// assembles the SRV/TXT/AAAA records from responses (both the answer and additional sections, possibly
/// spread across packets) into <see cref="DiscoveredService"/> instances, following up with a targeted
/// query for any instance introduced by a PTR that lacks an SRV. See RFC 6762 and RFC 6763.
/// </summary>
/// <remarks>
/// This is the composition root for discovery: it owns and disposes the supplied
/// <see cref="IMdnsInterface"/>. Records accumulate for the resolver's lifetime; goodbye packets
/// (TTL 0) for an instance's SRV/PTR remove it.
/// </remarks>
public sealed class MdnsBrowseResolver : IAsyncDisposable
{
    private readonly IMdnsInterface _iface;
    private readonly object _sync = new();
    private readonly HashSet<DnsSdServiceType> _browsedTypes = [];
    private readonly Dictionary<DnsName, InstanceState> _instances = [];
    private readonly Dictionary<DnsName, HashSet<IPAddress>> _hostAddresses = [];
    private bool _disposed;

    public MdnsBrowseResolver(IMdnsInterface iface)
    {
        _iface = iface ?? throw new ArgumentNullException(nameof(iface));
        _iface.DatagramReceived += OnDatagramReceived;
    }

    /// <summary>Raised when an instance first becomes fully resolved, or when its address set/port/TXT changes.</summary>
    public event EventHandler<DiscoveredService>? ServiceDiscovered;

    /// <summary>Raised when a previously resolved instance is withdrawn (a goodbye for its SRV/PTR).</summary>
    public event EventHandler<DiscoveredService>? ServiceRemoved;

    /// <summary>A snapshot of the currently resolved services.</summary>
    public IReadOnlyList<DiscoveredService> DiscoveredServices
    {
        get
        {
            lock (_sync)
            {
                var services = new List<DiscoveredService>(_instances.Count);
                foreach (InstanceState state in _instances.Values)
                {
                    if (state.Published is { } published)
                    {
                        services.Add(published);
                    }
                }

                return services;
            }
        }
    }

    /// <summary>Starts the mDNS interface so responses can be received.</summary>
    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _iface.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Multicasts a PTR query for <paramref name="serviceType"/> and continues resolving its instances.</summary>
    public async ValueTask BrowseAsync(DnsSdServiceType serviceType, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_sync)
        {
            _browsedTypes.Add(serviceType);
        }

        var query = new DnsMessage
        {
            Flags = DnsFlags.MulticastQuery,
            Questions = [new DnsQuestion { Name = serviceType.ServiceName, Type = DnsRecordType.Ptr }],
        };

        await _iface.SendMulticastAsync(query.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _iface.DatagramReceived -= OnDatagramReceived;
        await _iface.DisposeAsync().ConfigureAwait(false);
    }

    private void OnDatagramReceived(object? sender, MdnsDatagram datagram) => _ = HandleAsync(datagram);

    private async Task HandleAsync(MdnsDatagram datagram)
    {
        try
        {
            if (!DnsMessage.TryParse(datagram.Payload.Span, out DnsMessage message) || !message.IsResponse)
            {
                return;
            }

            List<DiscoveredService> discovered = [];
            List<DiscoveredService> removed = [];
            List<DnsName> toResolve = [];

            lock (_sync)
            {
                Process(message, discovered, removed, toResolve);
            }

            foreach (DiscoveredService service in discovered)
            {
                ServiceDiscovered?.Invoke(this, service);
            }

            foreach (DiscoveredService service in removed)
            {
                ServiceRemoved?.Invoke(this, service);
            }

            foreach (DnsName instance in toResolve)
            {
                await SendResolveQueryAsync(instance).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Fire-and-forget boundary: a malformed response or transient send failure must not escape.
        }
    }

    private void Process(DnsMessage message, List<DiscoveredService> discovered, List<DiscoveredService> removed, List<DnsName> toResolve)
    {
        foreach (DnsResourceRecord record in Enumerate(message))
        {
            switch (record)
            {
                case AaaaRecord aaaa:
                    UpdateAddress(aaaa.Name, aaaa.Address, aaaa.Ttl);
                    break;

                case ARecord a:
                    UpdateAddress(a.Name, a.Address, a.Ttl);
                    break;

                case SrvRecord srv when IsBrowsedInstance(srv.Name, out DnsSdServiceType type, out string label):
                    if (srv.Ttl == 0)
                    {
                        RemoveInstance(srv.Name, removed);
                    }
                    else
                    {
                        InstanceState state = GetOrAdd(srv.Name, type, label);
                        state.Port = srv.Port;
                        state.Host = srv.Target;
                    }

                    break;

                case TxtRecord txt when IsBrowsedInstance(txt.Name, out DnsSdServiceType type, out string label):
                    GetOrAdd(txt.Name, type, label).TxtEntries = txt.Entries;
                    break;

                case PtrRecord ptr when IsBrowsedInstance(ptr.Target, out DnsSdServiceType type, out string label):
                    if (ptr.Ttl == 0)
                    {
                        RemoveInstance(ptr.Target, removed);
                    }
                    else
                    {
                        InstanceState state = GetOrAdd(ptr.Target, type, label);
                        if (state.Port is null)
                        {
                            // The PTR named an instance we have no SRV for yet; resolve it directly.
                            toResolve.Add(ptr.Target);
                        }
                    }

                    break;
            }
        }

        // Addresses and instance fields may have changed; re-evaluate every instance for completion.
        foreach (InstanceState state in _instances.Values)
        {
            TryPublish(state, discovered);
        }
    }

    private void UpdateAddress(DnsName host, IPAddress address, uint ttl)
    {
        if (!_hostAddresses.TryGetValue(host, out HashSet<IPAddress>? set))
        {
            if (ttl == 0)
            {
                return;
            }

            set = [];
            _hostAddresses[host] = set;
        }

        if (ttl == 0)
        {
            set.Remove(address);
        }
        else
        {
            set.Add(address);
        }
    }

    private InstanceState GetOrAdd(DnsName instanceName, DnsSdServiceType serviceType, string label)
    {
        if (!_instances.TryGetValue(instanceName, out InstanceState? state))
        {
            state = new InstanceState { ServiceType = serviceType, InstanceLabel = label };
            _instances[instanceName] = state;
        }

        return state;
    }

    private void RemoveInstance(DnsName instanceName, List<DiscoveredService> removed)
    {
        if (_instances.Remove(instanceName, out InstanceState? state) && state.Published is { } published)
        {
            removed.Add(published);
        }
    }

    private void TryPublish(InstanceState state, List<DiscoveredService> discovered)
    {
        if (state.Port is not ushort port || state.Host is not DnsName host)
        {
            return;
        }

        if (!_hostAddresses.TryGetValue(host, out HashSet<IPAddress>? set) || set.Count == 0)
        {
            return;
        }

        IPAddress[] addresses = [.. set.OrderBy(a => a.ToString(), StringComparer.Ordinal)];
        var service = new DiscoveredService
        {
            ServiceType = state.ServiceType,
            InstanceName = state.InstanceLabel,
            HostName = host,
            Port = port,
            Addresses = addresses,
            TxtEntries = state.TxtEntries ?? [],
        };

        string signature = Signature(service);
        if (signature == state.Signature)
        {
            return;
        }

        state.Signature = signature;
        state.Published = service;
        discovered.Add(service);
    }

    private async Task SendResolveQueryAsync(DnsName instanceName)
    {
        var query = new DnsMessage
        {
            Flags = DnsFlags.MulticastQuery,
            Questions = [new DnsQuestion { Name = instanceName, Type = DnsRecordType.Any }],
        };

        try
        {
            await _iface.SendMulticastAsync(query.ToArray()).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best-effort follow-up; the periodic browse will retry.
        }
    }

    private bool IsBrowsedInstance(DnsName instanceName, out DnsSdServiceType serviceType, out string instanceLabel)
    {
        return TryParseInstanceName(instanceName, out serviceType, out instanceLabel) && _browsedTypes.Contains(serviceType);
    }

    private static IEnumerable<DnsResourceRecord> Enumerate(DnsMessage message)
    {
        foreach (DnsResourceRecord record in message.Answers)
        {
            yield return record;
        }

        foreach (DnsResourceRecord record in message.Additionals)
        {
            yield return record;
        }
    }

    private static bool TryParseInstanceName(DnsName name, out DnsSdServiceType serviceType, out string instanceLabel)
    {
        serviceType = default;
        instanceLabel = string.Empty;

        IReadOnlyList<string> labels = name.Labels;

        // A Matter service-instance name is exactly <instance>.<service>.<protocol>.local.
        if (labels.Count != 4 || !string.Equals(labels[3], MdnsConstants.LocalDomain, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        serviceType = new DnsSdServiceType(labels[1], labels[2]);
        instanceLabel = labels[0];
        return true;
    }

    private static string Signature(DiscoveredService service) =>
        string.Join(
            '|',
            service.HostName,
            service.Port,
            string.Join(',', service.Addresses),
            string.Join(',', service.TxtEntries));

    private sealed class InstanceState
    {
        public required DnsSdServiceType ServiceType { get; init; }

        public required string InstanceLabel { get; init; }

        public ushort? Port { get; set; }

        public DnsName? Host { get; set; }

        public IReadOnlyList<string>? TxtEntries { get; set; }

        public DiscoveredService? Published { get; set; }

        public string? Signature { get; set; }
    }
}