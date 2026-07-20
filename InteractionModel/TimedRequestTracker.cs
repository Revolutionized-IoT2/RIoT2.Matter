using System.Collections.Concurrent;
using RIoT2.Matter.Messaging;

namespace RIoT2.Matter.InteractionModel;

/// <summary>
/// Tracks the timed-interaction window opened by a <see cref="TimedRequestMessage"/> on each
/// exchange. The following timed Write or Invoke must arrive on the same exchange before the window
/// elapses. See the Matter Core Specification, section 8.5.3.
/// </summary>
public sealed class TimedRequestTracker
{
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<ExchangeContext, TimedWindow> _windows = new();

    public TimedRequestTracker(TimeProvider? timeProvider = null)
        => _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>Opens (or replaces) the timed window on <paramref name="exchange"/>.</summary>
    public void Open(ExchangeContext exchange, ushort timeoutMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        _windows[exchange] = new TimedWindow(_timeProvider.GetTimestamp(), TimeSpan.FromMilliseconds(timeoutMilliseconds));
    }

    /// <summary>
    /// Consumes the window on <paramref name="exchange"/>, if any. Returns whether a window existed;
    /// when it did, <paramref name="expired"/> reports whether it had already elapsed.
    /// </summary>
    public bool TryConsume(ExchangeContext exchange, out bool expired)
    {
        ArgumentNullException.ThrowIfNull(exchange);

        if (_windows.TryRemove(exchange, out var window))
        {
            expired = _timeProvider.GetElapsedTime(window.OpenedTimestamp) > window.Timeout;
            return true;
        }

        expired = false;
        return false;
    }

    /// <summary>Discards any window on <paramref name="exchange"/>. Call when the exchange closes.</summary>
    public void Remove(ExchangeContext exchange) => _windows.TryRemove(exchange, out _);

    private readonly record struct TimedWindow(long OpenedTimestamp, TimeSpan Timeout);
}