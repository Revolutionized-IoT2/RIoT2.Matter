using System.Buffers;
using System.Security.Cryptography;
using RIoT2.Matter.Credentials;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.Diagnostics;
using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.SecureChannel.Case;

/// <summary>
/// The device-side (responder) engine for a single CASE handshake, created by
/// <see cref="ManagedCaseCryptoProvider.CreateResponder"/>: ECDH with the initiator, the
/// Sigma2/Sigma3 AES-CCM/HKDF key schedule, this node's NOC signature, and Sigma3 validation
/// (initiator NOC/ICAC→RCAC chain + handshake signature). See the Matter Core Specification,
/// section 4.14.
/// </summary>
internal sealed class ManagedCaseResponderContext : ICaseResponderContext
{
    private const int KeyLength = 16;

    private static readonly byte[] S2KInfo = "Sigma2"u8.ToArray();
    private static readonly byte[] S3KInfo = "Sigma3"u8.ToArray();
    private static readonly byte[] SessionKeysInfo = "SessionKeys"u8.ToArray();
    private static readonly byte[] Sigma2Nonce = "NCASE_Sigma2N"u8.ToArray(); // 13-byte AES-CCM nonce
    private static readonly byte[] Sigma3Nonce = "NCASE_Sigma3N"u8.ToArray();

    private readonly ResolvedFabric _fabric;
    private readonly TimeProvider _timeProvider;
    private readonly ECDiffieHellman _ephemeral;
    private readonly byte[] _responderEphPub;
    private readonly byte[] _responderRandom;
    private readonly byte[] _resumptionId;
    private readonly List<byte> _transcript = new();

    private byte[]? _initiatorEphPub;
    private byte[]? _sharedSecret;
    private NodeId _peerNodeId;
    private uint[] _peerCaseAuthenticatedTags = System.Array.Empty<uint>();

    public ManagedCaseResponderContext(ResolvedFabric fabric, TimeProvider timeProvider)
    {
        _fabric = fabric;
        _timeProvider = timeProvider;
        _ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        _responderEphPub = EncodeUncompressed(_ephemeral.ExportParameters(includePrivateParameters: false).Q);
        _responderRandom = RandomNumberGenerator.GetBytes(32);
        _resumptionId = RandomNumberGenerator.GetBytes(16);
    }

    public ReadOnlyMemory<byte> ResponderEphemeralPublicKey => _responderEphPub;

    public ReadOnlyMemory<byte> ResponderRandom => _responderRandom;

    public NodeId PeerNodeId => _peerNodeId;

    public IReadOnlyList<uint> PeerCaseAuthenticatedTags => _peerCaseAuthenticatedTags;

    public ReadOnlyMemory<byte> SharedSecret => _sharedSecret ?? ReadOnlyMemory<byte>.Empty;

    public ReadOnlyMemory<byte> ResumptionId => _resumptionId;

    public void AppendToTranscript(ReadOnlySpan<byte> messagePayload) => _transcript.AddRange(messagePayload.ToArray());

    /// <inheritdoc />
    public byte[] BuildSigma2Encrypted(ReadOnlySpan<byte> initiatorEphemeralPublicKey)
    {
        _initiatorEphPub = initiatorEphemeralPublicKey.ToArray();
        _sharedSecret = DeriveEcdh(_initiatorEphPub);

        // S2K = HKDF(salt = IPK || Random_R || pubKey_R || SHA256(Sigma1), IKM = sharedSecret, info = "Sigma2").
        byte[] s2k = Hkdf(Concat(Ipk, _responderRandom, _responderEphPub, TranscriptHash()), _sharedSecret, S2KInfo, KeyLength);

        byte[] noc = _fabric.OperationalNoc;
        byte[]? icac = _fabric.OperationalIcac;

        byte[] tbs = BuildTbs(noc, icac, _responderEphPub, _initiatorEphPub);   // signed by this node's NOC key
        byte[] signature = _fabric.OperationalKey.Sign(tbs);

        // TODO(diagnostic): temporary - remove once CASE Sigma2 interop is confirmed. Verifies the
        // freshly produced signature against the NOC public key the peer will use, and confirms the
        // S2K AEAD round-trips. A failure here (rather than at the peer) localises the defect.
        if (MatterTrace.Enabled && MatterCertificateDecoder.TryDecode(noc, out var selfNoc) && selfNoc is not null)
        {
            bool sigOk = VerifyEcdsa(selfNoc.EllipticCurvePublicKey, tbs, signature);
            MatterTrace.WriteError(() =>
                $"[case] sigma2 self-check: sigBytes={signature.Length} (expect 64 raw P1363) " +
                $"nocPubLen={selfNoc.EllipticCurvePublicKey.Length} tbsLen={tbs.Length} signatureVerifies={sigOk}");
        }

        byte[] tbe = BuildTbe(noc, icac, signature, _resumptionId);
        byte[] encrypted = CcmEncrypt(s2k, Sigma2Nonce, tbe);

        // TODO(diagnostic): temporary - confirm the S2K schedule round-trips (encrypt then decrypt).
        if (MatterTrace.Enabled)
        {
            bool aeadOk = TryCcmDecrypt(s2k, Sigma2Nonce, encrypted, out _);
            MatterTrace.WriteError(() =>
                $"[case] sigma2 self-check: s2kAeadRoundTrips={aeadOk} tbeLen={tbe.Length} encryptedLen={encrypted.Length} " +
                $"ipkLen={Ipk.Length} transcriptHash={Convert.ToHexString(TranscriptHash())}");
        }

        return encrypted;
    }

    /// <inheritdoc />
    public bool TryProcessSigma3(ReadOnlySpan<byte> encrypted3)
    {
        // S3K = HKDF(salt = IPK || SHA256(Sigma1 || Sigma2), IKM = sharedSecret, info = "Sigma3").
        byte[] s3k = Hkdf(Concat(Ipk, TranscriptHash()), _sharedSecret!, S3KInfo, KeyLength);
        if (!TryCcmDecrypt(s3k, Sigma3Nonce, encrypted3.ToArray(), out byte[] tbe))
        {
            return false;
        }

        if (!TryParseTbe(tbe, out byte[] noc, out byte[]? icac, out byte[] signature) ||
            !TryValidateInitiator(noc, icac, out byte[] initiatorPublicKey, out NodeId peerNodeId, out uint[] peerCats))
        {
            return false;
        }

        byte[] tbs = BuildTbs(noc, icac, _initiatorEphPub!, _responderEphPub);
        if (!VerifyEcdsa(initiatorPublicKey, tbs, signature))
        {
            return false;
        }

        _peerNodeId = peerNodeId;
        _peerCaseAuthenticatedTags = peerCats;
        return true;
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

    /// <summary>
    /// Validates the initiator NOC (and optional ICAC) against the resolved fabric root: structural
    /// decode, role + validity constraints (spec §6.5.11), the NOC→ICAC→RCAC signature chain, and that
    /// the NOC is scoped to this fabric. On success returns the NOC public key (for the Sigma3
    /// signature check) and the authenticated peer node id. See the Matter Core Specification, §4.14.
    /// </summary>
    private bool TryValidateInitiator(byte[] noc, byte[]? icac, out byte[] publicKey, out NodeId nodeId, out uint[] caseAuthenticatedTags)
    {
        publicKey = [];
        nodeId = default;
        caseAuthenticatedTags = System.Array.Empty<uint>();

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

        // The initiator must belong to the fabric we resolved from the destination identifier.
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
        caseAuthenticatedTags = nocCertificate.Subject.CaseAuthenticatedTags.ToArray();
        return true;
    }

    /// <summary>
    /// Verifies the signature chain NOC ← ICAC ← RCAC (or NOC ← RCAC when no ICAC is present). The
    /// root is trusted as <see cref="ResolvedFabric.RootPublicKey"/>, already bound to this handshake
    /// by the matched CASE destination identifier.
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

    // Sigma2 TBEData2 = {1:responderNOC, 2:responderICAC?, 3:signature, 4:resumptionID}. The
    // resumptionID (field 4) is mandatory (spec §4.14.2.5); omitting it makes a spec-compliant
    // initiator (e.g. Google Home) decrypt TBEData2, reject the missing required field, silently drop
    // Sigma2, and restart the handshake with a fresh Sigma1 indefinitely.
    private static byte[] BuildTbe(byte[] noc, byte[]? icac, byte[] signature, byte[] resumptionId)
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
        writer.WriteByteString(TlvTag.ContextSpecific(4), resumptionId);
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