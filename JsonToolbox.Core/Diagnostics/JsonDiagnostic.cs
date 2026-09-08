namespace JsonToolbox.Core.Diagnostics;

public enum DiagnosticSeverity
{
    /// <summary>Worth knowing about, but the document is fine.</summary>
    Info,

    /// <summary>Valid JSON that will still bite somebody downstream.</summary>
    Warning,

    /// <summary>The document is not valid JSON, or will be read wrongly by conforming parsers.</summary>
    Error,
}

/// <summary>
/// Stable identifiers for the problems the inspector reports, so that a rule can be muted,
/// documented and tested by name rather than by matching its message text.
/// </summary>
public static class DiagnosticCodes
{
    public const string SyntaxError = "JE0001";
    public const string DuplicateKey = "JE0002";
    public const string IntegerPrecisionLoss = "JE0003";
    public const string FloatPrecisionLoss = "JE0004";
    public const string MixedArrayTypes = "JE0005";
    public const string InconsistentKeyNaming = "JE0006";
    public const string KeysDifferOnlyByCase = "JE0007";
    public const string SuspiciousStringValue = "JE0008";
    public const string EmptyKey = "JE0009";
    public const string DeepNesting = "JE0010";
    public const string InconsistentOptionality = "JE0011";
    public const string PossibleSecret = "JE0012";
    public const string EmbeddedJsonString = "JE0013";
    public const string HugeValue = "JE0014";
}

/// <summary>
/// One problem found in the document, addressed by both a path (which node) and a byte
/// offset (where in the text), because the user may be looking at either view.
/// </summary>
public sealed record JsonDiagnostic(
    string Code,
    DiagnosticSeverity Severity,
    string Message,
    string Path,
    long ByteOffset,
    long LineNumber = 0)
{
    /// <summary>
    /// What the user should do about it, when there is a useful answer. Kept separate from
    /// the message so the list stays scannable and the advice appears only on the selected row.
    /// </summary>
    public string? Hint { get; init; }

    /// <summary>
    /// How many values hit this rule at this path.
    /// </summary>
    /// <remarks>
    /// A file with a million records that all overflow the same numeric field has one
    /// problem, not a million. Reporting it once with a count is the difference between a
    /// usable list and an unusable one, and <see cref="ByteOffset"/> points at the first
    /// occurrence so the user still has somewhere to jump to.
    /// </remarks>
    public long Occurrences { get; init; } = 1;

    public override string ToString() => $"{Code} {Severity}: {Message} at {Path}";
}
