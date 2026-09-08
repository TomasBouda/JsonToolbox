using System.Text;
using JsonToolbox.Core.Documents;
using JsonToolbox.Core.Editing;
using JsonToolbox.Core.Model;

namespace JsonToolbox.Core.Tests;

public class JsonMemberSortTests
{
    /// <summary>
    /// Sorts a document and hands back what it now says, along with the pieces needed to keep
    /// working on it.
    /// </summary>
    private static (string Text, EditResult Result, JsonDocumentEditor Editor) Sort(
        string json,
        JsonMemberOrder order = JsonMemberOrder.Ascending)
    {
        var editable = new EditableJsonSource(JsonSource.FromText(json));
        var editor = new JsonDocumentEditor(editable);
        using IndexedJsonDocument document = IndexedJsonDocument.Open(editable);

        EditResult result = editor.SortMembers(document, document.Root, order);
        return (Read(editor), result, editor);
    }

    private static string Read(JsonDocumentEditor editor)
    {
        using var stream = new MemoryStream();
        editor.WriteTo(stream);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    [Fact]
    public void Orders_the_properties_of_an_object()
    {
        (string text, EditResult result, _) = Sort("""{"name":1,"age":2,"city":3}""");

        Assert.True(result.Applied);
        Assert.Equal("""{"age":2,"city":3,"name":1}""", text);
    }

    [Fact]
    public void Orders_them_backwards_when_asked()
    {
        (string text, _, _) = Sort("""{"age":2,"city":3,"name":1}""", JsonMemberOrder.Descending);

        Assert.Equal("""{"name":1,"city":3,"age":2}""", text);
    }

    [Fact]
    public void Reaches_every_object_inside()
    {
        (string text, _, _) = Sort("""{"b":{"z":1,"a":2},"a":[{"y":3,"x":4}]}""");

        Assert.Equal("""{"a":[{"x":4,"y":3}],"b":{"a":2,"z":1}}""", text);
    }

    [Fact]
    public void Leaves_array_elements_where_they_are()
    {
        // Elements are identified by position, so reordering them would be different data
        // rather than a different order for the same data.
        (string text, EditResult result, _) = Sort("""{"list":["c","a","b"]}""");

        Assert.False(result.Applied);
        Assert.Equal("""{"list":["c","a","b"]}""", text);
    }

    [Fact]
    public void Keeps_the_indentation_the_document_was_written_with()
    {
        const string Pretty = "{\n  \"name\": 1,\n  \"age\": 2\n}";
        (string text, _, _) = Sort(Pretty);

        Assert.Equal("{\n  \"age\": 2,\n  \"name\": 1\n}", text);
    }

    [Fact]
    public void Moves_the_comma_with_the_position_rather_than_with_the_member()
    {
        // The member that ends up last must lose its comma, and the one that ends up first
        // must gain one. Carrying each member's own trailing text would produce "b":2}"a":1,
        (string text, _, _) = Sort("""{"b":2,"a":1}""");

        Assert.Equal("""{"a":1,"b":2}""", text);
    }

    [Fact]
    public void Leaves_values_exactly_as_they_were_written()
    {
        // Numbers keep their digits and strings keep their escapes, because a member is copied
        // rather than re-serialised.
        (string text, _, _) = Sort("""{"z":1.2000,"a":"é\t","m":1e10}""");

        Assert.Equal("""{"a":"é\t","m":1e10,"z":1.2000}""", text);
    }

    [Fact]
    public void Sorts_case_insensitively_so_like_names_stay_together()
    {
        // A and a are neighbours rather than being separated by every lowercase key, and the
        // ordinal tie-break settles which of the pair comes first the same way every time.
        (string text, _, _) = Sort("""{"b":1,"A":2,"a":3,"B":4}""");

        Assert.Equal("""{"A":2,"a":3,"B":4,"b":1}""", text);
    }

    [Fact]
    public void Keeps_duplicate_names_in_the_order_the_document_gave_them()
    {
        (string text, _, _) = Sort("""{"b":1,"a":"first","a":"second"}""");

        Assert.Equal("""{"a":"first","a":"second","b":1}""", text);
    }

    [Fact]
    public void Handles_an_empty_object()
    {
        (string text, EditResult result, _) = Sort("""{"b":{},"a":1}""");

        Assert.True(result.Applied);
        Assert.Equal("""{"a":1,"b":{}}""", text);
    }

    [Fact]
    public void Refuses_an_order_that_is_already_in_place()
    {
        (_, EditResult result, JsonDocumentEditor editor) = Sort("""{"a":1,"b":2}""");

        Assert.False(result.Applied);
        Assert.False(editor.IsModified);
        Assert.Contains("already", result.Message);
    }

    [Fact]
    public void Refuses_to_apply_the_file_order()
    {
        (_, EditResult result, _) = Sort("""{"b":1,"a":2}""", JsonMemberOrder.FileOrder);

        Assert.False(result.Applied);
    }

    [Fact]
    public void Is_one_step_of_undo_however_many_objects_it_touched()
    {
        const string Original = """{"b":{"z":1,"a":2},"a":3}""";
        (string sorted, _, JsonDocumentEditor editor) = Sort(Original);

        Assert.NotEqual(Original, sorted);
        Assert.True(editor.Undo());
        Assert.Equal(Original, Read(editor));
        Assert.False(editor.CanUndo);
    }

    [Fact]
    public void Produces_a_document_that_still_reads_the_same()
    {
        const string Original = """
            {"records":[{"id":2,"tags":["b","a"],"name":"two"},{"name":"one","id":1,"tags":[]}]}
            """;

        (string text, _, _) = Sort(Original);

        using var before = System.Text.Json.JsonDocument.Parse(Original);
        using var after = System.Text.Json.JsonDocument.Parse(text);

        Assert.Equal(
            before.RootElement.GetProperty("records").GetArrayLength(),
            after.RootElement.GetProperty("records").GetArrayLength());
        Assert.Equal(
            before.RootElement.GetProperty("records")[0].GetProperty("id").GetInt32(),
            after.RootElement.GetProperty("records")[0].GetProperty("id").GetInt32());
    }

    [Fact]
    public void Sorts_one_object_without_touching_the_rest()
    {
        var editable = new EditableJsonSource(JsonSource.FromText("""{"b":{"z":1,"a":2},"a":{"y":3,"x":4}}"""));
        var editor = new JsonDocumentEditor(editable);
        using IndexedJsonDocument document = IndexedJsonDocument.Open(editable);

        JsonNodeInfo second = document.IndexChildren(document.Root).Children[1];
        EditResult result = editor.SortMembers(document, second, JsonMemberOrder.Ascending);

        Assert.True(result.Applied);
        Assert.Equal("""{"b":{"z":1,"a":2},"a":{"x":4,"y":3}}""", Read(editor));
    }
}
