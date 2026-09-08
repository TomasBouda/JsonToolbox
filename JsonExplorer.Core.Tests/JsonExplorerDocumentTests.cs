using JsonExplorer.Core.Documents;
using JsonExplorer.Core.Model;

namespace JsonExplorer.Core.Tests;

public class JsonExplorerDocumentTests
{
    private const string Sample = """
        {
          "name": "demo",
          "count": 3,
          "items": [
            { "id": 1, "tags": ["a", "b"] },
            { "id": 2, "tags": [] },
            null
          ],
          "enabled": true
        }
        """;

    [Fact]
    public void Opening_identifies_the_root_without_parsing()
    {
        using JsonExplorerDocument document = JsonExplorerDocument.OpenText(Sample);

        Assert.Equal(JsonKind.Object, document.Root.Kind);

        // The child count is deliberately unknown until somebody expands the root: counting
        // it would mean reading the whole document, which is the cost being avoided.
        Assert.Equal(-1, document.Root.ChildCount);
    }

    [Fact]
    public void Lists_the_direct_children_of_the_root()
    {
        using JsonExplorerDocument document = JsonExplorerDocument.OpenText(Sample);

        IReadOnlyList<JsonNodeInfo> children = document.IndexChildren(document.Root).Children;

        Assert.Equal(["name", "count", "items", "enabled"], children.Select(c => c.Name));
        Assert.Equal(
            [JsonKind.String, JsonKind.Integer, JsonKind.Array, JsonKind.True],
            children.Select(c => c.Kind));
    }

    [Fact]
    public void Counts_grandchildren_while_listing_children()
    {
        using JsonExplorerDocument document = JsonExplorerDocument.OpenText(Sample);

        JsonNodeInfo items = document.IndexChildren(document.Root).Children.Single(c => c.Name == "items");

        Assert.Equal(3, items.ChildCount);
        Assert.True(items.CanExpand);
    }

    [Fact]
    public void Expanding_a_nested_node_reads_only_that_node()
    {
        using JsonExplorerDocument document = JsonExplorerDocument.OpenText(Sample);
        JsonNodeInfo items = document.IndexChildren(document.Root).Children.Single(c => c.Name == "items");

        IReadOnlyList<JsonNodeInfo> elements = document.IndexChildren(items).Children;

        Assert.Equal(3, elements.Count);
        Assert.Equal([0, 1, 2], elements.Select(e => e.Index));
        Assert.Null(elements[0].Name);
        Assert.Equal(JsonKind.Null, elements[2].Kind);

        // The slice read for the nested scan must stay inside the node it belongs to.
        Assert.All(elements, e => Assert.True(e.Start >= items.Start && e.End <= items.End));
    }

    [Fact]
    public void Renders_a_value_the_way_it_is_written_in_the_document()
    {
        using JsonExplorerDocument document = JsonExplorerDocument.OpenText(Sample);

        IReadOnlyList<JsonNodeInfo> children = document.IndexChildren(document.Root).Children;

        // The quotes are the point: without them there is no telling 3 from "3".
        Assert.Equal("\"demo\"", children.Single(c => c.Name == "name").Preview);
        Assert.Equal("3", children.Single(c => c.Name == "count").Preview);
        Assert.Equal("true", children.Single(c => c.Name == "enabled").Preview);
        Assert.Equal("[…]", children.Single(c => c.Name == "items").Preview);
    }

    [Fact]
    public void Renders_an_object_as_a_collapsed_object()
    {
        using JsonExplorerDocument document = JsonExplorerDocument.OpenText("""{"a":{"b":1}}""");

        Assert.Equal("{…}", document.IndexChildren(document.Root).Children[0].Preview);
    }

    [Fact]
    public void Escapes_are_resolved_in_a_preview()
    {
        using JsonExplorerDocument document = JsonExplorerDocument.OpenText("""{"a":"line\nbreak é"}""");

        Assert.Equal("\"line\nbreak é\"", document.IndexChildren(document.Root).Children[0].Preview);
    }

    [Fact]
    public void Stops_listing_at_the_requested_limit()
    {
        using JsonExplorerDocument document = JsonExplorerDocument.OpenText(JsonScannerTests.BuildDocument(recordCount: 500));
        JsonNodeInfo records = document.IndexChildren(document.Root).Children.Single(c => c.Name == "records");

        JsonChildIndexer indexer = document.IndexChildren(records, maxChildren: 50);

        Assert.True(indexer.Truncated);
        Assert.Equal(50, indexer.Children.Count);
    }

    [Fact]
    public void Reads_back_the_raw_text_of_a_node()
    {
        using JsonExplorerDocument document = JsonExplorerDocument.OpenText(Sample);
        JsonNodeInfo items = document.IndexChildren(document.Root).Children.Single(c => c.Name == "items");

        string raw = document.ReadRaw(items);

        Assert.StartsWith("[", raw, StringComparison.Ordinal);
        Assert.EndsWith("]", raw, StringComparison.Ordinal);
        Assert.Contains("\"tags\"", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void Handles_a_scalar_root()
    {
        using JsonExplorerDocument document = JsonExplorerDocument.OpenText("  \"just a string\"  ");

        Assert.Equal(JsonKind.String, document.Root.Kind);
        Assert.Empty(document.IndexChildren(document.Root).Children);
    }
}
