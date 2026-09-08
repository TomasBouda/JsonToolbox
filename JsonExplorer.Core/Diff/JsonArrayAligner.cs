namespace JsonExplorer.Core.Diff;

/// <summary>What an alignment says happened to one position in an array.</summary>
public enum AlignmentOp
{
    /// <summary>The same element, on both sides.</summary>
    Match,

    /// <summary>Present on the left only.</summary>
    Delete,

    /// <summary>Present on the right only.</summary>
    Insert,
}

public readonly record struct AlignmentStep(AlignmentOp Op, int LeftIndex, int RightIndex);

/// <summary>
/// Works out which element of one array corresponds to which element of another.
/// </summary>
/// <remarks>
/// <para>
/// Comparing arrays position by position is wrong the moment anything is inserted or removed:
/// one new element at the front makes every element after it look changed. Trimming the head
/// and tail the two arrays agree on fixes the simple cases, but not a document where something
/// was inserted near the start <em>and</em> edited near the end — and that is the ordinary case.
/// </para>
/// <para>
/// So the elements are reduced to hashes and aligned by longest common subsequence, which is
/// the same thing a text diff does with lines. The hashes only decide which elements to pair
/// up; whether a pair actually matches is settled afterwards by comparing their bytes, so a
/// hash collision costs a wasted comparison rather than a missed difference.
/// </para>
/// </remarks>
public static class JsonArrayAligner
{
    /// <summary>
    /// Largest window this will align. The table is quadratic, so a limit is not optional;
    /// beyond it the caller falls back to matching by position and says that it did.
    /// </summary>
    public const int MaxWindow = 1_000;

    public static bool CanAlign(int leftCount, int rightCount) =>
        leftCount <= MaxWindow && rightCount <= MaxWindow;

    /// <summary>
    /// Aligns two sequences of element hashes, returning the steps in order.
    /// </summary>
    public static List<AlignmentStep> Align(ReadOnlySpan<ulong> left, ReadOnlySpan<ulong> right)
    {
        int n = left.Length;
        int m = right.Length;

        // lengths[i, j] is the longest common subsequence of left[i..] and right[j..].
        int[,] lengths = new int[n + 1, m + 1];

        for (int i = n - 1; i >= 0; i--)
        {
            for (int j = m - 1; j >= 0; j--)
            {
                lengths[i, j] = left[i] == right[j]
                    ? lengths[i + 1, j + 1] + 1
                    : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
            }
        }

        var steps = new List<AlignmentStep>(Math.Max(n, m));
        int x = 0;
        int y = 0;

        while (x < n && y < m)
        {
            if (left[x] == right[y])
            {
                steps.Add(new AlignmentStep(AlignmentOp.Match, x, y));
                x++;
                y++;
            }
            else if (lengths[x + 1, y] >= lengths[x, y + 1])
            {
                steps.Add(new AlignmentStep(AlignmentOp.Delete, x, -1));
                x++;
            }
            else
            {
                steps.Add(new AlignmentStep(AlignmentOp.Insert, -1, y));
                y++;
            }
        }

        while (x < n)
        {
            steps.Add(new AlignmentStep(AlignmentOp.Delete, x++, -1));
        }

        while (y < m)
        {
            steps.Add(new AlignmentStep(AlignmentOp.Insert, -1, y++));
        }

        return steps;
    }
}
