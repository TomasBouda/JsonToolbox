using JsonExplorer.Core.Documents;

namespace JsonExplorer.Core.Tests;

/// <summary>
/// Covers reading a pinned property out of every child of a container, which is what lets a
/// record be told apart without expanding it.
/// </summary>
public class PinnedKeyTests
{
    private const string Movies = """
        {"Movies": [
          {"ID": 715, "Name": "Swollen in the Trainer", "Year": 2006, "Cast": ["a", "b"], "Rate": 7.6},
          {"ID": 716, "Name": "Second Film", "Year": 1999, "Cast": [], "Rate": 5.1},
          {"ID": 717, "Year": 2020, "Cast": ["c"], "Rate": 6.0}
        ]}
        """;

    private static IReadOnlyList<JsonNodeInfo> Index(string json, params string[] pinned)
    {
        using JsonExplorerDocument document = JsonExplorerDocument.OpenText(json);
        JsonNodeInfo movies = document.IndexChildren(document.Root).Children[0];

        return document.IndexChildren(movies, pinnedKeys: new HashSet<string>(pinned)).Children;
    }

    [Fact]
    public void Reads_a_pinned_property_from_every_element()
    {
        IReadOnlyList<JsonNodeInfo> rows = Index(Movies, "Name");

        Assert.Equal("Name: \"Swollen in the Trainer\"", rows[0].PinnedSummary);
        Assert.Equal("Name: \"Second Film\"", rows[1].PinnedSummary);
    }

    [Fact]
    public void An_element_without_the_pinned_key_says_nothing()
    {
        IReadOnlyList<JsonNodeInfo> rows = Index(Movies, "Name");

        Assert.Null(rows[2].PinnedSummary);
    }

    [Fact]
    public void Several_pinned_keys_appear_in_document_order()
    {
        IReadOnlyList<JsonNodeInfo> rows = Index(Movies, "ID", "Year");

        Assert.Equal("ID: 715   Year: 2006", rows[0].PinnedSummary);
    }

    [Fact]
    public void A_pinned_container_is_summarised_rather_than_expanded()
    {
        IReadOnlyList<JsonNodeInfo> rows = Index(Movies, "Cast");

        // Pinning exists to avoid opening things, so a pinned array shows as one.
        Assert.Equal("Cast: […]", rows[0].PinnedSummary);
        Assert.Equal("Cast: […]", rows[1].PinnedSummary);
    }

    [Fact]
    public void Pinning_nothing_costs_nothing()
    {
        IReadOnlyList<JsonNodeInfo> rows = Index(Movies);

        Assert.All(rows, row => Assert.Null(row.PinnedSummary));
    }

    [Fact]
    public void A_pin_only_matches_a_direct_property_of_the_element()
    {
        const string json = """{"rows": [{"outer": {"name": "deep"}, "id": 1}]}""";

        IReadOnlyList<JsonNodeInfo> rows = Index(json, "name");

        // "name" lives one level below the element, so it is not this element's property.
        Assert.Null(rows[0].PinnedSummary);
    }

    [Fact]
    public void Child_counts_still_come_out_right_while_pins_are_collected()
    {
        IReadOnlyList<JsonNodeInfo> rows = Index(Movies, "Name", "Year");

        Assert.Equal(5, rows[0].ChildCount);
        Assert.Equal(4, rows[2].ChildCount);
    }
}
