using JsonExplorer.Core.Diff;

namespace JsonExplorer.Core.Tests;

public class TextDiffTests
{
    /// <summary>Renders the comparison as text so the expectation reads like the result.</summary>
    private static string Mark(string left, string right)
    {
        TextDiffParts parts = TextDiff.Compare(left, right);
        (int leftStart, int leftLength) = parts.MiddleOf(left);
        (int rightStart, int rightLength) = parts.MiddleOf(right);

        string l = left.Insert(leftStart + leftLength, "]").Insert(leftStart, "[");
        string r = right.Insert(rightStart + rightLength, "]").Insert(rightStart, "[");
        return $"{l} | {r}";
    }

    [Fact]
    public void Marks_a_letter_added_at_the_end()
    {
        Assert.Equal("\"Sahakar Nagar[]\" | \"Sahakar Nagar[s]\"", Mark("\"Sahakar Nagar\"", "\"Sahakar Nagars\""));
    }

    [Fact]
    public void Marks_a_digit_added_inside_a_quoted_number()
    {
        Assert.Equal("\"386[]\" | \"386[8]\"", Mark("\"386\"", "\"3868\""));
    }

    [Fact]
    public void Marks_a_replacement_in_the_middle()
    {
        Assert.Equal("ab[c]def | ab[X]def", Mark("abcdef", "abXdef"));
    }

    [Fact]
    public void Marks_a_removal()
    {
        Assert.Equal("hello [big ]world | hello []world", Mark("hello big world", "hello world"));
    }

    [Fact]
    public void Marks_the_whole_value_when_nothing_is_shared()
    {
        Assert.Equal("[abc] | [xyz]", Mark("abc", "xyz"));
    }

    [Fact]
    public void Identical_values_have_nothing_to_mark()
    {
        TextDiffParts parts = TextDiff.Compare("same", "same");

        Assert.Equal(0, parts.MiddleOf("same").Length);
    }

    [Fact]
    public void A_prefix_of_the_other_marks_only_the_extra()
    {
        Assert.Equal("aaa[] | aaa[a]", Mark("aaa", "aaaa"));
    }

    [Fact]
    public void The_prefix_and_suffix_never_overlap()
    {
        // Both strings are runs of the same letter, so a naive suffix scan would count
        // characters the prefix already claimed and produce a negative middle.
        TextDiffParts parts = TextDiff.Compare("aaaa", "aa");

        Assert.True(parts.PrefixLength + parts.SuffixLength <= 2);
        Assert.Equal(2, parts.MiddleOf("aaaa").Length);
        Assert.Equal(0, parts.MiddleOf("aa").Length);
    }

    [Fact]
    public void A_boundary_never_splits_a_surrogate_pair()
    {
        // Two different emoji sharing nothing but their leading surrogate.
        const string left = "x\U0001F600y";
        const string right = "x\U0001F601y";

        TextDiffParts parts = TextDiff.Compare(left, right);
        (int start, int length) = parts.MiddleOf(left);

        Assert.False(char.IsLowSurrogate(left[start]));
        Assert.False(char.IsHighSurrogate(left[start + length - 1]));
    }

    [Fact]
    public void An_empty_side_marks_nothing()
    {
        Assert.Equal(new TextDiffParts(0, 0), TextDiff.Compare("abc", string.Empty));
        Assert.Equal(new TextDiffParts(0, 0), TextDiff.Compare(null, "abc"));
    }
}
