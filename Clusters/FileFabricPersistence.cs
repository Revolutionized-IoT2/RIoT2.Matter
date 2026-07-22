using RIoT2.Matter.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace RIoT2.Matter.Clusters;

/// <summary>
/// Persists an <see cref="OperationalCredentialsManager"/>'s committed fabric table to disk and restores
/// it on startup, so commissioned fabrics survive process restarts. The entire snapshot � operational
/// keys and IPK material � is sealed in a single AES-256-GCM envelope keyed from <c>keyPassword</c>, so
/// no plaintext secrets are written.
/// </summary>
public sealed class FileFabricPersistence : IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    private readonly OperationalCredentialsManager _manager;
    private readonly string _path;
    private readonly string _keyPassword;
    private readonly object _ioGate = new();
    private readonly EventHandler _onChanged;

    private FileFabricPersistence(OperationalCredentialsManager manager, string path, string keyPassword)
    {
        _manager = manager;
        _path = path;
        _keyPassword = keyPassword;
        _onChanged = (_, _) => Save();
    }

    /// <summary>
    /// Creates the coordinator and begins persisting on every change. When <paramref name="restoreOnAttach"/>
    /// is true (the default), restores any persisted fabrics into <paramref name="manager"/> (which must
    /// be freshly constructed) immediately. Pass false to defer that decision to the caller (e.g. to ask
    /// an operator whether to resume a previously-saved identity or start fresh) - call <see
    /// cref="TryRestore"/> afterward to restore on demand.
    /// </summary>
    /// <param name="keyPassword">
    /// The passphrase that seals the snapshot; must be reproducible across restarts (derive it from a
    /// device-bound secret such as a TPM/secure-element sealed value).
    /// </param>
    public static FileFabricPersistence Attach(
        OperationalCredentialsManager manager, string path, string keyPassword, bool restoreOnAttach = true)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentException.ThrowIfNullOrEmpty(keyPassword);

        var persistence = new FileFabricPersistence(manager, path, keyPassword);
        if (restoreOnAttach)
        {
            persistence.Restore();
        }

        manager.Changed += persistence._onChanged;
        return persistence;
    }

    /// <summary>
    /// Peeks whether <paramref name="path"/> holds at least one persisted fabric, without importing
    /// anything into any manager. Useful for deciding (e.g. via an operator prompt) whether to restore an
    /// existing identity or start fresh, before a manager even exists to attach to.
    /// </summary>
    public static bool HasPersistedFabrics(string path, string keyPassword) =>
        ReadSnapshotsFromDisk(path, keyPassword) is { Count: > 0 };

    /// <summary>
    /// Re-attempts loading fabrics from <c>path</c> on demand, in addition to the automatic restore
    /// <see cref="Attach"/> already performs at startup. Useful for manually retrying a restore, e.g.
    /// from a console command, after replacing the persisted file with a backup snapshot.
    /// </summary>
    /// <returns>
    /// The number of fabrics restored. Always 0 (no-op) if the manager already has at least one fabric,
    /// since <see cref="OperationalCredentialsManager.ImportSnapshot"/> requires an empty fabric table;
    /// also 0 if the file is missing or contains no fabrics.
    /// </returns>
    public int TryRestore()
    {
        if (_manager.Fabrics.Count > 0)
        {
            MatterTrace.WriteError(() =>
                $"[FileFabricPersistence] restore skipped: the fabric table already has {_manager.Fabrics.Count} fabric(s).");
            return 0;
        }

        return Restore();
    }

    private int Restore()
    {
        lock (_ioGate)
        {
            var snapshots = ReadSnapshotsFromDisk(_path, _keyPassword);

            if (snapshots is { Count: > 0 })
            {
                _manager.ImportSnapshot(snapshots, _keyPassword);
            }

            return snapshots?.Count ?? 0;
        }
    }

    private static List<FabricSnapshot>? ReadSnapshotsFromDisk(string path, string keyPassword)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var envelope = File.ReadAllBytes(path);
        var plaintext = SnapshotEnvelope.Open(envelope, keyPassword);
        try
        {
            return JsonSerializer.Deserialize<List<FabricSnapshot>>(plaintext, SerializerOptions);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private void Save()
    {
        var snapshots = _manager.ExportSnapshot(_keyPassword);

        MatterTrace.WriteError(() => $"[FileFabricPersistence] saving {snapshots.Count} fabric(s) to '{_path}'.");
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(snapshots, SerializerOptions);
        try
        {
            var envelope = SnapshotEnvelope.Seal(plaintext, _keyPassword);
            lock (_ioGate)
            {
                var directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // Atomic replace: write to a temp file, then move over the target.
                var temp = _path + ".tmp";
                File.WriteAllBytes(temp, envelope);
                File.Move(temp, _path, overwrite: true);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>Unsubscribes from the manager; does not delete the persisted file.</summary>
    public void Dispose() => _manager.Changed -= _onChanged;
}