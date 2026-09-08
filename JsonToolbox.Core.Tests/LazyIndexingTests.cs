using JsonToolbox.Core.Documents;
using JsonToolbox.Core.Model;

namespace JsonToolbox.Core.Tests;

/// <summary>
/// Covers the behaviour that makes a large file usable: a node must be visible and openable
/// before the scan has reached the end of it.
/// </summary>
public class LazyIndexingTests
{
    /// <summary>A wrapper object whose last property is far larger than everything before it.</summary>
    private static string BuildTopHeavyDocument(int recordCount) =>
        $$"""{"meta":{"version":1},"records":[{{string.Join(",", Enumerable.Range(0, recordCount).Select(i => $$"""{"id":{{i}}}"""))}}]}""";

    [Fact]
    public void Announces_a_container_child_before_its_end_is_known()
    {
        using IndexedJsonDocument document = IndexedJsonDocument.OpenText(BuildTopHeavyDocument(200));
        var announced = new List<JsonNodeInfo>();

        document.IndexChildren(document.Root, onChildFound: announced.Add);

        JsonNodeInfo records = announced.Single(c => c.Name == "records");
        Assert.False(records.HasKnownExtent);
        Assert.Equal(-1, records.ChildCount);

        // The announcement has to arrive before the array has been read, or it is worthless.
        Assert.True(records.Start < document.Length / 2);
    }

    [Fact]
    public void Completes_the_child_once_its_closing_bracket_is_read()
    {
        using IndexedJsonDocument document = IndexedJsonDocument.OpenText(BuildTopHeavyDocument(200));
        var completed = new List<JsonNodeInfo>();

        JsonChildIndexer indexer = document.IndexChildren(document.Root, onChildCompleted: completed.Add);

        JsonNodeInfo records = completed.Single(c => c.Name == "records");
        Assert.True(records.HasKnownExtent);
        Assert.Equal(200, records.ChildCount);

        // The final list holds the completed nodes, not the provisional ones.
        Assert.All(indexer.Children, c => Assert.True(c.HasKnownExtent));
    }

    [Fact]
    public void Expands_a_node_whose_end_is_still_unknown()
    {
        using IndexedJsonDocument document = IndexedJsonDocument.OpenText(BuildTopHeavyDocument(50));
        var announced = new List<JsonNodeInfo>();
        document.IndexChildren(document.Root, onChildFound: announced.Add);

        JsonNodeInfo provisional = announced.Single(c => c.Name == "records");
        Assert.False(provisional.HasKnownExtent);

        // The indexer is given the rest of the file and has to stop at the array's own
        // closing bracket rather than running on into the document's trailing bytes.
        IReadOnlyList<JsonNodeInfo> elements = document.IndexChildren(provisional).Children;

        Assert.Equal(50, elements.Count);
        Assert.All(elements, e => Assert.Equal(JsonKind.Object, e.Kind));
    }

    [Fact]
    public void Stops_at_the_closing_bracket_of_the_indexed_node()
    {
        // Two sibling arrays: indexing the first must not spill into the second.
        using IndexedJsonDocument document = IndexedJsonDocument.OpenText("""{"a":[1,2,3],"b":[4,5,6,7]}""");
        JsonNodeInfo first = document.IndexChildren(document.Root).Children.Single(c => c.Name == "a");

        IReadOnlyList<JsonNodeInfo> elements = document.IndexChildren(first).Children;

        Assert.Equal(3, elements.Count);
        Assert.Equal(["1", "2", "3"], elements.Select(e => e.Preview));
    }

    [Fact]
    public void Reads_the_raw_text_of_a_node_with_an_unknown_end()
    {
        using IndexedJsonDocument document = IndexedJsonDocument.OpenText(BuildTopHeavyDocument(10));
        var announced = new List<JsonNodeInfo>();
        document.IndexChildren(document.Root, onChildFound: announced.Add);

        string raw = document.ReadRaw(announced.Single(c => c.Name == "records"));

        Assert.StartsWith("[", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void A_provisional_node_still_answers_containment_for_navigation()
    {
        using IndexedJsonDocument document = IndexedJsonDocument.OpenText(BuildTopHeavyDocument(10));
        var announced = new List<JsonNodeInfo>();
        document.IndexChildren(document.Root, onChildFound: announced.Add);

        JsonNodeInfo records = announced.Single(c => c.Name == "records");

        Assert.True(records.Contains(records.Start + 5));
        Assert.False(records.Contains(records.Start - 1));
    }
}
