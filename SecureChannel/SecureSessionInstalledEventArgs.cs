using RIoT2.Matter.Messaging;

namespace RIoT2.Matter.SecureChannel;

/// <summary>Raised by <see cref="HandshakeSessionInstaller"/> once a handshake's session is installed.</summary>
public sealed class SecureSessionInstalledEventArgs : EventArgs
{
    public SecureSessionInstalledEventArgs(SecureSessionRegistration registration) =>
        Registration = registration;

    /// <summary>The installed session together with its outbound counter and replay window.</summary>
    public SecureSessionRegistration Registration { get; }
}