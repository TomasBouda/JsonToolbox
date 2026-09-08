using JsonToolbox.Core.Documents;
using JsonToolbox.Core.Model;
using JsonToolbox.Core.Search;

namespace JsonToolbox.Core.Tests;

public class JsonSearchEngineTests
{
    private const string Sample = """
        {
          "userName": "Alice",
          "notes": ["alice was here", "bob was not"],
          "nested": { "userName": "alice cooper", "age": 42 },
          "aliceCount": 3
        }
        """;

    private static SearchResult Search(SearchQuery query, string json = Sample)
    {
        using JsonSource source = JsonSource.FromText(json);
        return JsonSearchEngine.Search(source, query);
    }

    [Fact]
    public void Finds_values_case_insensitively_by_default()
    {
        SearchResult result = Search(new SearchQuery { Text = "alice", Scope = SearchScope.Values });

        Assert.Equal(
            ["$.userName", "$.notes[0]", "$.nested.userName"],
            result.Hits.Select(h => h.Path));
    }

    [Fact]
    public void Honours_case_sensitivity()
    {
        SearchResult result = Search(new SearchQuery
        {
            Text = "Alice",
            Scope = SearchScope.Values,
            CaseSensitive = true,
        });

        Assert.Single(result.Hits);
        Assert.Equal("$.userName", result.Hits[0].Path);
    }

    [Fact]
    public void Searches_keys_separately_from_values()
    {
        SearchResult keys = Search(new SearchQuery { Text = "userName", Scope = SearchScope.Keys });

        Assert.Equal(["$.userName", "$.nested.userName"], keys.Hits.Select(h => h.Path));
        Assert.All(keys.Hits, h => Assert.Equal(SearchHitTarget.Key, h.Target));
    }

    [Fact]
    public void Whole_word_rejects_a_match_glued_to_other_letters()
    {
        SearchResult loose = Search(new SearchQuery { Text = "alice", Scope = SearchScope.Values });
        SearchResult strict = Search(new SearchQuery { Text = "alice", Scope = SearchScope.Values, WholeWord = true });

        // "alice cooper" still matches on a word boundary; "aliceCount" is a key, not a value.
        Assert.Equal(3, loose.Hits.Count);
        Assert.Equal(3, strict.Hits.Count);

        SearchResult gluedKey = Search(new SearchQuery { Text = "alice", Scope = SearchScope.Keys, WholeWord = true });
        Assert.Empty(gluedKey.Hits);
    }

    [Fact]
    public void Supports_regular_expressions()
    {
        SearchResult result = Search(new SearchQuery
        {
            Text = @"^\w+ was (here|not)$",
            Scope = SearchScope.Values,
            UseRegex = true,
        });

        Assert.Equal(["$.notes[0]", "$.notes[1]"], result.Hits.Select(h => h.Path));
    }

    [Fact]
    public void Restricts_matches_to_the_requested_kinds()
    {
        const string json = """{"a": "42", "b": 42, "c": [42]}""";

        SearchResult numbers = Search(
            new SearchQuery
            {
                Text = "42",
                Scope = SearchScope.Values,
                KindFilter = new HashSet<JsonKind> { JsonKind.Integer },
            },
            json);

        Assert.Equal(["$.b", "$.c[0]"], numbers.Hits.Select(h => h.Path));
    }

    [Fact]
    public void Reports_array_indices_in_the_path()
    {
        const string json = """{"rows": [{"v": "x"}, {"v": "x"}, {"v": "x"}]}""";

        SearchResult result = Search(new SearchQuery { Text = "x", Scope = SearchScope.Values }, json);

        Assert.Equal(["$.rows[0].v", "$.rows[1].v", "$.rows[2].v"], result.Hits.Select(h => h.Path));
    }

    [Fact]
    public void Offsets_point_at_the_matching_token()
    {
        SearchResult result = Search(new SearchQuery { Text = "Alice", Scope = SearchScope.Values, CaseSensitive = true });

        using JsonSource source = JsonSource.FromText(Sample);
        string atOffset = source.ReadText(result.Hits[0].ByteOffset, 7);

        Assert.Equal("\"Alice\"", atOffset);
    }

    [Fact]
    public void Stops_at_the_result_limit()
    {
        string json = JsonScannerTests.BuildDocument(recordCount: 500);

        SearchResult result = Search(new SearchQuery { Text = "item", Scope = SearchScope.Values, MaxResults = 25 }, json);

        Assert.True(result.Truncated);
        Assert.Equal(25, result.Hits.Count);
    }

    [Fact]
    public void Searches_a_malformed_document_up_to_the_point_it_breaks()
    {
        const string json = """{"a": "findme", "b": }""";

        SearchResult result = Search(new SearchQuery { Text = "findme" }, json);

        Assert.Single(result.Hits);
    }

    [Fact]
    public void An_empty_query_finds_nothing()
    {
        SearchResult result = Search(new SearchQuery { Text = string.Empty });

        Assert.Empty(result.Hits);
    }

    [Fact]
    public void Matches_non_ascii_text()
    {
        const string json = """{"mesto": "Příbram", "kraj": "Středočeský"}""";

        SearchResult result = Search(new SearchQuery { Text = "příbram", Scope = SearchScope.Values }, json);

        Assert.Single(result.Hits);
        Assert.Equal("$.mesto", result.Hits[0].Path);
    }
}
