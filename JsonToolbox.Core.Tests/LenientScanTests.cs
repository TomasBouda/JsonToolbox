using JsonToolbox.Core.Documents;
using JsonToolbox.Core.Scanning;

namespace JsonToolbox.Core.Tests;

/// <summary>
/// Configuration files written by hand are full of comments and trailing commas. Reporting that
/// as a problem is right; refusing to show the file because of it is not, so reading leniently
/// has to work on the same documents that reading strictly refuses.
/// </summary>
public class LenientScanTests
{
    private const string WithComments = """
        {
          // the database
          "cnn": "Server=.;Database=x",
          "port": 1433, // the usual one
          "hosts": [
            "a",
            "b",
          ],
        }
        """;

    private static readonly JsonScanOptions Lenient = new() { Strictness = JsonScanStrictness.Lenient };

    [Fact]
    public void Strict_reading_refuses_a_comment_and_says_where()
    {
        using IndexedJsonDocument document = IndexedJsonDocument.OpenText(WithComments);

        JsonScanException error = Assert.Throws<JsonScanException>(
            () => document.IndexChildren(document.Root));

        Assert.Equal(2, error.LineNumber);
    }

    [Fact]
    public void Lenient_reading_lists_the_children_anyway()
    {
        using IndexedJsonDocument document = IndexedJsonDocument.OpenText(WithComments);

        JsonChildIndexer indexer = document.IndexChildren(document.Root, options: Lenient);

        Assert.Equal(["cnn", "port", "hosts"], indexer.Children.Select(child => child.Name));
    }

    [Fact]
    public void Lenient_reading_reports_offsets_that_still_point_at_the_values()
    {
        using IndexedJsonDocument document = IndexedJsonDocument.OpenText(WithComments);

        JsonNodeInfo port = document.IndexChildren(document.Root, options: Lenient).Children[1];

        Assert.Equal("1433", document.ReadRaw(port));
    }

    [Fact]
    public void Lenient_reading_descends_into_a_container_after_a_trailing_comma()
    {
        using IndexedJsonDocument document = IndexedJsonDocument.OpenText(WithComments);

        JsonNodeInfo hosts = document.IndexChildren(document.Root, options: Lenient).Children[2];
        JsonChildIndexer inside = document.IndexChildren(hosts, options: Lenient);

        Assert.Equal(2, inside.Children.Count);
    }

    [Fact]
    public void A_comment_between_a_name_and_its_value_is_skipped_too()
    {
        using IndexedJsonDocument document = IndexedJsonDocument.OpenText("""{"a": /* here */ 1}""");

        JsonChildIndexer indexer = document.IndexChildren(document.Root, options: Lenient);

        Assert.Equal("a", indexer.Children[0].Name);
        Assert.Equal("1", document.ReadRaw(indexer.Children[0]));
    }

    [Fact]
    public void Reading_leniently_does_not_rescue_a_document_that_is_simply_broken()
    {
        // The point of the fallback is comments and trailing commas, not repairing JSON. A
        // missing brace still has to be reported rather than silently half-read.
        using IndexedJsonDocument document = IndexedJsonDocument.OpenText("""{"a": 1, "b": }""");

        Assert.Throws<JsonScanException>(() => document.IndexChildren(document.Root, options: Lenient));
    }
}
