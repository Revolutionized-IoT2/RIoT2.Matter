using System.Security.Cryptography;
using RIoT2.Matter.Crypto;

namespace RIoT2.Matter.SecureChannel.Pase;

/// <summary>
/// Generates SPAKE2+ verifiers and PBKDF parameters from a setup passcode. This is the device
/// provisioning counterpart to <see cref="Spake2PlusVerifierContext"/>. See the Matter Core
/// Specification, section 3.10 (byte-compatible with connectedhomeip's Spake2pVerifier::Generate).
/// </summary>
/// <remarks>
/// The verifier stores only w0 and L = w1·G; the secret w1 is derived, used to compute L, and
/// discarded, because possession of w1 permits device impersonation. NOTE: w1 is held transiently
/// in a <see cref="System.Numerics.BigInteger"/>, whose immutable backing store cannot be zeroed;
/// the PBKDF output buffer and passcode bytes are wiped.
/// </remarks>
public static class PaseVerifierGenerator
{
    /// <summary>crypto_w_size_bytes = crypto_group_size_bytes + 8 (specification section 3.10).</summary>
    private const int WSize = P256Curve.FieldLength + 8; // 40

    /// <summary>The serialized verifier blob length: w0 (32) || L (65).</summary>
    public const int SerializedVerifierLength = PaseVerifier.W0Length + PaseVerifier.LLength; // 97

    /// <summary>The minimum permitted PBKDF2 iteration count.</summary>
    public const uint MinIterations = 1_000;

    /// <summary>The maximum permitted PBKDF2 iteration count.</summary>
    public const uint MaxIterations = 100_000;

    /// <summary>A reasonable default PBKDF2 iteration count for provisioning.</summary>
    public const uint DefaultIterations = 10_000;

    /// <summary>Derives the SPAKE2+ verifier (w0, L) for the given passcode and PBKDF parameters.</summary>
    public static PaseVerifier GenerateVerifier(SetupPasscode passcode, PbkdfParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (!SetupPasscode.IsValid(passcode.Value))
        {
            throw new ArgumentException("The passcode is not a valid Matter setup passcode.", nameof(passcode));
        }

        ValidateParameters(parameters);

        Span<byte> passcodeBytes = stackalloc byte[sizeof(uint)];
        passcode.WriteLittleEndian(passcodeBytes);

        // w0s || w1s = PBKDF2-HMAC-SHA256(passcode, salt, iterations, 2 * crypto_w_size_bytes).
        Span<byte> ws = stackalloc byte[2 * WSize];
        Rfc2898DeriveBytes.Pbkdf2(passcodeBytes, parameters.Salt, ws, (int)parameters.Iterations, HashAlgorithmName.SHA256);

        // Reduce each 40-byte value modulo the group order n to obtain the scalars w0 and w1.
        var w0 = P256Curve.ReadFieldBE(ws[..WSize]) % P256Curve.Order;
        var w1 = P256Curve.ReadFieldBE(ws[WSize..]) % P256Curve.Order;

        CryptographicOperations.ZeroMemory(ws);
        CryptographicOperations.ZeroMemory(passcodeBytes);

        // L = w1 * G. w1 is discarded after this; only w0 and L are provisioned onto the device.
        var l = P256Curve.Multiply(w1, P256Curve.G);

        var w0Bytes = new byte[PaseVerifier.W0Length];
        P256Curve.WriteFieldBE(w0, w0Bytes);
        var lBytes = P256Curve.Encode(l);

        return new PaseVerifier(w0Bytes, lBytes);
    }

    /// <summary>Creates PBKDF parameters with a fresh random salt.</summary>
    public static PbkdfParameters GenerateParameters(
        uint iterations = DefaultIterations,
        int saltLength = PbkdfParameters.MaxSaltLength)
    {
        ValidateIterations(iterations);
        if (saltLength is < PbkdfParameters.MinSaltLength or > PbkdfParameters.MaxSaltLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(saltLength), saltLength, "Salt length must be between the permitted minimum and maximum.");
        }

        return new PbkdfParameters(iterations, RandomNumberGenerator.GetBytes(saltLength));
    }

    /// <summary>
    /// Produces a complete provisioning bundle: a random passcode (unless supplied), random PBKDF
    /// parameters, and the matching verifier.
    /// </summary>
    public static PaseProvisioning Provision(
        SetupPasscode? passcode = null,
        uint iterations = DefaultIterations,
        int saltLength = PbkdfParameters.MaxSaltLength)
    {
        var pin = passcode ?? SetupPasscode.GenerateRandom();
        var parameters = GenerateParameters(iterations, saltLength);
        return new PaseProvisioning(pin, parameters, GenerateVerifier(pin, parameters));
    }

    /// <summary>Serializes a verifier to its 97-byte on-device blob: w0 (32) || L (65).</summary>
    public static byte[] SerializeVerifier(PaseVerifier verifier)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        if (verifier.W0.Length != PaseVerifier.W0Length || verifier.L.Length != PaseVerifier.LLength)
        {
            throw new ArgumentException("Verifier fields have unexpected lengths.", nameof(verifier));
        }

        var blob = new byte[SerializedVerifierLength];
        verifier.W0.CopyTo(blob.AsSpan(0, PaseVerifier.W0Length));
        verifier.L.CopyTo(blob.AsSpan(PaseVerifier.W0Length, PaseVerifier.LLength));
        return blob;
    }

    /// <summary>Parses a 97-byte verifier blob, validating that L is a genuine P-256 point.</summary>
    public static PaseVerifier DeserializeVerifier(ReadOnlySpan<byte> data)
    {
        if (data.Length != SerializedVerifierLength)
        {
            throw new ArgumentException($"A serialized verifier must be {SerializedVerifierLength} bytes.", nameof(data));
        }

        var w0 = data[..PaseVerifier.W0Length].ToArray();
        var l = data[PaseVerifier.W0Length..].ToArray();
        _ = P256Curve.DecodePoint(l); // throws if L is not on the curve
        return new PaseVerifier(w0, l);
    }

    private static void ValidateParameters(PbkdfParameters parameters)
    {
        ValidateIterations(parameters.Iterations);
        if (parameters.Salt.Length is < PbkdfParameters.MinSaltLength or > PbkdfParameters.MaxSaltLength)
        {
            throw new ArgumentException("Salt length is outside the permitted range.", nameof(parameters));
        }
    }

    private static void ValidateIterations(uint iterations)
    {
        if (iterations is < MinIterations or > MaxIterations)
        {
            throw new ArgumentOutOfRangeException(
                nameof(iterations), iterations, $"Iterations must be between {MinIterations} and {MaxIterations}.");
        }
    }
}