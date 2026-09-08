using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using JsonExplorer.Core.Documents;
using JsonExplorer.Core.Model;
using JsonExplorer.Core.Scanning;

namespace JsonExplorer.Core.Search;

/// <summary>
/// Finds every key or value matching a query, reporting the path of each hit.
/// </summary>
/// <remarks>
/// The search deliberately keeps no index. Building one over a multi-gigabyte document would
/// cost more time and disk than the searches it saves, given that the first thing a person
/// does with a newly opened file is look at it rather than query it repeatedly. A streaming
/// scan reads at disk speed and reports its first hits immediately, which is what actually
/// feels fast.
/// </remarks>
public sealed class JsonSearchVisitor : JsonScanVisitor
{
    private readonly SearchQuery _query;
    private readonly Regex? _regex;
    private readonly byte[] _needleUtf8;
    private readonly bool _needleIsAscii;
    private readonly Action<SearchHit>? _onHit;
    private readonly IProgress<double>? _progress;
    private readonly List<SearchHit> _hits = [];
    private readonly List<Frame> _frames = [];

    private long _totalBytes;
    private long _lastProgressOffset;

    public JsonSearchVisitor(SearchQuery query, Action<SearchHit>? onHit = null, IProgress<double>? progress = null)
    {
        _query = query;
        _regex = query.TryCompileRegex();
        _needleUtf8 = Encoding.UTF8.GetBytes(query.Text);
        _needleIsAscii = ByteSearch.IsAscii(_needleUtf8);
        _onHit = onHit;
        _progress = progress;

        _frames.Add(new Frame(JsonKind.Undefined));
    }

    public IReadOnlyList<SearchHit> Hits => _hits;

    public bool Truncated { get; private set; }

    public override bool WantsMoreTokens => !Truncated;

    public override void OnScanStarting(long totalBytes) => _totalBytes = totalBytes;

    public override void OnToken(ref Utf8JsonReader reader, long start, long end)
    {
        ReportProgress(end);

        switch (reader.TokenType)
        {
            case JsonTokenType.PropertyName:
                string name = reader.GetString() ?? string.Empty;
                _frames[^1].PendingName = name;

                if (_query.Scope.HasFlag(SearchScope.Keys) && Matches(reader.ValueSpan, name))
                {
                    AddHit(SearchHitTarget.Key, JsonKind.String, start, name);
                }

                return;

            case JsonTokenType.StartObject:
                Descend(JsonKind.Object);
                return;

            case JsonTokenType.StartArray:
                Descend(JsonKind.Array);
                return;

            case JsonTokenType.EndObject:
            case JsonTokenType.EndArray:
                if (_frames.Count > 1)
                {
                    _frames.RemoveAt(_frames.Count - 1);
                }

                return;

            case JsonTokenType.String:
                MatchValue(ref reader, JsonKind.String, start);
                break;

            case JsonTokenType.Number:
                MatchValue(ref reader, LooksIntegral(reader.ValueSpan) ? JsonKind.Integer : JsonKind.Float, start);
                break;

            case JsonTokenType.True:
                MatchValue(ref reader, JsonKind.True, start);
                break;

            case JsonTokenType.False:
                MatchValue(ref reader, JsonKind.False, start);
                break;

            case JsonTokenType.Null:
                MatchValue(ref reader, JsonKind.Null, start);
                break;

            default:
                return;
        }

        AdvanceCursor();
    }

    /// <summary>
    /// Enters a container. The step that led into it is consumed first, so that the array
    /// index of the container itself is counted in its parent.
    /// </summary>
    private void Descend(JsonKind kind)
    {
        Frame parent = _frames[^1];
        JsonPathSegment segment = parent.PendingName is { } name
            ? JsonPathSegment.Property(name)
            : JsonPathSegment.Element(parent.NextIndex);

        AdvanceCursor();
        _frames.Add(new Frame(kind) { Segment = segment });
    }

    /// <summary>Consumes the pending property name, or moves the array cursor along.</summary>
    private void AdvanceCursor()
    {
        Frame frame = _frames[^1];
        if (frame.PendingName is not null)
        {
            frame.PendingName = null;
        }
        else
        {
            frame.NextIndex++;
        }
    }

    private void MatchValue(ref Utf8JsonReader reader, JsonKind kind, long start)
    {
        if (!_query.Scope.HasFlag(SearchScope.Values))
        {
            return;
        }

        if (_query.KindFilter.Count > 0 && !_query.KindFilter.Contains(kind))
        {
            return;
        }

        ReadOnlySpan<byte> raw = reader.ValueSpan;

        // Only decode when the fast path cannot answer: a regex, or a non-ASCII needle that
        // may need real Unicode case folding.
        if (_regex is not null || !_needleIsAscii)
        {
            string text = kind == JsonKind.String
                ? reader.GetString() ?? string.Empty
                : Encoding.UTF8.GetString(raw);

            if (MatchesText(text))
            {
                AddHit(SearchHitTarget.Value, kind, start, text);
            }

            return;
        }

        int index = ByteSearch.IndexOf(raw, _needleUtf8, !_query.CaseSensitive);
        if (index < 0)
        {
            return;
        }

        if (_query.WholeWord && !ByteSearch.IsWholeWord(raw, index, _needleUtf8.Length))
        {
            return;
        }

        AddHit(SearchHitTarget.Value, kind, start, Snippet(raw));
    }

    private bool Matches(ReadOnlySpan<byte> raw, string decoded)
    {
        if (_regex is not null || !_needleIsAscii)
        {
            return MatchesText(decoded);
        }

        int index = ByteSearch.IndexOf(raw, _needleUtf8, !_query.CaseSensitive);
        return index >= 0 && (!_query.WholeWord || ByteSearch.IsWholeWord(raw, index, _needleUtf8.Length));
    }

    private bool MatchesText(string text)
    {
        if (_regex is not null)
        {
            return _regex.IsMatch(text);
        }

        StringComparison comparison = _query.CaseSensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        int index = text.IndexOf(_query.Text, comparison);
        if (index < 0)
        {
            return false;
        }

        if (!_query.WholeWord)
        {
            return true;
        }

        bool leftOk = index == 0 || !IsWordChar(text[index - 1]);
        int after = index + _query.Text.Length;
        return leftOk && (after >= text.Length || !IsWordChar(text[after]));
    }

    private void AddHit(SearchHitTarget target, JsonKind kind, long offset, string snippet)
    {
        _hits.Add(new SearchHit(CurrentPath(target), target, kind, offset, Truncate(snippet)));
        _onHit?.Invoke(_hits[^1]);

        if (_hits.Count >= _query.MaxResults)
        {
            Truncated = true;
        }
    }

    /// <summary>
    /// Builds the path of the value or key currently under the cursor, with real array
    /// indices so the user can jump straight to it.
    /// </summary>
    private string CurrentPath(SearchHitTarget target)
    {
        // Frame 0 is the sentinel standing for the document and frame 1 is the root value,
        // neither of which is reached by a step from anywhere, so the path starts at frame 2.
        var segments = new List<JsonPathSegment>(_frames.Count);
        for (int i = 2; i < _frames.Count; i++)
        {
            segments.Add(_frames[i].Segment);
        }

        Frame frame = _frames[^1];
        if (frame.PendingName is { } name)
        {
            segments.Add(JsonPathSegment.Property(name));
        }
        else if (_frames.Count > 1 && frame.Kind == JsonKind.Array && target == SearchHitTarget.Value)
        {
            segments.Add(JsonPathSegment.Element(frame.NextIndex));
        }

        return JsonPathFormatter.ToJsonPath(segments);
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static bool LooksIntegral(ReadOnlySpan<byte> raw) =>
        raw.IndexOfAny((byte)'.', (byte)'e', (byte)'E') < 0;

    private static string Snippet(ReadOnlySpan<byte> raw) =>
        Encoding.UTF8.GetString(raw[..Math.Min(raw.Length, 200)]);

    private static string Truncate(string text) =>
        text.Length <= 160 ? text : string.Concat(text.AsSpan(0, 160), "…");

    private void ReportProgress(long offset)
    {
        if (_progress is null || _totalBytes <= 0 || offset - _lastProgressOffset < 4 * 1024 * 1024)
        {
            return;
        }

        _lastProgressOffset = offset;
        _progress.Report(Math.Clamp((double)offset / _totalBytes, 0, 1));
    }

    private sealed class Frame(JsonKind kind)
    {
        public JsonKind Kind { get; } = kind;

        public JsonPathSegment Segment { get; init; }

        public string? PendingName { get; set; }

        public int NextIndex { get; set; }
    }
}

public static class JsonSearchEngine
{
    public static SearchResult Search(
        JsonSource source,
        SearchQuery query,
        Action<SearchHit>? onHit = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(query);

        if (query.IsEmpty)
        {
            return new SearchResult
            {
                Hits = [],
                Truncated = false,
                BytesScanned = 0,
                Duration = TimeSpan.Zero,
            };
        }

        var visitor = new JsonSearchVisitor(query, onHit, progress);
        long stopwatchStart = Stopwatch.GetTimestamp();
        long bytesScanned = 0;

        try
        {
            using Stream stream = source.OpenRead();
            bytesScanned = JsonScanner.Scan(
                stream,
                visitor,
                options: new JsonScanOptions { Strictness = JsonScanStrictness.Lenient },
                cancellationToken: cancellationToken);
        }
        catch (JsonScanException)
        {
            // A malformed document is still worth searching up to the point it breaks; the
            // inspector is what reports the syntax error itself.
        }

        return new SearchResult
        {
            Hits = visitor.Hits,
            Truncated = visitor.Truncated,
            BytesScanned = bytesScanned,
            Duration = Stopwatch.GetElapsedTime(stopwatchStart),
        };
    }

    public static Task<SearchResult> SearchAsync(
        JsonSource source,
        SearchQuery query,
        Action<SearchHit>? onHit = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Search(source, query, onHit, progress, cancellationToken), cancellationToken);
}
