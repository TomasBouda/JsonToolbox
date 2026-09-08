using System.IO.MemoryMappedFiles;
using System.Text;

namespace JsonToolbox.Core.Documents;

/// <summary>
/// The bytes of a JSON document, addressable both as a forward stream (for scanning) and as
/// random-access slices (for reading back a single value the user clicked on).
/// </summary>
/// <remarks>
/// Both access patterns are needed and neither can replace the other. Scanning is sequential
/// and covers the whole file, so it must never materialise it; but once the index knows that
/// a value lives at bytes 4 812 993 990 to 4 812 994 021, reading exactly those 31 bytes has
/// to be instant, which rules out re-scanning from the start.
/// </remarks>
public abstract class JsonSource : IDisposable
{
    /// <summary>Total size of the document in bytes.</summary>
    public abstract long Length { get; }

    /// <summary>Where the document came from, for the window title and error messages.</summary>
    public abstract string DisplayName { get; }

    /// <summary>The file this document was loaded from, or <c>null</c> for in-memory documents.</summary>
    public virtual string? FilePath => null;

    /// <summary>
    /// Opens a forward-only stream positioned at <paramref name="offset"/>. The caller owns
    /// the returned stream and must dispose it.
    /// </summary>
    public abstract Stream OpenRead(long offset = 0);

    /// <summary>
    /// Opens a forward-only stream over exactly <paramref name="length"/> bytes starting at
    /// <paramref name="offset"/>.
    /// </summary>
    /// <remarks>
    /// Indexing one node means parsing the bytes of that node and nothing else. Without a
    /// hard end, the reader runs past the node's closing bracket into the rest of the
    /// document and rejects it as trailing content — the node is valid JSON, but the file
    /// from that offset onwards is not.
    /// </remarks>
    public Stream OpenRange(long offset, long length) => new BoundedReadStream(OpenRead(offset), length);

    /// <summary>
    /// Copies a slice of the document into memory. Used for previews and for indexing the
    /// children of one container, so the length is capped at <see cref="int.MaxValue"/> by
    /// its type on purpose — nothing in the UI ever needs more at once.
    /// </summary>
    public abstract byte[] ReadSlice(long offset, int length);

    /// <summary>
    /// Copies a slice into a buffer the caller already has, and returns how many bytes were
    /// available. Nothing is allocated.
    /// </summary>
    /// <remarks>
    /// Comparing two documents means comparing millions of small ranges, and doing that
    /// through a stream costs a file handle and a buffer per comparison. Reading straight out
    /// of the mapping instead turns each one into a copy and a compare.
    /// </remarks>
    public abstract int ReadInto(long offset, byte[] destination, int count);

    /// <summary>
    /// Reads a slice and decodes it as UTF-8, truncating at <paramref name="maxBytes"/> so
    /// that clicking a 40 MB string value cannot freeze the UI.
    /// </summary>
    public string ReadText(long offset, long length, int maxBytes = 64 * 1024)
    {
        if (length <= 0)
        {
            return string.Empty;
        }

        int take = (int)Math.Min(length, maxBytes);
        string text = Encoding.UTF8.GetString(ReadSlice(offset, take));
        return length > take ? text + "…" : text;
    }

    public static JsonSource FromFile(string path) => new FileJsonSource(path);

    public static JsonSource FromBytes(byte[] bytes, string displayName = "(in memory)") =>
        new MemoryJsonSource(bytes, displayName);

    public static JsonSource FromText(string json, string displayName = "(in memory)") =>
        new MemoryJsonSource(Encoding.UTF8.GetBytes(json), displayName);

    public virtual void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// A document backed by a file on disk, read through a memory mapping.
/// </summary>
/// <remarks>
/// The mapping is what makes multi-gigabyte files practical: the operating system pages in
/// only the regions actually touched, so opening a file costs nothing and jumping to a value
/// near the end does not read everything before it.
/// </remarks>
internal sealed class FileJsonSource : JsonSource
{
    private readonly MemoryMappedFile? _mapping;

    /// <summary>
    /// One view over the whole file, kept for the life of the document.
    /// </summary>
    /// <remarks>
    /// Creating a view per read is a system call each time, and a diff makes millions of small
    /// reads. One view costs address space, which on a 64-bit process is not a scarce resource,
    /// and the operating system still pages in only what is touched.
    /// </remarks>
    private readonly MemoryMappedViewAccessor? _view;

    private readonly string _path;

    public FileJsonSource(string path)
    {
        _path = Path.GetFullPath(path);
        Length = new FileInfo(_path).Length;

        // A zero-length file cannot be mapped, and there is nothing to read from it anyway.
        if (Length > 0)
        {
            _mapping = MemoryMappedFile.CreateFromFile(_path, FileMode.Open, mapName: null, capacity: 0, MemoryMappedFileAccess.Read);
            _view = _mapping.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        }
    }

    public override long Length { get; }

    public override string DisplayName => Path.GetFileName(_path);

    public override string FilePath => _path;

    public override Stream OpenRead(long offset = 0)
    {
        var stream = new FileStream(
            _path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1,
            FileOptions.SequentialScan);

        if (offset > 0)
        {
            stream.Seek(offset, SeekOrigin.Begin);
        }

        return stream;
    }

    public override byte[] ReadSlice(long offset, int length)
    {
        if (_view is null || length <= 0 || offset >= Length)
        {
            return [];
        }

        length = (int)Math.Min(length, Length - offset);
        byte[] buffer = new byte[length];
        _view.ReadArray(offset, buffer, 0, length);
        return buffer;
    }

    public override int ReadInto(long offset, byte[] destination, int count)
    {
        if (_view is null || count <= 0 || offset >= Length)
        {
            return 0;
        }

        count = (int)Math.Min(Math.Min(count, destination.Length), Length - offset);
        return _view.ReadArray(offset, destination, 0, count);
    }

    public override void Dispose()
    {
        _view?.Dispose();
        _mapping?.Dispose();
        base.Dispose();
    }
}

/// <summary>
/// A document held entirely in memory — pasted from the clipboard, fetched from a URL, or
/// constructed by a test.
/// </summary>
internal sealed class MemoryJsonSource(byte[] bytes, string displayName) : JsonSource
{
    public override long Length => bytes.Length;

    public override string DisplayName => displayName;

    public override Stream OpenRead(long offset = 0) =>
        new MemoryStream(bytes, (int)offset, bytes.Length - (int)offset, writable: false);

    public override byte[] ReadSlice(long offset, int length)
    {
        if (length <= 0 || offset >= bytes.Length)
        {
            return [];
        }

        length = (int)Math.Min(length, bytes.Length - offset);
        return bytes.AsSpan((int)offset, length).ToArray();
    }

    public override int ReadInto(long offset, byte[] destination, int count)
    {
        if (count <= 0 || offset >= bytes.Length)
        {
            return 0;
        }

        count = (int)Math.Min(Math.Min(count, destination.Length), bytes.Length - offset);
        bytes.AsSpan((int)offset, count).CopyTo(destination);
        return count;
    }
}
