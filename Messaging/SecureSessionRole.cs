namespace RIoT2.Matter.Messaging;

/// <summary>
/// This node's role in a secure session, which fixes the directional (I2R/R2I) key mapping used
/// for message security. A session's role is constant for its whole lifetime, unlike an exchange's.
/// </summary>
public enum SecureSessionRole : byte
{
    /// <summary>This node initiated the handshake: encrypts with the I2R key, decrypts with R2I.</summary>
    Initiator = 0,

    /// <summary>This node responded to the handshake: encrypts with the R2I key, decrypts with I2R.</summary>
    Responder = 1,
}