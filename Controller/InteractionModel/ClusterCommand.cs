using RIoT2.Matter.DataModel;

namespace RIoT2.Matter.Controller.InteractionModel;

/// <summary>Identifies a command to invoke on a concrete endpoint/cluster, with its (optional) TLV fields.</summary>
public readonly record struct ClusterCommand
{
    /// <summary>The endpoint hosting the command.</summary>
    public required EndpointId Endpoint { get; init; }

    /// <summary>The cluster hosting the command.</summary>
    public required ClusterId Cluster { get; init; }

    /// <summary>The command to invoke.</summary>
    public required CommandId Command { get; init; }

    /// <summary>The command fields as a standalone TLV element; empty for a command with no arguments.</summary>
    public ReadOnlyMemory<byte> Fields { get; init; }
}