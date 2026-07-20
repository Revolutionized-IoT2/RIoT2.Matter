using RIoT2.Matter.DataModel;
using RIoT2.Matter.InteractionModel;
using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.Device;

/// <summary>Non-generic view of a stored attribute, letting <see cref="AttributeStore"/> serve reads and writes without knowing <c>T</c>.</summary>
internal interface IAttributeSlot
{
    AttributeId Id { get; }
    bool IsWritable { get; }
    void Encode(TlvWriter writer, TlvTag tag);
    InteractionModelStatusCode Decode(ref TlvReader reader);
}

/// <summary>
/// A single strongly-typed cluster attribute held by an <see cref="AttributeStore"/>: its current
/// value, TLV codec, write access, and optional constraint. Device logic reads and writes it through
/// <see cref="Value"/>; the Interaction Model engine reaches it via the store.
/// </summary>
/// <typeparam name="T">The attribute's in-memory value type.</typeparam>
public sealed class Attribute<T> : IAttributeSlot
{
    private readonly AttributeStore _store;
    private readonly TlvCodec<T> _codec;
    private readonly Func<T, bool>? _validate;
    private T _value;

    internal Attribute(AttributeStore store, AttributeId id, TlvCodec<T> codec, T initialValue, bool writable, Func<T, bool>? validate)
    {
        _store = store;
        Id = id;
        _codec = codec;
        _value = initialValue;
        IsWritable = writable;
        _validate = validate;
    }

    /// <summary>This attribute's identifier within its cluster.</summary>
    public AttributeId Id { get; }

    /// <summary>Whether the Interaction Model may write this attribute over the wire.</summary>
    public bool IsWritable { get; }

    /// <summary>
    /// The current value. Assigning from device logic notifies the node's change broker (bumping the
    /// cluster's data version) only when the value actually changes, so unchanged sensor updates do
    /// not churn live subscriptions.
    /// </summary>
    public T Value
    {
        get => _value;
        set => Set(value);
    }

    /// <summary>Sets the value from device logic; see <see cref="Value"/>.</summary>
    public void Set(T value)
    {
        if (EqualityComparer<T>.Default.Equals(_value, value))
        {
            return;
        }

        _value = value;
        _store.NotifyChanged();
    }

    void IAttributeSlot.Encode(TlvWriter writer, TlvTag tag) => _codec.Encode(writer, tag, _value);

    InteractionModelStatusCode IAttributeSlot.Decode(ref TlvReader reader)
    {
        // May throw on a type/range mismatch; AttributeStore.Write maps that to InvalidDataType.
        T value = _codec.Decode(ref reader);

        if (_validate is not null && !_validate(value))
        {
            return InteractionModelStatusCode.ConstraintError;
        }

        // Engine write path: the base Cluster bumps DataVersion on success, so do NOT notify here
        // (that would double-count). The device path (Set) is the one that notifies.
        _value = value;
        return InteractionModelStatusCode.Success;
    }
}