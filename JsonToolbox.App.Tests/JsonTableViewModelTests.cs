using JsonToolbox.App.ViewModels;
using JsonToolbox.Core.Documents;

namespace JsonToolbox.App.Tests;

public class JsonTableViewModelTests
{
    private static async Task<JsonTableViewModel> TableOf(string json)
    {
        IndexedJsonDocument document = IndexedJsonDocument.OpenText(json);
        JsonTableViewModel? table = await JsonTableViewModel.BuildAsync(document, document.Root, "$");

        Assert.NotNull(table);
        return table;
    }

    private static IEnumerable<string> Column(JsonTableViewModel table, string name)
    {
        int index = table.Columns.ToList().FindIndex(c => c.Name == name);
        return table.Rows.Select(row => row.Cells[index].Text);
    }

    [Fact]
    public async Task Starts_in_the_order_the_file_writes()
    {
        JsonTableViewModel table = await TableOf("""[{"n":10},{"n":2},{"n":1}]""");

        Assert.Equal(["10", "2", "1"], Column(table, "n"));
    }

    [Fact]
    public async Task Sorts_numbers_as_numbers()
    {
        // As text this order would be 1, 10, 2 — wrong in the way that makes somebody stop
        // trusting a table.
        JsonTableViewModel table = await TableOf("""[{"n":10},{"n":2},{"n":1}]""");

        table.SortBy(table.Columns[0], TableSort.Ascending);

        Assert.Equal(["1", "2", "10"], Column(table, "n"));
    }

    [Fact]
    public async Task Sorts_text_ordinally()
    {
        JsonTableViewModel table = await TableOf("""[{"s":"pear"},{"s":"apple"},{"s":"fig"}]""");

        table.SortBy(table.Columns[0], TableSort.Ascending);

        Assert.Equal(["\"apple\"", "\"fig\"", "\"pear\""], Column(table, "s"));
    }

    [Fact]
    public async Task Reverses_and_then_returns_to_the_order_of_the_file()
    {
        JsonTableViewModel table = await TableOf("""[{"n":10},{"n":2},{"n":1}]""");
        TableColumnViewModel column = table.Columns[0];

        table.SortBy(column, TableSort.Descending);
        Assert.Equal(["10", "2", "1"], Column(table, "n"));

        table.SortBy(column, TableSort.None);
        Assert.Equal(["10", "2", "1"], Column(table, "n"));
        Assert.False(column.IsSorted);
    }

    [Fact]
    public async Task A_record_missing_the_property_sorts_last_either_way()
    {
        JsonTableViewModel table = await TableOf("""[{"n":2},{"other":1},{"n":1}]""");

        table.SortBy(table.Columns[0], TableSort.Ascending);
        Assert.Equal(["1", "2", ""], Column(table, "n"));

        table.SortBy(table.Columns[0], TableSort.Descending);
        Assert.Equal(["2", "1", ""], Column(table, "n"));
    }

    [Fact]
    public async Task Only_one_column_is_sorted_at_a_time()
    {
        JsonTableViewModel table = await TableOf("""[{"a":2,"b":1},{"a":1,"b":2}]""");

        table.SortBy(table.Columns[0], TableSort.Ascending);
        table.SortBy(table.Columns[1], TableSort.Ascending);

        Assert.False(table.Columns[0].IsSorted);
        Assert.True(table.Columns[1].IsSorted);
    }

    [Fact]
    public async Task The_filter_narrows_the_records_to_those_mentioning_it()
    {
        JsonTableViewModel table = await TableOf(
            """[{"level":"info"},{"level":"error"},{"level":"info"},{"level":"error"}]""");

        table.Filter = "error";

        Assert.Equal(2, table.Rows.Count);
        Assert.Contains("2 of 4 records", table.Summary);
    }

    [Fact]
    public async Task The_filter_looks_in_every_column()
    {
        JsonTableViewModel table = await TableOf("""[{"a":"x","b":"y"},{"a":"p","b":"q"}]""");

        table.Filter = "q";

        Assert.Single(table.Rows);
    }

    [Fact]
    public async Task The_filter_ignores_case()
    {
        JsonTableViewModel table = await TableOf("""[{"s":"Error"},{"s":"ok"}]""");

        table.Filter = "error";

        Assert.Single(table.Rows);
    }

    [Fact]
    public async Task Clearing_the_filter_brings_the_records_back()
    {
        JsonTableViewModel table = await TableOf("""[{"s":"a"},{"s":"b"}]""");

        table.Filter = "a";
        table.Filter = string.Empty;

        Assert.Equal(2, table.Rows.Count);
        Assert.False(table.HasFilter);
    }

    [Fact]
    public async Task A_row_keeps_the_position_the_record_has_in_the_file()
    {
        // After sorting, a row labelled [2] is still the third record — which is what makes
        // looking it up in the tree find the same thing.
        JsonTableViewModel table = await TableOf("""[{"n":10},{"n":2},{"n":1}]""");

        table.SortBy(table.Columns[0], TableSort.Ascending);

        Assert.Equal(["[2]", "[1]", "[0]"], table.Rows.Select(row => row.Label));
    }

    [Fact]
    public async Task Sorting_and_filtering_apply_together()
    {
        JsonTableViewModel table = await TableOf(
            """[{"k":"keep","n":3},{"k":"drop","n":9},{"k":"keep","n":1}]""");

        table.Filter = "keep";
        table.SortBy(table.Columns[1], TableSort.Ascending);

        Assert.Equal(["1", "3"], Column(table, "n"));
    }
}
