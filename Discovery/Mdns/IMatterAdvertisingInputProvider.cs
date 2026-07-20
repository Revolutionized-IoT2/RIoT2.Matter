namespace RIoT2.Matter.Discovery.Mdns;

/// <summary>
/// An immutable snapshot of everything the DNS-SD advertiser needs: the shared host facts, the
/// per-fabric operational services, the commissionable service (when discoverable for commissioning),
/// and the commissioner service (when this node can commission others). A snapshot avoids torn reads
/// while the advertiser rebuilds its record set.
/// </summary>
public sealed record MatterAdvertisingInputs
{
    /// <summary>The host facts shared by every advertised service.</summary>
    public required MatterHostInfo Host { get; init; }

    /// <summary>One operational service per fabric this node belongs to; empty before commissioning.</summary>
    public IReadOnlyList<OperationalServiceInfo> OperationalServices { get; init; } = [];

    /// <summary>The commissionable service when the node is discoverable for commissioning; otherwise null.</summary>
    public CommissionableServiceInfo? Commissionable { get; init; }

    /// <summary>The commissioner service when the node can commission others via UDC; otherwise null.</summary>
    public CommissionerServiceInfo? Commissioner { get; init; }
}

/// <summary>
/// Supplies the advertiser with the current advertising inputs and signals when they change, so the
/// advertiser can rebuild and re-announce. This is the single seam that decouples DNS-SD advertising
/// from operational credential stores (<c>IFabricStore</c>), the commissioning window, the Basic
/// Information cluster, and host networking; a concrete adapter over those sources is a later task.
/// </summary>
public interface IMatterAdvertisingInputProvider
{
    /// <summary>Returns an atomic snapshot of the current advertising inputs.</summary>
    MatterAdvertisingInputs GetCurrent();

    /// <summary>
    /// Raised when any advertising input changes (a fabric added/removed, the commissioning window
    /// opening/closing, the commissioner becoming available, or the node's addresses changing),
    /// prompting a rebuild and re-announce.
    /// </summary>
    event EventHandler? Changed;
}