using RIoT2.Matter.DataModel;

namespace RIoT2.Matter.Controller.Credentials;

/// <summary>
/// Allocates operational Node IDs for nodes commissioned onto the fabric. Operational Node IDs are
/// 64-bit values in the range 0x0000000000000001..0xFFFFFFEFFFFFFFFF (spec section 2.5.5); the upper
/// range is reserved for group/temporary/CAT identifiers and must not be handed out here.
/// </summary>
public interface INodeIdAllocator
{
    /// <summary>Returns the next unused operational Node ID.</summary>
    NodeId Allocate();
}

/// <summary>
/// A thread-safe, in-memory monotonic allocator. Persist the last value via <see cref="ICredentialStore"/>
/// (see roadmap Phase 7) so IDs remain unique across restarts.
/// </summary>
public sealed class MonotonicNodeIdAllocator : INodeIdAllocator
{
    private const ulong MinOperationalNodeId = 0x0000000000000001;
    private const ulong MaxOperationalNodeId = 0xFFFFFFEFFFFFFFFF;

    private readonly Lock _gate = new();
    private ulong _next;

    /// <summary>Creates an allocator that begins issuing at <paramref name="firstNodeId"/> (default 1).</summary>
    public MonotonicNodeIdAllocator(ulong firstNodeId = MinOperationalNodeId)
    {
        if (firstNodeId is < MinOperationalNodeId or > MaxOperationalNodeId)
        {
            throw new ArgumentOutOfRangeException(nameof(firstNodeId), firstNodeId, "First Node ID is outside the operational range.");
        }

        _next = firstNodeId;
    }

    public NodeId Allocate()
    {
        lock (_gate)
        {
            if (_next > MaxOperationalNodeId)
            {
                throw new InvalidOperationException("Operational Node ID space exhausted for this fabric.");
            }

            return new NodeId(_next++);
        }
    }
}