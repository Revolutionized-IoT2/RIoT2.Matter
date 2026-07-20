using RIoT2.Matter.DataModel;

namespace RIoT2.Matter.SecureChannel.Case;

/// <summary>
/// A fabric this node belongs to, together with the operational credentials the CASE responder
/// needs to authenticate as this node on that fabric. See the Matter Core Specification, section 4.14.
/// </summary>
public sealed record ResolvedFabric(
    FabricIndex FabricIndex,
    FabricId FabricId,
    NodeId NodeId,
    byte[] RootPublicKey,
    byte[] IdentityProtectionKey,
    byte[] OperationalNoc,
    byte[]? OperationalIcac,
    ICaseOperationalKey OperationalKey);