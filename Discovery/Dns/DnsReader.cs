using System.Buffers.Binary;
using System.Text;

namespace RIoT2.Matter.Discovery.Dns;

/// <summary>
/// A forward-only reader over a complete DNS message. Name reading resolves RFC 1035 section 4.1.4
/// compression pointers, which requires access to the full message buffer from offset zero.
/// </summary>
public ref struct DnsReader
{
    private readonly ReadOnlySpan<byte> _message;
    private int _position;

    public DnsReader(ReadOnlySpan<byte> message)
    {
        _message = message;
        _position = 0;
    }

    /// <summary>The current read offset. Settable so callers can align to a record's RDATA end.</summary>
    public int Position
    {
        readonly get => _position;
        set => _position = value;
    }

    /// <summary>The total message length.</summary>
    public readonly int Length => _message.Length;

    /// <summary>Reads a single byte.</summary>
    public byte ReadByte()
    {
        EnsureReadable(_position, 1);
        return _message[_position++];
    }

    /// <summary>Reads a 16-bit big-endian value.</summary>
    public ushort ReadUInt16()
    {
        EnsureReadable(_position, 2);
        ushort value = BinaryPrimitives.ReadUInt16BigEndian(_message.Slice(_position, 2));
        _position += 2;
        return value;
    }

    /// <summary>Reads a 32-bit big-endian value.</summary>
    public uint ReadUInt32()
    {
        EnsureReadable(_position, 4);
        uint value = BinaryPrimitives.ReadUInt32BigEndian(_message.Slice(_position, 4));
        _position += 4;
        return value;
    }

    /// <summary>Reads <paramref name="count"/> bytes as a span over the underlying message.</summary>
    public ReadOnlySpan<byte> ReadBytes(int count)
    {
        EnsureReadable(_position, count);
        ReadOnlySpan<byte> slice = _message.Slice(_position, count);
        _position += count;
        return slice;
    }

    /// <summary>Reads a domain name, following compression pointers (which must point backwards).</summary>
    public DnsName ReadName()
    {
        var labels = new List<string>();
        int position = _position;
        int continuation = -1;
        int guard = 0;

        while (true)
        {
            EnsureReadable(position, 1);
            byte length = _message[position];

            if ((length & 0xC0) == 0xC0)
            {
                EnsureReadable(position, 2);
                int pointer = ((length & 0x3F) << 8) | _message[position + 1];
                if (continuation < 0)
                {
                    continuation = position + 2;
                }

                if (pointer >= position)
                {
                    throw new InvalidDataException("DNS name compression pointer does not point backwards.");
                }

                position = pointer;
                if (++guard > _message.Length)
                {
                    throw new InvalidDataException("DNS name compression loop detected.");
                }

                continue;
            }

            if ((length & 0xC0) != 0)
            {
                throw new InvalidDataException("DNS label has reserved high bits set.");
            }

            if (length == 0)
            {
                if (continuation < 0)
                {
                    continuation = position + 1;
                }

                break;
            }

            position++;
            EnsureReadable(position, length);
            labels.Add(Encoding.UTF8.GetString(_message.Slice(position, length)));
            position += length;
            if (++guard > _message.Length)
            {
                throw new InvalidDataException("DNS name is malformed.");
            }
        }

        _position = continuation;
        return new DnsName([.. labels]);
    }

    private readonly void EnsureReadable(int start, int count)
    {
        if (count < 0 || start > _message.Length || count > _message.Length - start)
        {
            throw new InvalidDataException("Unexpected end of DNS message.");
        }
    }
}