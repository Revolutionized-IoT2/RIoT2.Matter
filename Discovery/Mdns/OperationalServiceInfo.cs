using RIoT2.Matter.DataModel;

namespace RIoT2.Matter.Discovery.Mdns;

/// <summary>
/// The identity of one operational (<c>_matter._tcp</c>) service instance: the fabric's operational
/// root public key together with the Fabric ID and this node's Node ID on that fabric. One instance is
/// advertised per fabric. This deliberately carries only public identity — never the fabric's IPK, NOC,
/// or operational private key — so advertising stays decoupled from operational credentials. See the
/// Matter Core Specification, section 4.3.2.
/// </summary>
public sealed record OperationalServiceInfo
{
    /// <summary>The fabric's operational root CA public key in uncompressed P-256 form (0x04 || X || Y).</summary>
    public required ReadOnlyMemory<byte> RootPublicKey { get; init; }

    /// <summary>The 64-bit Fabric ID; combined with <see cref="RootPublicKey"/> to derive the compressed fabric id.</summary>
    public required FabricId FabricId { get; init; }

    /// <summary>This node's operational Node ID on the fabric; the second half of the instance name.</summary>
    public required NodeId NodeId { get; init; }
}