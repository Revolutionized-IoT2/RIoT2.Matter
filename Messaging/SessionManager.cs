using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

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
/// </remarks>
public sealed class SessionManager : IDisposable
{
    /// <summary>The reserved id of the unsecured session; never allocated to a secure session.</summary>
    public const ushort UnsecuredSessionId = 0;

    private const int MaxAllocationAttempts = 1000;

    private readonly ConcurrentDictionary<ushort, SecureSessionRegistration> _sessions = new();
    private readonly ConcurrentDictionary<ushort, byte> _reservedIds = new();
    private readonly TimeProvider _timeProvider;
    private readonly object _allocationGate = new();
    private bool _disposed;

    public SessionManager(TimeProvider? timeProvider = null) =>
        _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>The number of currently installed secure sessions.</summary>
    public int Count => _sessions.Count;

    /// <summary>
    /// Reserves and returns a unique local session id in [1, 65535] for an in-flight handshake.
    /// The id is excluded from further allocation until the session is installed via
    /// <see cref="RegisterSecureSession"/> or released via <see cref="ReleaseSessionId"/>.
    /// </summary>
    public ushort AllocateSessionId()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_allocationGate)
        {
            for (int attempt = 0; attempt < MaxAllocationAttempts; attempt++)
            {
                var candidate = (ushort)Random.Shared.Next(1, ushort.MaxValue + 1);
                if (!_sessions.ContainsKey(candidate) && _reservedIds.TryAdd(candidate, 0))
                {
                    return candidate;
                }
            }

            throw new InvalidOperationException(
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
        if (!_sessions.TryAdd(session.LocalSessionId, registration))
        {
            registration.Dispose();
            throw new InvalidOperationException(
                $"A session with local id {session.LocalSessionId} is already installed.");
        }

        _reservedIds.TryRemove(session.LocalSessionId, out _);
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