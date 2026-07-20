using System.Buffers;
using System.Security.Cryptography;
using RIoT2.Matter.Credentials;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.SecureChannel.Case;

/// <summary>
/// The controller-side (initiator) engine for a single CASE handshake, created by
/// <see cref="ManagedCaseCryptoProvider.CreateInitiator"/>: ECDH with the responder, the
/// Sigma2/Sigma3 AES-CCM/HKDF key schedule, Sigma2 validation (responder NOC/ICAC→RCAC chain +
/// handshake signature), and this node's Sigma3 NOC signature. It is the mirror of
/// <see cref="ManagedCaseResponderContext"/> with the roles reversed, and shares the same key
/// schedule, nonces, and TBS/TBE wire layout so both ends interoperate. See the Matter Core
/// Specification, section 4.14.
/// </summary>
internal sealed class ManagedCaseInitiatorContext : ICaseInitiatorContext
{
    private const int KeyLength = 16;
    private const int PublicKeyLength = 65;
    private const int RandomLength = 32;

    private static readonly byte[] S2KInfo = "Sigma2"u8.ToArray();
    private static readonly byte[] S3KInfo = "Sigma3"u8.ToArray();
    private static readonly byte[] SessionKeysInfo = "SessionKeys"u8.ToArray();
    private static readonly byte[] Sigma2Nonce = "NCASE_Sigma2N"u8.ToArray(); // 13-byte AES-CCM nonce
    private static readonly byte[] Sigma3Nonce = "NCASE_Sigma3N"u8.ToArray();

    private readonly ResolvedFabric _fabric;
    private readonly TimeProvider _timeProvider;
    private readonly ECDiffieHellman _ephemeral;
    private readonly byte[] _initiatorEphPub;
    private readonly byte[] _initiatorRandom;
    private readonly byte[] _destinationId;
    private readonly List<byte> _transcript = new();

    private int _sigma1Length;
    private byte[]? _responderEphPub;
    private byte[]? _sharedSecret;
    private NodeId _peerNodeId;

    public ManagedCaseInitiatorContext(ResolvedFabric fabric, NodeId peerNodeId, ICaseCryptoProvider crypto, TimeProvider timeProvider)
    {
        _fabric = fabric;
        _timeProvider = timeProvider;
        _ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        _initiatorEphPub = EncodeUncompressed(_ephemeral.ExportParameters(includePrivateParameters: false).Q);
        _initiatorRandom = RandomNumberGenerator.GetBytes(RandomLength);
        _destinationId = crypto.ComputeDestinationIdentifier(
            _fabric.IdentityProtectionKey, _initiatorRandom, _fabric.RootPublicKey, _fabric.FabricId, peerNodeId);
    }

    public ReadOnlyMemory<byte> InitiatorEphemeralPublicKey => _initiatorEphPub;

    public ReadOnlyMemory<byte> InitiatorRandom => _initiatorRandom;

    public ReadOnlyMemory<byte> DestinationIdentifier => _destinationId;

    public NodeId PeerNodeId => _peerNodeId;

    public ReadOnlyMemory<byte> SharedSecret => _sharedSecret ?? ReadOnlyMemory<byte>.Empty;

    public void AppendToTranscript(ReadOnlySpan<byte> messagePayload) => _transcript.AddRange(messagePayload.ToArray());
    public void NoteSigma1Length(int length) => _sigma1Length = length;

    /// <inheritdoc />
    public bool TryProcessSigma2(ReadOnlySpan<byte> responderEphemeralPublicKey, ReadOnlySpan<byte> encrypted2)
    {
        if (responderEphemeralPublicKey.Length != PublicKeyLength)
        {
            return false;
        }

        _responderEphPub = responderEphemeralPublicKey.ToArray();
        _sharedSecret = DeriveEcdh(_responderEphPub);

        // S2K = HKDF(salt = IPK || Random_R || pubKey_R || SHA256(Sigma1), IKM = sharedSecret, info = "Sigma2").
        // The responder random is the leading Sigma2 field; recover it from the appended Sigma2 payload
        // so the salt matches the responder's exactly.
        if (!TryReadResponderRandom(out byte[] responderRandom))
        {
            return false;
        }

        byte[] s2k = Hkdf(Concat(Ipk, responderRandom, _responderEphPub, Sigma1Hash()), _sharedSecret, S2KInfo, KeyLength);
        if (!TryCcmDecrypt(s2k, Sigma2Nonce, encrypted2.ToArray(), out byte[] tbe))
        {
            return false;
        }

        if (!TryParseTbe(tbe, out byte[] noc, out byte[]? icac, out byte[] signature) ||
            !TryValidateResponder(noc, icac, out byte[] responderPublicKey, out NodeId peerNodeId))
        {
            return false;
        }

        // The responder signs TBSData {NOC, ICAC?, responderEphPub (sender), initiatorEphPub (receiver)}.
        byte[] tbs = BuildTbs(noc, icac, _responderEphPub, _initiatorEphPub);
        if (!VerifyEcdsa(responderPublicKey, tbs, signature))
        {
            return false;
        }

        _peerNodeId = peerNodeId;
        return true;
    }

    /// <inheritdoc />
    public byte[] BuildSigma3Encrypted()
    {
        // S3K = HKDF(salt = IPK || SHA256(Sigma1 || Sigma2), IKM = sharedSecret, info = "Sigma3").
        byte[] s3k = Hkdf(Concat(Ipk, TranscriptHash()), _sharedSecret!, S3KInfo, KeyLength);

        byte[] noc = _fabric.OperationalNoc;
        byte[]? icac = _fabric.OperationalIcac;

        // The initiator signs TBSData {NOC, ICAC?, initiatorEphPub (sender), responderEphPub (receiver)}.
        byte[] tbs = BuildTbs(noc, icac, _initiatorEphPub, _responderEphPub!);
        byte[] signature = _fabric.OperationalKey.Sign(tbs);
        byte[] tbe = BuildTbe(noc, icac, signature);
        return CcmEncrypt(s3k, Sigma3Nonce, tbe);
    }

    /// <inheritdoc />
    public CaseSessionKeys DeriveSessionKeys()
    {
        // I2R || R2I || AttestationChallenge = HKDF(salt = IPK || SHA256(Sigma1||2||3), IKM = sharedSecret, "SessionKeys").
        byte[] okm = Hkdf(Concat(Ipk, TranscriptHash()), _sharedSecret!, SessionKeysInfo, 3 * KeyLength);
        return new CaseSessionKeys(okm[..KeyLength], okm[KeyLength..(2 * KeyLength)], okm[(2 * KeyLength)..]);
    }

    public void Dispose()
    {
        _ephemeral.Dispose();
        if (_sharedSecret is not null)
        {
            CryptographicOperations.ZeroMemory(_sharedSecret);
        }
    }

    private byte[] Ipk => _fabric.IdentityProtectionKey;

    private byte[] TranscriptHash() => SHA256.HashData(_transcript.ToArray());

    // Before Sigma2 was appended the transcript held exactly Sigma1, so this hashes SHA256(Sigma1).
    private byte[] Sigma1Hash() => SHA256.HashData(_transcript.GetRange(0, _sigma1Length).ToArray());

    private byte[] DeriveEcdh(byte[] peerPublic65)
    {
        var peerParameters = new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = peerPublic65[1..33], Y = peerPublic65[33..65] },
        };
        using var peer = ECDiffieHellman.Create(peerParameters);
        return _ephemeral.DeriveRawSecretAgreement(peer.PublicKey); // Matter uses the ECDH x-coordinate
    }

    // Recovers the responder random (Sigma2 field 1) from the Sigma2 payload appended after Sigma1.
    private bool TryReadResponderRandom(out byte[] responderRandom)
    {
        responderRandom = [];
        if (_transcript.Count <= _sigma1Length)
        {
            return false;
        }

        var sigma2 = _transcript.GetRange(_sigma1Length, _transcript.Count - _sigma1Length).ToArray();
        var reader = new TlvReader(sigma2);
        var depth = 0;
        byte[]? random = null;
        while (reader.Read())
        {
            if (reader.IsContainer) { depth++; continue; }
            if (reader.IsEndOfContainer) { depth--; continue; }
            if (depth == 1 && reader.Tag.TagNumber == 1)
            {
                random = reader.GetByteString().ToArray();
            }
        }

        if (random is not { Length: RandomLength })
        {
            return false;
        }

        responderRandom = random;
        return true;
    }

    /// <summary>
    /// Validates the responder NOC (and optional ICAC) against the resolved fabric root: structural
    /// decode, role + validity constraints (spec §6.5.11), the NOC→ICAC→RCAC signature chain, and that
    /// the NOC is scoped to this fabric. On success returns the NOC public key (for the Sigma2
    /// signature check) and the authenticated peer node id. Mirrors
    /// <see cref="ManagedCaseResponderContext"/>'s initiator validation. See the Matter Core
    /// Specification, §4.14.
    /// </summary>
    private bool TryValidateResponder(byte[] noc, byte[]? icac, out byte[] publicKey, out NodeId nodeId)
    {
        publicKey = [];
        nodeId = default;

        if (!MatterCertificateDecoder.TryDecode(noc, out var nocCertificate) || nocCertificate is null)
        {
            return false;
        }

        MatterCertificate? icacCertificate = null;
        if (icac is { Length: > 0 } && (!MatterCertificateDecoder.TryDecode(icac, out icacCertificate) || icacCertificate is null))
        {
            return false;
        }

        // Role + validity: the NOC must be a leaf and the ICAC (if any) an intermediate.
        var now = _timeProvider.GetUtcNow();
        if (!MatterCertificateValidator.Validate(nocCertificate, MatterCertificateRole.Node, now) ||
            (icacCertificate is not null && !MatterCertificateValidator.Validate(icacCertificate, MatterCertificateRole.Intermediate, now)))
        {
            return false;
        }

        // The responder must belong to the fabric we resolved from the destination identifier.
        if (nocCertificate.Subject.MatterFabricId is not { } fabricId || fabricId != _fabric.FabricId ||
            nocCertificate.Subject.MatterNodeId is not { } peerNodeId)
        {
            return false;
        }

        if (!VerifyChain(nocCertificate, icacCertificate))
        {
            return false;
        }

        publicKey = nocCertificate.EllipticCurvePublicKey;
        nodeId = peerNodeId;
        return true;
    }

    /// <summary>
    /// Verifies the signature chain NOC ← ICAC ← RCAC (or NOC ← RCAC when no ICAC is present). The
    /// root is trusted as <see cref="ResolvedFabric.RootPublicKey"/>, already bound to this handshake
    /// by the CASE destination identifier we placed in Sigma1.
    /// </summary>
    private bool VerifyChain(MatterCertificate noc, MatterCertificate? icac)
    {
        try
        {
            return icac is not null
                ? MatterCertificateVerifier.VerifySignature(noc, icac.EllipticCurvePublicKey) &&
                  MatterCertificateVerifier.VerifySignature(icac, _fabric.RootPublicKey)
                : MatterCertificateVerifier.VerifySignature(noc, _fabric.RootPublicKey);
        }
        catch (ArgumentException)
        {
            return false; // A wrong-length issuer key is a validation failure, not a crash.
        }
    }

    // --- TLV TBS/TBE structures (spec section 4.14.1): {1:NOC, 2:ICAC?, 3:sender, 4:receiver} ----

    private static byte[] BuildTbs(byte[] noc, byte[]? icac, byte[] senderPublicKey, byte[] receiverPublicKey)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new TlvWriter(buffer);
        writer.StartStructure(TlvTag.Anonymous);
        writer.WriteByteString(TlvTag.ContextSpecific(1), noc);
        if (icac is not null)
        {
            writer.WriteByteString(TlvTag.ContextSpecific(2), icac);
        }

        writer.WriteByteString(TlvTag.ContextSpecific(3), senderPublicKey);
        writer.WriteByteString(TlvTag.ContextSpecific(4), receiverPublicKey);
        writer.EndContainer();
        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] BuildTbe(byte[] noc, byte[]? icac, byte[] signature)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new TlvWriter(buffer);
        writer.StartStructure(TlvTag.Anonymous);
        writer.WriteByteString(TlvTag.ContextSpecific(1), noc);
        if (icac is not null)
        {
            writer.WriteByteString(TlvTag.ContextSpecific(2), icac);
        }

        writer.WriteByteString(TlvTag.ContextSpecific(3), signature);
        writer.EndContainer();
        return buffer.WrittenSpan.ToArray();
    }

    private static bool TryParseTbe(byte[] tbe, out byte[] noc, out byte[]? icac, out byte[] signature)
    {
        byte[]? parsedNoc = null, parsedIcac = null, parsedSignature = null;
        var reader = new TlvReader(tbe);
        var depth = 0;
        while (reader.Read())
        {
            if (reader.IsContainer) { depth++; continue; }
            if (reader.IsEndOfContainer) { depth--; continue; }
            if (depth != 1) { continue; }

            switch (reader.Tag.TagNumber)
            {
                case 1: parsedNoc = reader.GetByteString().ToArray(); break;
                case 2: parsedIcac = reader.GetByteString().ToArray(); break;
                case 3: parsedSignature = reader.GetByteString().ToArray(); break;
            }
        }

        noc = parsedNoc ?? [];
        icac = parsedIcac;
        signature = parsedSignature ?? [];
        return parsedNoc is not null && parsedSignature is not null;
    }

    // --- Primitives ------------------------------------------------------------------------------

    private static bool VerifyEcdsa(byte[] publicKey65, byte[] data, byte[] signature)
    {
        var parameters = new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = publicKey65[1..33], Y = publicKey65[33..65] },
        };
        using var ecdsa = ECDsa.Create(parameters);
        return ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    private static byte[] CcmEncrypt(byte[] key, byte[] nonce, byte[] plaintext)
    {
        using var ccm = new AesCcm(key);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        ccm.Encrypt(nonce, plaintext, ciphertext, tag);

        var result = new byte[ciphertext.Length + tag.Length]; // Matter appends the tag to the ciphertext
        ciphertext.CopyTo(result, 0);
        tag.CopyTo(result, ciphertext.Length);
        return result;
    }

    private static bool TryCcmDecrypt(byte[] key, byte[] nonce, byte[] encrypted, out byte[] plaintext)
    {
        plaintext = [];
        if (encrypted.Length < 16)
        {
            return false;
        }

        byte[] ciphertext = encrypted[..^16];
        byte[] tag = encrypted[^16..];
        var buffer = new byte[ciphertext.Length];
        try
        {
            using var ccm = new AesCcm(key);
            ccm.Decrypt(nonce, ciphertext, tag, buffer);
            plaintext = buffer;
            return true;
        }
        catch (AuthenticationTagMismatchException)
        {
            return false;
        }
    }

    private static byte[] Hkdf(byte[] salt, byte[] ikm, ReadOnlySpan<byte> info, int length)
    {
        var okm = new byte[length];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, okm, salt, info);
        return okm;
    }

    private static byte[] EncodeUncompressed(ECPoint q)
    {
        var buffer = new byte[65];
        buffer[0] = 0x04;
        q.X!.CopyTo(buffer, 1 + (32 - q.X!.Length));
        q.Y!.CopyTo(buffer, 33 + (32 - q.Y!.Length));
        return buffer;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var length = 0;
        foreach (byte[] part in parts) { length += part.Length; }

        var result = new byte[length];
        var offset = 0;
        foreach (byte[] part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }

        return result;
    }
}