using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using RIoT2.Matter.Crypto;

namespace RIoT2.Matter.SecureChannel.Pase;

/// <summary>
/// The device-side SPAKE2+ verifier over P-256/SHA-256, implementing <see cref="IPaseVerifierContext"/>.
/// Follows the transcript and key-confirmation construction of the Matter Core Specification,
/// section 3.10 (byte-compatible with connectedhomeip).
/// </summary>
internal sealed class Spake2PlusVerifierContext : IPaseVerifierContext
{
    private const int KeyHalfLength = 16;

    private static readonly byte[] ConfirmationKeysInfo = "ConfirmationKeys"u8.ToArray();

    private readonly BigInteger _w0;
    private readonly EcPoint _l;
    private readonly byte[] _context;

    private byte[]? _responderShare;
    private byte[]? _responderConfirmation;
    private byte[]? _confirmationKeyA;
    private byte[]? _sharedSecret;
    private bool _disposed;
    private bool _confirmed;

    public Spake2PlusVerifierContext(BigInteger w0, EcPoint l, ReadOnlySpan<byte> context)
    {
        _w0 = w0;
        _l = l;
        _context = context.ToArray();
    }

    public ReadOnlyMemory<byte> ResponderShare =>
        _responderShare ?? throw new InvalidOperationException("ProcessInitiatorShare has not been called.");

    public ReadOnlyMemory<byte> ResponderConfirmation =>
        _responderConfirmation ?? throw new InvalidOperationException("ProcessInitiatorShare has not been called.");

    public ReadOnlyMemory<byte> SharedSecret =>
        _confirmed && _sharedSecret is not null
            ? _sharedSecret
            : throw new InvalidOperationException("The shared secret is available only after a successful VerifyInitiatorConfirmation.");

    public void ProcessInitiatorShare(ReadOnlySpan<byte> initiatorShare)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Validate pA is a genuine point on P-256 (throws on failure; rejects the identity).
        var pointA = P256Curve.DecodePoint(initiatorShare);

        var y = GenerateScalar();

        // pB = y*G + w0*N
        var pointB = P256Curve.Add(P256Curve.Multiply(y, P256Curve.G), P256Curve.Multiply(_w0, P256Curve.N));

        // Z = y*(pA - w0*M), V = y*L   (cofactor h = 1 for P-256)
        var minusW0M = P256Curve.Negate(P256Curve.Multiply(_w0, P256Curve.M));
        var z = P256Curve.Multiply(y, P256Curve.Add(pointA, minusW0M));
        var v = P256Curve.Multiply(y, _l);

        var pAEncoded = initiatorShare.ToArray();
        var pBEncoded = P256Curve.Encode(pointB);
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

        _sharedSecret = ke.ToArray();
        _confirmationKeyA = kcA.ToArray();
        _responderShare = pBEncoded;
        _responderConfirmation = HMACSHA256.HashData(kcB, pAEncoded); // cB = MAC(KcB, pA)

        CryptographicOperations.ZeroMemory(confirmationKeys);
        CryptographicOperations.ZeroMemory(digest);
    }

    public bool VerifyInitiatorConfirmation(ReadOnlySpan<byte> initiatorConfirmation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_confirmationKeyA is null || _responderShare is null)
        {
            throw new InvalidOperationException("ProcessInitiatorShare has not been called.");
        }

        // cA = MAC(KcA, pB): the initiator's confirmation is a MAC over the responder's share.
        var expected = HMACSHA256.HashData(_confirmationKeyA, _responderShare);
        var result = CryptographicOperations.FixedTimeEquals(expected, initiatorConfirmation);
        if (result)
        {
            _confirmed = true;
        }

        return result;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_confirmationKeyA is not null)
        {
            CryptographicOperations.ZeroMemory(_confirmationKeyA);
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
        var mEncoded = P256Curve.Encode(P256Curve.M);
        var nEncoded = P256Curve.Encode(P256Curve.N);
        Span<byte> w0Encoded = stackalloc byte[P256Curve.FieldLength];
        P256Curve.WriteFieldBE(_w0, w0Encoded);

        using var stream = new MemoryStream();

        void Append(ReadOnlySpan<byte> element)
        {
            byte[] lengthPrefix = new byte[8];
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
        Append(w0Encoded);

        return stream.ToArray();
    }

    private static BigInteger GenerateScalar()
    {
        // y in [2, n-1]; reducing a 256-bit uniform value modulo n has negligible bias for P-256.
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