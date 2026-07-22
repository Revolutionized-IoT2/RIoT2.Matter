using System.Security.Cryptography;
using System.Text.Json;
using RIoT2.Matter.Controller.Hosting;
using RIoT2.Matter.Credentials;
using RIoT2.Matter.DataModel;

namespace RIoT2.Matter.Controller.Credentials;

/// <summary>
/// A portable, file-backed <see cref="ICredentialStore"/> that persists the fabric identity, its
/// RCAC, per-node NOCs, and the RCAC private key across restarts. The secret key material (the IPK
/// and the PKCS#8 root key) is encrypted at rest with AES-256-GCM under a key derived from a
/// caller-supplied protection secret, so the on-disk file never contains plaintext secrets. Public
/// certificate material is stored as Matter compact-TLV. No native/platform dependencies are used.
/// </summary>
/// <remarks>
/// The protection secret must be provided out of band (e.g. an environment variable or key vault) and
/// kept stable across restarts; losing it renders the persisted fabric unrecoverable. This is a
/// pragmatic portable default — a hardware-backed store is preferable in production.
/// </remarks>
public sealed class FileCredentialStore : ICredentialStore
{
    private const int SaltLength = 16;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const int KeyLength = 32;
    private const int Pbkdf2Iterations = 210_000;

    private readonly string _fabricPath;
    private readonly string _nodesDirectory;
    private readonly byte[] _protectionSecret;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="directory">The directory that holds the fabric and per-node credential files.</param>
    /// <param name="protectionSecret">
    /// The out-of-band secret used to derive the at-rest encryption key. Must be stable across restarts
    /// and treated as sensitive; it is never persisted.
    /// </param>
    public FileCredentialStore(string directory, ReadOnlySpan<byte> protectionSecret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (protectionSecret.IsEmpty)
        {
            throw new ArgumentException("A non-empty protection secret is required.", nameof(protectionSecret));
        }

        Directory.CreateDirectory(directory);
        _nodesDirectory = Path.Combine(directory, "nodes");
        Directory.CreateDirectory(_nodesDirectory);
        _fabricPath = Path.Combine(directory, "fabric.json");
        _protectionSecret = protectionSecret.ToArray();
    }

    public async ValueTask<FabricIdentity?> LoadFabricAsync(CancellationToken cancellationToken = default)
        => (await LoadFabricCredentialsAsync(cancellationToken).ConfigureAwait(false))?.Fabric;

    public async ValueTask<PersistedFabricCredentials?> LoadFabricCredentialsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_fabricPath))
            {
                return null;
            }

            var json = await File.ReadAllBytesAsync(_fabricPath, cancellationToken).ConfigureAwait(false);
            var dto = JsonSerializer.Deserialize<FabricFileDto>(json)
                ?? throw new InvalidOperationException("The persisted fabric file is empty or corrupt.");

            var ipk = Decrypt(dto.ProtectedIpk);
            var rootKeyPkcs8 = Decrypt(dto.ProtectedRootKeyPkcs8);
            var rootCertificate = MatterCertificateDecoder.Decode(dto.RootCertificateTlv);

            var fabric = new FabricIdentity
            {
                FabricId = new FabricId(dto.FabricId),
                RootCaId = dto.RootCaId,
                AdminNodeId = new NodeId(dto.AdminNodeId),
                IdentityProtectionKey = ipk,
                AdminVendorId = new VendorId(dto.AdminVendorId),
                Label = dto.Label,
            };

            return new PersistedFabricCredentials
            {
                Fabric = fabric,
                RootCertificate = rootCertificate,
                RootKeyPkcs8 = rootKeyPkcs8,
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask SaveFabricAsync(
        FabricIdentity fabric, MatterCertificate rootCertificate, byte[] rootKeyPkcs8, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fabric);
        ArgumentNullException.ThrowIfNull(rootCertificate);
        ArgumentNullException.ThrowIfNull(rootKeyPkcs8);

        var dto = new FabricFileDto
        {
            FabricId = fabric.FabricId.Value,
            RootCaId = fabric.RootCaId,
            AdminNodeId = fabric.AdminNodeId.Value,
            AdminVendorId = fabric.AdminVendorId.Value,
            Label = fabric.Label,
            RootCertificateTlv = MatterCertificateWire.Encode(rootCertificate),
            ProtectedIpk = Encrypt(fabric.IdentityProtectionKey),
            ProtectedRootKeyPkcs8 = Encrypt(rootKeyPkcs8),
        };

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(dto);
            await WriteAtomicAsync(_fabricPath, json, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask SaveNodeCertificateAsync(
        NodeId nodeId, MatterCertificate nodeCertificate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nodeCertificate);

        var path = Path.Combine(_nodesDirectory, $"{nodeId.Value:X16}.tlv");
        var tlv = MatterCertificateWire.Encode(nodeCertificate);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteAtomicAsync(path, tlv, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<MatterCertificate?> LoadNodeCertificateAsync(
        NodeId nodeId, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(_nodesDirectory, $"{nodeId.Value:X16}.tlv");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var tlv = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            return MatterCertificateDecoder.Decode(tlv);
        }
        finally
        {
            _gate.Release();
        }
    }

    // --- At-rest encryption (AES-256-GCM, key derived per-record via PBKDF2) --------------------

    private ProtectedBlob Encrypt(ReadOnlySpan<byte> plaintext)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var key = DeriveKey(salt);
        try
        {
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[TagLength];
            using var aes = new AesGcm(key, TagLength);
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
            return new ProtectedBlob { Salt = salt, Nonce = nonce, Ciphertext = ciphertext, Tag = tag };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private byte[] Decrypt(ProtectedBlob blob)
    {
        ArgumentNullException.ThrowIfNull(blob);
        var key = DeriveKey(blob.Salt);
        try
        {
            var plaintext = new byte[blob.Ciphertext.Length];
            using var aes = new AesGcm(key, TagLength);
            aes.Decrypt(blob.Nonce, blob.Ciphertext, blob.Tag, plaintext);
            return plaintext;
        }
        catch (AuthenticationTagMismatchException ex)
        {
            throw new InvalidOperationException(
                "Failed to decrypt a persisted credential. This almost always means the configured " +
                $"'{nameof(MatterControllerOptions.CredentialProtectionSecret)}' does not match the secret used when the " +
                "credential file was written. Restore the original protection secret to recover the fabric, or delete the " +
                "credential store to bootstrap a new fabric (previously commissioned nodes must then be re-commissioned).",
                ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private byte[] DeriveKey(byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(_protectionSecret, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, KeyLength);

    private static async Task WriteAtomicAsync(string path, byte[] contents, CancellationToken cancellationToken)
    {
        // Write to a temp file then move, so a crash mid-write never leaves a half-written credential file.
        var temp = path + ".tmp";
        await File.WriteAllBytesAsync(temp, contents, cancellationToken).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }

    private sealed class FabricFileDto
    {
        public ulong FabricId { get; init; }
        public ulong RootCaId { get; init; }
        public ulong AdminNodeId { get; init; }
        public ushort AdminVendorId { get; init; }
        public string? Label { get; init; }
        public byte[] RootCertificateTlv { get; init; } = [];
        public ProtectedBlob ProtectedIpk { get; init; } = new();
        public ProtectedBlob ProtectedRootKeyPkcs8 { get; init; } = new();
    }

    private sealed class ProtectedBlob
    {
        public byte[] Salt { get; init; } = [];
        public byte[] Nonce { get; init; } = [];
        public byte[] Ciphertext { get; init; } = [];
        public byte[] Tag { get; init; } = [];
    }
}