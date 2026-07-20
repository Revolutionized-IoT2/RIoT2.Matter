using System.Buffers;
using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.InteractionModel;

/// <summary>
/// Splits a <see cref="ReportDataMessage"/> into one or more content chunks that each fit within a
/// per-chunk byte budget, preserving entry order (attributes first, then events). A single list
/// attribute value too large for one chunk is expanded into a whole-list clear plus per-element
/// appends via <see cref="AttributeListSplitter"/>. Flow-control flags
/// (<c>MoreChunkedMessages</c>/<c>SuppressResponse</c>) are applied by the delivery layer, not here.
/// See the Matter Core Specification, section 8.4.3.2 (Chunking).
/// </summary>
public static class ReportDataChunker
{
    /// <summary>
    /// Splits <paramref name="report"/> into content-only chunks (each carrying the shared
    /// subscription id). Always returns at least one chunk, so an empty report still yields a single
    /// keepalive message.
    /// </summary>
    public static IReadOnlyList<ReportDataMessage> Split(ReportDataMessage report, int maxEntryBytes = InteractionModelLimits.MaxReportEntryBytes)
    {
        var chunks = new List<ReportDataMessage>();
        var attributes = new List<AttributeReportIB>();
        var events = new List<EventReportIB>();
        var size = 0;

        void Flush()
        {
            if (attributes.Count == 0 && events.Count == 0)
            {
                return;
            }

            chunks.Add(new ReportDataMessage
            {
                SubscriptionId = report.SubscriptionId,
                AttributeReports = attributes.Count > 0 ? attributes.ToArray() : null,
                EventReports = events.Count > 0 ? events.ToArray() : null,
            });
            attributes = [];
            events = [];
            size = 0;
        }

        // Closes the current chunk before an entry would overflow it, but never emits an empty one:
        // an entry that cannot fit even an empty chunk lands alone (best effort).
        void PlaceAttribute(AttributeReportIB entry, int length)
        {
            if (size > 0 && size + length > maxEntryBytes)
            {
                Flush();
            }

            attributes.Add(entry);
            size += length;
        }

        if (report.AttributeReports is { } attributeReports)
        {
            foreach (var entry in attributeReports)
            {
                var length = Measure(entry.Encode);

                // An entry too large for even an empty chunk is expanded — when it carries a list
                // value — into a whole-list clear plus one append per element, each placed
                // independently so the list spreads across chunks.
                if (length > maxEntryBytes && AttributeListSplitter.TrySplit(entry, out var expansion))
                {
                    foreach (var part in expansion)
                    {
                        PlaceAttribute(part, Measure(part.Encode));
                    }
                }
                else
                {
                    PlaceAttribute(entry, length);
                }
            }
        }

        if (report.EventReports is { } eventReports)
        {
            foreach (var entry in eventReports)
            {
                var length = Measure(entry.Encode);
                if (size > 0 && size + length > maxEntryBytes)
                {
                    Flush();
                }

                events.Add(entry);
                size += length;
            }
        }

        Flush();

        if (chunks.Count == 0)
        {
            // No entries at all: preserve the (empty) report so keepalives are still delivered.
            chunks.Add(report with { AttributeReports = null, EventReports = null });
        }

        return chunks;
    }

    private static int Measure(Action<TlvWriter, TlvTag> encode)
    {
        var buffer = new ArrayBufferWriter<byte>();
        encode(new TlvWriter(buffer), TlvTag.Anonymous);
        return buffer.WrittenCount;
    }
}