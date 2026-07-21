using RIoT2.Matter.DataModel;
using RIoT2.Matter.Messaging;
using RIoT2.Matter.SecureChannel.Case;
using RIoT2.Matter.SecureChannel.Pase;

namespace RIoT2.Matter.SecureChannel;

/// <summary>
/// Bridges completed PASE/CASE handshakes to the <see cref="SessionManager"/>: on success it builds
/// a <see cref="SecureSession"/> from the derived keys and installs it, so subsequent messages
/// decrypt on the new session. This closes the session-install step of both handshake state
/// machines. See the Matter Core Specification, sections 4.13�4.14.
/// </summary>
/// <remarks>
/// A device responding to PASE/CASE installs sessions with <see cref="SecureSessionRole.Responder"/>
/// (encrypt with R2I, decrypt with I2R). A controller acting as the CASE initiator installs sessions
/// with <see cref="SecureSessionRole.Initiator"/> (the reverse direction) via <see cref="Attach(CaseClient)"/>.
/// </remarks>
public sealed class HandshakeSessionInstaller
{
    private readonly SessionManager _sessions;
    private readonly IFabricStore _fabrics;
    private readonly TimeProvider _timeProvider;

    /// <param name="sessions">The session table that receives installed sessions.</param>
    /// <param name="fabrics">The fabric store used to resolve this node's operational node id for CASE.</param>
    /// <param name="timeProvider">
    /// The clock used for session peer-activity tracking; pass the same instance given to
    /// <paramref name="sessions"/> so idle and activity timers agree. Defaults to <see cref="TimeProvider.System"/>.
    /// </param>
    public HandshakeSessionInstaller(SessionManager sessions, IFabricStore fabrics, TimeProvider? timeProvider = null)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _fabrics = fabrics ?? throw new ArgumentNullException(nameof(fabrics));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Raised after a session has been built and installed into the session manager.</summary>
    public event EventHandler<SecureSessionInstalledEventArgs>? SessionInstalled;

    /// <summary>Installs sessions produced by a PASE responder as its handshakes complete.</summary>
    public void Attach(PaseSession pase)
    {
        ArgumentNullException.ThrowIfNull(pase);
        pase.SessionEstablished += OnPaseEstablished;
    }

    /// <summary>Installs sessions produced by a CASE responder as its handshakes complete.</summary>
    public void Attach(CaseServer caseServer)
    {
        ArgumentNullException.ThrowIfNull(caseServer);
        caseServer.SessionEstablished += OnCaseResponderEstablished;
    }

    /// <summary>Installs the initiator-role session produced by a CASE client when its handshake completes.</summary>
    public void Attach(CaseClient caseClient)
    {
        ArgumentNullException.ThrowIfNull(caseClient);
        caseClient.SessionEstablished += OnCaseInitiatorEstablished;
    }

    /// <summary>Stops installing sessions from a previously attached PASE responder.</summary>
    public void Detach(PaseSession pase)
    {
        ArgumentNullException.ThrowIfNull(pase);
        pase.SessionEstablished -= OnPaseEstablished;
    }

    /// <summary>Stops installing sessions from a previously attached CASE responder.</summary>
    public void Detach(CaseServer caseServer)
    {
        ArgumentNullException.ThrowIfNull(caseServer);
        caseServer.SessionEstablished -= OnCaseResponderEstablished;
    }

    /// <summary>Stops installing the session from a previously attached CASE client.</summary>
    public void Detach(CaseClient caseClient)
    {
        ArgumentNullException.ThrowIfNull(caseClient);
        caseClient.SessionEstablished -= OnCaseInitiatorEstablished;
    }

    private void OnPaseEstablished(object? sender, PaseSessionEstablishedEventArgs e)
    {
        // PASE runs before any fabric exists: no node ids, no fabric index.
        var session = new SecureSession(
            SecureSessionType.Pase,
            SecureSessionRole.Responder,
            e.LocalSessionId,
            e.PeerSessionId,
            NodeId.Unspecified,
            NodeId.Unspecified,
            FabricIndex.NoFabric,
            e.Keys.I2RKey,
            e.Keys.R2IKey,
            e.Keys.AttestationChallenge,
            ReliableMessageProtocolConfig.Default, // TODO: use the peer's negotiated session params once parsed.
            _timeProvider);

        Install(session);
    }

    private void OnCaseResponderEstablished(object? sender, CaseSessionEstablishedEventArgs e) =>
        InstallCaseSession(SecureSessionRole.Responder, e);

    private void OnCaseInitiatorEstablished(object? sender, CaseSessionEstablishedEventArgs e) =>
        InstallCaseSession(SecureSessionRole.Initiator, e);

    private void InstallCaseSession(SecureSessionRole role, CaseSessionEstablishedEventArgs e)
    {
        var session = new SecureSession(
            SecureSessionType.Case,
            role,
            e.LocalSessionId,
            e.PeerSessionId,
            ResolveLocalNodeId(e.FabricIndex),
            e.PeerNodeId,
            e.FabricIndex,
            e.Keys.I2RKey,
            e.Keys.R2IKey,
            e.Keys.AttestationChallenge,
            ReliableMessageProtocolConfig.Default, // TODO: use the peer's negotiated session params once parsed.
            _timeProvider,
            e.PeerCaseAuthenticatedTags);

        Install(session);
    }

    private void Install(SecureSession session)
    {
        var registration = _sessions.RegisterSecureSession(session);
        SessionInstalled?.Invoke(this, new SecureSessionInstalledEventArgs(registration));
    }

    private NodeId ResolveLocalNodeId(FabricIndex fabricIndex)
    {
        foreach (var fabric in _fabrics.Fabrics)
        {
            if (fabric.FabricIndex == fabricIndex)
            {
                return fabric.NodeId;
            }
        }

        // The install runs immediately after the CASE handshake matched this fabric, so a miss is
        // not expected; fall back to unspecified rather than throwing inside the handshake completion.
        return NodeId.Unspecified;
    }
}