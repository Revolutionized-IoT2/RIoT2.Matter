using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RIoT2.Matter.SecureChannel.Case;

namespace RIoT2.Matter.Clusters;

/// <summary>
/// A portable, managed P-256 keypair backed by <see cref="ECDsa"/>: it produces raw ECDSA
/// <c>r‖s</c> signatures via <see cref="ICaseOperationalKey"/> (so it can back a
/// <see cref="RIoT2.Matter.SecureChannel.Case.ResolvedFabric"/>), exposes its uncompressed public
/// key, and can emit a PKCS#10 CSR. Used for freshly generated operational keys and, wrapping a
/// caller-provided key, as the Device Attestation Certificate signer. See the Matter Core
/// Specification, sections 6.4.4 (operational key) and 3.5 (ECDSA).
/// </summary>
/// <remarks>
/// The private key stays inside the owned <see cref="ECDsa"/>; a hardware-backed deployment can
/// implement <see cref="ICaseOperationalKey"/> over a secure element instead of using this type.
/// </remarks>
public sealed class EcdsaOperationalKey : ICaseOperationalKey, IDisposable
{
    private readonly ECDsa _key;
    private readonly byte[] _publicKey;

    /// <summary>Generates a new random P-256 operational keypair.</summary>
    public EcdsaOperationalKey()
        : this(ECDsa.Create(ECCurve.NamedCurves.nistP256))
    {
    }

    /// <summary>Wraps an existing <see cref="ECDsa"/> (e.g. an imported DAC key); takes ownership and disposes it.</summary>
    public EcdsaOperationalKey(ECDsa key)
    {
        ArgumentNullException.ThrowIfNull(key);
        _key = key;
        _publicKey = ExportUncompressedPublicKey(key);
    }

    /// <summary>The public key as a 65-byte uncompressed P-256 point (0x04 ‖ X ‖ Y).</summary>
    public ReadOnlySpan<byte> PublicKey => _publicKey;

    /// <summary>
    /// Exports the private key as an encrypted PKCS#8 blob (PBES2) so a fabric entry can be persisted and
    /// later restored via <see cref="ImportEncrypted"/>. The key never leaves this process in plaintext.
    /// </summary>
    /// <param name="password">The passphrase that wraps the key; derive it from a device-bound secret.</param>
    /// <param name="pbeParameters">
    /// The key-derivation and cipher parameters; defaults to AES-256-CBC with SHA-256 and 210,000
    /// PBKDF2 iterations when <see langword="null"/>.
    /// </param>
    public byte[] ExportEncryptedPrivateKey(ReadOnlySpan<char> password, PbeParameters? pbeParameters = null)
    {
        pbeParameters ??= new PbeParameters(
            PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, iterationCount: 210_000);
        return _key.ExportEncryptedPkcs8PrivateKey(password, pbeParameters);
    }

    /// <summary>Reconstructs an operational key previously produced by <see cref="ExportEncryptedPrivateKey"/>.</summary>
    /// <param name="password">The passphrase used at export time.</param>
    /// <param name="encryptedPkcs8PrivateKey">The encrypted PKCS#8 blob.</param>
    public static EcdsaOperationalKey ImportEncrypted(
        ReadOnlySpan<char> password, ReadOnlySpan<byte> encryptedPkcs8PrivateKey)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            key.ImportEncryptedPkcs8PrivateKey(password, encryptedPkcs8PrivateKey, out _);
        }
        catch
        {
            key.Dispose();
            throw;
        }

        return new EcdsaOperationalKey(key);
    }

    /// <inheritdoc />
    /// <remarks>Returns the 64-byte raw ECDSA r‖s (IEEE P1363), the form Matter requires.</remarks>
    public byte[] Sign(ReadOnlySpan<byte> message) => _key.SignData(message, HashAlgorithmName.SHA256);

    /// <summary>Produces a DER-encoded PKCS#10 certificate signing request with an empty subject, self-signed by this key.</summary>
    public byte[] CreateCertificateSigningRequest()
    {
        var request = new CertificateRequest(new X500DistinguishedName(string.Empty), _key, HashAlgorithmName.SHA256);
        return request.CreateSigningRequest();
    }

    /// <inheritdoc />
    public void Dispose() => _key.Dispose();

    private static byte[] ExportUncompressedPublicKey(ECDsa key)
    {
        var parameters = key.ExportParameters(includePrivateParameters: false);
        var x = parameters.Q.X ?? throw new CryptographicException("The key has no public X coordinate.");
        var y = parameters.Q.Y ?? throw new CryptographicException("The key has no public Y coordinate.");

        // 0x04 ‖ X ‖ Y, right-aligning each 32-byte coordinate in case the BCL trimmed a leading zero.
        var publicKey = new byte[65];
        publicKey[0] = 0x04;
        x.CopyTo(publicKey, 1 + (32 - x.Length));
        y.CopyTo(publicKey, 33 + (32 - y.Length));
        return publicKey;
    }
}