using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RIoT2.Matter.DataModel;

namespace RIoT2.Matter.Controller.Administration;

/// <summary>
/// A file-backed <see cref="ICommissionedNodeRegistry"/> that persists commissioned-node records as
/// JSON. All access is serialized through an in-process lock; the in-memory snapshot is the source
/// of truth and is flushed to disk after every mutation. Suitable for a single-process controller
/// backend; swap for a database-backed implementation for multi-process deployments.
/// </summary>
public sealed class JsonCommissionedNodeRegistry : ICommissionedNodeRegistry
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<(ulong Fabric, ulong Node), PersistedNode> _nodes = new();
    private bool _loaded;

    /// <param name="path">The JSON file path used to persist the registry.</param>
    public JsonCommissionedNodeRegistry(string path)
        => _path = string.IsNullOrWhiteSpace(path) ? throw new ArgumentException("Path is required.", nameof(path)) : path;

    public async Task AddOrUpdateAsync(CommissionedNode node, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(node);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            _nodes[Key(node.FabricId, node.NodeId)] = PersistedNode.From(node);
            await FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CommissionedNode?> GetAsync(FabricId fabricId, NodeId nodeId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            return _nodes.TryGetValue(Key(fabricId, nodeId), out var node) ? node.ToModel() : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<CommissionedNode>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            return _nodes.Values.Select(n => n.ToModel()).ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> RemoveAsync(FabricId fabricId, NodeId nodeId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            if (!_nodes.Remove(Key(fabricId, nodeId)))
            {
                return false;
            }

            await FlushAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_loaded)
        {
            return;
        }

        if (File.Exists(_path))
        {
            await using var stream = File.OpenRead(_path);
            var persisted = await JsonSerializer.DeserializeAsync<List<PersistedNode>>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false);
            if (persisted is not null)
            {
                foreach (var node in persisted)
                {
                    _nodes[(node.FabricId, node.NodeId)] = node;
                }
            }
        }

        _loaded = true;
    }

    private async Task FlushAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Write to a temp file and swap so a crash mid-write cannot corrupt the registry.
        var tempPath = _path + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, _nodes.Values.ToList(), SerializerOptions, cancellationToken).ConfigureAwait(false);
        }

        File.Move(tempPath, _path, overwrite: true);
    }

    private static (ulong, ulong) Key(FabricId fabricId, NodeId nodeId) => (fabricId.Value, nodeId.Value);

    /// <summary>The on-disk shape; primitives only, so the wire ids stay decoupled from persistence.</summary>
    private sealed record PersistedNode
    {
        public ulong FabricId { get; init; }
        public ulong NodeId { get; init; }
        public byte FabricIndex { get; init; }
        public ushort? VendorId { get; init; }
        public ushort? ProductId { get; init; }
        public string? Label { get; init; }
        public DateTimeOffset CommissionedAtUtc { get; init; }

        public static PersistedNode From(CommissionedNode node) => new()
        {
            FabricId = node.FabricId.Value,
            NodeId = node.NodeId.Value,
            FabricIndex = node.FabricIndex.Value,
            VendorId = node.VendorId?.Value,
            ProductId = node.ProductId,
            Label = node.Label,
            CommissionedAtUtc = node.CommissionedAtUtc,
        };

        public CommissionedNode ToModel() => new()
        {
            FabricId = new FabricId(FabricId),
            NodeId = new NodeId(NodeId),
            FabricIndex = new FabricIndex(FabricIndex),
            VendorId = VendorId is { } v ? new VendorId(v) : null,
            ProductId = ProductId,
            Label = Label,
            CommissionedAtUtc = CommissionedAtUtc,
        };
    }
}