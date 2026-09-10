using JsonToolbox.Core.Documents;
using JsonToolbox.Core.Model;

namespace JsonToolbox.Core.Tests;

/// <summary>
/// A file of documents, one per line, is not a JSON document. Read as one it yields its first
/// record and says nothing about the rest, which is the failure these tests exist to prevent.
/// </summary>
public class JsonSequenceTests
{
    private const string Lines = """
        {"id":1,"msg":"first"}
        {"id":2,"msg":"second"}
        {"id":3,"msg":"third"}
        """;

    [Fact]
    public void A_file_of_records_is_recognised_as_a_sequence()
    {
        using IndexedJsonDocument document = IndexedJsonDocument.OpenText(Lines);

        Assert.True(document.IsSequence);
        Assert.Equal(JsonKind.Array, document.Root.Kind);
    }

    [Fact]
    public void Every_record_is_a_child_of_the_stand_in_root()
    {
        using IndexedJsonDocument document = IndexedJsonDocument.OpenText(Lines);

        IReadOnlyList<JsonNodeInfo> records = document.IndexChildren(document.Root).Children;

        Assert.Equal(3, records.Count);
        Assert.Equal("""{"id":2,"msg":"second"}""", document.ReadRaw(records[1]));
    }

    [Fact]
    public void A_record_opens_like_any_other_object()
    {
        using IndexedJsonDocument document = IndexedJsonDocument.OpenText(Lines);

        JsonNodeInfo second = document.IndexChildren(document.Root).Children[1];
        IReadOnlyList<JsonNodeInfo> fields = document.IndexChildren(second).Children;

        Assert.Equal(["id", "msg"], fields.Select(f => f.Name));
        Assert.Equal("\"second\"", document.ReadRaw(fields[1]));
    }

    [Fact]
    public void The_records_can_be_read_as_a_table()
    {
        using IndexedJsonDocument document = IndexedJsonDocument.OpenText(Lines);

        JsonTableShape shape = JsonTableInference.Infer(document, document.Root);

        Assert.True(shape.IsTabular);
        Assert.Equal(["id", "msg"], shape.Columns);
    }

    [Fact]
    public void Values_of_a_named_field_come_out_of_the_one_pass()
    {
        using IndexedJsonDocument document = IndexedJsonDocument.OpenText(Lines);

        IReadOnlyList<JsonNodeInfo> records = document
            .IndexChildren(document.Root, pinnedKeys: new HashSet<string> { "msg" })
            .Children;

        Assert.Equal("\"third\"", records[2].PinnedValue("msg"));
    }

    [Fact]
    public void One_object_on_its_own_is_not_a_sequence()
    {
        using IndexedJsonDocument document = IndexedJsonDocument.OpenText("""{"id":1,"msg":"only"}""");

        Assert.False(document.IsSequence);
        Assert.Equal(JsonKind.Object, document.Root.Kind);
    }

    [Fact]
    public void An_array_on_several_lines_is_not_a_sequence()
    {
        // Every line here is part of one value, which is the case the probe must not mistake
        // for a file of documents.
        using IndexedJsonDocument document = IndexedJsonDocument.OpenText("[\n  {\"id\":1},\n  {\"id\":2}\n]");

        Assert.False(document.IsSequence);
        Assert.Equal(2, document.IndexChildren(document.Root).Children.Count);
    }

    [Fact]
    public void Trailing_whitespace_does_not_make_a_document_a_sequence()
    {
        using IndexedJsonDocument document = IndexedJsonDocument.OpenText("{\"id\":1}\n\n   \n");

        Assert.False(document.IsSequence);
    }

    [Fact]
    public void A_sequence_of_scalars_is_still_a_sequence()
    {
        using IndexedJsonDocument document = IndexedJsonDocument.OpenText("1\n2\n3");

        Assert.True(document.IsSequence);
        Assert.Equal(3, document.IndexChildren(document.Root).Children.Count);
    }

    [Fact]
    public void Records_written_on_one_line_are_a_sequence_too()
    {
        // The newlines are a convention, not the format. What decides is that the file holds
        // more than one value.
        using IndexedJsonDocument document = IndexedJsonDocument.OpenText("""{"id":1} {"id":2}""");

        Assert.True(document.IsSequence);
        Assert.Equal(2, document.IndexChildren(document.Root).Children.Count);
    }

    [Fact]
    public void A_record_larger_than_the_probe_reads_as_one_document()
    {
        // A single enormous value on one line is not JSON Lines, and deciding otherwise would
        // mean reading the file to find out — which is what opening a document must never do.
        string huge = $$"""{"pad":"{{new string('x', JsonSequenceProbe.HeadBytes)}}"}""";

        using IndexedJsonDocument document = IndexedJsonDocument.OpenText(huge);

        Assert.False(document.IsSequence);
    }

    [Fact]
    public void A_broken_file_is_left_to_the_scan_to_explain()
    {
        // The probe says nothing about what is wrong; the scan reports it at the position where
        // it happens, in its own words.
        using IndexedJsonDocument document = IndexedJsonDocument.OpenText("{oops}");

        Assert.False(document.IsSequence);
    }
}
