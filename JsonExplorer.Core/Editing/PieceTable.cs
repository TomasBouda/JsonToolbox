using System.Buffers;
using JsonExplorer.Core.Documents;

namespace JsonExplorer.Core.Editing;

/// <summary>
/// A document as a sequence of pieces, each pointing either into the original file or into a
/// buffer of everything that has been typed since.
/// </summary>
/// <remarks>
/// <para>
/// The explorer identifies every value by its byte offset into a file it never copies. Editing
/// breaks that: change one character near the front and every offset behind it moves. Rewriting
/// a three hundred megabyte file on each keystroke is not an option, and neither is loading it
/// into memory — that is the whole thing this application avoids.
/// </para>
/// <para>
/// A piece table sidesteps both. The original stays untouched and read-only; new text is
/// appended to a second buffer that is never rewritten either; and the document is just an
/// ordered list of slices of the two. An edit splits at most two pieces and inserts one, so it
/// costs the same whether the file is a kilobyte or a gigabyte, and undo is the previous list.
/// </para>
/// </remarks>
public sealed class PieceTable
{
    private readonly JsonSource _original;
    private readonly List<Piece> _pieces = [];

    /// <summary>Logical start of each piece, so a read can find its piece by bisection.</summary>
    private long[] _starts = [];

    private byte[] _added = new byte[4096];
    private int _addedLength;

    public PieceTable(JsonSource original)
    {
        _original = original;

        if (original.Length > 0)
        {
            _pieces.Add(new Piece(FromAdded: false, 0, original.Length));
        }

        Reindex();
    }

    public long Length { get; private set; }

    /// <summary>True once anything has been changed.</summary>
    public bool IsModified { get; private set; }

    /// <summary>How many pieces the document is currently in, for diagnostics and tests.</summary>
    public int PieceCount => _pieces.Count;

    /// <summary>
    /// Replaces <paramref name="length"/> bytes at <paramref name="offset"/> with
    /// <paramref name="replacement"/>. An empty replacement deletes; a zero length inserts.
    /// </summary>
    public void Replace(long offset, long length, ReadOnlySpan<byte> replacement)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        if (offset + length > Length)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "The range runs past the end of the document.");
        }

        SplitAt(offset);
        SplitAt(offset + length);

        int from = IndexOfPieceStartingAt(offset);
        int to = IndexOfPieceStartingAt(offset + length);

        _pieces.RemoveRange(from, to - from);

        if (!replacement.IsEmpty)
        {
            long start = Append(replacement);
            _pieces.Insert(from, new Piece(FromAdded: true, start, replacement.Length));
        }

        IsModified = true;
        Reindex();
    }

    /// <summary>Copies a slice of the document into a buffer the caller already has.</summary>
    public int ReadInto(long offset, byte[] destination, int count)
    {
        if (count <= 0 || offset >= Length)
        {
            return 0;
        }

        count = (int)Math.Min(Math.Min(count, destination.Length), Length - offset);
        int written = 0;
        int index = FindPiece(offset);

        while (written < count && index < _pieces.Count)
        {
            Piece piece = _pieces[index];
            long pieceOffset = offset + written - _starts[index];
            int take = (int)Math.Min(piece.Length - pieceOffset, count - written);

            if (take > 0)
            {
                CopyFrom(piece, pieceOffset, destination, written, take);
                written += take;
            }

            index++;
        }

        return written;
    }

    /// <summary>Opens a forward-only stream over a range of the document.</summary>
    public Stream OpenRange(long offset, long length) => new PieceStream(this, offset, length);

    /// <summary>
    /// Writes the whole document to a stream, piece by piece.
    /// </summary>
    /// <remarks>
    /// This is the only moment the document is materialised, and even then it is streamed
    /// rather than assembled: saving a gigabyte costs a gigabyte of writing and nothing more.
    /// </remarks>
    public void WriteTo(Stream destination, CancellationToken cancellationToken = default)
    {
        byte[] buffer = new byte[64 * 1024];
        long position = 0;

        while (position < Length)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int read = ReadInto(position, buffer, buffer.Length);
            if (read <= 0)
            {
                break;
            }

            destination.Write(buffer, 0, read);
            position += read;
        }
    }

    /// <summary>
    /// Records that the document as it stands has been written to disk.
    /// </summary>
    /// <remarks>
    /// The pieces are left alone: undo still walks back through the edits, and doing so marks
    /// the document modified again, which is right — it no longer matches what was saved.
    /// </remarks>
    public void MarkSaved() => IsModified = false;

    /// <summary>Records that the document no longer matches what was written to disk.</summary>
    public void MarkModified() => IsModified = true;

    /// <summary>Captures the current arrangement, so an edit can be taken back.</summary>
    /// <remarks>
    /// Only the list of pieces is captured. The buffer of typed text is append-only and is
    /// never rewound, which is what lets a redo find its bytes exactly where it left them.
    /// </remarks>
    public PieceTableSnapshot Snapshot() => new([.. _pieces], IsModified);

    public void Restore(PieceTableSnapshot snapshot)
    {
        _pieces.Clear();
        _pieces.AddRange(snapshot.Pieces);
        IsModified = snapshot.IsModified;
        Reindex();
    }

    /// <summary>A remembered arrangement of pieces, for undo and redo.</summary>
    public sealed class PieceTableSnapshot
    {
        private readonly Piece[] _pieces;

        internal PieceTableSnapshot(Piece[] pieces, bool isModified)
        {
            _pieces = pieces;
            IsModified = isModified;
        }

        internal Piece[] Pieces => _pieces;

        internal bool IsModified { get; }
    }

    private void SplitAt(long offset)
    {
        if (offset <= 0 || offset >= Length)
        {
            return;
        }

        int index = FindPiece(offset);
        long within = offset - _starts[index];

        if (within == 0)
        {
            return;
        }

        Piece piece = _pieces[index];
        _pieces[index] = piece with { Length = within };
        _pieces.Insert(index + 1, new Piece(piece.FromAdded, piece.Start + within, piece.Length - within));
        Reindex();
    }

    /// <summary>
    /// The index of the piece that begins exactly at <paramref name="offset"/>, or the count
    /// when the offset is the end of the document.
    /// </summary>
    private int IndexOfPieceStartingAt(long offset)
    {
        if (offset >= Length)
        {
            return _pieces.Count;
        }

        int index = FindPiece(offset);
        return _starts[index] == offset ? index : index + 1;
    }

    private int FindPiece(long offset)
    {
        int low = 0;
        int high = _pieces.Count - 1;

        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            if (_starts[middle] <= offset)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        return low;
    }

    private long Append(ReadOnlySpan<byte> bytes)
    {
        if (_addedLength + bytes.Length > _added.Length)
        {
            int capacity = Math.Max(_added.Length * 2, _addedLength + bytes.Length);
            Array.Resize(ref _added, capacity);
        }

        long start = _addedLength;
        bytes.CopyTo(_added.AsSpan(_addedLength));
        _addedLength += bytes.Length;
        return start;
    }

    private void CopyFrom(Piece piece, long pieceOffset, byte[] destination, int destinationOffset, int count)
    {
        if (piece.FromAdded)
        {
            _added.AsSpan((int)(piece.Start + pieceOffset), count).CopyTo(destination.AsSpan(destinationOffset));
            return;
        }

        // The original is read through its own mapping, so nothing is copied into memory that
        // the operating system has not already paged in. It reads from index zero of whatever
        // buffer it is given, hence the scratch when the destination is already part-filled.
        byte[] scratch = ArrayPool<byte>.Shared.Rent(count);
        try
        {
            int read = _original.ReadInto(piece.Start + pieceOffset, scratch, count);
            scratch.AsSpan(0, read).CopyTo(destination.AsSpan(destinationOffset));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    private void Reindex()
    {
        if (_starts.Length < _pieces.Count)
        {
            _starts = new long[Math.Max(_pieces.Count, 8)];
        }

        long running = 0;
        for (int i = 0; i < _pieces.Count; i++)
        {
            _starts[i] = running;
            running += _pieces[i].Length;
        }

        Length = running;
    }

    /// <summary>A slice of either the original file or the buffer of typed text.</summary>
    internal readonly record struct Piece(bool FromAdded, long Start, long Length);

    /// <summary>A forward-only view of a range of the document.</summary>
    private sealed class PieceStream(PieceTable table, long offset, long length) : Stream
    {
        private long _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int bufferOffset, int count)
        {
            long remaining = length - _position;
            if (remaining <= 0)
            {
                return 0;
            }

            count = (int)Math.Min(count, remaining);

            byte[] scratch = bufferOffset == 0 ? buffer : new byte[count];
            int read = table.ReadInto(offset + _position, scratch, count);

            if (bufferOffset != 0 && read > 0)
            {
                scratch.AsSpan(0, read).CopyTo(buffer.AsSpan(bufferOffset));
            }

            _position += read;
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long o, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int bufferOffset, int count) => throw new NotSupportedException();
    }
}
