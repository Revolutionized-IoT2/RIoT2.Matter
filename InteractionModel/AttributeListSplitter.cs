using System.Buffers;
using System.IO;
using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.InteractionModel;

/// <summary>
/// Splits a single list-valued <see cref="AttributeReportIB"/> that is too large for one chunk into
/// a whole-list clear followed by one append entry per element, so the chunker can spread the list
/// across multiple ReportData chunks. See the Matter Core Specification, section 8.4.3.2 (Chunking).
/// </summary>
/// <remarks>
/// The client reconstructs the list by replacing it with the empty array, then applying each append
/// in order; all entries share the source data version, so they resolve to one cluster version. Only
/// a TLV list (array) value is splittable — an oversized scalar (e.g. a large octet string) cannot
/// be divided at the Interaction Model layer and is left for best-effort single-chunk placement.
/// </remarks>
internal static class AttributeListSplitter
{
    /// <summary>
    /// Attempts to expand <paramref name="report"/> into a clear-plus-appends sequence. Returns
    /// <see langword="false"/> for status entries, empty values, and non-list (scalar) values.
    /// </summary>
    public static bool TrySplit(AttributeReportIB report, out IReadOnlyList<AttributeReportIB> expansion)
    {
        expansion = [];

        if (report.AttributeData is not { } data || data.Data.IsEmpty)
        {
            return false; // status entries and empty values are not splittable
        }

        var result = new List<AttributeReportIB>();
        try
        {
            var reader = new TlvReader(data.Data.Span);
            if (!reader.Read() || reader.Type != TlvElementType.Array)
            {
                return false; // only a TLV list (array) value can be split element-wise
            }

            // 1. Clear the list at the client with a whole-attribute empty-array replace.
            result.Add(AttributeReportIB.ForData(new AttributeDataIB
            {
                DataVersion = data.DataVersion,
                Path = data.Path with { ListIndex = ListIndex.WholeAttribute },
                Data = EmptyArray(),
            }));

            // 2. Re-emit each element as an append so the chunker can spread them across chunks.
            while (reader.Read() && !reader.IsEndOfContainer)
            {
                var element = TlvCopier.Capture(ref reader);
                result.Add(AttributeReportIB.ForData(new AttributeDataIB
                {
                    DataVersion = data.DataVersion,
                    Path = data.Path with { ListIndex = ListIndex.Append },
                    Data = element,
                }));
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
        {
            // Malformed value (should not occur for internally captured data): fall back to
            // best-effort single-entry placement rather than emitting a partial expansion.
            return false;
        }

        expansion = result;
        return true;
    }

    private static ReadOnlyMemory<byte> EmptyArray()
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new TlvWriter(buffer);
        writer.StartArray(TlvTag.Anonymous);
        writer.EndContainer();
        return buffer.WrittenMemory;
    }
}