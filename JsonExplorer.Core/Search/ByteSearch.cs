namespace JsonExplorer.Core.Search;

/// <summary>
/// Substring search directly over UTF-8 bytes.
/// </summary>
/// <remarks>
/// Searching a large document by decoding every value into a <see cref="string"/> first
/// allocates once per value and spends most of its time in the decoder rather than in the
/// comparison. Since the overwhelmingly common needle is plain ASCII, matching against the
/// raw bytes avoids both costs; anything else falls back to decoding, where correctness
/// matters more than speed.
/// </remarks>
internal static class ByteSearch
{
    public static bool IsAscii(ReadOnlySpan<byte> value)
    {
        foreach (byte b in value)
        {
            if (b > 0x7F)
            {
                return false;
            }
        }

        return true;
    }

    public static int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle, bool ignoreCase)
    {
        if (needle.IsEmpty || needle.Length > haystack.Length)
        {
            return -1;
        }

        if (!ignoreCase)
        {
            return haystack.IndexOf(needle);
        }

        byte firstUpper = ToUpperAscii(needle[0]);
        int last = haystack.Length - needle.Length;

        for (int i = 0; i <= last; i++)
        {
            if (ToUpperAscii(haystack[i]) != firstUpper)
            {
                continue;
            }

            if (MatchesAt(haystack[i..], needle))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// True when the match at <paramref name="index"/> is not glued to surrounding word
    /// characters, which is what "whole word" means to a person reading a value.
    /// </summary>
    public static bool IsWholeWord(ReadOnlySpan<byte> haystack, int index, int length)
    {
        if (index > 0 && IsWordByte(haystack[index - 1]))
        {
            return false;
        }

        int after = index + length;
        return after >= haystack.Length || !IsWordByte(haystack[after]);
    }

    private static bool MatchesAt(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        for (int j = 1; j < needle.Length; j++)
        {
            if (ToUpperAscii(haystack[j]) != ToUpperAscii(needle[j]))
            {
                return false;
            }
        }

        return true;
    }

    private static byte ToUpperAscii(byte b) => b is >= (byte)'a' and <= (byte)'z' ? (byte)(b - 32) : b;

    private static bool IsWordByte(byte b) =>
        b is >= (byte)'0' and <= (byte)'9' or
        >= (byte)'a' and <= (byte)'z' or
        >= (byte)'A' and <= (byte)'Z' or
        (byte)'_' ||
        b > 0x7F;
}
