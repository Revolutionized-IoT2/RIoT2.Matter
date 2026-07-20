using System;

namespace RIoT2.Matter.Controller.Commissioning;

/// <summary>
/// A per-attempt bridge that lets the singleton <see cref="SecureChannel.SecureChannelClient"/>
/// obtain a PASE local session id from the current commissioning attempt's session manager. The
/// <see cref="UdpCommissioningSessionFactory"/> sets the allocator when it opens a context; the
/// secure-channel client's <c>localSessionIdFactory</c> reads it when it starts the PASE handshake.
/// Commissioning attempts run one at a time per controller fabric, so a single ambient allocator is
/// sufficient. See the Matter Core Specification, section 4.8 (Secure Session Context).
/// </summary>
public sealed class CommissioningPaseSessionIdSource
{
    private readonly AsyncLocal<Func<ushort>?> _allocator = new();

    /// <summary>Binds the allocator used for the current attempt's PASE handshake.</summary>
    public void SetAllocator(Func<ushort> allocator) =>
        _allocator.Value = allocator ?? throw new ArgumentNullException(nameof(allocator));

    /// <summary>Reserves the PASE local session id for the current attempt.</summary>
    /// <exception cref="InvalidOperationException">No commissioning context has been opened on this flow.</exception>
    public ushort Allocate() =>
        (_allocator.Value ?? throw new InvalidOperationException(
            "No commissioning session context is active; open one via the commissioning session factory before establishing PASE."))
        .Invoke();
}