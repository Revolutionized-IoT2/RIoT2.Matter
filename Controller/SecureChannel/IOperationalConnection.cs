using System;
using RIoT2.Matter.Controller.InteractionModel;
using RIoT2.Matter.DataModel;

namespace RIoT2.Matter.Controller.SecureChannel;

/// <summary>
/// A live operational (CASE) connection to a commissioned node: the Interaction Model client bound
/// to the established secure session, plus the peer's identity. Disposing the connection tears down
/// the underlying session and transport. See the Matter Core Specification, section 4.14.
/// </summary>
public interface IOperationalConnection : IAsyncDisposable
{
    /// <summary>The operational node id of the peer this connection reaches.</summary>
    NodeId NodeId { get; }

    /// <summary>The fabric id this connection is scoped to.</summary>
    FabricId FabricId { get; }

    /// <summary>True while the underlying CASE session is established and usable.</summary>
    bool IsConnected { get; }

    /// <summary>The Interaction Model client for reading, writing, invoking, and subscribing over this session.</summary>
    IInteractionClient InteractionClient { get; }
}