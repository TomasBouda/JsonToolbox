using System.Text;
using JsonToolbox.Core.Documents;
using JsonToolbox.Core.Editing;
using JsonToolbox.Core.Model;

namespace JsonToolbox.Core.Tests;

public class JsonEditingTests
{
    private static (JsonDocumentEditor Editor, EditableJsonSource Source) Open(string json)
    {
        var source = new EditableJsonSource(JsonSource.FromText(json, "test"));
        return (new JsonDocumentEditor(source), source);
    }

    private static string TextOf(EditableJsonSource source) =>
        Encoding.UTF8.GetString(source.ReadSlice(0, (int)source.Length));

    private static JsonNodeInfo Child(EditableJsonSource source, string name)
    {
        using IndexedJsonDocument document = IndexedJsonDocument.Open(source);
        return document.IndexChildren(document.Root).Children.Single(c => c.Name == name);
    }

    private static JsonNodeInfo Element(EditableJsonSource source, int index)
    {
        using IndexedJsonDocument document = IndexedJsonDocument.Open(source);
        return document.IndexChildren(document.Root).Children[index];
    }

    // ---- The piece table ------------------------------------------------------------

    [Fact]
    public void An_untouched_document_reads_back_unchanged()
    {
        const string json = """{"a":1,"b":[1,2,3]}""";
        (_, EditableJsonSource source) = Open(json);

        Assert.Equal(json, TextOf(source));
        Assert.False(source.IsModified);
    }

    [Fact]
    public void An_edit_costs_a_handful_of_pieces_however_large_the_document()
    {
        (JsonDocumentEditor editor, EditableJsonSource source) = Open(
            $$"""{"pad":"{{new string('x', 100_000)}}","a":1}""");

        editor.ReplaceValue(Child(source, "a"), "2");

        // Split before, split after, one piece of new text: the file size does not come into it.
        Assert.True(source.Table.PieceCount <= 4, $"expected a few pieces, got {source.Table.PieceCount}");
    }

    [Fact]
    public void Reading_across_a_piece_boundary_returns_the_whole_range()
    {
        (JsonDocumentEditor editor, EditableJsonSource source) = Open("""{"a":1,"b":2}""");
        editor.ReplaceValue(Child(source, "a"), "111");

        Assert.Equal("""{"a":111,"b":2}""", TextOf(source));

        // A read that starts in the original, crosses the inserted piece and lands back in the
        // original is the case a naive implementation gets wrong. Offset 4 is the colon after
        // "a", so nine bytes span the colon, the new value, and the start of the next member.
        byte[] buffer = new byte[9];
        int read = source.ReadInto(4, buffer, 9);
        Assert.Equal(9, read);
        Assert.Equal(""":111,"b":""", Encoding.UTF8.GetString(buffer));
    }

    // ---- Changing a value -----------------------------------------------------------

    [Fact]
    public void Replaces_a_scalar()
    {
        (JsonDocumentEditor editor, EditableJsonSource source) = Open("""{"name":"Alice","age":30}""");

        Assert.True(editor.ReplaceValue(Child(source, "age"), "31").Applied);
        Assert.Equal("""{"name":"Alice","age":31}""", TextOf(source));
    }

    [Fact]
    public void Replaces_a_whole_subtree()
    {
        (JsonDocumentEditor editor, EditableJsonSource source) = Open("""{"a":{"deep":[1,2,3]},"b":2}""");

        editor.ReplaceValue(Child(source, "a"), "null");

        Assert.Equal("""{"a":null,"b":2}""", TextOf(source));
    }

    [Fact]
    public void Refuses_text_that_is_not_a_json_value()
    {
        (JsonDocumentEditor editor, EditableJsonSource source) = Open("""{"a":1}""");

        EditResult result = editor.ReplaceValue(Child(source, "a"), "{oops");

        Assert.False(result.Applied);
        Assert.NotNull(result.Message);
        Assert.Equal("""{"a":1}""", TextOf(source));
    }

    [Fact]
    public void Refuses_more_than_one_value()
    {
        // Pasting "1,2" would otherwise inject a second member into the object.
        (JsonDocumentEditor editor, EditableJsonSource source) = Open("""{"a":1}""");

        Assert.False(editor.ReplaceValue(Child(source, "a"), "1,2").Applied);
        Assert.Equal("""{"a":1}""", TextOf(source));
    }

    [Fact]
    public void Refuses_an_empty_value()
    {
        (JsonDocumentEditor editor, EditableJsonSource source) = Open("""{"a":1}""");

        Assert.False(editor.ReplaceValue(Child(source, "a"), "   ").Applied);
    }

    // ---- Renaming -------------------------------------------------------------------

    [Fact]
    public void Renames_a_property()
    {
        (JsonDocumentEditor editor, EditableJsonSource source) = Open("""{"old":1,"b":2}""");

        Assert.True(editor.RenameProperty(Child(source, "old"), "fresh").Applied);
        Assert.Equal("""{"fresh":1,"b":2}""", TextOf(source));
    }

    [Fact]
    public void A_new_name_is_escaped_rather_than_pasted()
    {
        (JsonDocumentEditor editor, EditableJsonSource source) = Open("""{"a":1}""");

        editor.RenameProperty(Child(source, "a"), "he said \"hi\"");

        // Pasting the name in unescaped would end the string early and break the document.
        Assert.Equal("""{"he said \"hi\"":1}""", TextOf(source));
        AssertStillValid(source);
    }

    [Fact]
    public void A_new_name_keeps_the_letters_it_was_typed_with()
    {
        (JsonDocumentEditor editor, EditableJsonSource source) = Open("""{"a":1}""");

        // Escaping these into \uXXXX would be valid JSON and unreadable in a document somebody
        // is going to look at.
        editor.RenameProperty(Child(source, "a"), "příjmení / 名前");

        Assert.Equal("""{"příjmení / 名前":1}""", TextOf(source));
        AssertStillValid(source);
    }

    [Fact]
    public void A_control_character_with_no_shorthand_goes_out_as_a_numeric_escape()
    {
        (JsonDocumentEditor editor, EditableJsonSource source) = Open("""{"a":1}""");

        // A bell character: legal in a name, and something JSON can carry no other way.
        editor.RenameProperty(Child(source, "a"), "bell" + (char)7);

        Assert.Equal("{\"bell\\u0007\":1}", TextOf(source));
        AssertStillValid(source);
    }

    [Fact]
    public void A_new_name_escapes_the_characters_JSON_has_no_other_way_to_carry()
    {
        (JsonDocumentEditor editor, EditableJsonSource source) = Open("""{"a":1}""");

        editor.RenameProperty(Child(source, "a"), "line\nbreak\ttab\\slash");

        Assert.Equal("""{"line\nbreak\ttab\\slash":1}""", TextOf(source));
        AssertStillValid(source);
    }


    [Fact]
    public void An_array_element_has_no_name_to_rename()
    {
        (JsonDocumentEditor editor, EditableJsonSource source) = Open("[1,2,3]");

        Assert.False(editor.RenameProperty(Element(source, 1), "x").Applied);
    }

    // ---- Deleting -------------------------------------------------------------------

    [Fact]
    public void Deletes_a_property_from_the_middle_with_its_comma()
    {
        (JsonDocumentEditor editor, EditableJsonSource source) = Open("""{"a":1,"b":2,"c":3}""");

        editor.Delete(Child(source, "b"));

        Assert.Equal("""{"a":1,"c":3}""", TextOf(source));
    }

    [Fact]
    public void Deletes_the_first_property()
    {
        (JsonDocumentEditor editor, EditableJsonSource source) = Open("""{"a":1,"b":2}""");

        editor.Delete(Child(source, "a"));

        Assert.Equal("""{"b":2}""", TextOf(source));
    }

    [Fact]
    public void Deletes_the_last_property_by_reaching_back_for_the_comma()
    {
        (JsonDocumentEditor editor, EditableJsonSource source) = Open("""{"a":1,"b":2}""");

        editor.Delete(Child(source, "b"));

        Assert.Equal("""{"a":1}""", TextOf(source));
    }

    [Fact]
    public void Deletes_the_only_property()
    {
        (JsonDocumentEditor editor, EditableJsonSource source) = Open("""{"a":1}""");

        editor.Delete(Child(source, "a"));

        Assert.Equal("{}", TextOf(source));
    }

    [Fact]
    public void Deletes_an_array_element_from_the_middle()
    {
        (JsonDocumentEditor editor, EditableJsonSource source) = Open("[1,2,3]");

        editor.Delete(Element(source, 1));

        Assert.Equal("[1,3]", TextOf(source));
    }

    [Fact]
    public void Deletes_the_last_array_element()
    {
        (JsonDocumentEditor editor, EditableJsonSource source) = Open("[1,2,3]");

        editor.Delete(Element(source, 2));

        Assert.Equal("[1,2]", TextOf(source));
    }

    [Fact]
    public void Deleting_copes_with_whitespace_around_the_comma()
    {
        (JsonDocumentEditor editor, EditableJsonSource source) = Open("""
            {
              "a": 1,
              "b": 2
            }
            """);

        editor.Delete(Child(source, "a"));

        Assert.DoesNotContain("\"a\"", TextOf(source), StringComparison.Ordinal);
        AssertStillValid(source);
    }

    // ---- Inserting ------------------------------------------------------------------

    [Fact]
    public void Adds_a_property_to_an_object_that_has_some()
    {
        (JsonDocumentEditor editor, EditableJsonSource source) = Open("""{"a":1}""");

        editor.Insert(RootOf(source), "b", "2");

        Assert.Equal("""{"a":1,"b":2}""", TextOf(source));
    }

    [Fact]
    public void Adds_the_first_property_without_a_stray_comma()
    {
        // The root has not been opened, so its child count is unknown: whether a comma is
        // needed has to come from the document itself.
        (JsonDocumentEditor editor, EditableJsonSource source) = Open("{}");

        editor.Insert(RootOf(source), "a", "1");

        Assert.Equal("""{"a":1}""", TextOf(source));
        AssertStillValid(source);
    }

    [Fact]
    public void Adds_the_first_element_of_an_empty_array()
    {
        (JsonDocumentEditor editor, EditableJsonSource source) = Open("[ ]");

        editor.Insert(RootOf(source), null, "1");

        AssertStillValid(source);
        Assert.Equal("[ 1]", TextOf(source));
    }

    [Fact]
    public void Adds_an_element_to_an_array()
    {
        (JsonDocumentEditor editor, EditableJsonSource source) = Open("[1,2]");

        editor.Insert(RootOf(source), null, "3");

        Assert.Equal("[1,2,3]", TextOf(source));
    }

    [Fact]
    public void Refuses_a_new_member_that_is_not_a_json_value()
    {
        (JsonDocumentEditor editor, EditableJsonSource source) = Open("""{"a":1}""");

        Assert.False(editor.Insert(RootOf(source), "b", "oops").Applied);
        Assert.Equal("""{"a":1}""", TextOf(source));
    }

    private static JsonNodeInfo RootOf(EditableJsonSource source)
    {
        using IndexedJsonDocument document = IndexedJsonDocument.Open(source);
        return document.Root;
    }

    // ---- Undo and redo --------------------------------------------------------------

    [Fact]
    public void Undo_puts_the_document_back()
    {
        const string json = """{"a":1,"b":2}""";
        (JsonDocumentEditor editor, EditableJsonSource source) = Open(json);

        editor.ReplaceValue(Child(source, "a"), "99");
        Assert.True(editor.CanUndo);

        editor.Undo();

        Assert.Equal(json, TextOf(source));
        Assert.False(editor.CanUndo);
        Assert.True(editor.CanRedo);
    }

    [Fact]
    public void Redo_puts_the_edit_back()
    {
        (JsonDocumentEditor editor, EditableJsonSource source) = Open("""{"a":1}""");

        editor.ReplaceValue(Child(source, "a"), "99");
        editor.Undo();
        editor.Redo();

        Assert.Equal("""{"a":99}""", TextOf(source));
    }

    [Fact]
    public void A_new_edit_discards_what_was_undone()
    {
        (JsonDocumentEditor editor, EditableJsonSource source) = Open("""{"a":1}""");

        editor.ReplaceValue(Child(source, "a"), "99");
        editor.Undo();
        editor.ReplaceValue(Child(source, "a"), "7");

        Assert.False(editor.CanRedo);
        Assert.Equal("""{"a":7}""", TextOf(source));
    }

    [Fact]
    public void Undo_walks_back_through_several_edits()
    {
        const string json = """{"a":1,"b":2,"c":3}""";
        (JsonDocumentEditor editor, EditableJsonSource source) = Open(json);

        editor.ReplaceValue(Child(source, "a"), "10");
        editor.ReplaceValue(Child(source, "b"), "20");
        editor.Delete(Child(source, "c"));

        editor.Undo();
        editor.Undo();
        editor.Undo();

        Assert.Equal(json, TextOf(source));
    }

    // ---- The rest of the application still works on an edited document --------------

    [Fact]
    public void The_tree_reads_an_edited_document_like_any_other()
    {
        (JsonDocumentEditor editor, EditableJsonSource source) = Open("""{"a":1,"b":{"c":2}}""");
        editor.ReplaceValue(Child(source, "a"), """["x","y"]""");

        using IndexedJsonDocument document = IndexedJsonDocument.Open(source);
        JsonNodeInfo edited = document.IndexChildren(document.Root).Children.Single(c => c.Name == "a");

        Assert.Equal(JsonKind.Array, edited.Kind);
        Assert.Equal(2, edited.ChildCount);
        Assert.Equal(["\"x\"", "\"y\""], document.IndexChildren(edited).Children.Select(c => c.Preview));
    }

    [Fact]
    public void Saving_writes_what_the_document_now_says()
    {
        (JsonDocumentEditor editor, EditableJsonSource source) = Open("""{"a":1,"b":2}""");
        editor.ReplaceValue(Child(source, "a"), "42");

        string path = Path.Combine(Path.GetTempPath(), $"jsontoolbox-save-{Guid.NewGuid():N}.json");
        try
        {
            editor.SaveAs(path);
            Assert.Equal("""{"a":42,"b":2}""", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Reads the whole document back through the scanner, which rejects invalid JSON.</summary>
    private static void AssertStillValid(EditableJsonSource source)
    {
        using IndexedJsonDocument document = IndexedJsonDocument.Open(source);
        document.IndexChildren(document.Root);
    }
}
