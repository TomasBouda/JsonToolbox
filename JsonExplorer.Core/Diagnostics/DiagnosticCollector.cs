namespace JsonExplorer.Core.Diagnostics;

/// <summary>
/// Gathers findings during a scan, folding repeats of the same rule at the same shape into a
/// single entry with a count.
/// </summary>
/// <remarks>
/// Without this, inspecting a file of a million records where one field is consistently
/// malformed would produce a million identical rows — technically accurate and completely
/// useless. Folding them by rule and normalized path turns that into one row saying what is
/// wrong, where in the shape, and how often.
/// </remarks>
internal sealed class DiagnosticCollector(int maxDistinct = 5_000)
{
    private readonly Dictionary<(string Code, string Path), Entry> _entries = [];

    public bool Truncated { get; private set; }

    public void Report(
        string code,
        DiagnosticSeverity severity,
        string message,
        string normalizedPath,
        string concretePath,
        long byteOffset,
        string? hint = null)
    {
        if (_entries.TryGetValue((code, normalizedPath), out Entry? existing))
        {
            existing.Occurrences++;
            return;
        }

        if (_entries.Count >= maxDistinct)
        {
            Truncated = true;
            return;
        }

        _entries.Add((code, normalizedPath), new Entry
        {
            Code = code,
            Severity = severity,
            Message = message,
            Path = concretePath,
            ByteOffset = byteOffset,
            Hint = hint,
            Occurrences = 1,
        });
    }

    public List<JsonDiagnostic> Build() => _entries.Values
        .OrderByDescending(e => e.Severity)
        .ThenByDescending(e => e.Occurrences)
        .Select(e => new JsonDiagnostic(e.Code, e.Severity, e.Message, e.Path, e.ByteOffset)
        {
            Hint = e.Hint,
            Occurrences = e.Occurrences,
        })
        .ToList();

    private sealed class Entry
    {
        public required string Code { get; init; }

        public required DiagnosticSeverity Severity { get; init; }

        public required string Message { get; init; }

        public required string Path { get; init; }

        public required long ByteOffset { get; init; }

        public string? Hint { get; init; }

        public long Occurrences { get; set; }
    }
}
