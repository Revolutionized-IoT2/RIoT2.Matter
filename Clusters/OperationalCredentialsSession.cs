using RIoT2.Matter.DataModel;

namespace RIoT2.Matter.Clusters;

/// <summary>
/// The accessing session's context that Operational Credentials commands need but the Interaction
/// Model invoke pipeline does not yet thread through: the accessing <see cref="FabricIndex"/> and the
/// session's attestation challenge (used as the TBS suffix when signing AttestationResponse and
/// CSRResponse). See the Matter Core Specification, sections 11.18.6.1 and 11.18.6.5.
/// </summary>
/// <param name="FabricIndex">The accessing fabric, or <see cref="FabricIndex.NoFabric"/> over a PASE session.</param>
/// <param name="AttestationChallenge">The 16-byte attestation challenge derived alongside the session keys.</param>
public sealed record OperationalCredentialsSession(FabricIndex FabricIndex, byte[] AttestationChallenge);