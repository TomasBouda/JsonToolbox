using JsonExplorer.Core.Model;

namespace JsonExplorer.Core.Diff;

public enum JsonDifferenceKind
{
    /// <summary>Present on the right and not on the left.</summary>
    Added,

    /// <summary>Present on the left and not on the right.</summary>
    Removed,

    /// <summary>Present on both, holding a different value of the same type.</summary>
    ValueChanged,

    /// <summary>Present on both, but one is a string where the other is a number, and so on.</summary>
    TypeChanged,
}

/// <summary>
/// One difference between two documents, addressed by a path that exists in whichever side
/// has it.
/// </summary>
/// <param name="Path">The JSONPath of the value, with real array indices.</param>
/// <param name="LeftOffset">Byte offset in the left document, or <c>-1</c> when it has no such value.</param>
/// <param name="RightOffset">Byte offset in the right document, or <c>-1</c> when it has no such value.</param>
public sealed record JsonDifference(
    JsonDifferenceKind Kind,
    string Path,
    JsonKind LeftKind,
    JsonKind RightKind,
    string? LeftValue,
    string? RightValue,
    long LeftOffset,
    long RightOffset)
{
    public override string ToString() => Kind switch
    {
        JsonDifferenceKind.Added => $"+ {Path} = {RightValue}",
        JsonDifferenceKind.Removed => $"- {Path} = {LeftValue}",
        _ => $"~ {Path}: {LeftValue} → {RightValue}",
    };
}

public sealed record DiffOptions
{
    /// <summary>
    /// Stops once this many differences have been found. Two documents that share nothing
    /// produce a difference per value, and nobody reads a million of them.
    /// </summary>
    public int MaxDifferences { get; init; } = 10_000;

    /// <summary>
    /// Cap on the children compared at one level. Beyond it the rest of that container is
    /// reported as unexamined rather than silently ignored.
    /// </summary>
    public int MaxChildrenPerLevel { get; init; } = 200_000;

    /// <summary>Length above which a differing value is shown truncated.</summary>
    public int MaxValueLength { get; init; } = 200;
}

public sealed record DiffResult
{
    public required IReadOnlyList<JsonDifference> Differences { get; init; }

    /// <summary>True when the comparison stopped at <see cref="DiffOptions.MaxDifferences"/>.</summary>
    public required bool Truncated { get; init; }

    /// <summary>
    /// True when some container held more children than the comparison was willing to read,
    /// so part of the document was never looked at.
    /// </summary>
    public required bool Incomplete { get; init; }

    /// <summary>
    /// True when some array was longer than the comparison could align, so its elements were
    /// matched by position and an insertion may read as a run of changes.
    /// </summary>
    public required bool Approximate { get; init; }

    public required TimeSpan Duration { get; init; }

    public bool Identical => Differences.Count == 0 && !Truncated;

    public int AddedCount => Differences.Count(d => d.Kind == JsonDifferenceKind.Added);

    public int RemovedCount => Differences.Count(d => d.Kind == JsonDifferenceKind.Removed);

    public int ChangedCount => Differences.Count(d =>
        d.Kind is JsonDifferenceKind.ValueChanged or JsonDifferenceKind.TypeChanged);
}
