using JsonToolbox.App.ViewModels;
using JsonToolbox.Core.Documents;

namespace JsonToolbox.App.Tests;

public class RawTextViewModelTests
{
    private const string Json = "{\n  \"a\": 1,\n  \"b\": [\n    true\n  ]\n}";

    private static async Task<(RawTextViewModel Text, List<long> Revealed)> TextOf(string json)
    {
        var revealed = new List<long>();
        var text = new RawTextViewModel(IndexedJsonDocument.OpenText(json), new SearchHighlight(), revealed.Add);
        await text.BuildAsync();
        return (text, revealed);
    }

    [Fact]
    public async Task Lists_the_rows_once_built()
    {
        (RawTextViewModel text, _) = await TextOf(Json);

        Assert.False(text.IsBuilding);
        Assert.Equal(6, text.Rows.Count);
        Assert.Equal("  \"a\": 1,", ((TextRowViewModel)text.Rows[1]!).Text);
        Assert.Equal("2", ((TextRowViewModel)text.Rows[1]!).LineLabel);
    }

    [Fact]
    public async Task Showing_a_value_marks_its_bytes_and_selects_its_row()
    {
        (RawTextViewModel text, _) = await TextOf(Json);
        int start = Json.IndexOf("true", StringComparison.Ordinal);

        text.Show(start, start + 4);

        Assert.Equal(start, text.Mark.Start);
        Assert.Equal(start + 4, text.Mark.End);
        Assert.Equal(3, text.SelectedRow?.Index);
    }

    [Fact]
    public async Task A_value_shown_before_the_count_reaches_it_is_selected_when_it_does()
    {
        var revealed = new List<long>();
        var text = new RawTextViewModel(IndexedJsonDocument.OpenText(Json), new SearchHighlight(), revealed.Add);

        text.Show(Json.Length - 1, Json.Length);
        Assert.Null(text.SelectedRow);

        await text.BuildAsync();

        Assert.Equal(5, text.SelectedRow?.Index);
    }

    [Fact]
    public async Task Rows_are_found_by_index_however_they_were_created()
    {
        (RawTextViewModel text, _) = await TextOf(Json);

        object row = text.Rows[4]!;

        Assert.Equal(4, text.Rows.IndexOf(row));
        Assert.True(text.Rows.Contains(row));
        Assert.Equal(-1, text.Rows.IndexOf("not a row"));
    }

    [Fact]
    public async Task Clearing_the_selection_unmarks_the_text()
    {
        (RawTextViewModel text, _) = await TextOf(Json);
        text.Show(0, 1);

        text.ClearSelection();

        Assert.False(text.Mark.IsSet);
        Assert.Null(text.SelectedRow);
    }

    [Fact]
    public async Task A_click_is_passed_on_as_the_offset_to_reveal()
    {
        (RawTextViewModel text, List<long> revealed) = await TextOf(Json);

        text.Reveal(17);

        Assert.Equal([17L], revealed);
    }
}
