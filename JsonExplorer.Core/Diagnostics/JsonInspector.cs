using System.Text;
using System.Text.Json;
using JsonExplorer.Core.Documents;
using JsonExplorer.Core.Model;
using JsonExplorer.Core.Scanning;

namespace JsonExplorer.Core.Diagnostics;

public sealed record InspectionOptions
{
    /// <summary>
    /// Integers above this magnitude are exact in a 64-bit integer but not in an IEEE-754
    /// double, so any consumer that decodes JSON numbers as doubles — JavaScript, and a great
    /// many libraries elsewhere — will read back a different value.
    /// </summary>
    public long SafeIntegerLimit { get; init; } = 9_007_199_254_740_991;

    /// <summary>Depth beyond which the document is worth a remark of its own.</summary>
    public int DeepNestingThreshold { get; init; } = 32;

    /// <summary>A single value larger than this is called out, because it usually is not meant to be there.</summary>
    public long HugeValueBytes { get; init; } = 8L * 1024 * 1024;

    /// <summary>
    /// Above this ratio, a missing field reads as an omission rather than as an optional
    /// field. A field present in 999 of 1000 records is a bug; one present in 600 is a design.
    /// </summary>
    public double OptionalityReportThreshold { get; init; } = 0.75;

    public int MaxProfilePaths { get; init; } = 50_000;

    public int MaxDistinctDiagnostics { get; init; } = 5_000;

    /// <summary>
    /// Cap on the keys tracked per object for duplicate detection. Objects used as lookup
    /// tables can hold millions of unique keys, and remembering all of them to prove they are
    /// unique would cost more memory than the explorer is allowed to spend.
    /// </summary>
    public int MaxTrackedKeysPerObject { get; init; } = 50_000;

    public bool DetectSecrets { get; init; } = true;
}

public sealed record DocumentStats
{
    public long Values { get; init; }

    public long Objects { get; init; }

    public long Arrays { get; init; }

    public long Strings { get; init; }

    public long Numbers { get; init; }

    public long Booleans { get; init; }

    public long Nulls { get; init; }

    public int MaxDepth { get; init; }

    public long Bytes { get; init; }
}

public sealed record InspectionReport
{
    public required IReadOnlyList<JsonDiagnostic> Diagnostics { get; init; }

    public required StructureProfile Profile { get; init; }

    public required DocumentStats Stats { get; init; }

    /// <summary>False when the scan was cancelled or stopped at a syntax error.</summary>
    public required bool Completed { get; init; }

    public int ErrorCount => Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);

    public int WarningCount => Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);
}

/// <summary>
/// Reads a document once and reports everything that can be learned from a single pass:
/// syntax errors, values that are valid but will be misread, and the shape of the data.
/// </summary>
/// <remarks>
/// Doing all of it in one pass is not an optimisation detail, it is the whole design. On a
/// file too large to hold in memory, every extra pass costs another full read from disk, so
/// features that would each want their own traversal are instead written as rules inside this
/// one.
/// </remarks>
public sealed class JsonInspector : JsonScanVisitor
{
    private static readonly string[] SecretKeyMarkers =
    [
        "password", "passwd", "secret", "token", "apikey", "api_key", "api-key",
        "accesskey", "access_key", "privatekey", "private_key", "clientsecret",
        "client_secret", "authorization", "connectionstring", "connection_string",
    ];

    private readonly InspectionOptions _options;
    private readonly DiagnosticCollector _collector;
    private readonly StructureProfile _profile;
    private readonly IProgress<double>? _progress;
    private readonly List<Frame> _frames = [];

    private long _totalBytes;
    private long _lastProgressOffset;
    private long _values;
    private long _objects;
    private long _arrays;
    private long _strings;
    private long _numbers;
    private long _booleans;
    private long _nulls;
    private int _maxDepth;

    public JsonInspector(InspectionOptions? options = null, IProgress<double>? progress = null)
    {
        _options = options ?? new InspectionOptions();
        _collector = new DiagnosticCollector(_options.MaxDistinctDiagnostics);
        _profile = new StructureProfile(_options.MaxProfilePaths);
        _progress = progress;

        // A sentinel frame standing for the document itself, so that the root value is
        // handled by exactly the same code as every other value.
        _frames.Add(new Frame(JsonKind.Undefined, string.Empty, JsonPathSegment.Element(0), 0));
    }

    public override void OnScanStarting(long totalBytes) => _totalBytes = totalBytes;

    public override void OnToken(ref Utf8JsonReader reader, long start, long end)
    {
        _maxDepth = Math.Max(_maxDepth, reader.CurrentDepth);
        ReportProgress(end);

        switch (reader.TokenType)
        {
            case JsonTokenType.PropertyName:
                OnPropertyName(ref reader, start);
                return;

            case JsonTokenType.EndObject:
            case JsonTokenType.EndArray:
                CloseFrame(end);
                return;

            case JsonTokenType.StartObject:
                _objects++;
                OpenFrame(JsonKind.Object, start);
                return;

            case JsonTokenType.StartArray:
                _arrays++;
                OpenFrame(JsonKind.Array, start);
                return;

            case JsonTokenType.String:
                _strings++;
                OnString(ref reader, start, end);
                break;

            case JsonTokenType.Number:
                _numbers++;
                OnNumber(ref reader, start, end);
                break;

            case JsonTokenType.True:
            case JsonTokenType.False:
                _booleans++;
                ObserveValue(reader.TokenType == JsonTokenType.True ? JsonKind.True : JsonKind.False, start, end, null, null);
                break;

            case JsonTokenType.Null:
                _nulls++;
                ObserveValue(JsonKind.Null, start, end, null, null);
                break;

            default:
                return;
        }

        // Cleared only after the rules have run, so that a diagnostic raised on a scalar can
        // still name the property that holds it.
        Current.PendingName = null;
    }

    /// <summary>Builds the report once the scan is done.</summary>
    public InspectionReport BuildReport(bool completed)
    {
        AnalyseProfile();

        return new InspectionReport
        {
            Diagnostics = _collector.Build(),
            Profile = _profile,
            Completed = completed,
            Stats = new DocumentStats
            {
                Values = _values,
                Objects = _objects,
                Arrays = _arrays,
                Strings = _strings,
                Numbers = _numbers,
                Booleans = _booleans,
                Nulls = _nulls,
                MaxDepth = _maxDepth,
                Bytes = _totalBytes,
            },
        };
    }

    private Frame Current => _frames[^1];

    private void OnPropertyName(ref Utf8JsonReader reader, long start)
    {
        string name = reader.GetString() ?? string.Empty;
        Frame frame = Current;
        frame.PendingName = name;

        if (name.Length == 0)
        {
            Report(DiagnosticCodes.EmptyKey, DiagnosticSeverity.Warning,
                "Object has a property with an empty name.",
                frame.NormalizedPath, start,
                hint: "Empty keys are legal but unreachable in most path syntaxes and are usually a serialization bug.");
            return;
        }

        frame.NoteKeyStyle(name);

        if (frame.Keys is null || frame.Keys.Count >= _options.MaxTrackedKeysPerObject)
        {
            return;
        }

        if (!frame.Keys.Add(name))
        {
            Report(DiagnosticCodes.DuplicateKey, DiagnosticSeverity.Error,
                $"Duplicate key '{name}' in the same object.",
                ChildNormalizedPath(frame, name), start,
                hint: "The JSON specification leaves this undefined: parsers variously keep the first value, the last one, or both. Whichever your producer meant, some consumer will disagree.");
            return;
        }

        if (frame.CaseFolded is not null && !frame.CaseFolded.Add(name.ToLowerInvariant()))
        {
            Report(DiagnosticCodes.KeysDifferOnlyByCase, DiagnosticSeverity.Warning,
                $"Key '{name}' differs from another key in the same object only by letter case.",
                ChildNormalizedPath(frame, name), start,
                hint: "Case-insensitive consumers — many .NET and SQL mappings among them — will treat these as one field and drop a value.");
        }
    }

    private void OnString(ref Utf8JsonReader reader, long start, long end)
    {
        ReadOnlySpan<byte> raw = reader.ValueSpan;
        string? name = Current.PendingName;

        ObserveValue(JsonKind.String, start, end, null, raw.Length);

        if (IsPlaceholderText(raw))
        {
            Report(DiagnosticCodes.SuspiciousStringValue, DiagnosticSeverity.Warning,
                $"String value \"{Encoding.UTF8.GetString(raw)}\" looks like an absent value that was written as text.",
                LastObservedPath, start,
                hint: "A consumer checking for null will not find one here.");
        }

        if (LooksLikeEmbeddedJson(raw))
        {
            Report(DiagnosticCodes.EmbeddedJsonString, DiagnosticSeverity.Info,
                "String value contains a JSON document of its own.",
                LastObservedPath, start,
                hint: "Double-encoded JSON usually means a serializer ran twice. The explorer can expand it inline.");
        }

        if (_options.DetectSecrets && name is not null && raw.Length >= 8 && IsSecretKey(name))
        {
            Report(DiagnosticCodes.PossibleSecret, DiagnosticSeverity.Warning,
                $"Property '{name}' looks like it holds a credential.",
                LastObservedPath, start,
                hint: "Turn on redaction before sharing this file or a screenshot of it.");
        }
    }

    private void OnNumber(ref Utf8JsonReader reader, long start, long end)
    {
        ReadOnlySpan<byte> raw = reader.ValueSpan;
        bool integral = raw.IndexOfAny((byte)'.', (byte)'e', (byte)'E') < 0;
        double? numeric = reader.TryGetDouble(out double d) ? d : null;

        ObserveValue(integral ? JsonKind.Integer : JsonKind.Float, start, end, numeric, null);

        if (integral)
        {
            if (!reader.TryGetInt64(out long value))
            {
                Report(DiagnosticCodes.IntegerPrecisionLoss, DiagnosticSeverity.Error,
                    $"Integer {Encoding.UTF8.GetString(raw)} does not fit in a signed 64-bit integer.",
                    LastObservedPath, start,
                    hint: "Nothing will read this value back exactly. If it is an identifier, it belongs in a string.");
            }
            else if (Math.Abs(value) > _options.SafeIntegerLimit)
            {
                Report(DiagnosticCodes.IntegerPrecisionLoss, DiagnosticSeverity.Warning,
                    $"Integer {value} is beyond the range where IEEE-754 doubles are exact.",
                    LastObservedPath, start,
                    hint: "JavaScript and every parser that decodes JSON numbers as doubles will read a nearby but different number. Identifiers of this size belong in a string.");
            }
        }
        else if (CountSignificantDigits(raw) > 17)
        {
            Report(DiagnosticCodes.FloatPrecisionLoss, DiagnosticSeverity.Info,
                $"Number {Encoding.UTF8.GetString(raw[..Math.Min(raw.Length, 40)])} carries more digits than a double can represent.",
                LastObservedPath, start,
                hint: "The trailing digits will be lost on the first round trip through a typical parser.");
        }
    }

    private void OpenFrame(JsonKind kind, long start)
    {
        // The container is observed with no byte length yet: its size is not known until its
        // closing token, which is where the profile is topped up.
        JsonPathSegment segment = NextSegment(out string normalizedPath, out string? parentPath);
        _values++;
        _profile.Observe(normalizedPath, parentPath, kind, 0, null, null);
        LastObservedPath = normalizedPath;

        Current.PendingName = null;
        _frames.Add(new Frame(kind, normalizedPath, segment, start)
        {
            Keys = kind == JsonKind.Object ? [] : null,
            CaseFolded = kind == JsonKind.Object ? [] : null,
        });
    }

    private void CloseFrame(long end)
    {
        if (_frames.Count <= 1)
        {
            return;
        }

        // Reported before the frame is popped, so that the concrete path still names this
        // container rather than the one that holds it.
        Frame frame = Current;
        long byteLength = end - frame.Start;
        _profile.Find(frame.NormalizedPath)?.AddBytes(byteLength);

        if (frame.KeyStyles != 0 && (frame.KeyStyles & (frame.KeyStyles - 1)) != 0)
        {
            Report(DiagnosticCodes.InconsistentKeyNaming, DiagnosticSeverity.Info,
                $"Object mixes naming conventions in its keys ({frame.DescribeKeyStyles()}).",
                frame.NormalizedPath, frame.Start,
                hint: "Mixed conventions usually mean two producers write into the same object, and one of them will eventually disagree about a name.", aboutCurrentFrame: true);
        }

        if (byteLength > _options.HugeValueBytes)
        {
            Report(DiagnosticCodes.HugeValue, DiagnosticSeverity.Info,
                $"Value occupies {ByteSize.Format(byteLength)}.",
                frame.NormalizedPath, frame.Start, aboutCurrentFrame: true);
        }

        if (_frames.Count - 1 >= _options.DeepNestingThreshold)
        {
            Report(DiagnosticCodes.DeepNesting, DiagnosticSeverity.Info,
                $"Nesting reaches {_frames.Count - 1} levels here.",
                frame.NormalizedPath, frame.Start,
                hint: "Many parsers refuse documents deeper than 64 levels by default.", aboutCurrentFrame: true);
        }

        _frames.RemoveAt(_frames.Count - 1);
    }

    private void ObserveValue(JsonKind kind, long start, long end, double? numeric, int? textLength)
    {
        NextSegment(out string normalizedPath, out string? parentPath);
        _values++;
        _profile.Observe(normalizedPath, parentPath, kind, end - start, numeric, textLength);
        LastObservedPath = normalizedPath;

        if (end - start > _options.HugeValueBytes)
        {
            Report(DiagnosticCodes.HugeValue, DiagnosticSeverity.Info,
                $"Value occupies {ByteSize.Format(end - start)}.",
                normalizedPath, start);
        }
    }

    /// <summary>
    /// Works out the path of the value that is about to be read, and advances the array
    /// cursor of the containing frame.
    /// </summary>
    private JsonPathSegment NextSegment(out string normalizedPath, out string? parentPath)
    {
        Frame frame = Current;
        bool isRoot = _frames.Count == 1;

        JsonPathSegment segment = frame.PendingName is { } name
            ? JsonPathSegment.Property(name)
            : JsonPathSegment.Element(frame.NextIndex++);

        if (isRoot)
        {
            normalizedPath = "$";
            parentPath = null;
        }
        else
        {
            normalizedPath = frame.PendingName is { } propertyName
                ? ChildNormalizedPath(frame, propertyName)
                : frame.NormalizedPath + "[]";
            parentPath = frame.NormalizedPath;
        }

        return segment;
    }

    private static string ChildNormalizedPath(Frame frame, string name) =>
        frame.NormalizedPath.Length == 0 ? "$." + name : frame.NormalizedPath + "." + name;

    /// <summary>
    /// The normalized path of the most recent value, used when a rule fires on a value it has
    /// already observed.
    /// </summary>
    private string LastObservedPath { get; set; } = "$";

    /// <param name="aboutCurrentFrame">
    /// True when the finding is about the container currently on top of the stack rather than
    /// about a value inside it. The container is already named by its own frame, so appending
    /// the cursor position would point one level too deep.
    /// </param>
    private void Report(
        string code,
        DiagnosticSeverity severity,
        string message,
        string path,
        long offset,
        string? hint = null,
        bool aboutCurrentFrame = false) =>
        _collector.Report(code, severity, message, path, BuildConcretePath(!aboutCurrentFrame), offset, hint);

    /// <summary>
    /// Renders the path with real array indices, so the user can jump to the actual value and
    /// not just to the shape it belongs to.
    /// </summary>
    private string BuildConcretePath(bool includeCursor = true)
    {
        // Frame 0 is the sentinel standing for the document and frame 1 is the root value,
        // neither of which is reached by a step from anywhere, so the path starts at frame 2.
        var segments = new List<JsonPathSegment>(_frames.Count);
        for (int i = 2; i < _frames.Count; i++)
        {
            segments.Add(_frames[i].Segment);
        }

        Frame frame = Current;
        if (!includeCursor)
        {
            return JsonPathFormatter.ToJsonPath(segments);
        }

        if (frame.PendingName is { } name)
        {
            segments.Add(JsonPathSegment.Property(name));
        }
        else if (_frames.Count > 1 && frame.Kind == JsonKind.Array && frame.NextIndex > 0)
        {
            segments.Add(JsonPathSegment.Element(frame.NextIndex - 1));
        }

        return JsonPathFormatter.ToJsonPath(segments);
    }

    /// <summary>
    /// Rules that need the whole document before they can fire: a field is only "sometimes
    /// missing" once every record has been counted.
    /// </summary>
    private void AnalyseProfile()
    {
        foreach (PathStats stats in _profile.Paths.Values)
        {
            if (stats.SignificantKinds.Count() > 1)
            {
                _collector.Report(DiagnosticCodes.MixedArrayTypes, DiagnosticSeverity.Warning,
                    $"Values at this path are not consistently typed: {stats.DescribeTypes()}.",
                    stats.Path, stats.Path, 0,
                    "The rare type is usually the bug. Sort the diagnostics by this path to find the offending records.");
            }

            if (stats.ParentPath is not { } parentPath)
            {
                continue;
            }

            PathStats? parent = _profile.Find(parentPath);
            if (parent is null || parent.DominantKind != JsonKind.Object || parent.Count <= 1)
            {
                continue;
            }

            double presence = (double)stats.Count / parent.Count;
            if (presence >= 1 || presence < _options.OptionalityReportThreshold)
            {
                continue;
            }

            _collector.Report(DiagnosticCodes.InconsistentOptionality,
                presence >= 0.95 ? DiagnosticSeverity.Warning : DiagnosticSeverity.Info,
                $"Present in {stats.Count} of {parent.Count} occurrences ({presence:P1}) of the containing object.",
                stats.Path, stats.Path, 0,
                "A field this consistent is not optional by design; the records missing it are worth a look.");
        }
    }

    private void ReportProgress(long offset)
    {
        if (_progress is null || _totalBytes <= 0 || offset - _lastProgressOffset < 4 * 1024 * 1024)
        {
            return;
        }

        _lastProgressOffset = offset;
        _progress.Report(Math.Clamp((double)offset / _totalBytes, 0, 1));
    }

    private static bool IsSecretKey(string name)
    {
        string folded = name.ToLowerInvariant();
        foreach (string marker in SecretKeyMarkers)
        {
            if (folded.Contains(marker, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Recognises absent values that were written as text. These survive every schema check
    /// and then fail at the point of use, because a consumer testing for null never finds one.
    /// </summary>
    private static bool IsPlaceholderText(ReadOnlySpan<byte> raw) => raw.Length is >= 3 and <= 9 && (
        raw.SequenceEqual("null"u8) ||
        raw.SequenceEqual("undefined"u8) ||
        raw.SequenceEqual("NaN"u8) ||
        raw.SequenceEqual("None"u8) ||
        raw.SequenceEqual("nil"u8) ||
        raw.SequenceEqual("N/A"u8));

    private static bool LooksLikeEmbeddedJson(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < 2)
        {
            return false;
        }

        // The bytes are still escaped here, so a quoted key reads as \" — which is exactly
        // the signature of a JSON document that was serialized into a string.
        return (raw[0] == (byte)'{' && raw[^1] == (byte)'}')
            || (raw[0] == (byte)'[' && raw[^1] == (byte)']');
    }

    private static int CountSignificantDigits(ReadOnlySpan<byte> raw)
    {
        int digits = 0;
        bool started = false;

        foreach (byte b in raw)
        {
            if (b is (byte)'e' or (byte)'E')
            {
                break;
            }

            if (b is < (byte)'0' or > (byte)'9')
            {
                continue;
            }

            if (b != (byte)'0')
            {
                started = true;
            }

            if (started)
            {
                digits++;
            }
        }

        return digits;
    }

    /// <summary>One open container, and everything the rules need to know about it.</summary>
    private sealed class Frame(JsonKind kind, string normalizedPath, JsonPathSegment segment, long start)
    {
        public JsonKind Kind { get; } = kind;

        public string NormalizedPath { get; } = normalizedPath;

        public JsonPathSegment Segment { get; } = segment;

        public long Start { get; } = start;

        public string? PendingName { get; set; }

        public int NextIndex { get; set; }

        public HashSet<string>? Keys { get; init; }

        public HashSet<string>? CaseFolded { get; init; }

        public int KeyStyles { get; private set; }

        public void NoteKeyStyle(string name) => KeyStyles |= (int)ClassifyKeyStyle(name);

        public string DescribeKeyStyles()
        {
            var names = new List<string>(4);
            if ((KeyStyles & (int)KeyStyle.Camel) != 0)
            {
                names.Add("camelCase");
            }

            if ((KeyStyles & (int)KeyStyle.Pascal) != 0)
            {
                names.Add("PascalCase");
            }

            if ((KeyStyles & (int)KeyStyle.Snake) != 0)
            {
                names.Add("snake_case");
            }

            if ((KeyStyles & (int)KeyStyle.Kebab) != 0)
            {
                names.Add("kebab-case");
            }

            return string.Join(", ", names);
        }

        /// <summary>
        /// Classifies a key by convention, deliberately returning <see cref="KeyStyle.None"/>
        /// for names that fit several conventions at once — a single lowercase word is not
        /// evidence of anything.
        /// </summary>
        private static KeyStyle ClassifyKeyStyle(string name)
        {
            if (name.Contains('_', StringComparison.Ordinal))
            {
                return KeyStyle.Snake;
            }

            if (name.Contains('-', StringComparison.Ordinal))
            {
                return KeyStyle.Kebab;
            }

            bool hasUpper = false;
            foreach (char c in name)
            {
                if (char.IsUpper(c))
                {
                    hasUpper = true;
                    break;
                }
            }

            if (!hasUpper)
            {
                return KeyStyle.None;
            }

            return char.IsUpper(name[0]) ? KeyStyle.Pascal : KeyStyle.Camel;
        }
    }

    [Flags]
    private enum KeyStyle
    {
        None = 0,
        Camel = 1,
        Pascal = 2,
        Snake = 4,
        Kebab = 8,
    }

}

/// <summary>Runs an inspection over a whole document.</summary>
public static class JsonInspection
{
    public static InspectionReport Inspect(
        JsonSource source,
        InspectionOptions? options = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        var inspector = new JsonInspector(options, progress);
        bool completed;

        try
        {
            using Stream stream = source.OpenRead();
            JsonScanner.Scan(stream, inspector, cancellationToken: cancellationToken);
            completed = true;
        }
        catch (JsonScanException ex)
        {
            InspectionReport partial = inspector.BuildReport(completed: false);
            var diagnostics = new List<JsonDiagnostic>
            {
                new(DiagnosticCodes.SyntaxError, DiagnosticSeverity.Error, ex.Message, "$", ex.ByteOffset, ex.LineNumber)
                {
                    Hint = SyntaxHints.Explain(source, ex),
                },
            };
            diagnostics.AddRange(partial.Diagnostics);

            return partial with { Diagnostics = diagnostics };
        }

        return inspector.BuildReport(completed);
    }

    public static Task<InspectionReport> InspectAsync(
        JsonSource source,
        InspectionOptions? options = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Inspect(source, options, progress, cancellationToken), cancellationToken);
}
