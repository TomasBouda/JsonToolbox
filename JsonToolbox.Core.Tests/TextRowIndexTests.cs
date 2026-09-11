using System.Text;
using JsonToolbox.Core.Documents;

namespace JsonToolbox.Core.Tests;

public class TextRowIndexTests
{
    private static TextRowIndex IndexOf(string text)
    {
        var index = new TextRowIndex(JsonSource.FromText(text));
        index.Build();
        return index;
    }

    private static IEnumerable<string> Rows(TextRowIndex index)
    {
        for (long i = 0; i < index.RowCount; i++)
        {
            yield return index.Read(index.Row(i));
        }
    }

    [Fact]
    public void Splits_at_line_breaks_and_hides_them()
    {
        TextRowIndex index = IndexOf("{\r\n  \"a\": 1,\n  \"b\": 2\n}");

        Assert.True(index.IsComplete);
        Assert.Equal(["{", "  \"a\": 1,", "  \"b\": 2", "}"], Rows(index));
        Assert.Equal([1L, 2L, 3L, 4L], Enumerable.Range(0, 4).Select(i => index.Row(i).Line));
        Assert.All(Enumerable.Range(0, 4), i => Assert.False(index.Row(i).IsContinuation));
    }

    [Fact]
    public void A_final_line_break_does_not_add_an_empty_row()
    {
        Assert.Equal(["a", "b"], Rows(IndexOf("a\nb\n")));
        Assert.Equal(0, IndexOf(string.Empty).RowCount);
    }

    [Fact]
    public void Finds_the_row_an_offset_is_on()
    {
        const string text = "first\nsecond\nthird";
        TextRowIndex index = IndexOf(text);

        Assert.Equal(0, index.RowAt(0).Index);
        Assert.Equal(0, index.RowAt(5).Index);
        Assert.Equal(1, index.RowAt(6).Index);
        Assert.Equal(2, index.RowAt(text.Length - 1).Index);
        Assert.Equal(2, index.RowAt(text.Length + 100).Index);
    }

    [Fact]
    public void A_line_too_long_for_one_row_is_shown_as_several()
    {
        string line = new string('x', TextRowIndex.MaxRowBytes * 2 + 10);
        TextRowIndex index = IndexOf(line + "\nshort");

        Assert.Equal(4, index.RowCount);
        Assert.Equal([false, true, true, false], Enumerable.Range(0, 4).Select(i => index.Row(i).IsContinuation));
        Assert.Equal([1L, 1L, 1L, 2L], Enumerable.Range(0, 4).Select(i => index.Row(i).Line));
        Assert.Equal(line, string.Concat(Rows(index).Take(3)));
        Assert.Equal("short", index.Read(index.Row(3)));
    }

    [Fact]
    public void Never_tears_a_character_across_two_rows()
    {
        // Three-byte characters that cannot divide MaxRowBytes evenly, so a naive cut would
        // land inside one of them.
        string line = new string('€', TextRowIndex.MaxRowBytes);
        TextRowIndex index = IndexOf(line);

        Assert.Equal(line, string.Concat(Rows(index)));
        Assert.DoesNotContain('�', string.Concat(Rows(index)));
    }

    [Fact]
    public void Rows_past_the_first_checkpoint_and_the_read_buffer_agree_with_the_count()
    {
        // Enough rows to cross many checkpoints and the 1 MB buffer the count reads through,
        // with line lengths that vary so a checkpoint never lands on a regular boundary.
        var builder = new StringBuilder();
        var expected = new List<string>();
        for (int i = 0; i < 60_000; i++)
        {
            string row = "  \"" + new string('k', i % 37) + "\": " + i + ",";
            expected.Add(row);
            builder.Append(row).Append('\n');
        }

        TextRowIndex index = IndexOf(builder.ToString());

        Assert.Equal(expected.Count, index.RowCount);
        Assert.Equal(expected, Rows(index));
        Assert.Equal(60_000, index.Row(59_999).Line);

        long offsetOfLast = builder.Length - expected[^1].Length - 1;
        Assert.Equal(59_999, index.RowAt(offsetOfLast).Index);
        Assert.Equal(59_998, index.RowAt(offsetOfLast - 1).Index);
    }

    [Fact]
    public void Reports_progress_and_can_be_read_while_incomplete()
    {
        var builder = new StringBuilder();
        for (int i = 0; i < 250_000; i++)
        {
            builder.Append(i).Append('\n');
        }

        var index = new TextRowIndex(JsonSource.FromText(builder.ToString()));
        var seen = new List<long>();
        index.Build(rows =>
        {
            seen.Add(rows);
            Assert.Equal((rows - 1).ToString(), index.Read(index.Row(rows - 1)));
        });

        Assert.True(seen.Count >= 2);
        Assert.Equal(250_000, seen[^1]);
    }
}
