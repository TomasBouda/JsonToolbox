using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JsonToolbox.Core.Diff;
using JsonToolbox.Core.Documents;
using JsonToolbox.Core.Model;

namespace JsonToolbox.App.ViewModels;

/// <summary>One side of a diff row: what that document has at this position, if anything.</summary>
public sealed class DiffSideViewModel(JsonNodeInfo node, string header, bool isExpanded, string? counterpart, bool isRight)
{
    public JsonNodeInfo Node { get; } = node;

    public string Header { get; } = header;

    public string? Preview => Node.Preview;

    /// <summary>
    /// The same value as the other document has it, so the row can mark the part that changed
    /// rather than leaving both strings to be read and compared by eye.
    /// </summary>
    public string? Counterpart { get; } = counterpart;

    /// <summary>Marks are green on the right, where things arrived, and red on the left.</summary>
    public bool IsRight { get; } = isRight;

    public bool IsLeft => !IsRight;

    public bool ShowsPreview => !string.IsNullOrEmpty(Node.Preview) && !(Node.IsContainer && isExpanded);

    public string? CountBadge => Node.IsContainer && Node.ChildCount >= 0
        ? Node.ChildCount.ToString("N0")
        : null;

    public bool IsString => Node.Kind == JsonKind.String;

    public bool IsNumber => Node.Kind.IsNumber();

    public bool IsBoolean => Node.Kind.IsBoolean();

    public bool IsNull => Node.Kind == JsonKind.Null;

    public bool IsContainerKind => Node.IsContainer;
}

/// <summary>
/// One line of the side-by-side view: a value on the left beside its counterpart on the right.
/// </summary>
public sealed partial class JsonDiffRowViewModel : ObservableObject
{
    private const double IndentPerLevel = 16;

    private readonly JsonDiffTree? _tree;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Left))]
    [NotifyPropertyChangedFor(nameof(Right))]
    private bool _isExpanded;

    partial void OnIsExpandedChanged(bool value)
    {
        // The two sides describe whether they show a collapsed preview, so they are rebuilt
        // when the row opens or closes.
        _left = null;
        _right = null;
    }

    /// <summary>Creates the trailing row of a level that is only partly shown.</summary>
    private JsonDiffRowViewModel(JsonDiffTree tree, JsonDiffRowViewModel owner, string text, bool canShowMore)
    {
        _tree = tree;
        Depth = owner.Depth + 1;
        Owner = owner;
        MoreText = text;
        IsMoreRow = true;
        CanShowMore = canShowMore;
        State = DiffState.Unchanged;
    }

    public JsonDiffRowViewModel(JsonDiffTree tree, JsonDiffPair pair, int depth)
    {
        _tree = tree;
        Pair = pair;
        Depth = depth;
        State = pair.State;
        Header = pair.Name ?? (depth == 0 ? "$" : $"[{pair.Index}]");
    }

    public JsonDiffPair Pair { get; }

    public int Depth { get; }

    public DiffState State { get; }

    public string Header { get; } = string.Empty;

    public Thickness IndentMargin => new(Depth * IndentPerLevel, 0, 0, 0);

    // Built once rather than on every binding evaluation: a virtualised list asks for these
    // repeatedly as rows scroll past.
    private DiffSideViewModel? _left;
    private DiffSideViewModel? _right;

    public DiffSideViewModel? Left => _left ??= Pair.Left is { } node
        ? new DiffSideViewModel(node, Header, IsExpanded, Pair.Right?.Preview, isRight: false)
        : null;

    public DiffSideViewModel? Right => _right ??= Pair.Right is { } node
        ? new DiffSideViewModel(node, Header, IsExpanded, Pair.Left?.Preview, isRight: true)
        : null;

    public bool HasLeft => Pair.HasLeft;

    public bool HasRight => Pair.HasRight;

    public bool CanExpand => !IsMoreRow && Pair.CanExpand;

    /// <summary>
    /// The expander is drawn beside whichever side actually has a value, so a row that exists
    /// on one side only does not leave an arrow floating in an empty column.
    /// </summary>
    public bool ShowsLeftExpander => CanExpand && HasLeft;

    public bool ShowsRightExpander => CanExpand && !HasLeft;

    // The four states drive the row's tint through style classes rather than through code,
    // so the colours stay in the theme and follow light and dark.
    public bool IsAdded => State == DiffState.Added;

    public bool IsRemoved => State == DiffState.Removed;

    public bool IsChanged => State == DiffState.Changed;

    public bool IsUnchanged => State == DiffState.Unchanged;

    // ---- The "show more" row --------------------------------------------------------

    public bool IsMoreRow { get; }

    public JsonDiffRowViewModel? Owner { get; }

    public string? MoreText { get; }

    public bool CanShowMore { get; }

    internal static JsonDiffRowViewModel CreateMoreRow(JsonDiffTree tree, JsonDiffRowViewModel owner, string text, bool canShowMore) =>
        new(tree, owner, text, canShowMore);

    [RelayCommand]
    private Task ToggleAsync() => _tree?.ToggleAsync(this) ?? Task.CompletedTask;

    [RelayCommand]
    private void ShowMoreChildren()
    {
        if (Owner is { } owner)
        {
            _tree?.ShowMore(owner);
        }
    }
}

/// <summary>
/// The two documents shown as one flat list of paired rows.
/// </summary>
/// <remarks>
/// <para>
/// A side-by-side diff has to keep its two halves lined up, and the usual way — two panels
/// with their scrolling tied together — is a lot of machinery that drifts the moment the two
/// sides have different row heights. Here there is only one list, and each of its rows draws
/// two columns. Alignment is then not something to maintain: a row is a pair.
/// </para>
/// <para>
/// Pairs are worked out one level at a time, as they are opened, by the same code that
/// produces the list of findings. So the two views can never disagree about what corresponds
/// to what, and a document too large to hold in memory can still be compared.
/// </para>
/// </remarks>
public sealed partial class JsonDiffTree : ObservableObject
{
    private const int PageSize = 500;

    private readonly JsonDiffPairing _pairing;
    private readonly Dictionary<JsonDiffRowViewModel, LevelState> _levels = [];

    [ObservableProperty]
    private bool _changesOnly = true;

    /// <summary>
    /// Narrows the view to the rows mentioning this text, in a key or in either value.
    /// </summary>
    /// <remarks>
    /// A comparison of two large documents answers "what changed" with hundreds of rows, and the
    /// question that follows is always narrower than that — what changed about the price, about
    /// this record, about anything called `id`. This is that second question. It is a filter over
    /// the differences rather than a search of the two files: what it can hide is what the
    /// comparison found, which is the set somebody is looking through at this point.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearch))]
    private string _searchText = string.Empty;

    /// <summary>How many rows the text was found in, once it has been looked for.</summary>
    [ObservableProperty]
    private int _matchCount;

    public bool HasSearch => !string.IsNullOrWhiteSpace(SearchText);

    public JsonDiffTree(IndexedJsonDocument left, IndexedJsonDocument right, DiffOptions? options = null)
    {
        _pairing = new JsonDiffPairing(left, right, options);

        LeftName = left.DisplayName;
        RightName = right.DisplayName;

        Root = new JsonDiffRowViewModel(
            this,
            new JsonDiffPair(left.Root, right.Root, _pairing.Classify(left.Root, right.Root), null, 0),
            depth: 0);

        Rows.Add(Root);
    }

    public string LeftName { get; }

    public string RightName { get; }

    public JsonDiffRowViewModel Root { get; }

    public ObservableRangeCollection<JsonDiffRowViewModel> Rows { get; } = [];

    public Task ToggleAsync(JsonDiffRowViewModel row, CancellationToken cancellationToken = default) =>
        row.IsExpanded ? CollapseTask(row) : ExpandAsync(row, cancellationToken);

    private Task CollapseTask(JsonDiffRowViewModel row)
    {
        Collapse(row);
        return Task.CompletedTask;
    }

    public async Task ExpandAsync(JsonDiffRowViewModel row, CancellationToken cancellationToken = default)
    {
        if (row.IsExpanded || !row.CanExpand)
        {
            return;
        }

        LevelState state = await LoadAsync(row, cancellationToken).ConfigureAwait(true);

        row.IsExpanded = true;
        state.Reset();
        InsertPage(row, state);
    }

    public void Collapse(JsonDiffRowViewModel row)
    {
        if (!row.IsExpanded)
        {
            return;
        }

        int start = Rows.IndexOf(row);
        if (start < 0)
        {
            return;
        }

        int end = start + 1;
        while (end < Rows.Count && Rows[end].Depth > row.Depth)
        {
            JsonDiffRowViewModel child = Rows[end];
            child.IsExpanded = false;

            if (_levels.TryGetValue(child, out LevelState? nested))
            {
                nested.Reset();
            }

            end++;
        }

        Rows.RemoveRange(start + 1, end - start - 1);
        row.IsExpanded = false;
        _levels[row].Reset();
    }

    public void ShowMore(JsonDiffRowViewModel row)
    {
        if (_levels.TryGetValue(row, out LevelState? state) && row.IsExpanded)
        {
            InsertPage(row, state);
        }
    }

    /// <summary>
    /// Opens every level that leads to a difference, so the view starts on the changes rather
    /// than on a closed root.
    /// </summary>
    /// <remarks>
    /// Bounded on purpose: a document with differences everywhere would otherwise open into
    /// tens of thousands of rows, which is not a view of anything.
    /// </remarks>
    public async Task ExpandChangesAsync(int maxRows = 2_000, CancellationToken cancellationToken = default)
    {
        for (int i = 0; i < Rows.Count && Rows.Count < maxRows; i++)
        {
            JsonDiffRowViewModel row = Rows[i];
            if (row is not { IsMoreRow: false, IsExpanded: false, CanExpand: true })
            {
                continue;
            }

            // A changed container is opened because the change is somewhere inside it. While a
            // search is on, a whole record that arrived or left is opened as well: whether
            // anything in it mentions the text cannot be decided without looking, and leaving it
            // shut would list a container under a search that has nothing to do with it.
            bool worthOpening = row.State == DiffState.Changed
                || (HasSearch && row.State != DiffState.Unchanged);

            if (worthOpening)
            {
                await ExpandAsync(row, cancellationToken).ConfigureAwait(true);
            }
        }
    }

    partial void OnChangesOnlyChanged(bool value) => Rebuild();

    partial void OnSearchTextChanged(string value) => Rebuild();

    private void Rebuild()
    {
        // Both filters decide which rows are built, so the tree is built again from the root.
        JsonDiffRowViewModel root = Root;
        Collapse(root);
        _levels.Clear();
        _ = RebuildAsync(root);
    }

    private async Task RebuildAsync(JsonDiffRowViewModel root)
    {
        await ExpandAsync(root).ConfigureAwait(true);
        await ExpandChangesAsync().ConfigureAwait(true);

        PruneBranchesWithNothingInThem();
        MatchCount = Rows.Count(row => !row.IsMoreRow && Matches(row.Pair));
    }

    /// <summary>Whether a pair mentions the text being searched for.</summary>
    private bool Matches(JsonDiffPair pair) =>
        !HasSearch
        || Contains(pair.Name)
        || Contains(pair.Left?.Preview)
        || Contains(pair.Right?.Preview);

    private bool Contains(string? text) =>
        text is not null && text.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Removes the containers a search opened and then emptied.
    /// </summary>
    /// <remarks>
    /// A container is kept while the rows are built, because whether anything inside it matches
    /// is not known until it has been opened. Once it has, one that neither matches itself nor
    /// holds anything that does is a heading over nothing. Walking backwards is what makes one
    /// pass enough: a container is reached after the children that would have kept it, so
    /// emptying one empties its parent in time to be seen.
    /// </remarks>
    private void PruneBranchesWithNothingInThem()
    {
        if (!HasSearch)
        {
            return;
        }

        for (int i = Rows.Count - 1; i > 0; i--)
        {
            JsonDiffRowViewModel row = Rows[i];

            if (row.IsMoreRow || !row.IsExpanded || Matches(row.Pair))
            {
                continue;
            }

            bool holdsSomething = i + 1 < Rows.Count && Rows[i + 1].Depth > row.Depth;
            if (!holdsSomething)
            {
                Rows.RemoveRange(i, 1);
            }
        }
    }

    private async Task<LevelState> LoadAsync(JsonDiffRowViewModel row, CancellationToken cancellationToken)
    {
        if (_levels.TryGetValue(row, out LevelState? existing))
        {
            return existing;
        }

        JsonDiffPair pair = row.Pair;
        List<JsonDiffPair> pairs = await Task
            .Run(() => _pairing.PairChildren(pair.Left, pair.Right), cancellationToken)
            .ConfigureAwait(true);

        var state = new LevelState { Pairs = pairs };
        _levels[row] = state;
        return state;
    }

    private void InsertPage(JsonDiffRowViewModel row, LevelState state)
    {
        int anchor = Rows.IndexOf(row);
        if (anchor < 0)
        {
            return;
        }

        int insertAt = anchor + 1;
        while (insertAt < Rows.Count && Rows[insertAt].Depth > row.Depth)
        {
            insertAt++;
        }

        if (state.MoreRow is not null)
        {
            int moreIndex = Rows.IndexOf(state.MoreRow);
            if (moreIndex >= 0)
            {
                Rows.RemoveRange(moreIndex, 1);
                insertAt--;
            }

            state.MoreRow = null;
        }

        var page = new List<JsonDiffRowViewModel>(PageSize);
        while (state.Shown < state.Pairs.Count && page.Count < PageSize)
        {
            JsonDiffPair pair = state.Pairs[state.Shown++];

            // Unchanged values are the bulk of any comparison; hiding them is what makes the
            // changes findable at all.
            if (ChangesOnly && pair.State == DiffState.Unchanged)
            {
                continue;
            }

            // A value has to mention the text to survive a search. A container does not: what is
            // inside it has not been read yet, so it is kept and emptied afterwards if nothing
            // in it turned out to match.
            if (!Matches(pair) && !pair.CanExpand)
            {
                continue;
            }

            page.Add(new JsonDiffRowViewModel(this, pair, row.Depth + 1));
        }

        state.Rows.AddRange(page);

        List<JsonDiffRowViewModel> toInsert = [.. page];
        if (CreateMoreRow(row, state) is { } moreRow)
        {
            state.MoreRow = moreRow;
            toInsert.Add(moreRow);
        }

        Rows.InsertRange(insertAt, toInsert);
    }

    private JsonDiffRowViewModel? CreateMoreRow(JsonDiffRowViewModel row, LevelState state)
    {
        int remaining = state.Pairs.Count - state.Shown;
        return remaining > 0
            ? JsonDiffRowViewModel.CreateMoreRow(this, row, $"Show more · {remaining:N0} left", canShowMore: true)
            : null;
    }

    private sealed class LevelState
    {
        public required List<JsonDiffPair> Pairs { get; init; }

        public int Shown { get; set; }

        public List<JsonDiffRowViewModel> Rows { get; } = [];

        public JsonDiffRowViewModel? MoreRow { get; set; }

        public void Reset()
        {
            Shown = 0;
            Rows.Clear();
            MoreRow = null;
        }
    }
}
