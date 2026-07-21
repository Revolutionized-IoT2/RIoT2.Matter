using RIoT2.Matter.Clusters;
using RIoT2.Matter.Messaging;
using RIoT2.Matter.SecureChannel.Pase;

namespace RIoT2.Matter.SecureChannel;

/// <summary>
/// The host-side bridge that turns an <see cref="AdministratorCommissioningController"/>'s
/// commissioning window into a live PASE responder. Registered once as the
/// <see cref="SecureChannelHandler"/>'s PASE delegate, it starts a fresh <see cref="PaseSession"/>
/// when a window opens and tears it down when the window closes, so a PASE handshake is only
/// accepted while the node is commissionable. See the Matter Core Specification, sections 4.13 and 11.19.
/// </summary>
/// <remarks>
/// The Secure Channel handler binds to a single PASE delegate for its lifetime, so this responder
/// stays fixed while the underlying <see cref="PaseSession"/> is swapped per window. An enhanced
/// window is provisioned from the administrator's verifier (W0 ‖ L, 97 octets) and PBKDF parameters;
/// a basic window uses the device's factory-provisioned verifier supplied to the constructor. When
/// no window is open, an incoming handshake is rejected with a Busy StatusReport. Wire it in the
/// composition root:
/// <code>
/// var responder = new CommissioningPaseResponder(support.AdministratorCommissioning, crypto, sessions, installer);
/// var secureChannel = new SecureChannelHandler(paseDelegate: responder, caseDelegate: caseServer);
/// exchangeManager.RegisterUnsolicitedHandler(MatterProtocolId.SecureChannel, secureChannel);
/// </code>
/// </remarks>
public sealed class CommissioningPaseResponder : ISessionEstablishmentDelegate, IDisposable
{
    private const int VerifierLength = PaseVerifier.W0Length + PaseVerifier.LLength; // 97 octets

    private readonly AdministratorCommissioningController _controller;
    private readonly IPaseCryptoProvider _crypto;
    private readonly SessionManager _sessions;
    private readonly HandshakeSessionInstaller _installer;
    private readonly PaseVerifier? _basicVerifier;
    private readonly PbkdfParameters? _basicPbkdfParameters;
    private readonly EventHandler<CommissioningWindowOpenedEventArgs> _onWindowOpened;
    private readonly EventHandler _onWindowClosed;
    private readonly object _gate = new();

    private PaseSession? _active;
    private ushort _activeSessionId;

    /// <param name="controller">The window manager whose open/close lifecycle drives the responder.</param>
    /// <param name="crypto">The SPAKE2+/key-derivation provider each <see cref="PaseSession"/> uses.</param>
    /// <param name="sessions">The session table that allocates local session ids for each handshake.</param>
    /// <param name="installer">The bridge that installs a completed PASE session into <paramref name="sessions"/>.</param>
    /// <param name="basicWindowVerifier">
    /// The device's factory-provisioned SPAKE2+ verifier, used when a basic commissioning window opens.
    /// Required only if the node advertises the Basic Commissioning feature; otherwise leave null.
    /// </param>
    /// <param name="basicWindowPbkdfParameters">The factory-provisioned PBKDF parameters paired with <paramref name="basicWindowVerifier"/>.</param>
    public CommissioningPaseResponder(
        AdministratorCommissioningController controller,
        IPaseCryptoProvider crypto,
        SessionManager sessions,
        HandshakeSessionInstaller installer,
        PaseVerifier? basicWindowVerifier = null,
        PbkdfParameters? basicWindowPbkdfParameters = null)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _crypto = crypto ?? throw new ArgumentNullException(nameof(crypto));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _installer = installer ?? throw new ArgumentNullException(nameof(installer));
        _basicVerifier = basicWindowVerifier;
        _basicPbkdfParameters = basicWindowPbkdfParameters;

        _onWindowOpened = OnWindowOpened;
        _onWindowClosed = OnWindowClosed;
        _controller.WindowOpened += _onWindowOpened;
        _controller.WindowClosed += _onWindowClosed;
    }

    /// <inheritdoc />
    public ValueTask OnMessageAsync(
        ExchangeContext exchange, SecureChannelOpcode opcode, MatterMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        ArgumentNullException.ThrowIfNull(message);

        PaseSession? active;
        lock (_gate)
        {
            active = _active;
        }

        // Trace every PASE message reaching the responder so a stalled commissioning attempt can be told
        // apart from one that never arrived: the expected order is PbkdfParamRequest → PasePake1 → PasePake3.
        // A Busy rejection here means no commissioning window is open; silence means no PASE datagram was
        // delivered at all (cross-check the "[MatterNodeHost] dropped inbound datagram" diagnostics).
        Console.WriteLine(active is null
            ? $"[pase] {opcode} received but NO window open → rejecting with Busy."
            : $"[pase] {opcode} received; dispatching to active session.");

        // No commissioning window is open: the node is not accepting PASE, so reject with Busy.
        return active is null
            ? SecureChannelHandler.SendStatusReportAsync(exchange, GeneralStatusCode.Busy, SecureChannelStatusCode.Busy, cancellationToken: cancellationToken)
            : active.OnMessageAsync(exchange, opcode, message, cancellationToken);
    }

    /// <inheritdoc />
    public void OnExchangeClosed(ExchangeContext exchange)
    {
        PaseSession? active;
        lock (_gate)
        {
            active = _active;
        }

        active?.OnExchangeClosed(exchange);
    }

    private void OnWindowOpened(object? sender, CommissioningWindowOpenedEventArgs e)
    {
        if (!TryResolveVerifier(e.Request, out var verifier, out var pbkdf))
        {
            // A basic window was requested but no factory verifier was provisioned; the node should
            // not advertise the Basic Commissioning feature, so this window cannot accept PASE.
            return;
        }

        var sessionId = _sessions.AllocateSessionId();
        var pase = new PaseSession(_crypto, verifier, pbkdf, sessionId);
        _installer.Attach(pase);

        lock (_gate)
        {
            StopActiveLocked();
            _active = pase;
            _activeSessionId = sessionId;
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            StopActiveLocked();
        }
    }

    private bool TryResolveVerifier(EnhancedCommissioningWindowRequest? request, out PaseVerifier verifier, out PbkdfParameters pbkdf)
    {
        if (request is { } enhanced)
        {
            // The controller validated the 97-octet verifier before raising the event; split W0 ‖ L.
            var raw = enhanced.PakePasscodeVerifier;
            verifier = new PaseVerifier(raw[..PaseVerifier.W0Length], raw[PaseVerifier.W0Length..VerifierLength]);
            pbkdf = new PbkdfParameters(enhanced.Iterations, enhanced.Salt);
            return true;
        }

        if (_basicVerifier is not null && _basicPbkdfParameters is not null)
        {
            verifier = _basicVerifier;
            pbkdf = _basicPbkdfParameters;
            return true;
        }

        verifier = null!;
        pbkdf = null!;
        return false;
    }

    private void StopActiveLocked()
    {
        if (_active is null)
        {
            return;
        }

        _installer.Detach(_active);

        // Reclaim the reserved id if the handshake never installed a session; a no-op once installed.
        _sessions.ReleaseSessionId(_activeSessionId);

        _active = null;
        _activeSessionId = 0;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _controller.WindowOpened -= _onWindowOpened;
        _controller.WindowClosed -= _onWindowClosed;

        lock (_gate)
        {
            StopActiveLocked();
        }
    }
}