using System.Buffers;
using System.Buffers.Binary;
using RIoT2.Matter.DataModel;

namespace RIoT2.Matter.Messaging;

/// <summary>
/// Encodes and decodes the cleartext Matter message framing (message header and
/// payload header). All multi-byte fields are little-endian. Message and secured
/// extensions are not supported and cause <see cref="NotSupportedException"/>.
/// </summary>
public static class MatterMessageCodec
{
    // Message flags (first header byte).
    private const int VersionShift = 4;
    private const byte SourceNodeIdPresentFlag = 0x04;
    private const byte DestinationSizeMask = 0x03;

    // Security flags.
    private const byte PrivacyFlag = 0x80;
    private const byte ControlMessageFlag = 0x40;
    private const byte MessageExtensionsFlag = 0x20;
    private const byte SessionTypeMask = 0x03;

    // Exchange (payload header) flags.
    private const byte InitiatorFlag = 0x01;
    private const byte AckFlag = 0x02;
    private const byte ReliabilityFlag = 0x04;
    private const byte SecuredExtensionsFlag = 0x08;
    private const byte VendorFlag = 0x10;

    // Destination size (DSIZ) values.
    private const byte DestinationNone = 0x00;
    private const byte DestinationNode = 0x01;
    private const byte DestinationGroup = 0x02;

    /// <summary>Encodes a message into a newly allocated array. Convenience over the buffer overload.</summary>
    public static byte[] Encode(in MessageHeader header, in ProtocolHeader protocol, ReadOnlySpan<byte> applicationPayload)
    {
        var buffer = new ArrayBufferWriter<byte>();
        Encode(buffer, header, protocol, applicationPayload);
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Encodes a message into the supplied buffer.</summary>
    public static void Encode(IBufferWriter<byte> buffer, in MessageHeader header, in ProtocolHeader protocol, ReadOnlySpan<byte> applicationPayload)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        if (header.DestinationNodeId.HasValue && header.DestinationGroupId.HasValue)
        {
            throw new ArgumentException("A message cannot target both a destination node id and a destination group id.", nameof(header));
        }

        byte destinationSize = header.DestinationNodeId.HasValue ? DestinationNode
            : header.DestinationGroupId.HasValue ? DestinationGroup
            : DestinationNone;

        byte messageFlags = (byte)((header.Version & 0x0F) << VersionShift);
        if (header.SourceNodeId.HasValue)
        {
            messageFlags |= SourceNodeIdPresentFlag;
        }

        messageFlags |= destinationSize;

        byte securityFlags = (byte)((byte)header.SessionType & SessionTypeMask);
        if (header.IsControlMessage)
        {
            securityFlags |= ControlMessageFlag;
        }

        if (header.HasPrivacy)
        {
            securityFlags |= PrivacyFlag;
        }

        WriteByte(buffer, messageFlags);
        WriteUInt16(buffer, header.SessionId);
        WriteByte(buffer, securityFlags);
        WriteUInt32(buffer, header.MessageCounter);

        if (header.SourceNodeId.HasValue)
        {
            WriteUInt64(buffer, header.SourceNodeId.Value.Value);
        }

        if (header.DestinationNodeId.HasValue)
        {
            WriteUInt64(buffer, header.DestinationNodeId.Value.Value);
        }
        else if (header.DestinationGroupId.HasValue)
        {
            WriteUInt16(buffer, header.DestinationGroupId.Value.Value);
        }

        byte exchangeFlags = 0;
        if (protocol.IsInitiator)
        {
            exchangeFlags |= InitiatorFlag;
        }

        if (protocol.AcknowledgedMessageCounter.HasValue)
        {
            exchangeFlags |= AckFlag;
        }

        if (protocol.IsReliable)
        {
            exchangeFlags |= ReliabilityFlag;
        }

        if (protocol.ProtocolVendorId.HasValue)
        {
            exchangeFlags |= VendorFlag;
        }

        WriteByte(buffer, exchangeFlags);
        WriteByte(buffer, protocol.ProtocolOpcode);
        WriteUInt16(buffer, protocol.ExchangeId);

        if (protocol.ProtocolVendorId.HasValue)
        {
            WriteUInt16(buffer, protocol.ProtocolVendorId.Value);
        }

        WriteUInt16(buffer, protocol.ProtocolId);

        if (protocol.AcknowledgedMessageCounter.HasValue)
        {
            WriteUInt32(buffer, protocol.AcknowledgedMessageCounter.Value);
        }

        if (!applicationPayload.IsEmpty)
        {
            Span<byte> span = buffer.GetSpan(applicationPayload.Length);
            applicationPayload.CopyTo(span);
            buffer.Advance(applicationPayload.Length);
        }
    }

    /// <summary>Decodes a cleartext Matter message. The returned payload aliases <paramref name="data"/>.</summary>
    public static MatterMessage Decode(ReadOnlyMemory<byte> data)
    {
        ReadOnlySpan<byte> span = data.Span;
        int position = 0;
        var header = DecodeMessageHeader(span, ref position);
        var protocol = DecodeProtocolHeader(span, ref position);
        return new MatterMessage(header, protocol, data.Slice(position));
    }

    /// <summary>Encodes only the message header — the cleartext AAD prefix of a secure message.</summary>
    public static void EncodeMessageHeader(IBufferWriter<byte> buffer, in MessageHeader header)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        if (header.DestinationNodeId.HasValue && header.DestinationGroupId.HasValue)
        {
            throw new ArgumentException("A message cannot target both a destination node id and a destination group id.", nameof(header));
        }

        byte destinationSize = header.DestinationNodeId.HasValue ? DestinationNode
            : header.DestinationGroupId.HasValue ? DestinationGroup
            : DestinationNone;

        byte messageFlags = (byte)((header.Version & 0x0F) << VersionShift);
        if (header.SourceNodeId.HasValue)
        {
            messageFlags |= SourceNodeIdPresentFlag;
        }

        messageFlags |= destinationSize;

        WriteByte(buffer, messageFlags);
        WriteUInt16(buffer, header.SessionId);
        WriteByte(buffer, GetSecurityFlags(header));
        WriteUInt32(buffer, header.MessageCounter);

        if (header.SourceNodeId.HasValue)
        {
            WriteUInt64(buffer, header.SourceNodeId.Value.Value);
        }

        if (header.DestinationNodeId.HasValue)
        {
            WriteUInt64(buffer, header.DestinationNodeId.Value.Value);
        }
        else if (header.DestinationGroupId.HasValue)
        {
            WriteUInt16(buffer, header.DestinationGroupId.Value.Value);
        }
    }

    /// <summary>Computes the security-flags byte for a header, matching the byte written into the encoding.</summary>
    public static byte GetSecurityFlags(in MessageHeader header)
    {
        byte securityFlags = (byte)((byte)header.SessionType & SessionTypeMask);
        if (header.IsControlMessage)
        {
            securityFlags |= ControlMessageFlag;
        }

        if (header.HasPrivacy)
        {
            securityFlags |= PrivacyFlag;
        }

        return securityFlags;
    }

    /// <summary>Encodes only the protocol (payload) header — the plaintext prefix encrypted with the payload.</summary>
    public static void EncodeProtocolHeader(IBufferWriter<byte> buffer, in ProtocolHeader protocol)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        byte exchangeFlags = 0;
        if (protocol.IsInitiator)
        {
            exchangeFlags |= InitiatorFlag;
        }

        if (protocol.AcknowledgedMessageCounter.HasValue)
        {
            exchangeFlags |= AckFlag;
        }

        if (protocol.IsReliable)
        {
            exchangeFlags |= ReliabilityFlag;
        }

        if (protocol.ProtocolVendorId.HasValue)
        {
            exchangeFlags |= VendorFlag;
        }

        WriteByte(buffer, exchangeFlags);
        WriteByte(buffer, protocol.ProtocolOpcode);
        WriteUInt16(buffer, protocol.ExchangeId);

        if (protocol.ProtocolVendorId.HasValue)
        {
            WriteUInt16(buffer, protocol.ProtocolVendorId.Value);
        }

        WriteUInt16(buffer, protocol.ProtocolId);

        if (protocol.AcknowledgedMessageCounter.HasValue)
        {
            WriteUInt32(buffer, protocol.AcknowledgedMessageCounter.Value);
        }
    }

    /// <summary>Decodes only the message header, advancing <paramref name="position"/> past it.</summary>
    public static MessageHeader DecodeMessageHeader(ReadOnlySpan<byte> span, ref int position)
    {
        byte messageFlags = ReadByte(span, ref position);
        var version = (byte)((messageFlags >> VersionShift) & 0x0F);
        bool hasSource = (messageFlags & SourceNodeIdPresentFlag) != 0;
        byte destinationSize = (byte)(messageFlags & DestinationSizeMask);

        ushort sessionId = ReadUInt16(span, ref position);
        byte securityFlags = ReadByte(span, ref position);

        if ((securityFlags & MessageExtensionsFlag) != 0)
        {
            throw new NotSupportedException("Message extensions are not supported.");
        }

        var sessionType = (SessionType)(securityFlags & SessionTypeMask);
        bool isControl = (securityFlags & ControlMessageFlag) != 0;
        bool hasPrivacy = (securityFlags & PrivacyFlag) != 0;

        uint messageCounter = ReadUInt32(span, ref position);
        NodeId? sourceNodeId = hasSource ? new NodeId(ReadUInt64(span, ref position)) : null;

        NodeId? destinationNodeId = null;
        GroupId? destinationGroupId = null;
        switch (destinationSize)
        {
            case DestinationNone:
                break;
            case DestinationNode:
                destinationNodeId = new NodeId(ReadUInt64(span, ref position));
                break;
            case DestinationGroup:
                destinationGroupId = new GroupId(ReadUInt16(span, ref position));
                break;
            default:
                throw new NotSupportedException("Reserved destination size type.");
        }

        return new MessageHeader
        {
            Version = version,
            SessionId = sessionId,
            SessionType = sessionType,
            IsControlMessage = isControl,
            HasPrivacy = hasPrivacy,
            MessageCounter = messageCounter,
            SourceNodeId = sourceNodeId,
            DestinationNodeId = destinationNodeId,
            DestinationGroupId = destinationGroupId,
        };
    }

    /// <summary>Decodes only the protocol header, advancing <paramref name="position"/> past it.</summary>
    public static ProtocolHeader DecodeProtocolHeader(ReadOnlySpan<byte> span, ref int position)
    {
        byte exchangeFlags = ReadByte(span, ref position);
        bool isInitiator = (exchangeFlags & InitiatorFlag) != 0;
        bool hasAck = (exchangeFlags & AckFlag) != 0;
        bool isReliable = (exchangeFlags & ReliabilityFlag) != 0;
        bool hasVendor = (exchangeFlags & VendorFlag) != 0;

        if ((exchangeFlags & SecuredExtensionsFlag) != 0)
        {
            throw new NotSupportedException("Secured extensions are not supported.");
        }

        byte opcode = ReadByte(span, ref position);
        ushort exchangeId = ReadUInt16(span, ref position);
        ushort? vendorId = hasVendor ? ReadUInt16(span, ref position) : null;
        ushort protocolId = ReadUInt16(span, ref position);
        uint? ackCounter = hasAck ? ReadUInt32(span, ref position) : null;

        return new ProtocolHeader
        {
            IsInitiator = isInitiator,
            IsReliable = isReliable,
            ProtocolOpcode = opcode,
            ExchangeId = exchangeId,
            ProtocolId = protocolId,
            ProtocolVendorId = vendorId,
            AcknowledgedMessageCounter = ackCounter,
        };
    }

    private static void WriteByte(IBufferWriter<byte> buffer, byte value)
    {
        Span<byte> span = buffer.GetSpan(1);
        span[0] = value;
        buffer.Advance(1);
    }

    private static void WriteUInt16(IBufferWriter<byte> buffer, ushort value)
    {
        Span<byte> span = buffer.GetSpan(2);
        BinaryPrimitives.WriteUInt16LittleEndian(span, value);
        buffer.Advance(2);
    }

    private static void WriteUInt32(IBufferWriter<byte> buffer, uint value)
    {
        Span<byte> span = buffer.GetSpan(4);
        BinaryPrimitives.WriteUInt32LittleEndian(span, value);
        buffer.Advance(4);
    }

    private static void WriteUInt64(IBufferWriter<byte> buffer, ulong value)
    {
        Span<byte> span = buffer.GetSpan(8);
        BinaryPrimitives.WriteUInt64LittleEndian(span, value);
        buffer.Advance(8);
    }

    private static byte ReadByte(ReadOnlySpan<byte> span, ref int position) => span[position++];

    private static ushort ReadUInt16(ReadOnlySpan<byte> span, ref int position)
    {
        ushort value = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(position, 2));
        position += 2;
        return value;
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> span, ref int position)
    {
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(position, 4));
        position += 4;
        return value;
    }

    private static ulong ReadUInt64(ReadOnlySpan<byte> span, ref int position)
    {
        ulong value = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(position, 8));
        position += 8;
        return value;
    }
}