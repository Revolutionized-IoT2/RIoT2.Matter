using System.Buffers;
using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.Messaging;

/// <summary>
/// Encodes and decodes the SessionParameterStruct that PASE and CASE exchange during session
/// establishment (as <c>initiatorSessionParams</c> / <c>responderSessionParams</c>). It carries the
/// sender's MRP timing (SII/SAI/SAT); every field is optional and an absent field means the peer keeps
/// its own default. See the Matter Core Specification, section 4.11.2 (Session Parameters).
/// </summary>
public static class SessionParametersCodec
{
    // SessionParameterStruct field tags (spec §4.11.2). Milliseconds for the three intervals.
    private const byte SessionIdleIntervalTag = 1;   // SESSION_IDLE_INTERVAL (SII)
    private const byte SessionActiveIntervalTag = 2; // SESSION_ACTIVE_INTERVAL (SAI)
    private const byte SessionActiveThresholdTag = 3; // SESSION_ACTIVE_THRESHOLD (SAT)

    /// <summary>
    /// Writes the SessionParameterStruct for <paramref name="config"/> under <paramref name="tag"/>
    /// into <paramref name="writer"/>. Only the three MRP intervals are emitted; the optional data
    /// model / interaction model revision fields are omitted.
    /// </summary>
    public static void Write(TlvWriter writer, TlvTag tag, ReliableMessageProtocolConfig config)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.StartStructure(tag);
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(SessionIdleIntervalTag), (uint)config.IdleRetransmitTimeout.TotalMilliseconds);
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(SessionActiveIntervalTag), (uint)config.ActiveRetransmitTimeout.TotalMilliseconds);
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(SessionActiveThresholdTag), (uint)config.ActiveThreshold.TotalMilliseconds);
        writer.EndContainer();
    }

    /// <summary>Serializes the SessionParameterStruct as a standalone anonymous-tagged structure.</summary>
    public static byte[] Encode(ReliableMessageProtocolConfig config)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new TlvWriter(buffer);
        Write(writer, TlvTag.Anonymous, config);
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Reads a SessionParameterStruct that the reader is positioned on (the reader has just read the
    /// container element). Absent fields fall back to <see cref="ReliableMessageProtocolConfig.Default"/>.
    /// </summary>
    public static ReliableMessageProtocolConfig ReadStructure(ref TlvReader reader)
    {
        var config = ReliableMessageProtocolConfig.Default;

        while (reader.Read() && !reader.IsEndOfContainer)
        {
            if (reader.IsContainer)
            {
                // Skip any nested container we don't interpret (keeps the cursor balanced).
                TlvCopier.Skip(ref reader);
                continue;
            }

            switch (reader.Tag.TagNumber)
            {
                case SessionIdleIntervalTag:
                    config = config with { IdleRetransmitTimeout = TimeSpan.FromMilliseconds(reader.GetUnsignedInteger()) };
                    break;
                case SessionActiveIntervalTag:
                    config = config with { ActiveRetransmitTimeout = TimeSpan.FromMilliseconds(reader.GetUnsignedInteger()) };
                    break;
                case SessionActiveThresholdTag:
                    config = config with { ActiveThreshold = TimeSpan.FromMilliseconds(reader.GetUnsignedInteger()) };
                    break;
            }
        }

        return config;
    }
}