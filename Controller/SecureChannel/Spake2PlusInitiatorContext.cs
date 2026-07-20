using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using RIoT2.Matter.Crypto;
using RIoT2.Matter.SecureChannel.Pase;

namespace RIoT2.Matter.Controller.SecureChannel;

/// <summary>
/// The commissioner-side SPAKE2+ prover over P-256/SHA-256, implementing
/// <see cref="IPaseInitiatorContext"/>. It is the initiator counterpart to the library's
/// <c>Spake2PlusVerifierContext</c> and reproduces the identical transcript (TT) and key-confirmation
/// construction, so it is byte-compatible with that responder and with connectedhomeip. Unlike the
/// responder — which holds only w0 and L — the prover derives both w0 and w1 from the passcode and
/// uses w1 directly to compute V. See the Matter Core Specification, section 3.10.
/// </summary>
internal sealed class Spake2PlusInitiatorContext : IPaseInitiatorContext
{
    private const int KeyHalfLength = 16;

    private static readonly byte[] ConfirmationKeysInfo = "ConfirmationKeys"u8.ToArray();

    private readonly BigInteger _w0;
    private readonly BigInteger _w1;
    private readonly byte[] _context;

    private readonly BigInteger _x;         // the initiator's random scalar
    private readonly byte[] _initiatorShare; // pA = x*G + w0*M

    private byte[]? _initiatorConfirmation; // cA = MAC(KcA, pB)
    private byte[]? _sharedSecret;          // Ke
    private bool _disposed;
    private bool _confirmed;

    public Spake2PlusInitiatorContext(BigInteger w0, BigInteger w1, ReadOnlySpan<byte> context)
    {
        _w0 = w0;
        _w1 = w1;
        _context = context.ToArray();

        _x = GenerateScalar();

        // pA = x*G + w0*M.
        var pointA = P256Curve.Add(P256Curve.Multiply(_x, P256Curve.G), P256Curve.Multiply(_w0, P256Curve.M));
        _initiatorShare = P256Curve.Encode(pointA);
    }

    public ReadOnlyMemory<byte> InitiatorShare => _initiatorShare;

    public ReadOnlyMemory<byte> InitiatorConfirmation =>
        _initiatorConfirmation ?? throw new InvalidOperationException("ProcessResponderShare has not been called.");

    public ReadOnlySpan<byte> SharedSecret =>
        _confirmed && _sharedSecret is not null
            ? _sharedSecret
            : throw new InvalidOperationException("The shared secret is available only after a successful ProcessResponderShare.");

    public bool ProcessResponderShare(ReadOnlySpan<byte> responderShare, ReadOnlySpan<byte> responderConfirmation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Validate pB is a genuine point on P-256 (throws on failure; rejects the identity).
        var pointB = P256Curve.DecodePoint(responderShare);

        // T = pB - w0*N; Z = x*T, V = w1*T   (cofactor h = 1 for P-256).
        var t = P256Curve.Add(pointB, P256Curve.Negate(P256Curve.Multiply(_w0, P256Curve.N)));
        var z = P256Curve.Multiply(_x, t);
        var v = P256Curve.Multiply(_w1, t);

        var pAEncoded = _initiatorShare;
        var pBEncoded = responderShare.ToArray();
        var zEncoded = P256Curve.Encode(z);
        var vEncoded = P256Curve.Encode(v);

        var transcript = BuildTranscript(pAEncoded, pBEncoded, zEncoded, vEncoded);

        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(transcript, digest);
        CryptographicOperations.ZeroMemory(transcript);

        // Ka = digest[0..16], Ke = digest[16..32].
        var ka = digest[..KeyHalfLength];
        var ke = digest[KeyHalfLength..];

        // KcA || KcB = HKDF(salt = "", IKM = Ka, info = "ConfirmationKeys").
        Span<byte> confirmationKeys = stackalloc byte[2 * KeyHalfLength];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, ka, confirmationKeys, ReadOnlySpan<byte>.Empty, ConfirmationKeysInfo);
        var kcA = confirmationKeys[..KeyHalfLength];
        var kcB = confirmationKeys[KeyHalfLength..];

        // Verify cB = MAC(KcB, pA) before trusting the shared secret.
        var expectedResponderConfirmation = HMACSHA256.HashData(kcB, pAEncoded);
        var confirmationOk = CryptographicOperations.FixedTimeEquals(expectedResponderConfirmation, responderConfirmation);

        if (confirmationOk)
        {
            // cA = MAC(KcA, pB): our confirmation is a MAC over the responder's share.
            _initiatorConfirmation = HMACSHA256.HashData(kcA, pBEncoded);
            _sharedSecret = ke.ToArray();
            _confirmed = true;
        }

        CryptographicOperations.ZeroMemory(confirmationKeys);
        CryptographicOperations.ZeroMemory(digest);
        return confirmationOk;
    }

    public PaseSessionKeys DeriveSessionKeys()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_confirmed || _sharedSecret is null)
        {
            throw new InvalidOperationException("Session keys are available only after a successful ProcessResponderShare.");
        }

        // I2RKey || R2IKey || AttestationChallenge = HKDF(salt = "", IKM = Ke, info = "SessionKeys").
        const int sessionKeyLength = 16;
        Span<byte> okm = stackalloc byte[3 * sessionKeyLength];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, _sharedSecret, okm, ReadOnlySpan<byte>.Empty, "SessionKeys"u8);

        var keys = new PaseSessionKeys(
            okm[..sessionKeyLength].ToArray(),
            okm[sessionKeyLength..(2 * sessionKeyLength)].ToArray(),
            okm[(2 * sessionKeyLength)..].ToArray());

        CryptographicOperations.ZeroMemory(okm);
        return keys;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_initiatorConfirmation is not null)
        {
            CryptographicOperations.ZeroMemory(_initiatorConfirmation);
        }

        if (_sharedSecret is not null)
        {
            CryptographicOperations.ZeroMemory(_sharedSecret);
        }
    }

    private byte[] BuildTranscript(byte[] pA, byte[] pB, byte[] z, byte[] v)
    {
        // TT = for each element: little-endian uint64 length || element bytes, in the order
        // Context, A(empty), B(empty), M, N, pA, pB, Z, V, w0. See specification section 3.10.
        // This must match Spake2PlusVerifierContext.BuildTranscript byte-for-byte.
        var mEncoded = P256Curve.Encode(P256Curve.M);
        var nEncoded = P256Curve.Encode(P256Curve.N);
        Span<byte> w0Encoded = stackalloc byte[P256Curve.FieldLength];
        P256Curve.WriteFieldBE(_w0, w0Encoded);
        var w0Bytes = w0Encoded.ToArray();

        using var stream = new MemoryStream();

        void Append(ReadOnlySpan<byte> element)
        {
            Span<byte> lengthPrefix = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(lengthPrefix, (ulong)element.Length);
            stream.Write(lengthPrefix);
            stream.Write(element);
        }

        Append(_context);
        Append(ReadOnlySpan<byte>.Empty); // prover identity A (empty in Matter)
        Append(ReadOnlySpan<byte>.Empty); // verifier identity B (empty in Matter)
        Append(mEncoded);
        Append(nEncoded);
        Append(pA);
        Append(pB);
        Append(z);
        Append(v);
        Append(w0Bytes);

        return stream.ToArray();
    }

    private static BigInteger GenerateScalar()
    {
        // x in [2, n-1]; reducing a 256-bit uniform value modulo n has negligible bias for P-256.
        Span<byte> buffer = stackalloc byte[P256Curve.FieldLength];
        while (true)
        {
            RandomNumberGenerator.Fill(buffer);
            var candidate = new BigInteger(buffer, isUnsigned: true, isBigEndian: true) % P256Curve.Order;
            if (candidate > BigInteger.One)
            {
                return candidate;
            }
        }
    }
}