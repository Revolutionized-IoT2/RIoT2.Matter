namespace RIoT2.Matter.Messaging;

/// <summary>
/// Tracks the highest accepted message counter and a sliding bitmap of recently seen counters to
/// detect duplicate (replayed) messages on a session. See the Matter Core Specification,
/// section 4.6 (Message Counter Synchronization) and its duplicate-message detection algorithm.
/// </summary>
/// <remarks>
/// The first accepted message synchronizes the window (trust-first); this is safe for secure
/// sessions because messages are authenticated before reaching this check. Secure (encrypted)
/// sessions do not permit counter rollover, whereas the unsecured session's global counter does.
/// </remarks>
public sealed class MessageReceptionState
{
    /// <summary>MSG_COUNTER_WINDOW_SIZE: the number of prior counters tracked behind the maximum.</summary>
    public const int WindowSize = 32;

    private readonly object _gate = new();
    private readonly bool _rolloverAllowed;
    private uint _maxCounter;
    private uint _bitmap;
    private bool _synced;

    /// <param name="rolloverAllowed">
    /// True for the unsecured session's global counter (which may wrap); false for secure sessions,
    /// which are re-established before their counter can roll over.
    /// </param>
    public MessageReceptionState(bool rolloverAllowed = false) => _rolloverAllowed = rolloverAllowed;

    /// <summary>True once the first message has synchronized the window.</summary>
    public bool IsSynced
    {
        get
        {
            lock (_gate)
            {
                return _synced;
            }
        }
    }

    /// <summary>The highest message counter accepted so far.</summary>
    public uint MaxMessageCounter
    {
        get
        {
            lock (_gate)
            {
                return _maxCounter;
            }
        }
    }

    /// <summary>
    /// Tests <paramref name="counter"/> against the window and, if it is fresh, records it.
    /// Returns <see langword="true"/> when the message should be accepted, or <see langword="false"/>
    /// when it is a duplicate or falls outside the replay window and must be dropped.
    /// </summary>
    public bool TryAccept(uint counter) => TryAccept(counter, out _);

    /// <summary>
    /// As <see cref="TryAccept(uint)"/>, additionally classifying a rejection via
    /// <paramref name="isDuplicate"/>: <see langword="true"/> when <paramref name="counter"/> is one
    /// we have already accepted (almost always an MRP retransmission of a message we already
    /// processed, sent because our acknowledgement for it was lost); <see langword="false"/> when it
    /// falls outside the tracked window entirely (too old to tell, so treated as a plain replay). The
    /// caller should still acknowledge a duplicate - without reprocessing it - so the sender's
    /// retransmit timer clears (spec 4.12.5); a message that is merely too old gets no such courtesy.
    /// </summary>
    public bool TryAccept(uint counter, out bool isDuplicate)
    {
        lock (_gate)
        {
            isDuplicate = false;

            if (!_synced)
            {
                // Trust the first authenticated message and anchor the window on it.
                _maxCounter = counter;
                _bitmap = 0;
                _synced = true;
                return true;
            }

            // Distance to the current max: serial-number arithmetic when rollover is allowed,
            // otherwise a plain comparison that rejects wrapped-around counters as too old.
            long distance = _rolloverAllowed
                ? unchecked((int)(counter - _maxCounter))
                : (long)counter - _maxCounter;

            if (distance == 0)
            {
                isDuplicate = true; // exactly the current max: duplicate
                return false;
            }

            if (distance > 0)
            {
                // A newer counter: slide the window forward, recording the previous max.
                // Note: C# masks uint shift counts to 5 bits, so shifts >= 32 must be special-cased.
                uint shift = (uint)distance;
                _bitmap = shift >= WindowSize
                    ? 0u
                    : (_bitmap << (int)shift) | (1u << (int)(shift - 1));

                _maxCounter = counter;
                return true;
            }

            // An older counter: accept only if it is inside the window and not already seen.
            uint offset = (uint)(-distance);
            if (offset > WindowSize)
            {
                return false; // outside the window: too old to tell if it's a duplicate
            }

            uint mask = 1u << (int)(offset - 1);
            if ((_bitmap & mask) != 0)
            {
                isDuplicate = true; // already seen: duplicate
                return false;
            }

            _bitmap |= mask;
            return true;
        }
    }
}