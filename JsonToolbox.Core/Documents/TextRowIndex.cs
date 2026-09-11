using System.Buffers;
using System.Text;

namespace JsonToolbox.Core.Documents;

/// <summary>
/// One row of a document's text as the text view shows it.
/// </summary>
/// <param name="Index">The row's position among all rows.</param>
/// <param name="Start">The byte offset of the row's first byte.</param>
/// <param name="Length">How many bytes the row spans, the line break included.</param>
/// <param name="Line">The line the row is on, counting from one.</param>
/// <param name="IsContinuation">
/// True when the row carries on a line that was too long to show as one row.
/// </param>
public readonly record struct TextRow(long Index, long Start, int Length, long Line, bool IsContinuation)
{
    public long End => Start + Length;

    public bool Contains(long offset) => offset >= Start && offset < End;
}

/// <summary>
/// The rows of a document's text, found once and read on demand.
/// </summary>
/// <remarks>
/// <para>
/// Showing a file as text means knowing where its rows start, and a file that is read
/// through a mapping rather than loaded has no rows until something counts them. Counting is
/// one pass over the bytes; what that pass keeps is the question, because a gigabyte of
/// pretty-printed JSON has tens of millions of lines and an offset for each of them is more
/// memory than the file deserves. So only every <see cref="RowsPerCheckpoint"/>th row is
/// kept. Any other row is found by reading forward from the checkpoint before it, which is a
/// bounded amount of work — at most a block of rows — and the same splitting the pass used,
/// so the two can never disagree about where a row begins.
/// </para>
/// <para>
/// A row is a line, unless the line is longer than <see cref="MaxRowBytes"/>: a minified
/// document is one line of the whole file, and no text control can lay that out. Such a line
/// is shown as several rows, each marked as continuing the one before, so the line numbers in
/// the margin still count lines.
/// </para>
/// <para>
/// The pass runs on whatever thread builds the index, and the rows can be read while it is
/// still running: the count grows as it goes, and every row below the count is final.
/// </para>
/// </remarks>
public sealed class TextRowIndex
{
    /// <summary>A line longer than this is shown as more than one row.</summary>
    public const int MaxRowBytes = 2048;

    /// <summary>How many rows lie between one remembered offset and the next.</summary>
    public const int RowsPerCheckpoint = 64;

    /// <summary>How many blocks of rows are kept decoded, for the rows the view is showing.</summary>
    private const int CachedBlocks = 8;

    private readonly JsonSource _source;
    private readonly List<Checkpoint> _checkpoints = [];
    private readonly Dictionary<long, TextRow[]> _blocks = [];
    private readonly Queue<long> _blockOrder = [];
    private readonly object _gate = new();
    private long _rowCount;
    private bool _isComplete;

    private readonly record struct Checkpoint(long Offset, long Line, bool IsContinuation);

    public TextRowIndex(JsonSource source)
    {
        _source = source;
    }

    /// <summary>How many rows have been found so far.</summary>
    public long RowCount
    {
        get
        {
            lock (_gate)
            {
                return _rowCount;
            }
        }
    }

    /// <summary>True once the whole document has been counted.</summary>
    public bool IsComplete
    {
        get
        {
            lock (_gate)
            {
                return _isComplete;
            }
        }
    }

    /// <summary>
    /// Counts the rows of the whole document, reporting how far it has got as it goes.
    /// </summary>
    /// <param name="progress">Told the number of rows found, now and then.</param>
    public void Build(Action<long>? progress = null, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_isComplete || _checkpoints.Count > 0)
            {
                return;
            }
        }

        // Enough for a whole block of the longest rows, plus one more row so the split never
        // has to be decided with the row's end out of sight.
        byte[] buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        long length = _source.Length;
        long offset = 0;
        long line = 1;
        long rows = 0;
        bool continuation = false;
        long reported = 0;

        try
        {
            while (offset < length)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int read = _source.ReadInto(offset, buffer, buffer.Length);
                if (read <= 0)
                {
                    break;
                }

                ReadOnlySpan<byte> chunk = buffer.AsSpan(0, read);
                int position = 0;

                // Stop short of the chunk's end unless it is the file's end: a row cut off by
                // the buffer would otherwise be split where the buffer ends, not where it should.
                int usable = offset + read >= length ? read : Math.Max(read - MaxRowBytes - 1, 1);

                while (position < usable)
                {
                    if (rows % RowsPerCheckpoint == 0)
                    {
                        lock (_gate)
                        {
                            _checkpoints.Add(new Checkpoint(offset + position, line, continuation));
                        }
                    }

                    (int rowLength, bool endsLine) = Split(chunk[position..]);
                    position += rowLength;
                    rows++;
                    continuation = !endsLine;

                    if (endsLine)
                    {
                        line++;
                    }
                }

                offset += position;

                lock (_gate)
                {
                    _rowCount = rows;
                }

                if (progress is not null && rows - reported >= 100_000)
                {
                    reported = rows;
                    progress(rows);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        lock (_gate)
        {
            _rowCount = rows;
            _isComplete = true;
        }

        progress?.Invoke(rows);
    }

    /// <summary>The row at the given position among all rows found so far.</summary>
    public TextRow Row(long index)
    {
        if (index < 0 || index >= RowCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        TextRow[] block = Block(index / RowsPerCheckpoint);
        return block[index % RowsPerCheckpoint];
    }

    /// <summary>
    /// The row a byte offset falls on, or the last row for an offset past the rows found so
    /// far.
    /// </summary>
    public TextRow RowAt(long offset)
    {
        long blockIndex;
        lock (_gate)
        {
            if (_rowCount == 0)
            {
                throw new InvalidOperationException("The index has no rows yet.");
            }

            // The last checkpoint at or before the offset starts the block the offset is in.
            int low = 0;
            int high = _checkpoints.Count - 1;
            while (low < high)
            {
                int middle = (low + high + 1) / 2;
                if (_checkpoints[middle].Offset <= offset)
                {
                    low = middle;
                }
                else
                {
                    high = middle - 1;
                }
            }

            blockIndex = low;
        }

        TextRow[] block = Block(blockIndex);
        foreach (TextRow row in block)
        {
            if (row.Contains(offset))
            {
                return row;
            }
        }

        return block[^1];
    }

    /// <summary>
    /// The text of one row, without the line break that ends it.
    /// </summary>
    public string Read(TextRow row)
    {
        byte[] bytes = _source.ReadSlice(row.Start, row.Length);
        int length = bytes.Length;

        if (length > 0 && bytes[length - 1] == (byte)'\n')
        {
            length--;
        }

        if (length > 0 && bytes[length - 1] == (byte)'\r')
        {
            length--;
        }

        return Encoding.UTF8.GetString(bytes, 0, length);
    }

    /// <summary>
    /// The rows of one block, read from its checkpoint with the same splitting the count
    /// used.
    /// </summary>
    private TextRow[] Block(long blockIndex)
    {
        Checkpoint checkpoint;
        long available;

        lock (_gate)
        {
            if (_blocks.TryGetValue(blockIndex, out TextRow[]? cached))
            {
                return cached;
            }

            checkpoint = _checkpoints[(int)blockIndex];
            available = Math.Min(RowsPerCheckpoint, _rowCount - blockIndex * RowsPerCheckpoint);
        }

        // The block's rows cannot be longer than this, and the last one may end at the file.
        int span = (int)Math.Min(RowsPerCheckpoint * (MaxRowBytes + 1L), _source.Length - checkpoint.Offset);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(span);

        try
        {
            int read = _source.ReadInto(checkpoint.Offset, buffer, span);
            ReadOnlySpan<byte> bytes = buffer.AsSpan(0, read);
            var rows = new TextRow[available];
            int position = 0;
            long line = checkpoint.Line;
            bool continuation = checkpoint.IsContinuation;

            for (int i = 0; i < rows.Length; i++)
            {
                (int rowLength, bool endsLine) = Split(bytes[position..]);
                rows[i] = new TextRow(blockIndex * RowsPerCheckpoint + i, checkpoint.Offset + position, rowLength, line, continuation);
                position += rowLength;
                continuation = !endsLine;

                if (endsLine)
                {
                    line++;
                }
            }

            lock (_gate)
            {
                // Blocks are kept only while the view is near them; a scroll through the whole
                // file must not leave every block it passed behind in memory.
                if (_blocks.TryAdd(blockIndex, rows))
                {
                    _blockOrder.Enqueue(blockIndex);
                    while (_blockOrder.Count > CachedBlocks)
                    {
                        _blocks.Remove(_blockOrder.Dequeue());
                    }
                }
            }

            return rows;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// How long the row at the start of <paramref name="bytes"/> is: up to and including the
    /// next line break, or <see cref="MaxRowBytes"/> when none comes before that — cut back to
    /// a character boundary, so a multi-byte character is never torn across two rows.
    /// </summary>
    private static (int Length, bool EndsLine) Split(ReadOnlySpan<byte> bytes)
    {
        int limit = Math.Min(bytes.Length, MaxRowBytes);
        int newline = bytes[..limit].IndexOf((byte)'\n');

        if (newline >= 0)
        {
            return (newline + 1, true);
        }

        if (bytes.Length <= MaxRowBytes)
        {
            return (bytes.Length, false);
        }

        int length = MaxRowBytes;
        while (length > 1 && (bytes[length] & 0xC0) == 0x80)
        {
            length--;
        }

        return (length, false);
    }
}
