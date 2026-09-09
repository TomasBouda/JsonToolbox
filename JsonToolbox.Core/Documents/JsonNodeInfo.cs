using JsonToolbox.Core.Model;

namespace JsonToolbox.Core.Documents;

/// <summary>
/// One value in the document, described well enough to render a tree row without reading
/// the document again.
/// </summary>
/// <param name="Kind">The type of the value.</param>
/// <param name="Name">
/// The property name that holds this value, or <c>null</c> when the value is an array
/// element or the document root.
/// </param>
/// <param name="Index">Position among its siblings, zero based.</param>
/// <param name="Start">Absolute byte offset of the first byte of the value.</param>
/// <param name="End">
/// Absolute byte offset just past the last byte of the value, or <c>-1</c> while the value is
/// still being read. A container is announced as soon as it opens so the user can see and
/// enter it immediately; where it ends is only known once the scan reaches its closing
/// bracket, which for the last child of a huge document is the end of the file.
/// </param>
/// <param name="ChildCount">
/// Number of direct children, or <c>-1</c> when they have not been counted yet. Counting
/// requires reading the whole value, which for the root of a large file is exactly the work
/// the toolbox is trying to defer.
/// </param>
/// <param name="Preview">
/// A short rendering of the value for the tree row. Captured during the scan that found the
/// node, because re-reading it later would mean one random read per visible row.
/// </param>
public readonly record struct JsonNodeInfo(
    JsonKind Kind,
    string? Name,
    int Index,
    long Start,
    long End,
    int ChildCount,
    string? Preview)
{
    /// <summary>
    /// Byte offset of the opening quote of this value's property name, or <c>-1</c> when the
    /// value is an array element or the document root.
    /// </summary>
    /// <remarks>
    /// Renaming a key, or deleting a property together with the name that introduces it, needs
    /// to know where the name is written — which the value's own range does not say.
    /// </remarks>
    public long NameStart { get; init; } = -1;

    /// <summary>Byte offset just past the closing quote of the property name.</summary>
    public long NameEnd { get; init; } = -1;

    /// <summary>True when the property name's position in the file is known.</summary>
    public bool HasNamePosition => NameStart >= 0 && NameEnd > NameStart;

    /// <summary>
    /// The asked-for properties of this value, in the order the document writes them, or
    /// <c>null</c> when none were asked for or this value has none of them.
    /// </summary>
    /// <remarks>
    /// Filled in by the same scan that lists a container's children, so asking for them costs
    /// no extra reading: the scan is already inside each child, one level down, counting its
    /// contents. This is what lets a pinned key show on every row of a container, and what lets
    /// a whole table of records be read in a single pass over the file.
    /// </remarks>
    public IReadOnlyList<KeyValuePair<string, string>>? PinnedValues { get; init; }

    /// <summary>Those same properties as one line, for a tree row that has no columns.</summary>
    public string? PinnedSummary => PinnedValues is { Count: > 0 } values
        ? string.Join("   ", values.Select(pair => $"{pair.Key}: {pair.Value}"))
        : null;

    /// <summary>The value of one asked-for property, or <c>null</c> when this value has no such property.</summary>
    public string? PinnedValue(string key)
    {
        if (PinnedValues is null)
        {
            return null;
        }

        foreach (KeyValuePair<string, string> pair in PinnedValues)
        {
            if (pair.Key == key)
            {
                return pair.Value;
            }
        }

        return null;
    }

    /// <summary>The byte offset doubles as a stable identity: no two values start at the same byte.</summary>
    public long Id => Start;

    /// <summary>True once the value's closing byte is known.</summary>
    public bool HasKnownExtent => End >= 0;

    /// <summary>
    /// Size of the value in bytes including everything nested inside it, or <c>-1</c> while
    /// the value is still being read.
    /// </summary>
    public long ByteLength => End < 0 ? -1 : End - Start;

    public bool IsContainer => Kind.IsContainer();

    /// <summary>True when <paramref name="offset"/> falls inside this value.</summary>
    public bool Contains(long offset) => offset >= Start && (End < 0 || offset < End);

    /// <summary>
    /// Whether the row should offer an expander. Unknown child counts are treated as
    /// expandable, so the root of an unscanned file can still be opened.
    /// </summary>
    public bool CanExpand => IsContainer && ChildCount != 0;

    /// <summary>The step from the parent to this value, for building a path.</summary>
    public JsonPathSegment ToPathSegment() =>
        Name is null ? JsonPathSegment.Element(Index) : JsonPathSegment.Property(Name);
}
