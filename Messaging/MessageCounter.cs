using System.Buffers.Binary;
using System.Security.Cryptography;

namespace RIoT2.Matter.Messaging;

/// <summary>
/// A monotonic 32-bit per-session outbound message counter. Each message sent on a session is
/// assigned the next counter value; the value never repeats within a session. See the Matter Core
/// Specification, section 4.5 (Message Counters).
/// </summary>
/// <remarks>
/// Secure-session counters are seeded with a random start value so counters are not predictable
/// across sessions. When the counter reaches its maximum the session must be torn down and
/// re-established rather than allowed to roll over (section 4.5.1.1).
/// </remarks>
public sealed class MessageCounter
{
    // Initial secure-session counter is random in [1, 2^28] to match connectedhomeip and leave
    // ample headroom before rollover (spec section 4.5.1.1).
    private const uint RandomInitMask = 0x0FFF_FFFF;

    private readonly object _gate = new();
    private uint _value;
    private bool _exhausted;

    /// <summary>Creates a counter starting at an explicit value (primarily for tests).</summary>
    public MessageCounter(uint initialValue) => _value = initialValue;

    /// <summary>Creates a counter seeded with a random start value in the range [1, 2^28].</summary>
    public static MessageCounter CreateRandom()
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        RandomNumberGenerator.Fill(bytes);
        uint initial = (BinaryPrimitives.ReadUInt32LittleEndian(bytes) & RandomInitMask) + 1;
        return new MessageCounter(initial);
    }

    /// <summary>The value that the next call to <see cref="Next"/> will return.</summary>
    public uint Current
    {
        get
        {
            lock (_gate)
            {
                return _value;
            }
        }
    }

    /// <summary>True once the counter has been exhausted and the session must be re-established.</summary>
    public bool IsExhausted
    {
        get
        {
            lock (_gate)
            {
                return _exhausted;
            }
        }
    }

    /// <summary>
    /// Returns the counter value to place in the next outbound message header and advances the
    /// counter. Throws once the counter space is exhausted.
    /// </summary>
    public uint Next()
    {
        lock (_gate)
        {
            if (_exhausted)
            {
                throw new InvalidOperationException(
                    "The session message counter is exhausted; the session must be re-established.");
            }

            uint assigned = _value;
            if (_value == uint.MaxValue)
            {
                _exhausted = true;
            }
            else
            {
                _value++;
            }

            return assigned;
        }
    }
}