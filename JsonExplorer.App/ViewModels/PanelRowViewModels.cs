using JsonExplorer.Core.Diagnostics;
using JsonExplorer.Core.Diff;
using JsonExplorer.Core.Model;
using JsonExplorer.Core.Search;

namespace JsonExplorer.App.ViewModels;

/// <summary>
/// Presentation wrappers for the three side panels.
/// </summary>
/// <remarks>
/// The core types they wrap are deliberately free of any UI concern, so the formatting and
/// the style classes that colour a row live here instead. Exposing the classes as booleans
/// rather than converting a value to a brush keeps the colours in the theme, where they can
/// follow light and dark without any code being involved.
/// </remarks>
public sealed class DiagnosticRowViewModel(JsonDiagnostic diagnostic)
{
    public JsonDiagnostic Diagnostic { get; } = diagnostic;

    public string Code => Diagnostic.Code;

    public string Message => Diagnostic.Message;

    public string Path => Diagnostic.Path;

    public string? Hint => Diagnostic.Hint;

    public bool HasHint => !string.IsNullOrEmpty(Diagnostic.Hint);

    public long ByteOffset => Diagnostic.ByteOffset;

    public string SeverityLabel => Diagnostic.Severity switch
    {
        DiagnosticSeverity.Error => "ERROR",
        DiagnosticSeverity.Warning => "WARNING",
        _ => "INFO",
    };

    public bool IsError => Diagnostic.Severity == DiagnosticSeverity.Error;

    public bool IsWarning => Diagnostic.Severity == DiagnosticSeverity.Warning;

    public bool IsInfo => Diagnostic.Severity == DiagnosticSeverity.Info;

    /// <summary>Shown only when the finding was folded, so a single occurrence carries no badge.</summary>
    public string? OccurrenceText => Diagnostic.Occurrences > 1 ? $"{Diagnostic.Occurrences:N0}×" : null;

    public bool IsFolded => Diagnostic.Occurrences > 1;
}

/// <summary>One path in the inferred structure, with its share of the document's bytes.</summary>
public sealed class PathRowViewModel(PathStats stats, long largestPathBytes)
{
    public PathStats Stats { get; } = stats;

    public string Path => Stats.Path;

    public string TypesText => Stats.DescribeTypes();

    public string CountText => $"{Stats.Count:N0}×";

    public string SizeText => ByteSize.Format(Stats.TotalBytes);

    /// <summary>
    /// Width of the row's bar, relative to the heaviest path in the document.
    /// </summary>
    /// <remarks>
    /// Scaled against the largest path rather than against the file, because the root always
    /// accounts for everything and would flatten every other bar to nothing.
    /// </remarks>
    public double Share => largestPathBytes <= 0 ? 0 : Math.Clamp((double)Stats.TotalBytes / largestPathBytes, 0, 1);

    /// <summary>True when the path holds more than one type, which is the finding worth seeing here.</summary>
    public bool IsMixed => Stats.SignificantKinds.Count() > 1;
}

/// <summary>One difference between the open document and the one it was compared against.</summary>
public sealed class DiffRowViewModel(JsonDifference difference)
{
    public JsonDifference Difference { get; } = difference;

    public string Path => Difference.Path;

    public string? LeftValue => Difference.LeftValue;

    public string? RightValue => Difference.RightValue;

    /// <summary>
    /// Where to jump to in the open document. Added values exist only in the other file, so
    /// there is nowhere to go.
    /// </summary>
    public long ByteOffset => Difference.LeftOffset;

    public bool CanReveal => Difference.LeftOffset >= 0;

    public bool IsAdded => Difference.Kind == JsonDifferenceKind.Added;

    public bool IsRemoved => Difference.Kind == JsonDifferenceKind.Removed;

    public bool IsChanged => Difference.Kind is JsonDifferenceKind.ValueChanged or JsonDifferenceKind.TypeChanged;

    public string KindLabel => Difference.Kind switch
    {
        JsonDifferenceKind.Added => "ADDED",
        JsonDifferenceKind.Removed => "REMOVED",
        JsonDifferenceKind.TypeChanged => "TYPE",
        _ => "CHANGED",
    };

    /// <summary>True for a change, where both sides are worth showing side by side.</summary>
    public bool ShowsBothSides => IsChanged;

    /// <summary>The single value to show when only one side has one.</summary>
    public string? OnlyValue => IsAdded ? RightValue : LeftValue;

    /// <summary>Spelled out for a type change, where the kinds are the point.</summary>
    public string? TypeNote => Difference.Kind == JsonDifferenceKind.TypeChanged
        ? $"{Difference.LeftKind.ToDisplayName()} → {Difference.RightKind.ToDisplayName()}"
        : null;

    public bool HasTypeNote => TypeNote is not null;
}

/// <summary>One search hit, coloured by the kind of value it was found in.</summary>
public sealed class SearchHitRowViewModel(SearchHit hit, SearchHighlight highlight)
{
    public SearchHit Hit { get; } = hit;

    /// <summary>The term to mark up in the snippet, which is the term that produced this hit.</summary>
    public SearchHighlight Highlight { get; } = highlight;

    public string Path => Hit.Path;

    public string Snippet => Hit.Snippet;

    public long ByteOffset => Hit.ByteOffset;

    public string TargetLabel => Hit.Target == SearchHitTarget.Key ? "key" : Hit.Kind.ToDisplayName();

    public bool IsString => Hit.Target == SearchHitTarget.Value && Hit.Kind == JsonKind.String;

    public bool IsNumber => Hit.Target == SearchHitTarget.Value && Hit.Kind.IsNumber();

    public bool IsBoolean => Hit.Target == SearchHitTarget.Value && Hit.Kind.IsBoolean();

    public bool IsNull => Hit.Target == SearchHitTarget.Value && Hit.Kind == JsonKind.Null;
}
