using RIoT2.Matter.DataModel;
using RIoT2.Matter.Hosting;

namespace RIoT2.Matter.SecureChannel.Case;

/// <summary>
/// Stores the <see cref="CaseResumptionRecord"/>s a node keeps so a CASE session can be resumed
/// without a full handshake. A responder looks up a record by the <c>resumptionID</c> presented in
/// Sigma1; an initiator looks one up by the peer it is about to contact. See the Matter Core
/// Specification, section 4.14.2.6.
/// </summary>
/// <remarks>
/// Implementations hold secret key material and should bound their capacity (evicting the least
/// recently used record when full) and zero evicted secrets. <see cref="ManagedCaseResumptionStore"/>
/// is the portable, in-memory implementation.
/// </remarks>
public interface ICaseResumptionStore
{
    /// <summary>
    /// Persists <paramref name="record"/>, replacing any existing record for the same peer, and
    /// evicting the least recently used record if the store is at capacity.
    /// </summary>
    void Save(CaseResumptionRecord record);

    /// <summary>
    /// Looks up the record whose <see cref="CaseResumptionRecord.ResumptionId"/> matches
    /// <paramref name="resumptionId"/> (the responder path from Sigma1).
    /// </summary>
    bool TryGetByResumptionId(ReadOnlySpan<byte> resumptionId, out CaseResumptionRecord record);

    /// <summary>
    /// Looks up the most recent record for <paramref name="peer"/> (the initiator path before Sigma1).
    /// </summary>
    bool TryGetByPeer(OperationalPeer peer, out CaseResumptionRecord record);

    /// <summary>Removes the record with the given <paramref name="resumptionId"/>, if present.</summary>
    void Remove(ReadOnlySpan<byte> resumptionId);
}