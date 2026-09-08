using System.Text.RegularExpressions;
using JsonToolbox.Core.Model;

namespace JsonToolbox.Core.Search;

/// <summary>Which part of the document a search looks at.</summary>
[Flags]
public enum SearchScope
{
    Keys = 1,
    Values = 2,
    Both = Keys | Values,
}

public sealed record SearchQuery
{
    public required string Text { get; init; }

    public SearchScope Scope { get; init; } = SearchScope.Both;

    public bool CaseSensitive { get; init; }

    public bool UseRegex { get; init; }

    /// <summary>Matches only when the hit is bounded by non-word characters. Ignored for regex searches.</summary>
    public bool WholeWord { get; init; }

    /// <summary>
    /// Restricts value matches to these kinds. Empty means every kind.
    /// </summary>
    /// <remarks>
    /// Searching for <c>0</c> across a large document is close to useless until it can be
    /// limited to numbers, which is why this is part of the query rather than a filter over
    /// the results.
    /// </remarks>
    public IReadOnlySet<JsonKind> KindFilter { get; init; } = new HashSet<JsonKind>();

    /// <summary>
    /// Stops the scan once this many hits are found. The user gets results in a fraction of a
    /// second and can narrow the query, which beats waiting for a complete count nobody reads.
    /// </summary>
    public int MaxResults { get; init; } = 5_000;

    public bool IsEmpty => string.IsNullOrEmpty(Text);

    /// <summary>Compiles the query into a regex, or returns <c>null</c> for a plain substring search.</summary>
    internal Regex? TryCompileRegex()
    {
        if (!UseRegex)
        {
            return null;
        }

        RegexOptions options = RegexOptions.CultureInvariant;
        if (!CaseSensitive)
        {
            options |= RegexOptions.IgnoreCase;
        }

        return new Regex(Text, options, TimeSpan.FromSeconds(2));
    }
}

/// <summary>Where a match was found.</summary>
public enum SearchHitTarget
{
    Key,
    Value,
}

public sealed record SearchHit(
    string Path,
    SearchHitTarget Target,
    JsonKind Kind,
    long ByteOffset,
    string Snippet);

public sealed record SearchResult
{
    public required IReadOnlyList<SearchHit> Hits { get; init; }

    /// <summary>True when the scan stopped at <see cref="SearchQuery.MaxResults"/> rather than at the end of the document.</summary>
    public required bool Truncated { get; init; }

    public required long BytesScanned { get; init; }

    public required TimeSpan Duration { get; init; }
}
