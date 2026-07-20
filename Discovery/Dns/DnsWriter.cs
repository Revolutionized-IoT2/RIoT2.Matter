using System.Buffers.Binary;
using System.Text;

namespace RIoT2.Matter.Discovery.Dns;

/// <summary>
/// Serializes a DNS message into a self-managed growable buffer, applying RFC 1035 section 4.1.4
/// name compression across the whole message and back-patching RDATA length prefixes. A single
/// writer instance encodes one message so that compression pointers stay valid.
/// </summary>
public sealed class DnsWriter
{
    private const int MaxCompressionOffset = 0x3FFF;

    private readonly Dictionary<string, int> _nameOffsets = new(StringComparer.OrdinalIgnoreCase);
    private byte[] _buffer;
    private int _length;

    public DnsWriter(int initialCapacity = 512)
    {
        _buffer = new byte[Math.Max(initialCapacity, 12)];
    }

    /// <summary>The number of bytes written so far (also the current message offset).</summary>
    public int Length => _length;

    /// <summary>Writes a single byte.</summary>
    public void WriteByte(byte value)
    {
        EnsureCapacity(1);
        _buffer[_length++] = value;
    }

    /// <summary>Writes a 16-bit value in network (big-endian) byte order.</summary>
    public void WriteUInt16(ushort value)
    {
        EnsureCapacity(2);
        BinaryPrimitives.WriteUInt16BigEndian(_buffer.AsSpan(_length, 2), value);
        _length += 2;
    }

    /// <summary>Writes a 32-bit value in network (big-endian) byte order.</summary>
    public void WriteUInt32(uint value)
    {
        EnsureCapacity(4);
        BinaryPrimitives.WriteUInt32BigEndian(_buffer.AsSpan(_length, 4), value);
        _length += 4;
    }

    /// <summary>Writes a raw byte span verbatim.</summary>
    public void WriteBytes(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
        {
            return;
        }

        EnsureCapacity(value.Length);
        value.CopyTo(_buffer.AsSpan(_length));
        _length += value.Length;
    }

    /// <summary>Writes a domain name, emitting a compression pointer to any previously written suffix.</summary>
    public void WriteName(DnsName name)
    {
        IReadOnlyList<string> labels = name.Labels;
        for (int i = 0; i < labels.Count; i++)
        {
            string suffix = string.Join('.', labels.Skip(i));
            if (_nameOffsets.TryGetValue(suffix, out int pointer))
            {
                WriteUInt16((ushort)(0xC000 | pointer));
                return;
            }

            if (_length <= MaxCompressionOffset)
            {
                _nameOffsets[suffix] = _length;
            }

            string label = labels[i];
            int byteCount = Encoding.UTF8.GetByteCount(label);
            if (byteCount is 0 or > 63)
            {
                throw new InvalidOperationException($"DNS label '{label}' must be between 1 and 63 bytes.");
            }

            WriteByte((byte)byteCount);
            EnsureCapacity(byteCount);
            Encoding.UTF8.GetBytes(label, _buffer.AsSpan(_length, byteCount));
            _length += byteCount;
        }

        WriteByte(0);
    }

    /// <summary>Reserves the two-byte RDLENGTH prefix and returns a token for <see cref="EndRdata"/>.</summary>
    public int BeginRdata()
    {
        int position = _length;
        WriteUInt16(0);
        return position;
    }

    /// <summary>Back-patches the RDLENGTH prefix reserved by <see cref="BeginRdata"/>.</summary>
    public void EndRdata(int token)
    {
        int rdataLength = _length - (token + 2);
        if (rdataLength > ushort.MaxValue)
        {
            throw new InvalidOperationException("DNS RDATA exceeds 65535 bytes.");
        }

        BinaryPrimitives.WriteUInt16BigEndian(_buffer.AsSpan(token, 2), (ushort)rdataLength);
    }

    /// <summary>Returns the encoded message as a newly allocated array.</summary>
    public byte[] ToArray() => _buffer.AsSpan(0, _length).ToArray();

    private void EnsureCapacity(int extra)
    {
        if (_length + extra <= _buffer.Length)
        {
            return;
        }

        Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + extra));
    }
}