using JsonToolbox.Core.Diagnostics;
using JsonToolbox.Core.Documents;
using JsonToolbox.Core.Model;

namespace JsonToolbox.Core.Tests;

public class JsonInspectorTests
{
    private static InspectionReport Inspect(string json, InspectionOptions? options = null)
    {
        using JsonSource source = JsonSource.FromText(json);
        return JsonInspection.Inspect(source, options);
    }

    private static JsonDiagnostic? Find(InspectionReport report, string code) =>
        report.Diagnostics.FirstOrDefault(d => d.Code == code);

    [Fact]
    public void Reports_duplicate_keys_as_an_error()
    {
        InspectionReport report = Inspect("""{"id": 1, "name": "a", "id": 2}""");

        JsonDiagnostic? diagnostic = Find(report, DiagnosticCodes.DuplicateKey);

        Assert.NotNull(diagnostic);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("id", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reports_integers_beyond_the_exact_range_of_a_double()
    {
        InspectionReport report = Inspect("""{"id": 9007199254740993}""");

        JsonDiagnostic? diagnostic = Find(report, DiagnosticCodes.IntegerPrecisionLoss);

        Assert.NotNull(diagnostic);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
    }

    [Fact]
    public void Reports_integers_that_do_not_fit_a_long_as_an_error()
    {
        InspectionReport report = Inspect("""{"id": 123456789012345678901234567890}""");

        JsonDiagnostic? diagnostic = Find(report, DiagnosticCodes.IntegerPrecisionLoss);

        Assert.NotNull(diagnostic);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Fact]
    public void Leaves_ordinary_integers_alone()
    {
        InspectionReport report = Inspect("""{"id": 42, "big": 9007199254740991}""");

        Assert.Null(Find(report, DiagnosticCodes.IntegerPrecisionLoss));
    }

    [Fact]
    public void Reports_a_path_that_is_not_consistently_typed()
    {
        InspectionReport report = Inspect("""
            {"rows": [{"price": 10}, {"price": 12}, {"price": "13"}]}
            """);

        JsonDiagnostic? diagnostic = Find(report, DiagnosticCodes.MixedArrayTypes);

        Assert.NotNull(diagnostic);
        Assert.Contains("$.rows[].price", diagnostic.Path, StringComparison.Ordinal);
    }

    [Fact]
    public void Reports_a_field_that_is_missing_from_only_a_few_records()
    {
        InspectionReport report = Inspect("""
            {"rows": [
              {"id": 1, "email": "a@example.com"},
              {"id": 2, "email": "b@example.com"},
              {"id": 3, "email": "c@example.com"},
              {"id": 4}
            ]}
            """);

        JsonDiagnostic? diagnostic = Find(report, DiagnosticCodes.InconsistentOptionality);

        Assert.NotNull(diagnostic);
        Assert.Equal("$.rows[].email", diagnostic.Path);
        Assert.Contains("3 of 4", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reports_keys_that_differ_only_by_case()
    {
        InspectionReport report = Inspect("""{"userId": 1, "userid": 2}""");

        Assert.NotNull(Find(report, DiagnosticCodes.KeysDifferOnlyByCase));
    }

    [Fact]
    public void Reports_mixed_key_naming_conventions()
    {
        InspectionReport report = Inspect("""{"userName": "a", "user_email": "b"}""");

        JsonDiagnostic? diagnostic = Find(report, DiagnosticCodes.InconsistentKeyNaming);

        Assert.NotNull(diagnostic);
        Assert.Contains("camelCase", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("snake_case", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reports_absent_values_written_as_text()
    {
        InspectionReport report = Inspect("""{"middleName": "null", "suffix": "undefined"}""");

        JsonDiagnostic? diagnostic = Find(report, DiagnosticCodes.SuspiciousStringValue);

        Assert.NotNull(diagnostic);
    }

    [Fact]
    public void Reports_a_property_that_looks_like_a_credential()
    {
        InspectionReport report = Inspect("""{"api_key": "sk-live-9f8a7b6c5d4e"}""");

        JsonDiagnostic? diagnostic = Find(report, DiagnosticCodes.PossibleSecret);

        Assert.NotNull(diagnostic);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
    }

    [Fact]
    public void Reports_double_encoded_json()
    {
        InspectionReport report = Inspect("""{"payload": "{\"nested\": true}"}""");

        Assert.NotNull(Find(report, DiagnosticCodes.EmbeddedJsonString));
    }

    [Fact]
    public void Folds_a_repeated_finding_into_one_row_with_a_count()
    {
        string rows = string.Join(",", Enumerable.Range(0, 50).Select(_ => """{"id": 9007199254740993}"""));
        InspectionReport report = Inspect($"{{\"rows\": [{rows}]}}");

        JsonDiagnostic? diagnostic = Find(report, DiagnosticCodes.IntegerPrecisionLoss);

        Assert.NotNull(diagnostic);
        Assert.Equal(50, diagnostic.Occurrences);
        Assert.Single(report.Diagnostics, d => d.Code == DiagnosticCodes.IntegerPrecisionLoss);
    }

    [Fact]
    public void Explains_a_trailing_comma_rather_than_the_bracket_it_broke_on()
    {
        InspectionReport report = Inspect("""
            {
              "a": 1,
            }
            """);

        JsonDiagnostic? diagnostic = Find(report, DiagnosticCodes.SyntaxError);

        Assert.NotNull(diagnostic);
        Assert.False(report.Completed);
        Assert.Contains("trailing comma", diagnostic.Hint ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Explains_apostrophe_quoted_strings()
    {
        InspectionReport report = Inspect("{'a': 1}");

        JsonDiagnostic? diagnostic = Find(report, DiagnosticCodes.SyntaxError);

        Assert.NotNull(diagnostic);
        Assert.Contains("double quotes", diagnostic.Hint ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Infers_the_shape_of_the_document()
    {
        InspectionReport report = Inspect("""
            {"rows": [{"id": 1, "name": "a"}, {"id": 2, "name": "b"}]}
            """);

        PathStats? id = report.Profile.Find("$.rows[].id");

        Assert.NotNull(id);
        Assert.Equal(2, id.Count);
        Assert.Equal(JsonKind.Integer, id.DominantKind);
        Assert.Equal("$.rows[]", id.ParentPath);
    }

    [Fact]
    public void Counts_the_bytes_each_shape_accounts_for()
    {
        InspectionReport report = Inspect("""
            {"small": 1, "big": {"blob": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}}
            """);

        PathStats big = report.Profile.Find("$.big")!;
        PathStats small = report.Profile.Find("$.small")!;

        Assert.True(big.TotalBytes > small.TotalBytes);
    }

    [Fact]
    public void Counts_the_document()
    {
        InspectionReport report = Inspect("""{"a": [1, 2, 3], "b": null}""");

        Assert.Equal(2, report.Stats.Objects + report.Stats.Arrays);
        Assert.Equal(3, report.Stats.Numbers);
        Assert.Equal(1, report.Stats.Nulls);
        Assert.True(report.Completed);
    }
}
