using System.Buffers;
using JsonExplorer.Core.Model;
using JsonExplorer.Core.Scanning;

namespace JsonExplorer.Core.Documents;

/// <summary>
/// An open JSON document, browsable without ever having been fully parsed.
/// </summary>
/// <remarks>
/// <para>
/// Opening only looks at the first non-whitespace byte, which is enough to know whether the
/// root is an object, an array or a scalar. Everything below it is indexed the first time
/// somebody expands it, and only one level at a time. That is what makes a five-gigabyte
/// file appear instantly instead of after a coffee break.
/// </para>
/// <para>
/// The trade-off is that a container's child count is unknown until its children have been
/// listed, because counting them means reading all of their bytes. The UI shows this as an
/// indeterminate expander rather than pretending to know.
/// </para>
/// </remarks>
public sealed class JsonExplorerDocument : IDisposable
{
    private readonly JsonSource _source;

    private JsonExplorerDocument(JsonSource source, JsonNodeInfo root)
    {
        _source = source;
        Root = root;
    }

    public JsonSource Source => _source;

    public JsonNodeInfo Root { get; }

    public string DisplayName => _source.DisplayName;

    public long Length => _source.Length;

    public static JsonExplorerDocument Open(JsonSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        (JsonKind kind, long start) = ProbeRoot(source);
        var root = new JsonNodeInfo(
            kind,
            Name: null,
            Index: 0,
            Start: start,
            End: source.Length,
            ChildCount: kind.IsContainer() ? -1 : 0,
            Preview: kind.IsContainer() ? null : "(root)");

        return new JsonExplorerDocument(source, root);
    }

    public static JsonExplorerDocument OpenFile(string path) => Open(JsonSource.FromFile(path));

    public static JsonExplorerDocument OpenText(string json, string displayName = "(in memory)") =>
        Open(JsonSource.FromText(json, displayName));

    /// <summary>
    /// Lists the direct children of <paramref name="node"/>.
    /// </summary>
    /// <param name="onChildFound">
    /// Called on the scanning thread for every child as it is discovered, so a caller can
    /// render the first rows of a huge array long before the last one is known.
    /// </param>
    /// <param name="maxChildren">
    /// Stops the scan once this many children have been found. A tree cannot usefully show
    /// more rows than a person can scroll through, and the cut-off is reported back so the
    /// UI can offer to keep going.
    /// </param>
    public JsonChildIndexer IndexChildren(
        JsonNodeInfo node,
        Action<JsonNodeInfo>? onChildFound = null,
        int maxChildren = int.MaxValue,
        Action<JsonNodeInfo>? onChildCompleted = null,
        IReadOnlySet<string>? pinnedKeys = null,
        JsonScanOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var indexer = new JsonChildIndexer(onChildFound, maxChildren, onChildCompleted, pinnedKeys);

        if (!node.IsContainer)
        {
            return indexer;
        }

        // A node whose end is not known yet is given the rest of the file; the indexer stops
        // itself at the node's closing bracket, so nothing beyond it is ever parsed.
        long available = node.HasKnownExtent ? node.ByteLength : _source.Length - node.Start;

        using Stream stream = _source.OpenRange(node.Start, available);
        JsonScanner.Scan(stream, indexer, node.Start, options, cancellationToken);
        return indexer;
    }

    public Task<JsonChildIndexer> IndexChildrenAsync(
        JsonNodeInfo node,
        Action<JsonNodeInfo>? onChildFound = null,
        int maxChildren = int.MaxValue,
        Action<JsonNodeInfo>? onChildCompleted = null,
        IReadOnlySet<string>? pinnedKeys = null,
        JsonScanOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(
            () => IndexChildren(node, onChildFound, maxChildren, onChildCompleted, pinnedKeys, options, cancellationToken),
            cancellationToken);

    /// <summary>Reads the raw text of one value, truncated to a size worth displaying.</summary>
    public string ReadRaw(JsonNodeInfo node, int maxBytes = 256 * 1024) =>
        _source.ReadText(node.Start, node.HasKnownExtent ? node.ByteLength : _source.Length - node.Start, maxBytes);

    /// <summary>
    /// Finds which line a byte offset falls on, so the text view and the tree can point at
    /// the same place. Returns <c>-1</c> when the offset is further into the file than
    /// <paramref name="maxBytesToScan"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Counting newlines from the start of the file is linear, and there is no way around
    /// that without an index nobody has asked to pay for. The limit exists so that clicking a
    /// value near the end of a huge document costs a bounded amount of work rather than a
    /// pause; past it, the byte offset is the honest answer and the line number is not.
    /// </para>
    /// <para>
    /// The bytes are counted in chunks rather than read into one buffer: the offset can be
    /// tens of megabytes, and allocating that just to look for newlines in it is the kind of
    /// thing that shows up as a frozen window.
    /// </para>
    /// </remarks>
    public long GetLineNumber(long offset, long maxBytesToScan = 8 * 1024 * 1024, CancellationToken cancellationToken = default)
    {
        if (offset <= 0)
        {
            return 1;
        }

        if (offset > maxBytesToScan)
        {
            return -1;
        }

        using Stream stream = _source.OpenRange(0, offset);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        long line = 1;

        try
        {
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                ReadOnlySpan<byte> chunk = buffer.AsSpan(0, read);
                int index;
                while ((index = chunk.IndexOf((byte)'\n')) >= 0)
                {
                    line++;
                    chunk = chunk[(index + 1)..];
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return line;
    }

    private static (JsonKind Kind, long Start) ProbeRoot(JsonSource source)
    {
        // 64 bytes is more leading whitespace than any real document has, and reading it
        // costs one page fault rather than a parse.
        byte[] head = source.ReadSlice(0, (int)Math.Min(source.Length, 64));

        for (int i = 0; i < head.Length; i++)
        {
            byte b = head[i];

            // Skip a byte order mark as well as whitespace: neither is the root value.
            if (b is 0x20 or 0x09 or 0x0A or 0x0D or 0xEF or 0xBB or 0xBF)
            {
                continue;
            }

            JsonKind kind = b switch
            {
                (byte)'{' => JsonKind.Object,
                (byte)'[' => JsonKind.Array,
                (byte)'"' => JsonKind.String,
                (byte)'t' => JsonKind.True,
                (byte)'f' => JsonKind.False,
                (byte)'n' => JsonKind.Null,
                >= (byte)'0' and <= (byte)'9' or (byte)'-' => JsonKind.Integer,
                _ => JsonKind.Undefined,
            };

            return (kind, i);
        }

        return (JsonKind.Undefined, 0);
    }

    public void Dispose() => _source.Dispose();
}
