using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;

namespace JsonToolbox.App.ViewModels;

/// <summary>
/// The term the last search ran with, shared by every control that marks it up.
/// </summary>
/// <remarks>
/// One object rather than a copy of the query on every row: the tree can hold thousands of
/// rows, and when the term changes they all have to be redrawn at once. Passing the same
/// instance down means a single notification reaches every control that is currently visible.
/// </remarks>
public sealed partial class SearchHighlight : ObservableObject
{
    private Regex? _regex;

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private bool _caseSensitive;

    [ObservableProperty]
    private bool _useRegex;

    public bool IsActive => Query.Length > 0;

    /// <summary>Records the query a search just ran with, so its hits can be marked up.</summary>
    public void Set(string query, bool caseSensitive, bool useRegex)
    {
        Query = query;
        CaseSensitive = caseSensitive;
        UseRegex = useRegex;
        _regex = null;
        OnPropertyChanged(nameof(IsActive));
    }

    public void Clear() => Set(string.Empty, false, false);

    /// <summary>
    /// Finds every match of the current term in <paramref name="text"/>.
    /// </summary>
    /// <remarks>
    /// An invalid regular expression yields no matches rather than an exception: the term
    /// comes from a box the user is still typing in, and a half-written pattern must not take
    /// down the row that is trying to draw itself.
    /// </remarks>
    public IReadOnlyList<Range> Find(string? text)
    {
        if (string.IsNullOrEmpty(text) || !IsActive)
        {
            return [];
        }

        try
        {
            return UseRegex ? FindByRegex(text) : FindBySubstring(text);
        }
        catch (ArgumentException)
        {
            return [];
        }
        catch (RegexMatchTimeoutException)
        {
            return [];
        }
    }

    private List<Range> FindByRegex(string text)
    {
        _regex ??= new Regex(
            Query,
            RegexOptions.CultureInvariant | (CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase),
            TimeSpan.FromMilliseconds(250));

        var ranges = new List<Range>();
        foreach (Match match in _regex.Matches(text))
        {
            // A zero-width match would loop forever and marks up nothing worth seeing.
            if (match.Length > 0)
            {
                ranges.Add(new Range(match.Index, match.Index + match.Length));
            }
        }

        return ranges;
    }

    private List<Range> FindBySubstring(string text)
    {
        StringComparison comparison = CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var ranges = new List<Range>();

        for (int i = 0; i < text.Length;)
        {
            int found = text.IndexOf(Query, i, comparison);
            if (found < 0)
            {
                break;
            }

            ranges.Add(new Range(found, found + Query.Length));
            i = found + Query.Length;
        }

        return ranges;
    }
}
