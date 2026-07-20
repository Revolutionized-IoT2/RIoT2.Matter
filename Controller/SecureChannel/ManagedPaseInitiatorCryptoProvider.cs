using System.Numerics;
using System.Security.Cryptography;
using RIoT2.Matter.Crypto;
using RIoT2.Matter.SecureChannel.Pase;

namespace RIoT2.Matter.Controller.SecureChannel;

/// <summary>
/// A portable, managed <see cref="IPaseInitiatorCryptoProvider"/> implementing the SPAKE2+ prover
/// over P-256/SHA-256 for the commissioner. It derives the SPAKE2+ scalars w0 and w1 from the setup
/// passcode via PBKDF2 (the same derivation the device provisioning uses in
/// <c>PaseVerifierGenerator</c>) and binds the handshake transcript to the PBKDFParamRequest/Response
/// payloads. See the Matter Core Specification, sections 3.10 (SPAKE2+) and 4.13.2.
/// </summary>
public sealed class ManagedPaseInitiatorCryptoProvider : IPaseInitiatorCryptoProvider
{
    /// <summary>crypto_w_size_bytes = crypto_group_size_bytes + 8 (specification section 3.10).</summary>
    private const int WSize = P256Curve.FieldLength + 8; // 40

    private static readonly byte[] ContextPrefix = "CHIP PAKE V1 Commissioning"u8.ToArray();

    /// <inheritdoc />
    public IPaseInitiatorContext CreateInitiator(
        SetupPasscode passcode,
        PbkdfParameters parameters,
        ReadOnlySpan<byte> pbkdfParamRequestPayload,
        ReadOnlySpan<byte> pbkdfParamResponsePayload)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        var (w0, w1) = DeriveScalars(passcode, parameters);

        // Context = SHA-256("CHIP PAKE V1 Commissioning" || PBKDFParamRequest || PBKDFParamResponse).
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(ContextPrefix);
        hash.AppendData(pbkdfParamRequestPayload);
        hash.AppendData(pbkdfParamResponsePayload);
        var context = hash.GetHashAndReset();

        return new Spake2PlusInitiatorContext(w0, w1, context);
    }

    /// <summary>
    /// w0s || w1s = PBKDF2-HMAC-SHA256(passcode, salt, iterations, 2 * crypto_w_size_bytes), each
    /// reduced modulo the group order n. Mirrors <c>PaseVerifierGenerator</c>, but the prover retains
    /// w1 (the responder discards it, storing only L = w1*G).
    /// </summary>
    private static (BigInteger W0, BigInteger W1) DeriveScalars(SetupPasscode passcode, PbkdfParameters parameters)
    {
        Span<byte> passcodeBytes = stackalloc byte[sizeof(uint)];
        passcode.WriteLittleEndian(passcodeBytes);

        Span<byte> ws = stackalloc byte[2 * WSize];
        Rfc2898DeriveBytes.Pbkdf2(passcodeBytes, parameters.Salt, ws, (int)parameters.Iterations, HashAlgorithmName.SHA256);

        var w0 = P256Curve.ReadFieldBE(ws[..WSize]) % P256Curve.Order;
        var w1 = P256Curve.ReadFieldBE(ws[WSize..]) % P256Curve.Order;

        CryptographicOperations.ZeroMemory(ws);
        CryptographicOperations.ZeroMemory(passcodeBytes);
        return (w0, w1);
    }
}