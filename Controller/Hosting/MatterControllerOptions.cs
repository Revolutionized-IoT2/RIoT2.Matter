using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace RIoT2.Matter.Controller.Hosting;

/// <summary>
/// Configuration for the Matter controller backend, bound via the options pattern. Contains only
/// backend concerns; no UI/presentation settings and no secrets are stored here (private key
/// material lives in the <c>ICredentialStore</c>).
/// </summary>
public sealed class MatterControllerOptions
{
    /// <summary>The configuration section name conventionally bound to these options.</summary>
    public const string SectionName = "MatterController";

    /// <summary>
    /// The 64-bit Fabric ID this controller owns. Used when bootstrapping a new fabric; ignored when
    /// an existing fabric is loaded from the credential store.
    /// </summary>
    [Range(1, long.MaxValue)]
    public ulong FabricId { get; set; } = 1;

    /// <summary>The administrator vendor id recorded on nodes' fabric entries (AddNOC AdminVendorId).</summary>
    [Range(1, ushort.MaxValue)]
    public ushort AdminVendorId { get; set; } = 0xFFF1;

    /// <summary>A human-readable label for the fabric; diagnostics only, never used on the wire.</summary>
    public string? FabricLabel { get; set; }

    /// <summary>The file path backing the persistent commissioned-node registry.</summary>
    [Required]
    public string CommissionedNodeRegistryPath { get; set; } = "commissioned-nodes.json";

    /// <summary>
    /// The directory backing the persistent, encrypted <c>ICredentialStore</c> (fabric identity, RCAC,
    /// root key, and per-node NOCs). Defaults to a <c>credentials</c> folder in the working directory.
    /// </summary>
    [Required]
    public string CredentialStorePath { get; set; } = "credentials";

    /// <summary>
    /// The out-of-band secret used to encrypt private key material at rest in the file credential
    /// store. Must be supplied (e.g. via configuration/secret manager) and kept stable across restarts;
    /// losing or changing it renders the persisted fabric unrecoverable. Never logged.
    /// </summary>
    public string? CredentialProtectionSecret { get; set; }

    /// <summary>The DER-encoded PAA certificates the device-attestation chain must anchor to.</summary>
    public IList<byte[]> TrustedPaaCertificates { get; } = new List<byte[]>();

    /// <summary>How long an idle operational session is kept before eviction by background hosting.</summary>
    public TimeSpan OperationalSessionIdleTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Whether the background host discovers and tracks operational nodes on startup.</summary>
    public bool DiscoverOperationalNodesOnStart { get; set; } = true;
}