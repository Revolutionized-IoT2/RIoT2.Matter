namespace RIoT2.Matter.InteractionModel;

/// <summary>
/// The targeting mode of a list write, corresponding to the tri-state of the wire ListIndex field
/// (spec §10.6.2).
/// </summary>
public enum ListIndexKind
{
    /// <summary>The ListIndex field is absent: the operation targets the whole attribute value.</summary>
    WholeAttribute = 0,

    /// <summary>The ListIndex field is null: a new element is appended to the end of the list.</summary>
    Append,

    /// <summary>The ListIndex field carries a value: the existing element at that index is targeted.</summary>
    Element,
}

/// <summary>
/// Identifies how an <see cref="AttributePathIB"/> targets a list attribute, capturing the tri-state
/// of the wire ListIndex field (spec §10.6.2): absent (the whole attribute), present-null (append a
/// new element), or a specific existing element index. The default is <see cref="WholeAttribute"/>,
/// which is the only form used for Read, Subscribe, and Report.
/// </summary>
public readonly record struct ListIndex
{
    private ListIndex(ListIndexKind kind, ushort element)
    {
        Kind = kind;
        Element = element;
    }

    /// <summary>The targeting mode.</summary>
    public ListIndexKind Kind { get; }

    /// <summary>The element index; meaningful only when <see cref="Kind"/> is <see cref="ListIndexKind.Element"/>.</summary>
    public ushort Element { get; }

    /// <summary>Targets the whole attribute value (the wire ListIndex is absent). The default form.</summary>
    public static ListIndex WholeAttribute => default;

    /// <summary>Appends a new element to the end of the list (the wire ListIndex is null).</summary>
    public static ListIndex Append => new(ListIndexKind.Append, 0);

    /// <summary>Targets the existing element at <paramref name="index"/> (the wire ListIndex is that value).</summary>
    public static ListIndex At(ushort index) => new(ListIndexKind.Element, index);
}