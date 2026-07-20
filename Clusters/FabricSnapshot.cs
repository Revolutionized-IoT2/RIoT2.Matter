namespace RIoT2.Matter.Clusters;

/// <summary>
/// A persistable projection of one committed fabric: everything needed to rebuild a fabric entry and
/// re-authenticate CASE after a restart, including the operational private key (PKCS#8) and IPK
/// material. Treat instances as secrets — encrypt at rest.
/// </summary>
/// <param name="FabricIndex">The 1-based fabric index.</param>
/// <param name="FabricId">The fabric identifier.</param>
/// <param name="NodeId">This node's operational id on the fabric.</param>
/// <param name="RootCertificate">The trusted root (RCAC) in Matter TLV form.</param>
/// <param name="VendorId">The administrator's vendor id.</param>
/// <param name="Label">The user-assigned fabric label.</param>
/// <param name="Noc">The node operational certificate (Matter TLV).</param>
/// <param name="Icac">The optional intermediate certificate (Matter TLV).</param>
/// <param name="OperationalPrivateKey">The operational key as an encrypted PKCS#8 blob (see EcdsaOperationalKey.ExportEncryptedPrivateKey).</param>
/// <param name="OperationalIpk">The derived operational IPK (used by CASE / ResolvedFabric).</param>
/// <param name="EpochIpk">The 16-octet epoch IPK (used to re-seed the IPK group key set).</param>
/// <param name="CaseAdminSubject">The administrator subject seeded into Access Control.</param>
public sealed record FabricSnapshot(
    byte FabricIndex,
    ulong FabricId,
    ulong NodeId,
    byte[] RootCertificate,
    ushort VendorId,
    string Label,
    byte[] Noc,
    byte[]? Icac,
    byte[] OperationalPrivateKey,
    byte[] OperationalIpk,
    byte[] EpochIpk,
    ulong CaseAdminSubject);