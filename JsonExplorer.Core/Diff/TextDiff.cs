namespace JsonExplorer.Core.Diff;

/// <summary>
/// How much of two strings is shared at the front and at the back.
/// </summary>
/// <param name="PrefixLength">Characters the two strings begin with in common.</param>
/// <param name="SuffixLength">Characters they end with in common, not overlapping the prefix.</param>
public readonly record struct TextDiffParts(int PrefixLength, int SuffixLength)
{
    /// <summary>The part of <paramref name="text"/> that differs, given its length.</summary>
    public (int Start, int Length) MiddleOf(string text) =>
        (PrefixLength, Math.Max(0, text.Length - PrefixLength - SuffixLength));
}

/// <summary>
/// Finds the part of a value that actually changed.
/// </summary>
/// <remarks>
/// <para>
/// Saying that <c>"Sahakar Nagar"</c> became <c>"Sahakar Nagars"</c> is true and unhelpful:
/// what changed is one letter, and the eye should not have to find it. Trimming the shared
/// beginning and the shared end leaves exactly the edit, which is what gets marked.
/// </para>
/// <para>
/// This is deliberately not a character-by-character alignment. A value rewritten from scratch
/// would come out of one as a confetti of matched letters that reads as noise; here it comes
/// out as one replaced stretch, which is what happened.
/// </para>
/// </remarks>
public static class TextDiff
{
    public static TextDiffParts Compare(string? left, string? right)
    {
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
        {
            return new TextDiffParts(0, 0);
        }

        int max = Math.Min(left.Length, right.Length);

        int prefix = 0;
        while (prefix < max && left[prefix] == right[prefix])
        {
            prefix++;
        }

        prefix = BackOffSurrogate(left, prefix);

        int suffix = 0;
        int remaining = max - prefix;
        while (suffix < remaining && left[^(suffix + 1)] == right[^(suffix + 1)])
        {
            suffix++;
        }

        // The suffix is measured from the end, so a split in the middle of a pair shows up as
        // a low surrogate at the boundary.
        if (suffix > 0 && suffix < left.Length && char.IsLowSurrogate(left[^suffix]))
        {
            suffix--;
        }

        return new TextDiffParts(prefix, suffix);
    }

    /// <summary>
    /// Pulls a boundary back off the second half of a surrogate pair, so that marking a
    /// difference never cuts a character in two.
    /// </summary>
    private static int BackOffSurrogate(string text, int index) =>
        index > 0 && index < text.Length && char.IsLowSurrogate(text[index]) ? index - 1 : index;
}
