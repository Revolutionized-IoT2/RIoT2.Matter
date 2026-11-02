using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace RIoT2.Matter.Messaging;

/// <summary>
/// Owns the node's secure sessions: allocates unique local session ids, installs sessions produced
/// by PASE/CASE handshakes, resolves inbound messages to a session by id, and evicts idle sessions.
/// See the Matter Core Specification, section 4.8 (Secure Session Context).
/// </summary>
/// <remarks>
/// Local session ids are reserved at handshake start (so a concurrent handshake cannot pick the same
/// id) and consumed on install. Session id 0 is reserved for the unsecured session and is never
/// handed out. The transmit/receive message path (<see cref="IMessageSession"/>) is layered on top
/// of this registry once message framing and the transport sink are available.
/// <para>
/// Both tables are bounded (backlog item 8, I43): at most <see cref="MaxSessions"/> installed sessions,
/// where installing one more evicts the least recently used (spec §4.13.2.1), and at most
/// <see cref="MaxPendingHandshakes"/> reserved ids, beyond which <see cref="AllocateSessionId"/> throws
/// <see cref="SessionTableFullException"/> and the handshake is answered Busy.
/// </para>
/// </remarks>
public sealed class SessionManager : IDisposable
{
    /// <summary>The reserved id of the unsecured session; never allocated to a secure session.</summary>
    public const ushort UnsecuredSessionId = 0;

    private const int MaxAllocationAttempts = 1000;

    /// <summary>The default bound of installed secure sessions: 16 fabrics with 3 CASE sessions each, plus PASE.</summary>
    public const int DefaultMaxSessions = 64;

    /// <summary>The default bound of handshakes in flight (reserved, not yet installed session ids).</summary>
    public const int DefaultMaxPendingHandshakes = 32;

    private readonly ConcurrentDictionary<ushort, SecureSessionRegistration> _sessions = new();
    private readonly ConcurrentDictionary<ushort, byte> _reservedIds = new();
    private readonly TimeProvider _timeProvider;
    private readonly object _allocationGate = new();
    private bool _disposed;

    public SessionManager(
        TimeProvider? timeProvider = null,
        int maxSessions = DefaultMaxSessions,
        int maxPendingHandshakes = DefaultMaxPendingHandshakes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSessions, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPendingHandshakes, 1);
        _timeProvider = timeProvider ?? TimeProvider.System;
        MaxSessions = maxSessions;
        MaxPendingHandshakes = maxPendingHandshakes;
    }

    /// <summary>The number of currently installed secure sessions.</summary>
    public int Count => _sessions.Count;

    /// <summary>The number of reserved session ids of handshakes in flight.</summary>
    public int PendingHandshakes => _reservedIds.Count;

    /// <summary>The most installed sessions; one more evicts the least recently used.</summary>
    public int MaxSessions { get; }

    /// <summary>The most handshakes in flight; one more is refused with <see cref="SessionTableFullException"/>.</summary>
    public int MaxPendingHandshakes { get; }

    /// <summary>
    /// Reserves and returns a unique local session id in [1, 65535] for an in-flight handshake.
    /// The id is excluded from further allocation until the session is installed via
    /// <see cref="RegisterSecureSession"/> or released via <see cref="ReleaseSessionId"/>.
    /// </summary>
    /// <exception cref="SessionTableFullException"><see cref="MaxPendingHandshakes"/> handshakes are in flight.</exception>
    public ushort AllocateSessionId() => AllocateSessionId(ignorePendingLimit: false);

    /// <summary>
    /// <see cref="AllocateSessionId()"/>, optionally beyond <see cref="MaxPendingHandshakes"/>: for the one
    /// id a commissioning window reserves, which peers can't multiply.
    /// </summary>
    /// <exception cref="SessionTableFullException">No free id is left, or too many handshakes are in flight.</exception>
    public ushort AllocateSessionId(bool ignorePendingLimit)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_allocationGate)
        {
            if (!ignorePendingLimit && _reservedIds.Count >= MaxPendingHandshakes)
            {
                throw new SessionTableFullException(
                    $"{MaxPendingHandshakes} session handshakes are already in flight.");
            }

            for (int attempt = 0; attempt < MaxAllocationAttempts; attempt++)
            {
                // Unpredictable ids (S10): a peer must not guess the next session's id.
                var candidate = (ushort)RandomNumberGenerator.GetInt32(1, ushort.MaxValue + 1);
                if (!_sessions.ContainsKey(candidate) && _reservedIds.TryAdd(candidate, 0))
                {
                    return candidate;
                }
            }

            throw new SessionTableFullException(
                "Unable to allocate a free session id; the session table may be exhausted.");
        }
    }

    /// <summary>Releases a reserved id whose handshake failed before the session was installed.</summary>
    public void ReleaseSessionId(ushort sessionId) => _reservedIds.TryRemove(sessionId, out _);

    /// <summary>Installs a fully-established secure session, keyed by its local session id.</summary>
    public SecureSessionRegistration RegisterSecureSession(SecureSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (session.LocalSessionId == UnsecuredSessionId)
        {
            throw new ArgumentException("Session id 0 is reserved for the unsecured session.", nameof(session));
        }

        var registration = new SecureSessionRegistration(session, _timeProvider);
        lock (_allocationGate)
        {
            if (_sessions.ContainsKey(session.LocalSessionId))
            {
                registration.Dispose();
                throw new InvalidOperationException(
                    $"A session with local id {session.LocalSessionId} is already installed.");
            }

            if (_sessions.Count >= MaxSessions)
            {
                EvictLeastRecentlyUsedLocked();
            }

            _sessions[session.LocalSessionId] = registration;
            _reservedIds.TryRemove(session.LocalSessionId, out _);
        }

        return registration;
    }

    /// <summary>Resolves the session addressed by a message's local session id, if installed.</summary>
    public bool TryGetSecureSession(ushort localSessionId, [NotNullWhen(true)] out SecureSessionRegistration? registration) =>
        _sessions.TryGetValue(localSessionId, out registration);

    /// <summary>Removes and disposes an installed session (e.g. on CloseSession or teardown).</summary>
    public bool RemoveSession(ushort localSessionId)
    {
        _reservedIds.TryRemove(localSessionId, out _);
        if (_sessions.TryRemove(localSessionId, out var registration))
        {
            registration.Dispose();
            return true;
        }

        return false;
    }

    /// <summary>Evicts and disposes sessions idle for at least <paramref name="idleTimeout"/>. Returns the count removed.</summary>
    public int EvictIdleSessions(TimeSpan idleTimeout)
    {
        var evicted = 0;
        foreach (var pair in _sessions)
        {
            if (pair.Value.IdleTime >= idleTimeout && _sessions.TryRemove(pair.Key, out var registration))
            {
                registration.Dispose();
                evicted++;
            }
        }

        return evicted;
    }

    // The caller holds the allocation gate, so no session is installed meanwhile.
    private void EvictLeastRecentlyUsedLocked()
    {
        KeyValuePair<ushort, SecureSessionRegistration>? oldest = null;
        foreach (var pair in _sessions)
        {
            if (oldest is null || pair.Value.IdleTime > oldest.Value.Value.IdleTime)
            {
                oldest = pair;
            }
        }

        if (oldest is { } victim && _sessions.TryRemove(victim.Key, out var registration))
        {
            registration.Dispose();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var pair in _sessions)
        {
            pair.Value.Dispose();
        }

        _sessions.Clear();
        _reservedIds.Clear();
    }
}

/// <summary>
/// The session table can't take another handshake: <see cref="SessionManager.MaxPendingHandshakes"/> are
/// in flight, or no free id is left. A responder answers the handshake with Busy.
/// </summary>
public sealed class SessionTableFullException : InvalidOperationException
{
    public SessionTableFullException(string message) : base(message) { }
}
