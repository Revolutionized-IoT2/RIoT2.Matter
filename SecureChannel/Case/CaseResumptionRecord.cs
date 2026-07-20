using RIoT2.Matter.DataModel;
using RIoT2.Matter.Hosting;
using RIoT2.Matter.Messaging;

namespace RIoT2.Matter.SecureChannel.Case;

/// <summary>
/// The persisted material from a successful CASE handshake that enables a later session to be
/// re-established via resumption instead of a full Sigma1/2/3 exchange. See the Matter Core
/// Specification, section 4.14.2.6.
/// </summary>
/// <remarks>
/// A record binds a <see cref="ResumptionId"/> to the ECDH <see cref="SharedSecret"/> negotiated
/// during the originating handshake and to the peer it authenticated. Both are rotated on every
/// subsequent successful handshake (full or resumed). Records hold secret key material and must be
/// stored securely and zeroed when evicted.
/// </remarks>
/// <param name="ResumptionId">The 16-byte resumption identifier exchanged for a future resumption.</param>
/// <param name="SharedSecret">The ECDH shared secret used to derive the resumed session keys.</param>
/// <param name="Peer">The fabric and peer node id the resumed session authenticates.</param>
/// <param name="PeerSessionParameters">The peer's last-known MRP configuration, reused on resumption.</param>
public sealed record CaseResumptionRecord(
    byte[] ResumptionId,
    byte[] SharedSecret,
    OperationalPeer Peer,
    ReliableMessageProtocolConfig PeerSessionParameters);