namespace RIoT2.Matter.Tlv;

/// <summary>
/// Represents a Matter TLV tag. Supports the tag forms most commonly used in the
/// Matter Interaction Model: anonymous, context-specific, and common-profile tags.
/// </summary>
public readonly struct TlvTag : IEquatable<TlvTag>
{
    private TlvTag(TlvTagControl control, uint tagNumber)
    {
        Control = control;
        TagNumber = tagNumber;
    }

    /// <summary>The tag-control form that determines how the tag is encoded.</summary>
    public TlvTagControl Control { get; }

    /// <summary>The tag number (meaning depends on <see cref="Control"/>).</summary>
    public uint TagNumber { get; }

    /// <summary>An anonymous tag (used for container members without a field id).</summary>
    public static TlvTag Anonymous { get; } = new(TlvTagControl.Anonymous, 0);

    /// <summary>A context-specific tag identifying a field within a structure (0–255).</summary>
    public static TlvTag ContextSpecific(byte tagNumber) =>
        new(TlvTagControl.ContextSpecific, tagNumber);

    /// <summary>
    /// A common-profile tag. A 2-byte form is used when the number fits in 16 bits,
    /// otherwise the 4-byte form is used.
    /// </summary>
    public static TlvTag Common(uint tagNumber) => new(
        tagNumber <= ushort.MaxValue
            ? TlvTagControl.CommonProfile2Bytes
            : TlvTagControl.CommonProfile4Bytes,
        tagNumber);

    /// <summary>True when this is the anonymous tag.</summary>
    public bool IsAnonymous => Control == TlvTagControl.Anonymous;

    public bool Equals(TlvTag other) => Control == other.Control && TagNumber == other.TagNumber;

    public override bool Equals(object? obj) => obj is TlvTag other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Control, TagNumber);

    public override string ToString() => IsAnonymous ? "Anonymous" : $"{Control}:{TagNumber}";

    public static bool operator ==(TlvTag left, TlvTag right) => left.Equals(right);

    public static bool operator !=(TlvTag left, TlvTag right) => !left.Equals(right);
}