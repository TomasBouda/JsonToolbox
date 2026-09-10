using System.Text.Json;

namespace JsonToolbox.Core.Documents;

/// <summary>
/// Decides whether a file holds one document or a sequence of them.
/// </summary>
/// <remarks>
/// <para>
/// JSON Lines and its relatives write a record per line with nothing wrapping them. Read as a
/// single document such a file yields its first record and nothing else — no error, no mention
/// of the rest — which for a tool whose whole claim is that you can trust what it shows is worse
/// than refusing to open it.
/// </para>
/// <para>
/// Telling the two apart cannot mean reading the file: the question is asked when a document is
/// opened, and opening is meant to cost a few milliseconds whatever the size. So only the head
/// is read. If a complete value ends inside it and something follows, the file is a sequence. If
/// the first value has not finished by the end of the head, it is one large document — a
/// gigabyte-long record on a single line is not JSON Lines, whatever the extension says.
/// </para>
/// </remarks>
public static class JsonSequenceProbe
{
    /// <summary>
    /// How much of the file is read to decide.
    /// </summary>
    /// <remarks>
    /// Generous next to a line of JSON Lines, which is a record; small next to the documents
    /// this application exists for, so the cost does not grow with the file.
    /// </remarks>
    public const int HeadBytes = 64 * 1024;

    public static bool LooksLikeSequence(JsonSource source, long start)
    {
        int length = (int)Math.Min(HeadBytes, source.Length - start);
        if (length <= 0)
        {
            return false;
        }

        byte[] head = source.ReadSlice(start, length);
        bool complete = length < HeadBytes;

        try
        {
            var reader = new Utf8JsonReader(
                head,
                isFinalBlock: complete,
                state: new JsonReaderState(new JsonReaderOptions { AllowMultipleValues = true }));

            if (!reader.Read())
            {
                return false;
            }

            // Skip needs the whole value in the buffer, and says so by returning false rather
            // than by throwing when the buffer runs out mid-value.
            if (!reader.TrySkip())
            {
                return false;
            }

            return reader.Read();
        }
        catch (JsonException)
        {
            // Not readable as JSON at all. Whatever is wrong with it is for the scan to report
            // in its own words, at the position where it happens.
            return false;
        }
    }
}
