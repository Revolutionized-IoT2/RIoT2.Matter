using System.Buffers.Binary;

namespace RIoT2.Matter.SecureChannel;

/// <summary>
/// A Secure Channel StatusReport message. Reports the outcome of an operation (most often a
/// session-establishment step) via a general code plus a protocol-specific code and optional
/// data. See the Matter Core Specification, section 4.10.1.7.
/// </summary>
/// <remarks>
/// Wire layout (little-endian): GeneralCode (u16), ProtocolId (u32), ProtocolCode (u16),
/// followed by optional variable-length ProtocolData.
/// </remarks>
public readonly record struct SecureChannelStatusReport
{
    /// <summary>The number of fixed header bytes preceding any protocol data.</summary>
    public const int MinimumLength = 8;

    /// <summary>The general (protocol-agnostic) status code.</summary>
    public GeneralStatusCode GeneralCode { get; init; }

    /// <summary>The protocol the <see cref="ProtocolCode"/> is defined by.</summary>
    public uint ProtocolId { get; init; }

    /// <summary>The protocol-specific status code (see <see cref="SecureChannelStatusCode"/>).</summary>
    public ushort ProtocolCode { get; init; }

    /// <summary>Optional protocol-specific data appended after the fixed fields.</summary>
    public ReadOnlyMemory<byte> ProtocolData { get; init; }

    /// <summary>The <see cref="ProtocolCode"/> interpreted as a Secure Channel status code.</summary>
    public SecureChannelStatusCode SecureChannelStatus => (SecureChannelStatusCode)ProtocolCode;

    /// <summary>True when the report indicates success.</summary>
    public bool IsSuccess => GeneralCode == GeneralStatusCode.Success;

    /// <summary>Attempts to parse a StatusReport from the given payload.</summary>
    public static bool TryParse(ReadOnlySpan<byte> payload, out SecureChannelStatusReport report)
    {
        if (payload.Length < MinimumLength)
        {
            report = default;
            return false;
        }

        report = new SecureChannelStatusReport
        {
            GeneralCode = (GeneralStatusCode)BinaryPrimitives.ReadUInt16LittleEndian(payload),
            ProtocolId = BinaryPrimitives.ReadUInt32LittleEndian(payload[2..]),
            ProtocolCode = BinaryPrimitives.ReadUInt16LittleEndian(payload[6..]),
            ProtocolData = payload.Length > MinimumLength
                ? payload[MinimumLength..].ToArray()
                : ReadOnlyMemory<byte>.Empty,
        };
        return true;
    }

    /// <summary>Serializes this StatusReport into <paramref name="destination"/> and returns the byte count written.</summary>
    public int WriteTo(Span<byte> destination)
    {
        var length = MinimumLength + ProtocolData.Length;
        if (destination.Length < length)
        {
            throw new ArgumentException("Destination is too small for the StatusReport.", nameof(destination));
        }

        BinaryPrimitives.WriteUInt16LittleEndian(destination, (ushort)GeneralCode);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[2..], ProtocolId);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[6..], ProtocolCode);
        ProtocolData.Span.CopyTo(destination[MinimumLength..]);
        return length;
    }

    /// <summary>Serializes this StatusReport into a newly allocated array.</summary>
    public byte[] ToArray()
    {
        var buffer = new byte[MinimumLength + ProtocolData.Length];
        WriteTo(buffer);
        return buffer;
    }
}