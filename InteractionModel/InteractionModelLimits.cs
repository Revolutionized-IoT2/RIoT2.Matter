namespace RIoT2.Matter.InteractionModel;

/// <summary>
/// Size limits governing Interaction Model message construction, in particular the point at which a
/// ReportData must be split across multiple chunks. See the Matter Core Specification, section 8.4.3.2.
/// </summary>
public static class InteractionModelLimits
{
    // The IPv6 minimum MTU bounds a single Matter UDP datagram (spec §4.4.4).
    private const int IPv6MinimumMtu = 1280;

    // Worst-case per-message framing carried outside the application payload: message header
    // (~26 bytes with a source node id) + payload header (~12) + AES-CCM MIC (16) + margin.
    private const int MessageFramingReserve = 64;

    /// <summary>The maximum IM application payload that fits one datagram after framing and encryption.</summary>
    public const int MaxApplicationPayload = IPv6MinimumMtu - MessageFramingReserve; // 1216

    // Reserve within a ReportData payload for its own TLV envelope: outer structure, subscription
    // id, both report arrays' open/close, the MoreChunkedMessages/SuppressResponse flags, and revision.
    private const int ReportEnvelopeReserve = 48;

    /// <summary>The budget available for report entries themselves within a single chunk.</summary>
    public const int MaxReportEntryBytes = MaxApplicationPayload - ReportEnvelopeReserve; // 1168
}