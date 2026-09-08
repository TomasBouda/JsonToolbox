using System.Text;
using System.Text.Json;
using JsonToolbox.Core.Documents;

namespace JsonToolbox.Core.Editing;

/// <summary>The outcome of an attempted edit.</summary>
/// <param name="Message">Why it was refused, when it was.</param>
public readonly record struct EditResult(bool Applied, string? Message)
{
    public static EditResult Ok() => new(true, null);

    public static EditResult Refused(string message) => new(false, message);
}

/// <summary>
/// Changes a document, one value at a time, and never leaves it invalid.
/// </summary>
/// <remarks>
/// <para>
/// Every edit here is a byte range and its replacement, applied to a piece table. Nothing is
/// reparsed and nothing is rewritten, so changing a value in a three hundred megabyte file
/// costs the same as changing one in a small one.
/// </para>
/// <para>
/// What each operation guards is that the result is still JSON. A replacement value is parsed
/// before it is accepted; a new key is escaped rather than pasted in; and deleting a member
/// takes the comma that joined it to its neighbours, because leaving that behind is the one
/// mistake that turns a working document into a broken one.
/// </para>
/// </remarks>
public sealed class JsonDocumentEditor(EditableJsonSource source)
{
    private readonly List<PieceTable.PieceTableSnapshot> _undo = [];
    private readonly List<PieceTable.PieceTableSnapshot> _redo = [];

    public EditableJsonSource Source => source;

    public bool IsModified => source.IsModified;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>
    /// Replaces a value with the JSON text given, which must itself be a single valid value.
    /// </summary>
    public EditResult ReplaceValue(JsonNodeInfo node, string json)
    {
        if (Validate(json) is { } problem)
        {
            return EditResult.Refused(problem);
        }

        long end = node.HasKnownExtent ? node.End : source.Length;
        Apply(node.Start, end - node.Start, Encoding.UTF8.GetBytes(json));
        return EditResult.Ok();
    }

    /// <summary>
    /// Renames a property.
    /// </summary>
    /// <remarks>
    /// The name is written as a JSON string rather than pasted in, so a key containing a quote
    /// or a backslash comes out escaped instead of breaking the document.
    /// </remarks>
    public EditResult RenameProperty(JsonNodeInfo node, string newName)
    {
        if (!node.HasNamePosition)
        {
            return EditResult.Refused("This value is not a property, so it has no name to change.");
        }

        if (string.IsNullOrEmpty(newName))
        {
            return EditResult.Refused("A property name cannot be empty.");
        }

        Apply(node.NameStart, node.NameEnd - node.NameStart, Encoding.UTF8.GetBytes(Quote(newName)));
        return EditResult.Ok();
    }

    /// <summary>
    /// Removes a value from its container, together with the name that introduces it and the
    /// comma that joins it to its neighbours.
    /// </summary>
    public EditResult Delete(JsonNodeInfo node)
    {
        if (node.Start <= 0 && !node.HasNamePosition)
        {
            return EditResult.Refused("The document root cannot be deleted.");
        }

        long from = node.HasNamePosition ? node.NameStart : node.Start;
        long to = node.HasKnownExtent ? node.End : source.Length;

        (from, to) = AbsorbSeparator(from, to);
        Apply(from, to - from, []);
        return EditResult.Ok();
    }

    /// <summary>
    /// Adds a member to a container, written just inside its closing bracket.
    /// </summary>
    /// <param name="name">
    /// The property name for an object, or <c>null</c> for an array element.
    /// </param>
    public EditResult Insert(JsonNodeInfo container, string? name, string json)
    {
        if (!container.IsContainer)
        {
            return EditResult.Refused("Only an object or an array can take a new member.");
        }

        if (Validate(json) is { } problem)
        {
            return EditResult.Refused(problem);
        }

        long end = container.HasKnownExtent ? container.End : source.Length;
        long at = end - 1;

        // Whether a comma is needed is read from the document rather than from the child
        // count, which is unknown until a container has been opened. The byte before the
        // closing bracket says it plainly: an opening bracket means there is nothing yet.
        long previous = SkipWhitespaceBackward(at - 1);
        bool empty = previous < 0 || ByteAt(previous) is (byte)'{' or (byte)'[';

        string member = name is null ? json : $"{Quote(name)}:{json}";
        Apply(at, 0, Encoding.UTF8.GetBytes(empty ? member : "," + member));
        return EditResult.Ok();
    }

    public bool Undo()
    {
        if (_undo.Count == 0)
        {
            return false;
        }

        _redo.Add(source.Table.Snapshot());
        source.Table.Restore(_undo[^1]);
        _undo.RemoveAt(_undo.Count - 1);

        // Stepping away from what was saved is itself a change from what is on disk.
        source.Table.MarkModified();
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0)
        {
            return false;
        }

        _undo.Add(source.Table.Snapshot());
        source.Table.Restore(_redo[^1]);
        _redo.RemoveAt(_redo.Count - 1);
        source.Table.MarkModified();
        return true;
    }

    /// <summary>Writes the document as it now stands to a stream.</summary>
    public void WriteTo(Stream destination, CancellationToken cancellationToken = default) =>
        source.Table.WriteTo(destination, cancellationToken);

    /// <summary>
    /// Records that what is on disk now matches the document, and forgets the history.
    /// </summary>
    /// <remarks>
    /// Saving over the file the document was opened from means letting go of the mapping it is
    /// read through and opening the new file instead. The snapshots kept for undo point into
    /// the old one, so they cannot survive that — which is why saving in place starts the
    /// history again.
    /// </remarks>
    public void ForgetHistory()
    {
        _undo.Clear();
        _redo.Clear();
        source.Table.MarkSaved();
    }

    /// <summary>
    /// Writes the document to a file other than the one it was opened from.
    /// </summary>
    /// <remarks>
    /// It goes to a temporary file beside the target and is moved into place afterwards, so an
    /// interrupted save leaves the original intact rather than half of it.
    /// </remarks>
    public void SaveAs(string path, CancellationToken cancellationToken = default)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        string temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                source.Table.WriteTo(stream, cancellationToken);
            }

            File.Move(temporary, path, overwrite: true);
            source.Table.MarkSaved();
        }
        catch
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }

            throw;
        }
    }

    /// <summary>Checks that a piece of text is exactly one JSON value.</summary>
    private static string? Validate(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return "A value cannot be empty.";
        }

        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json));
            if (!reader.Read())
            {
                return "That is not a JSON value.";
            }

            reader.Skip();

            // Anything after the first value would be pasted into the document unchecked.
            return reader.Read() ? "That is more than one JSON value." : null;
        }
        catch (JsonException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// The encoder used for names the user typed.
    /// </summary>
    /// <remarks>
    /// The default one escapes a quote as <c>"</c> and mangles anything non-Latin, which
    /// is correct but unreadable in a document a person is going to look at. The relaxed
    /// encoder still escapes what JSON requires — quotes, backslashes, control characters —
    /// and leaves the rest as typed.
    /// </remarks>
    private static readonly JsonSerializerOptions NameEncoding = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Writes a string as JSON, escaping whatever needs it.</summary>
    private static string Quote(string text) => JsonSerializer.Serialize(text, NameEncoding);

    /// <summary>
    /// Widens a deletion to take the comma that separated the member from its neighbours.
    /// </summary>
    /// <remarks>
    /// The comma after is preferred, because that is the one a member in the middle or at the
    /// front owns. Only the last member has to reach backwards for the comma before it.
    /// </remarks>
    private (long From, long To) AbsorbSeparator(long from, long to)
    {
        long after = SkipWhitespaceForward(to);
        if (after < source.Length && ByteAt(after) == (byte)',')
        {
            return (from, after + 1);
        }

        long before = SkipWhitespaceBackward(from - 1);
        if (before >= 0 && ByteAt(before) == (byte)',')
        {
            return (before, to);
        }

        return (from, to);
    }

    private long SkipWhitespaceForward(long offset)
    {
        while (offset < source.Length && IsWhitespace(ByteAt(offset)))
        {
            offset++;
        }

        return offset;
    }

    private long SkipWhitespaceBackward(long offset)
    {
        while (offset >= 0 && IsWhitespace(ByteAt(offset)))
        {
            offset--;
        }

        return offset;
    }

    private static bool IsWhitespace(byte b) => b is 0x20 or 0x09 or 0x0A or 0x0D;

    private byte ByteAt(long offset)
    {
        byte[] one = new byte[1];
        return source.ReadInto(offset, one, 1) == 1 ? one[0] : (byte)0;
    }

    private void Apply(long offset, long length, ReadOnlySpan<byte> replacement)
    {
        _undo.Add(source.Table.Snapshot());
        _redo.Clear();
        source.Table.Replace(offset, length, replacement);
    }
}
