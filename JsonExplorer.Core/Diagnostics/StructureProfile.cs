using JsonExplorer.Core.Model;

namespace JsonExplorer.Core.Diagnostics;

/// <summary>
/// What was observed at one normalized path — that is, at one position in the shape of the
/// document, with all array indices collapsed together.
/// </summary>
/// <remarks>
/// This is the schema the explorer infers from the data rather than from a declaration, and
/// it is where most of the interesting findings come from. A field that is a string in 98 %
/// of records and a number in the other 2 % is almost always a bug, and no syntax checker
/// will ever tell you about it.
/// </remarks>
public sealed class PathStats
{
    private readonly Dictionary<JsonKind, long> _kindCounts = [];

    public PathStats(string path, string? parentPath)
    {
        Path = path;
        ParentPath = parentPath;
    }

    /// <summary>The normalized path, for example <c>$.orders[].customer.id</c>.</summary>
    public string Path { get; }

    /// <summary>
    /// The normalized path of the containing value, or <c>null</c> for the root.
    /// </summary>
    /// <remarks>
    /// Stored rather than derived by trimming the last segment off <see cref="Path"/>,
    /// because a property name is allowed to contain dots and brackets, which would make
    /// that trimming quietly wrong on exactly the documents worth inspecting.
    /// </remarks>
    public string? ParentPath { get; }

    /// <summary>How many values were seen at this path.</summary>
    public long Count { get; private set; }

    /// <summary>Total bytes occupied by the values at this path, nested content included.</summary>
    public long TotalBytes { get; private set; }

    public IReadOnlyDictionary<JsonKind, long> KindCounts => _kindCounts;

    public double? MinNumber { get; private set; }

    public double? MaxNumber { get; private set; }

    public int MinLength { get; private set; } = int.MaxValue;

    public int MaxLength { get; private set; }

    public long NullCount { get; private set; }

    /// <summary>The kind seen most often, which is what the inferred schema would declare.</summary>
    public JsonKind DominantKind
    {
        get
        {
            JsonKind best = JsonKind.Undefined;
            long bestCount = -1;
            foreach ((JsonKind kind, long count) in _kindCounts)
            {
                if (count > bestCount)
                {
                    best = kind;
                    bestCount = count;
                }
            }

            return best;
        }
    }

    /// <summary>
    /// Share of values at this path that are of the dominant kind. Anything below 1 means the
    /// path is not consistently typed.
    /// </summary>
    public double DominantShare => Count == 0 ? 0 : (double)_kindCounts.GetValueOrDefault(DominantKind) / Count;

    /// <summary>
    /// Distinct kinds, ignoring the difference between <c>true</c> and <c>false</c> and not
    /// counting <c>null</c>, which is an absence rather than a competing type.
    /// </summary>
    public IEnumerable<JsonKind> SignificantKinds =>
        _kindCounts.Keys.Where(k => k != JsonKind.Null).Select(k => k.Normalize()).Distinct();

    internal void Observe(JsonKind kind, long byteLength, double? numericValue, int? textLength)
    {
        Count++;
        TotalBytes += byteLength;
        _kindCounts[kind] = _kindCounts.GetValueOrDefault(kind) + 1;

        if (kind == JsonKind.Null)
        {
            NullCount++;
        }

        if (numericValue is { } number)
        {
            MinNumber = MinNumber is null ? number : Math.Min(MinNumber.Value, number);
            MaxNumber = MaxNumber is null ? number : Math.Max(MaxNumber.Value, number);
        }

        if (textLength is { } length)
        {
            MinLength = Math.Min(MinLength, length);
            MaxLength = Math.Max(MaxLength, length);
        }
    }

    /// <summary>
    /// Adds the byte size of a container once its closing token has been read.
    /// </summary>
    /// <remarks>
    /// A container is observed when it opens, because that is when its path is known, but its
    /// size is only known when it closes. Splitting the two is what lets the size profiler
    /// attribute bytes to a shape without a second pass.
    /// </remarks>
    internal void AddBytes(long byteLength) => TotalBytes += byteLength;

    /// <summary>Renders the observed types the way an inferred schema would read.</summary>
    public string DescribeTypes()
    {
        if (Count == 0)
        {
            return "-";
        }

        // Counts come first and percentages second, because the interesting case is the rare
        // type: "string 0.0 %" hides the eleven records that actually differ, "11×" does not.
        IEnumerable<string> parts = _kindCounts
            .OrderByDescending(pair => pair.Value)
            .Select(pair => pair.Value == Count
                ? pair.Key.ToDisplayName()
                : $"{pair.Key.ToDisplayName()} {pair.Value:N0}× ({(double)pair.Value / Count:P2})");

        return string.Join(" | ", parts);
    }
}

/// <summary>
/// The inferred shape of the whole document: every normalized path that was seen, and what
/// was seen there.
/// </summary>
public sealed class StructureProfile
{
    private readonly Dictionary<string, PathStats> _paths = [];
    private readonly int _maxPaths;

    public StructureProfile(int maxPaths = 50_000)
    {
        _maxPaths = maxPaths;
    }

    public IReadOnlyDictionary<string, PathStats> Paths => _paths;

    /// <summary>
    /// True when the document had more distinct paths than the profile was willing to hold.
    /// This happens with objects used as lookup tables, where every key is unique and the
    /// "shape" is really data.
    /// </summary>
    public bool Truncated { get; private set; }

    internal void Observe(string path, string? parentPath, JsonKind kind, long byteLength, double? numericValue, int? textLength)
    {
        if (!_paths.TryGetValue(path, out PathStats? stats))
        {
            if (_paths.Count >= _maxPaths)
            {
                Truncated = true;
                return;
            }

            stats = new PathStats(path, parentPath);
            _paths.Add(path, stats);
        }

        stats.Observe(kind, byteLength, numericValue, textLength);
    }

    public PathStats? Find(string path) => _paths.GetValueOrDefault(path);

    /// <summary>Paths ordered by how much of the file they account for — the size profiler.</summary>
    public IEnumerable<PathStats> ByWeight() => _paths.Values.OrderByDescending(s => s.TotalBytes);
}
