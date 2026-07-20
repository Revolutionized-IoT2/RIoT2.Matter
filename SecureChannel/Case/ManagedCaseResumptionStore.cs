using System.Security.Cryptography;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.Hosting;

namespace RIoT2.Matter.SecureChannel.Case;

/// <summary>
/// The portable, in-memory <see cref="ICaseResumptionStore"/>. Keeps a bounded set of resumption
/// records, evicting the least recently used one when full and zeroing the shared secret of any
/// record it drops or replaces. See the Matter Core Specification, section 4.14.2.6.
/// </summary>
/// <remarks>
/// Thread-safe. At most one record is retained per peer: saving a new record for a peer replaces the
/// previous one (a fresh resumption id is issued on every successful handshake). This is process
/// memory only; a deployment that must resume across restarts should provide a persistent store.
/// </remarks>
public sealed class ManagedCaseResumptionStore : ICaseResumptionStore
{
    /// <summary>The default number of resumption records retained (Matter recommends at least 4).</summary>
    public const int DefaultCapacity = 8;

    private readonly int _capacity;
    private readonly object _gate = new();

    // Most-recently-used at the end of the list; index for O(1) lookups by peer and by resumption id.
    private readonly LinkedList<CaseResumptionRecord> _lru = new();
    private readonly Dictionary<OperationalPeer, LinkedListNode<CaseResumptionRecord>> _byPeer = new();

    /// <param name="capacity">The maximum number of records to retain; must be positive.</param>
    public ManagedCaseResumptionStore(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
    }

    /// <inheritdoc />
    public void Save(CaseResumptionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        lock (_gate)
        {
            // Replace any existing record for this peer, zeroing its secret.
            if (_byPeer.TryGetValue(record.Peer, out var existing))
            {
                CryptographicOperations.ZeroMemory(existing.Value.SharedSecret);
                _lru.Remove(existing);
                _byPeer.Remove(record.Peer);
            }

            // Evict the least recently used record when at capacity.
            while (_lru.Count >= _capacity && _lru.First is { } oldest)
            {
                CryptographicOperations.ZeroMemory(oldest.Value.SharedSecret);
                _lru.RemoveFirst();
                _byPeer.Remove(oldest.Value.Peer);
            }

            var node = _lru.AddLast(record);
            _byPeer[record.Peer] = node;
        }
    }

    /// <inheritdoc />
    public bool TryGetByResumptionId(ReadOnlySpan<byte> resumptionId, out CaseResumptionRecord record)
    {
        lock (_gate)
        {
            for (var node = _lru.Last; node is not null; node = node.Previous)
            {
                if (CryptographicOperations.FixedTimeEquals(node.Value.ResumptionId, resumptionId))
                {
                    Touch(node);
                    record = node.Value;
                    return true;
                }
            }
        }

        record = null!;
        return false;
    }

    /// <inheritdoc />
    public bool TryGetByPeer(OperationalPeer peer, out CaseResumptionRecord record)
    {
        lock (_gate)
        {
            if (_byPeer.TryGetValue(peer, out var node))
            {
                Touch(node);
                record = node.Value;
                return true;
            }
        }

        record = null!;
        return false;
    }

    /// <inheritdoc />
    public void Remove(ReadOnlySpan<byte> resumptionId)
    {
        lock (_gate)
        {
            for (var node = _lru.First; node is not null; node = node.Next)
            {
                if (CryptographicOperations.FixedTimeEquals(node.Value.ResumptionId, resumptionId))
                {
                    CryptographicOperations.ZeroMemory(node.Value.SharedSecret);
                    _lru.Remove(node);
                    _byPeer.Remove(node.Value.Peer);
                    return;
                }
            }
        }
    }

    // Marks a node as most-recently-used. Caller holds the lock.
    private void Touch(LinkedListNode<CaseResumptionRecord> node)
    {
        if (!ReferenceEquals(_lru.Last, node))
        {
            _lru.Remove(node);
            _lru.AddLast(node);
        }
    }
}