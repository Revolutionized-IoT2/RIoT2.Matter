using RIoT2.Matter.DataModel;
using RIoT2.Matter.InteractionModel;

namespace RIoT2.Matter.Controller.InteractionModel;

/// <summary>
/// The controller-side Interaction Model client: issues Read, Write, Invoke, and Subscribe
/// interactions against a peer over an established (CASE or PASE) secure session. See the Matter
/// Core Specification, section 8.
/// </summary>
public interface IInteractionClient
{
    /// <summary>Reads the given (possibly wildcarded) attribute paths, returning every attribute report.</summary>
    Task<IReadOnlyList<AttributeReportIB>> ReadAttributesAsync(
        IReadOnlyList<AttributePathIB> paths,
        CancellationToken cancellationToken = default);

    /// <summary>Writes the given attribute values, returning the per-path write statuses.</summary>
    Task<IReadOnlyList<AttributeStatusIB>> WriteAttributesAsync(
        IReadOnlyList<AttributeDataIB> values,
        bool timed = false,
        ushort timedInvokeTimeoutMs = 0,
        CancellationToken cancellationToken = default);

    /// <summary>Invokes a single command, returning its response data or status.</summary>
    Task<InvokeResult> InvokeAsync(
        ClusterCommand command,
        bool timed = false,
        ushort timedInvokeTimeoutMs = 0,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Establishes a subscription to the given attribute paths. The returned handle streams reports
    /// (starting with the priming report) until disposed.
    /// </summary>
    Task<ISubscription> SubscribeAsync(
        IReadOnlyList<AttributePathIB> paths,
        ushort minIntervalFloorSeconds,
        ushort maxIntervalCeilingSeconds,
        CancellationToken cancellationToken = default);
}

/// <summary>An active subscription. Streams reports and cancels the subscription on disposal.</summary>
public interface ISubscription : IAsyncDisposable
{
    /// <summary>The server-allocated subscription id.</summary>
    uint SubscriptionId { get; }

    /// <summary>The negotiated maximum reporting interval, in seconds.</summary>
    ushort MaxIntervalSeconds { get; }

    /// <summary>Streams each attribute report as ReportData messages arrive, until the subscription ends.</summary>
    IAsyncEnumerable<AttributeReportIB> ReadReportsAsync(CancellationToken cancellationToken = default);
}