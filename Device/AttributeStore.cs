using RIoT2.Matter.DataModel;
using RIoT2.Matter.InteractionModel;
using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.Device;

/// <summary>
/// A reusable backing store for a cluster's non-global attributes. It registers strongly-typed
/// <see cref="Attribute{T}"/> slots, serves Interaction Model reads (TLV-encode) and writes
/// (decode + constraint-check), and exposes the attribute-id list for the cluster's
/// <see cref="Cluster.AttributeIds"/>. This collapses the per-cluster
/// <see cref="Cluster.ReadAttributeCoreAsync"/> / <see cref="Cluster.WriteAttributeCoreAsync"/>
/// boilerplate into a few declarative registrations. See the Matter Core Specification, section 7.
/// </summary>
/// <remarks>
/// Construct the store in the owning cluster's constructor, passing the cluster's
/// <c>IncrementDataVersion</c> as <paramref name="onChanged"/>:
/// <code>
/// _attributes = new AttributeStore(IncrementDataVersion);
/// _onOff = _attributes.Add(OnOffAttributeId, TlvCodec.Bool, initialValue: false);
/// </code>
/// A device-driven <see cref="Attribute{T}.Value"/> set routes through <paramref name="onChanged"/>;
/// an engine write routes through <see cref="Write"/>, where the base <see cref="Cluster"/> performs
/// the data-version bump instead (so the store deliberately does not notify on that path).
/// </remarks>
public sealed class AttributeStore
{
    private readonly Dictionary<AttributeId, IAttributeSlot> _slots = new();
    private readonly List<AttributeId> _ids = new();
    private readonly Action _onChanged;

    /// <param name="onChanged">Invoked when a device-driven value change occurs; wire to the cluster's <c>IncrementDataVersion</c>.</param>
    public AttributeStore(Action onChanged)
    {
        ArgumentNullException.ThrowIfNull(onChanged);
        _onChanged = onChanged;
    }

    /// <summary>The registered attribute ids, in registration order; feed to <see cref="Cluster.AttributeIds"/>.</summary>
    public IReadOnlyCollection<AttributeId> Ids => _ids;

    /// <summary>
    /// Registers an attribute and returns its typed handle. <paramref name="writable"/> gates
    /// Interaction Model writes (read-only by default); <paramref name="validate"/> is an optional
    /// constraint whose failure yields <see cref="InteractionModelStatusCode.ConstraintError"/>.
    /// </summary>
    public Attribute<T> Add<T>(
        AttributeId id, TlvCodec<T> codec, T initialValue, bool writable = false, Func<T, bool>? validate = null)
    {
        ArgumentNullException.ThrowIfNull(codec);

        var attribute = new Attribute<T>(this, id, codec, initialValue, writable, validate);
        _slots.Add(id, attribute);
        _ids.Add(id);
        return attribute;
    }

    /// <summary>
    /// Encodes the current value of <paramref name="id"/> under <paramref name="tag"/>. Returns
    /// <see langword="false"/> when the store does not host the attribute, letting the cluster report
    /// <see cref="InteractionModelStatusCode.UnsupportedAttribute"/>.
    /// </summary>
    public bool TryRead(AttributeId id, TlvWriter writer, TlvTag tag)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (!_slots.TryGetValue(id, out var slot))
        {
            return false;
        }

        slot.Encode(writer, tag);
        return true;
    }

    /// <summary>
    /// Decodes the single TLV element in <paramref name="data"/> into attribute <paramref name="id"/>,
    /// applying its constraint. Returns the per-path status: <see cref="InteractionModelStatusCode.Success"/>,
    /// <see cref="InteractionModelStatusCode.UnsupportedAttribute"/>,
    /// <see cref="InteractionModelStatusCode.UnsupportedWrite"/>,
    /// <see cref="InteractionModelStatusCode.ConstraintError"/>, or
    /// <see cref="InteractionModelStatusCode.InvalidDataType"/>.
    /// </summary>
    public InteractionModelStatusCode Write(AttributeId id, ReadOnlyMemory<byte> data)
    {
        if (!_slots.TryGetValue(id, out var slot))
        {
            return InteractionModelStatusCode.UnsupportedAttribute;
        }

        if (!slot.IsWritable)
        {
            return InteractionModelStatusCode.UnsupportedWrite;
        }

        var reader = new TlvReader(data.Span);
        try
        {
            return reader.Read()
                ? slot.Decode(ref reader)
                : InteractionModelStatusCode.InvalidDataType; // no element present
        }
        catch (Exception ex) when (
            ex is OverflowException or InvalidOperationException or InvalidDataException or FormatException or NotSupportedException)
        {
            return InteractionModelStatusCode.InvalidDataType;
        }
    }

    internal void NotifyChanged() => _onChanged();
}