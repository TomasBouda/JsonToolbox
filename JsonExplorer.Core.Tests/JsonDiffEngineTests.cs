using JsonExplorer.Core.Diff;
using JsonExplorer.Core.Documents;
using JsonExplorer.Core.Model;

namespace JsonExplorer.Core.Tests;

public class JsonDiffEngineTests
{
    private static DiffResult Diff(string left, string right, DiffOptions? options = null)
    {
        using JsonExplorerDocument a = JsonExplorerDocument.OpenText(left, "left");
        using JsonExplorerDocument b = JsonExplorerDocument.OpenText(right, "right");
        return JsonDiffEngine.Compare(a, b, options);
    }

    [Fact]
    public void Identical_documents_have_no_differences()
    {
        DiffResult result = Diff("""{"a":1,"b":[1,2,3]}""", """{"a":1,"b":[1,2,3]}""");

        Assert.True(result.Identical);
        Assert.Empty(result.Differences);
    }

    [Fact]
    public void Key_order_is_not_a_difference()
    {
        // A textual diff calls this a change on every line; a structural one calls it nothing.
        DiffResult result = Diff("""{"a":1,"b":2}""", """{"b":2,"a":1}""");

        Assert.True(result.Identical);
    }

    [Fact]
    public void Whitespace_is_not_a_difference()
    {
        DiffResult result = Diff("""{"a":1,"b":[1,2]}""", """
            {
              "a": 1,
              "b": [ 1, 2 ]
            }
            """);

        Assert.True(result.Identical);
    }

    [Fact]
    public void Reports_a_changed_value_with_both_sides()
    {
        DiffResult result = Diff("""{"name":"Alice","age":30}""", """{"name":"Alice","age":31}""");

        JsonDifference difference = Assert.Single(result.Differences);
        Assert.Equal(JsonDifferenceKind.ValueChanged, difference.Kind);
        Assert.Equal("$.age", difference.Path);
        Assert.Equal("30", difference.LeftValue);
        Assert.Equal("31", difference.RightValue);
    }

    [Fact]
    public void Reports_a_type_change_separately_from_a_value_change()
    {
        DiffResult result = Diff("""{"price":10}""", """{"price":"10"}""");

        JsonDifference difference = Assert.Single(result.Differences);
        Assert.Equal(JsonDifferenceKind.TypeChanged, difference.Kind);
        Assert.Equal(JsonKind.Integer, difference.LeftKind);
        Assert.Equal(JsonKind.String, difference.RightKind);
    }

    [Fact]
    public void True_and_false_are_a_value_change_not_a_type_change()
    {
        DiffResult result = Diff("""{"ok":true}""", """{"ok":false}""");

        Assert.Equal(JsonDifferenceKind.ValueChanged, Assert.Single(result.Differences).Kind);
    }

    [Fact]
    public void Reports_added_and_removed_properties()
    {
        DiffResult result = Diff("""{"a":1,"gone":2}""", """{"a":1,"fresh":3}""");

        Assert.Equal(
            [JsonDifferenceKind.Removed, JsonDifferenceKind.Added],
            result.Differences.Select(d => d.Kind));
        Assert.Equal(["$.gone", "$.fresh"], result.Differences.Select(d => d.Path));
    }

    [Fact]
    public void An_added_subtree_is_one_finding()
    {
        DiffResult result = Diff("""{"a":1}""", """{"a":1,"tree":{"x":1,"y":[1,2,3]}}""");

        JsonDifference difference = Assert.Single(result.Differences);
        Assert.Equal("$.tree", difference.Path);
        Assert.Equal("{…}", difference.RightValue);
    }

    [Fact]
    public void Descends_into_nested_objects()
    {
        DiffResult result = Diff(
            """{"user":{"address":{"city":"Brno","zip":"602 00"}}}""",
            """{"user":{"address":{"city":"Praha","zip":"602 00"}}}""");

        JsonDifference difference = Assert.Single(result.Differences);
        Assert.Equal("$.user.address.city", difference.Path);
    }

    [Fact]
    public void An_element_inserted_at_the_front_is_one_addition()
    {
        // Matching by position alone would report every element after the insertion.
        DiffResult result = Diff("""{"xs":[2,3,4]}""", """{"xs":[1,2,3,4]}""");

        JsonDifference difference = Assert.Single(result.Differences);
        Assert.Equal(JsonDifferenceKind.Added, difference.Kind);
        Assert.Equal("$.xs[0]", difference.Path);
        Assert.Equal("1", difference.RightValue);
    }

    [Fact]
    public void An_element_removed_from_the_middle_is_one_removal()
    {
        DiffResult result = Diff("""{"xs":[1,2,3,4]}""", """{"xs":[1,2,4]}""");

        JsonDifference difference = Assert.Single(result.Differences);
        Assert.Equal(JsonDifferenceKind.Removed, difference.Kind);
        Assert.Equal("$.xs[2]", difference.Path);
        Assert.Equal("3", difference.LeftValue);
    }

    [Fact]
    public void An_element_appended_is_one_addition()
    {
        DiffResult result = Diff("""{"xs":[1,2]}""", """{"xs":[1,2,3]}""");

        Assert.Equal("$.xs[2]", Assert.Single(result.Differences).Path);
    }

    [Fact]
    public void A_changed_element_in_the_middle_is_reported_at_its_index()
    {
        DiffResult result = Diff("""{"xs":[1,2,3]}""", """{"xs":[1,9,3]}""");

        JsonDifference difference = Assert.Single(result.Differences);
        Assert.Equal(JsonDifferenceKind.ValueChanged, difference.Kind);
        Assert.Equal("$.xs[1]", difference.Path);
    }

    [Fact]
    public void Records_are_paired_by_their_identifier_not_by_their_position()
    {
        // Trimming the head and tail cannot align these: the head differs because of the
        // insertion and the tail because of the edit, so the whole array is the window. Pairing
        // by id keeps the three untouched records out of the report, and treats a record whose
        // id changed as one record leaving and another arriving — a different id is a different
        // record, which is what an identifier means.
        DiffResult result = Diff(
            """{"xs":[{"id":1},{"id":2},{"id":3},{"id":4}]}""",
            """{"xs":[{"id":0},{"id":1},{"id":2},{"id":3},{"id":9}]}""");

        Assert.Equal(3, result.Differences.Count);
        Assert.Equal(2, result.AddedCount);
        Assert.Equal(1, result.RemovedCount);
        Assert.Equal(0, result.ChangedCount);
    }

    [Fact]
    public void A_record_that_moved_and_changed_is_still_the_same_record()
    {
        // Reordered and edited at once: only content matching by identity finds this.
        DiffResult result = Diff(
            """{"rows":[{"id":"a","v":1},{"id":"b","v":2},{"id":"c","v":3}]}""",
            """{"rows":[{"id":"c","v":3},{"id":"b","v":99},{"id":"a","v":1}]}""");

        JsonDifference difference = Assert.Single(result.Differences);
        Assert.Equal(JsonDifferenceKind.ValueChanged, difference.Kind);
        Assert.Equal("$.rows[1].v", difference.Path);
        Assert.Equal("2", difference.LeftValue);
        Assert.Equal("99", difference.RightValue);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("employee_id")]
    [InlineData("orderId")]
    [InlineData("sku")]
    public void Recognises_the_names_records_actually_use_for_an_identifier(string key)
    {
        DiffResult result = Diff(
            $$"""{"rows":[{"{{key}}":"a","v":1},{"{{key}}":"b","v":2}]}""",
            $$"""{"rows":[{"{{key}}":"b","v":99},{"{{key}}":"a","v":1}]}""");

        Assert.Equal("$.rows[1].v", Assert.Single(result.Differences).Path);
    }

    [Fact]
    public void A_word_that_merely_ends_in_id_is_not_an_identifier()
    {
        // "valid" would otherwise be taken for an identifier, and a boolean identifies nothing.
        DiffResult result = Diff(
            """{"rows":[{"valid":true,"v":1},{"valid":false,"v":2}]}""",
            """{"rows":[{"valid":true,"v":1},{"valid":false,"v":3}]}""");

        Assert.Equal("$.rows[1].v", Assert.Single(result.Differences).Path);
    }

    [Fact]
    public void A_repeated_identifier_is_not_used_as_one()
    {
        // Two records sharing an id cannot identify anything, so the comparison falls back to
        // aligning by content rather than pairing them arbitrarily.
        DiffResult result = Diff(
            """{"rows":[{"id":1,"v":"a"},{"id":1,"v":"b"}]}""",
            """{"rows":[{"id":1,"v":"a"},{"id":1,"v":"c"}]}""");

        Assert.Equal("$.rows[1].v", Assert.Single(result.Differences).Path);
    }

    [Fact]
    public void An_element_missing_the_identifier_falls_back_to_content()
    {
        DiffResult result = Diff(
            """{"rows":[{"id":1,"v":"a"},{"v":"b"}]}""",
            """{"rows":[{"id":1,"v":"a"},{"v":"c"}]}""");

        Assert.Equal("$.rows[1].v", Assert.Single(result.Differences).Path);
    }

    [Fact]
    public void An_element_that_moved_is_found_rather_than_reported_as_two_changes()
    {
        DiffResult result = Diff("""{"xs":["a","b","c","d"]}""", """{"xs":["b","c","d","a"]}""");

        // "a" left the front and turned up at the back: one removal and one addition, not
        // four changed elements.
        Assert.Equal(2, result.Differences.Count);
        Assert.Equal(1, result.RemovedCount);
        Assert.Equal(1, result.AddedCount);
    }

    [Fact]
    public void An_edited_record_reads_as_a_changed_field_not_a_swapped_record()
    {
        DiffResult result = Diff(
            """{"rows":[{"id":1,"n":"a"},{"id":2,"n":"b"},{"id":3,"n":"c"}]}""",
            """{"rows":[{"id":1,"n":"a"},{"id":2,"n":"CHANGED"},{"id":3,"n":"c"}]}""");

        JsonDifference difference = Assert.Single(result.Differences);
        Assert.Equal("$.rows[1].n", difference.Path);
        Assert.Equal(JsonDifferenceKind.ValueChanged, difference.Kind);
    }

    [Fact]
    public void An_insertion_at_the_front_of_a_long_array_is_still_one_addition()
    {
        // Nothing else changed, so trimming the tail lines the two arrays up on its own and
        // the alignment limit never comes into it.
        int n = JsonArrayAligner.MaxWindow + 50;
        string left = Array(Enumerable.Range(0, n));
        string right = Array([-1, .. Enumerable.Range(0, n)]);

        DiffResult result = Diff(left, right, new DiffOptions { MaxDifferences = 50_000 });

        Assert.False(result.Approximate);
        Assert.Equal(JsonDifferenceKind.Added, Assert.Single(result.Differences).Kind);
    }

    [Fact]
    public void A_long_array_falls_back_to_matching_by_position_and_says_so()
    {
        // An insertion at the front and an edit near the end leave a window longer than the
        // aligner will take on. Past that limit the pairing is a guess, and the report admits it.
        int n = JsonArrayAligner.MaxWindow + 100;
        int[] changed = [.. Enumerable.Range(0, n)];
        changed[JsonArrayAligner.MaxWindow + 50] = 999_999;

        DiffResult result = Diff(
            Array(Enumerable.Range(0, n)),
            Array([-1, .. changed]),
            new DiffOptions { MaxDifferences = 50_000 });

        Assert.True(result.Approximate);
    }

    private static string Array(IEnumerable<int> values) =>
        $$"""{"xs":[{{string.Join(",", values.Select(v => $$"""{"v":{{v}}}"""))}}]}""";

    [Fact]
    public void Compares_arrays_of_records_field_by_field()
    {
        DiffResult result = Diff(
            """{"rows":[{"id":1,"n":"a"},{"id":2,"n":"b"}]}""",
            """{"rows":[{"id":1,"n":"a"},{"id":2,"n":"B"}]}""");

        JsonDifference difference = Assert.Single(result.Differences);
        Assert.Equal("$.rows[1].n", difference.Path);
    }

    [Fact]
    public void Offsets_point_into_each_document()
    {
        const string left = """{"a":1}""";
        const string right = """{"a":2}""";

        DiffResult result = Diff(left, right);
        JsonDifference difference = Assert.Single(result.Differences);

        Assert.Equal(left.IndexOf('1'), difference.LeftOffset);
        Assert.Equal(right.IndexOf('2'), difference.RightOffset);
    }

    [Fact]
    public void An_added_value_has_no_offset_on_the_left()
    {
        DiffResult result = Diff("""{"a":1}""", """{"a":1,"b":2}""");

        JsonDifference difference = Assert.Single(result.Differences);
        Assert.Equal(-1, difference.LeftOffset);
        Assert.True(difference.RightOffset > 0);
    }

    [Fact]
    public void Stops_at_the_requested_number_of_differences()
    {
        string left = $"{{\"xs\":[{string.Join(",", Enumerable.Range(0, 100))}]}}";
        string right = $"{{\"xs\":[{string.Join(",", Enumerable.Range(1000, 100))}]}}";

        DiffResult result = Diff(left, right, new DiffOptions { MaxDifferences = 10 });

        Assert.True(result.Truncated);
        Assert.False(result.Identical);
        Assert.Equal(10, result.Differences.Count);
    }

    [Fact]
    public void Counts_the_kinds_it_found()
    {
        DiffResult result = Diff(
            """{"keep":1,"drop":2,"change":3}""",
            """{"keep":1,"change":4,"add":5}""");

        Assert.Equal(1, result.RemovedCount);
        Assert.Equal(1, result.AddedCount);
        Assert.Equal(1, result.ChangedCount);
    }

    [Fact]
    public void A_root_that_changed_type_is_one_finding()
    {
        DiffResult result = Diff("""{"a":1}""", """[1]""");

        JsonDifference difference = Assert.Single(result.Differences);
        Assert.Equal(JsonDifferenceKind.TypeChanged, difference.Kind);
        Assert.Equal("$", difference.Path);
    }

    [Fact]
    public void Duplicate_keys_do_not_break_the_comparison()
    {
        // The inspector reports duplicates as the defect they are; the diff must still run.
        DiffResult result = Diff("""{"a":1,"a":2}""", """{"a":1,"a":3}""");

        Assert.False(result.Identical);
    }

    [Fact]
    public void Nulls_compare_as_values()
    {
        DiffResult result = Diff("""{"a":null}""", """{"a":1}""");

        Assert.Equal(JsonDifferenceKind.TypeChanged, Assert.Single(result.Differences).Kind);
    }

    [Fact]
    public void An_unchanged_neighbour_of_a_change_is_not_reported()
    {
        DiffResult result = Diff(
            """{"big":{"lots":[1,2,3,4,5,6,7,8,9]},"small":1}""",
            """{"big":{"lots":[1,2,3,4,5,6,7,8,9]},"small":2}""");

        Assert.Equal("$.small", Assert.Single(result.Differences).Path);
    }
}
