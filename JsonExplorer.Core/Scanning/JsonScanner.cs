using System.Buffers;
using System.Text.Json;

namespace JsonExplorer.Core.Scanning;

/// <summary>
/// How permissive the scanner is about input that is not strictly valid JSON.
/// </summary>
public enum JsonScanStrictness
{
    /// <summary>
    /// Reject comments and trailing commas. This is the mode used when reporting problems,
    /// because the point is to tell the user their file is not portable JSON.
    /// </summary>
    Strict,

    /// <summary>
    /// Tolerate comments and trailing commas so the document can still be explored.
    /// Used after the user has acknowledged the diagnostics and just wants to read the data.
    /// </summary>
    Lenient,
}

public sealed record JsonScanOptions
{
    public JsonScanStrictness Strictness { get; init; } = JsonScanStrictness.Strict;

    /// <summary>
    /// Nesting limit. Deliberately far above the reader default of 64, because real
    /// documents (and hand-rolled serializers) do exceed it, and refusing to open such a
    /// file is worse than reporting how deep it goes.
    /// </summary>
    public int MaxDepth { get; init; } = 512;

    /// <summary>Size of the rolling read buffer. Grown automatically for oversized tokens.</summary>
    public int BufferSize { get; init; } = 128 * 1024;

    internal JsonReaderOptions ToReaderOptions() => new()
    {
        MaxDepth = MaxDepth,
        CommentHandling = Strictness == JsonScanStrictness.Lenient
            ? JsonCommentHandling.Skip
            : JsonCommentHandling.Disallow,
        AllowTrailingCommas = Strictness == JsonScanStrictness.Lenient,
    };
}

/// <summary>
/// Reads a JSON document of any size in a single forward pass, without ever holding more
/// than one buffer of it in memory.
/// </summary>
/// <remarks>
/// <para>
/// This is the one place in the application that touches the chunked reading protocol of
/// <c>Utf8JsonReader</c>, which is fiddly enough to be worth isolating: the reader consumes
/// whole tokens only, so whatever it leaves behind has to be moved to the front of the
/// buffer and topped up from the stream, and a token larger than the buffer forces the
/// buffer to grow instead.
/// </para>
/// <para>
/// Everything expensive the explorer does — indexing, searching, validating, profiling —
/// is a visitor over this one scan, so no feature needs its own parser and none of them
/// need the document to fit in RAM.
/// </para>
/// </remarks>
public static class JsonScanner
{
    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    /// <summary>
    /// Scans <paramref name="stream"/> from its current position to its end.
    /// </summary>
    /// <param name="baseOffset">
    /// Absolute offset in the underlying document that the current stream position
    /// corresponds to. Non-zero when scanning a slice, so the offsets reported to the
    /// visitor stay meaningful for the whole file.
    /// </param>
    /// <returns>The number of bytes read.</returns>
    /// <exception cref="JsonScanException">The document is not valid JSON.</exception>
    public static long Scan(
        Stream stream,
        JsonScanVisitor visitor,
        long baseOffset = 0,
        JsonScanOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(visitor);

        options ??= new JsonScanOptions();
        JsonReaderOptions readerOptions = options.ToReaderOptions();

        visitor.OnScanStarting(TryGetLength(stream));

        byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Max(options.BufferSize, 4096));
        var state = new JsonReaderState(readerOptions);

        // Absolute offset of buffer[0] within the document.
        long bufferStart = baseOffset;
        int dataLength = 0;
        bool completed = false;

        // A byte order mark only ever appears at the very start of a file, so a slice scan
        // starting mid-document must not look for one.
        bool bomHandled = baseOffset != 0;

        // End of the last token that parsed cleanly. On failure this is where the good part
        // of the document stops, and therefore where an explanation has to start looking.
        long lastGoodOffset = baseOffset;

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                dataLength += FillBuffer(stream, buffer, dataLength);
                bool isFinalBlock = dataLength < buffer.Length;

                if (!bomHandled && (dataLength >= Utf8Bom.Length || isFinalBlock))
                {
                    // The reader treats a byte order mark as a stray token, so the file would
                    // fail to parse for a reason that has nothing to do with its content.
                    if (dataLength >= Utf8Bom.Length && buffer.AsSpan(0, Utf8Bom.Length).SequenceEqual(Utf8Bom))
                    {
                        dataLength -= Utf8Bom.Length;
                        Buffer.BlockCopy(buffer, Utf8Bom.Length, buffer, 0, dataLength);
                        bufferStart += Utf8Bom.Length;
                    }

                    bomHandled = true;
                }

                long consumed = ReadChunk(
                    buffer.AsSpan(0, dataLength),
                    isFinalBlock,
                    bufferStart,
                    visitor,
                    ref state,
                    ref lastGoodOffset,
                    out bool visitorStopped);

                bufferStart += consumed;
                int leftover = dataLength - (int)consumed;

                if (visitorStopped || isFinalBlock)
                {
                    completed = isFinalBlock && !visitorStopped;
                    break;
                }

                if (leftover > 0 && consumed > 0)
                {
                    Buffer.BlockCopy(buffer, (int)consumed, buffer, 0, leftover);
                }

                if (consumed == 0 && leftover == buffer.Length)
                {
                    // A single token is longer than the whole buffer — a very long string,
                    // typically an embedded base64 blob. Double the buffer and retry.
                    byte[] larger = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                    Buffer.BlockCopy(buffer, 0, larger, 0, leftover);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = larger;
                }

                dataLength = leftover;
            }
        }
        catch (JsonException ex)
        {
            throw new JsonScanException(
                ex.Message,
                byteOffset: lastGoodOffset,
                lineNumber: (ex.LineNumber ?? 0) + 1,
                bytePositionInLine: ex.BytePositionInLine ?? 0,
                ex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            visitor.OnScanFinished(bufferStart - baseOffset, completed);
        }

        return bufferStart - baseOffset;
    }

    private static long ReadChunk(
        ReadOnlySpan<byte> span,
        bool isFinalBlock,
        long bufferStart,
        JsonScanVisitor visitor,
        ref JsonReaderState state,
        ref long lastGoodOffset,
        out bool visitorStopped)
    {
        var reader = new Utf8JsonReader(span, isFinalBlock, state);
        visitorStopped = false;

        while (reader.Read())
        {
            visitor.OnToken(ref reader, bufferStart + reader.TokenStartIndex, bufferStart + reader.BytesConsumed);

            // Written before the next read, so that if that read throws, the caller still has
            // the position where the document last made sense.
            lastGoodOffset = bufferStart + reader.BytesConsumed;

            if (!visitor.WantsMoreTokens)
            {
                visitorStopped = true;
                break;
            }
        }

        state = reader.CurrentState;
        return reader.BytesConsumed;
    }

    /// <summary>
    /// Reads until the buffer is full or the stream ends, so that a short read from the
    /// underlying stream is never mistaken for the end of the document.
    /// </summary>
    private static int FillBuffer(Stream stream, byte[] buffer, int offset)
    {
        int total = 0;
        while (offset + total < buffer.Length)
        {
            int read = stream.Read(buffer, offset + total, buffer.Length - offset - total);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static long TryGetLength(Stream stream)
    {
        try
        {
            return stream.CanSeek ? stream.Length - stream.Position : -1;
        }
        catch (NotSupportedException)
        {
            return -1;
        }
    }
}
