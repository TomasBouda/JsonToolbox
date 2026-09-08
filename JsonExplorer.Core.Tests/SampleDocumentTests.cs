using JsonExplorer.Core.Diagnostics;
using JsonExplorer.Core.Documents;

namespace JsonExplorer.Core.Tests;

/// <summary>
/// Runs the inspector over the sample document shipped with the repository.
/// </summary>
/// <remarks>
/// The sample exists to demonstrate what the explorer finds, which makes it worth asserting
/// against: if a rule stops firing, the file that advertises it should fail the build rather
/// than quietly stop being a demonstration of anything.
/// </remarks>
public class SampleDocumentTests
{
    private static string SamplePath =>
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "samples", "problematic.json");

    [Fact]
    public void The_sample_document_exercises_every_advertised_rule()
    {
        using JsonSource source = JsonSource.FromFile(Path.GetFullPath(SamplePath));
        InspectionReport report = JsonInspection.Inspect(source);

        string[] codes = [.. report.Diagnostics.Select(d => d.Code).Distinct()];

        Assert.Contains(DiagnosticCodes.DuplicateKey, codes);
        Assert.Contains(DiagnosticCodes.IntegerPrecisionLoss, codes);
        Assert.Contains(DiagnosticCodes.FloatPrecisionLoss, codes);
        Assert.Contains(DiagnosticCodes.MixedArrayTypes, codes);
        Assert.Contains(DiagnosticCodes.InconsistentKeyNaming, codes);
        Assert.Contains(DiagnosticCodes.KeysDifferOnlyByCase, codes);
        Assert.Contains(DiagnosticCodes.SuspiciousStringValue, codes);
        Assert.Contains(DiagnosticCodes.InconsistentOptionality, codes);
        Assert.Contains(DiagnosticCodes.PossibleSecret, codes);
        Assert.Contains(DiagnosticCodes.EmbeddedJsonString, codes);

        Assert.True(report.Completed);
    }

    [Fact]
    public void Every_finding_points_at_a_path_and_an_offset_the_user_can_follow()
    {
        using JsonSource source = JsonSource.FromFile(Path.GetFullPath(SamplePath));
        InspectionReport report = JsonInspection.Inspect(source);

        foreach (JsonDiagnostic diagnostic in report.Diagnostics)
        {
            Assert.StartsWith("$", diagnostic.Path, StringComparison.Ordinal);
            Assert.InRange(diagnostic.ByteOffset, 0, source.Length);
            Assert.True(diagnostic.Occurrences >= 1);
        }
    }
}
