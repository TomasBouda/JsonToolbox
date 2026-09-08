using System.Buffers;
using JsonToolbox.Core.Documents;

namespace JsonToolbox.Core.Diff;

/// <summary>
/// Compares two byte ranges from two documents.
/// </summary>
/// <remarks>
/// This is what makes a structural diff affordable. Two subtrees whose bytes are identical are
/// identical, whatever they contain, so an entire branch can be dismissed without being parsed;
/// and two that differ almost always differ near their start, so the comparison stops after a
/// few bytes. The expensive case — reading a large range in full — happens only when the range
/// really is unchanged, which is precisely the work that buys the pruning.
/// </remarks>
internal static class JsonByteComparer
{
    private const int ChunkSize = 64 * 1024;

    public static bool RangesEqual(
        JsonSource left, long leftStart, long leftLength,
        JsonSource right, long rightStart, long rightLength,
        CancellationToken cancellationToken = default)
    {
        // Different lengths cannot be equal, and finding that out costs nothing.
        if (leftLength != rightLength)
        {
            return false;
        }

        if (leftLength <= 0)
        {
            return true;
        }

        // Most comparisons are of small values, and there are millions of them. Going through
        // a stream would cost a file handle and a buffer each time; reading straight out of
        // the mapping costs a copy.
        if (leftLength <= ChunkSize)
        {
            return SmallRangesEqual(left, leftStart, (int)leftLength, right, rightStart);
        }

        using Stream a = left.OpenRange(leftStart, leftLength);
        using Stream b = right.OpenRange(rightStart, rightLength);

        byte[] bufferA = ArrayPool<byte>.Shared.Rent(ChunkSize);
        byte[] bufferB = ArrayPool<byte>.Shared.Rent(ChunkSize);

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int readA = ReadBlock(a, bufferA);
                int readB = ReadBlock(b, bufferB);

                if (readA != readB)
                {
                    return false;
                }

                if (readA == 0)
                {
                    return true;
                }

                if (!bufferA.AsSpan(0, readA).SequenceEqual(bufferB.AsSpan(0, readB)))
                {
                    return false;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bufferA);
            ArrayPool<byte>.Shared.Return(bufferB);
        }
    }

    private static bool SmallRangesEqual(JsonSource left, long leftStart, int length, JsonSource right, long rightStart)
    {
        byte[] a = ArrayPool<byte>.Shared.Rent(length);
        byte[] b = ArrayPool<byte>.Shared.Rent(length);

        try
        {
            int readA = left.ReadInto(leftStart, a, length);
            int readB = right.ReadInto(rightStart, b, length);

            return readA == readB && a.AsSpan(0, readA).SequenceEqual(b.AsSpan(0, readB));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(a);
            ArrayPool<byte>.Shared.Return(b);
        }
    }

    /// <summary>
    /// Fills the buffer or reaches the end, so that a short read from one stream is never
    /// mistaken for a difference in length.
    /// </summary>
    private static int ReadBlock(Stream stream, byte[] buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
