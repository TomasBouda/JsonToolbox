using JsonToolbox.Core.Documents;

namespace JsonToolbox.Core.Tests;

public class JsonTableTests
{
    private static (IndexedJsonDocument Document, JsonNodeInfo Array) OpenArray(string json)
    {
        IndexedJsonDocument document = IndexedJsonDocument.OpenText(json);
        return (document, document.Root);
    }

    [Fact]
    public void Columns_are_the_property_names_in_the_order_the_records_write_them()
    {
        (IndexedJsonDocument document, JsonNodeInfo array) =
            OpenArray("""[{"id":1,"name":"a"},{"id":2,"name":"b"}]""");

        JsonTableShape shape = JsonTableInference.Infer(document, array);

        Assert.True(shape.IsTabular);
        Assert.Equal(["id", "name"], shape.Columns);
    }

    [Fact]
    public void A_field_only_some_records_carry_still_becomes_a_column()
    {
        (IndexedJsonDocument document, JsonNodeInfo array) =
            OpenArray("""[{"id":1},{"id":2,"note":"late"}]""");

        JsonTableShape shape = JsonTableInference.Infer(document, array);

        // Optional by accident or by design, it is a field of these records either way, and a
        // column that is empty for most rows says that plainly.
        Assert.Equal(["id", "note"], shape.Columns);
    }

    [Fact]
    public void An_array_of_scalars_has_no_columns()
    {
        (IndexedJsonDocument document, JsonNodeInfo array) = OpenArray("""["a","b","c"]""");

        JsonTableShape shape = JsonTableInference.Infer(document, array);

        Assert.False(shape.IsTabular);
        Assert.False(shape.Uniform);
    }

    [Fact]
    public void An_array_mixing_records_and_scalars_is_not_a_table()
    {
        (IndexedJsonDocument document, JsonNodeInfo array) = OpenArray("""[{"id":1},7]""");

        JsonTableShape shape = JsonTableInference.Infer(document, array);

        Assert.False(shape.IsTabular);
    }

    [Fact]
    public void An_empty_array_is_not_a_table()
    {
        (IndexedJsonDocument document, JsonNodeInfo array) = OpenArray("[]");

        Assert.False(JsonTableInference.Infer(document, array).IsTabular);
    }

    [Fact]
    public void An_object_is_not_a_table()
    {
        using IndexedJsonDocument document = IndexedJsonDocument.OpenText("""{"a":1}""");

        Assert.False(JsonTableInference.Infer(document, document.Root).IsTabular);
    }

    [Fact]
    public void Only_the_sample_is_read_however_long_the_array()
    {
        // The hundredth record carries a field the first two do not. Reading every record to
        // find it would mean reading the whole file to draw the first screen.
        string json = "[" + string.Join(",", Enumerable.Range(0, 100)
            .Select(i => i == 99 ? """{"id":1,"rare":true}""" : """{"id":1}""")) + "]";

        (IndexedJsonDocument document, JsonNodeInfo array) = OpenArray(json);

        JsonTableShape shape = JsonTableInference.Infer(document, array, sampleSize: 10);

        Assert.Equal(["id"], shape.Columns);
        Assert.Equal(10, shape.Sampled);
    }

    [Fact]
    public void The_cells_of_every_row_come_out_of_one_pass()
    {
        (IndexedJsonDocument document, JsonNodeInfo array) =
            OpenArray("""[{"id":1,"name":"a"},{"id":2,"name":"b"}]""");

        JsonTableShape shape = JsonTableInference.Infer(document, array);
        IReadOnlyList<JsonNodeInfo> rows = document
            .IndexChildren(array, pinnedKeys: shape.Columns.ToHashSet())
            .Children;

        Assert.Equal("2", rows[1].PinnedValue("id"));
        Assert.Equal("\"b\"", rows[1].PinnedValue("name"));
    }

    [Fact]
    public void A_cell_a_record_does_not_have_is_empty_rather_than_wrong()
    {
        (IndexedJsonDocument document, JsonNodeInfo array) =
            OpenArray("""[{"id":1},{"id":2,"note":"late"}]""");

        IReadOnlyList<JsonNodeInfo> rows = document
            .IndexChildren(array, pinnedKeys: new HashSet<string> { "id", "note" })
            .Children;

        Assert.Null(rows[0].PinnedValue("note"));
        Assert.Equal("\"late\"", rows[1].PinnedValue("note"));
    }

    [Fact]
    public void A_cell_holding_a_container_is_summarised_rather_than_expanded()
    {
        (IndexedJsonDocument document, JsonNodeInfo array) =
            OpenArray("""[{"id":1,"tags":["a","b"],"meta":{"x":1}}]""");

        JsonNodeInfo row = document
            .IndexChildren(array, pinnedKeys: new HashSet<string> { "tags", "meta" })
            .Children[0];

        // A table cell is one line; putting a whole subtree in it would make the row unreadable
        // and the column useless.
        Assert.Equal("[…]", row.PinnedValue("tags"));
        Assert.Equal("{…}", row.PinnedValue("meta"));
    }
}
