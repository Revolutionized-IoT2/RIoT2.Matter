using RIoT2.Matter.DataModel;

namespace RIoT2.Matter.Hosting;

/// <summary>
/// Identifies an operational peer on a specific fabric: the pair a controller keys a CASE session by.
/// Two unicast <c>BindingTarget</c>s that name the same node on the same fabric share one session
/// regardless of the endpoint or cluster they narrow to. See the Matter Core Specification, section 4.14.
/// </summary>
/// <param name="FabricIndex">The fabric the session authenticates on.</param>
/// <param name="NodeId">The peer's operational node id on that fabric.</param>
public readonly record struct OperationalPeer(FabricIndex FabricIndex, NodeId NodeId);