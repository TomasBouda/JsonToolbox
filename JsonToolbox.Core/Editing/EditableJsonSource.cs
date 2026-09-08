using JsonToolbox.Core.Documents;

namespace JsonToolbox.Core.Editing;

/// <summary>
/// A document that can be changed, presented through the same interface as one that cannot.
/// </summary>
/// <remarks>
/// Everything the toolbox does — scanning, indexing, searching, inspecting, comparing — reads
/// its document through <see cref="JsonSource"/> and asks only for a length and some bytes at
/// an offset. A piece table can answer exactly that. So editing needed no changes anywhere
/// else: the same code walks an edited document as walks a file on disk, and it cannot tell
/// the difference.
/// </remarks>
public sealed class EditableJsonSource : JsonSource
{
    private readonly JsonSource _original;

    public EditableJsonSource(JsonSource original)
    {
        _original = original;
        Table = new PieceTable(original);
    }

    public PieceTable Table { get; }

    public override long Length => Table.Length;

    public override string DisplayName => _original.DisplayName;

    public override string? FilePath => _original.FilePath;

    public bool IsModified => Table.IsModified;

    public override Stream OpenRead(long offset = 0) => Table.OpenRange(offset, Math.Max(0, Length - offset));

    public override byte[] ReadSlice(long offset, int length)
    {
        if (length <= 0 || offset >= Length)
        {
            return [];
        }

        byte[] buffer = new byte[(int)Math.Min(length, Length - offset)];
        int read = Table.ReadInto(offset, buffer, buffer.Length);
        return read == buffer.Length ? buffer : buffer[..read];
    }

    public override int ReadInto(long offset, byte[] destination, int count) =>
        Table.ReadInto(offset, destination, count);

    public override void Dispose()
    {
        _original.Dispose();
        base.Dispose();
    }
}
