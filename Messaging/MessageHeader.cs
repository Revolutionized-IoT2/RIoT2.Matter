using RIoT2.Matter.DataModel;

namespace RIoT2.Matter.Messaging;

/// <summary>
/// The Matter message header (transport/session layer). Precedes the encrypted or
/// cleartext message payload. See the Matter Core Specification, section 4.4.
/// </summary>
public readonly record struct MessageHeader
{
    /// <summary>Protocol version (currently 0).</summary>
    public byte Version { get; init; }

    /// <summary>Identifies the session; 0 designates the unsecured session.</summary>
    public ushort SessionId { get; init; }

    /// <summary>The session type (unicast or group).</summary>
    public SessionType SessionType { get; init; }

    /// <summary>True if this is a control message (e.g. an MRP standalone acknowledgement).</summary>
    public bool IsControlMessage { get; init; }

    /// <summary>True if message privacy (header obfuscation) is applied.</summary>
    public bool HasPrivacy { get; init; }

    /// <summary>Monotonic per-session message counter used for replay protection.</summary>
    public uint MessageCounter { get; init; }

    /// <summary>The source node id, when present.</summary>
    public NodeId? SourceNodeId { get; init; }

    /// <summary>The destination node id, when addressing a single node.</summary>
    public NodeId? DestinationNodeId { get; init; }

    /// <summary>The destination group id, when addressing a group.</summary>
    public GroupId? DestinationGroupId { get; init; }
}