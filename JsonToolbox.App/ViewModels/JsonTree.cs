using JsonToolbox.Core.Model;
using JsonToolbox.Core.Documents;
using JsonToolbox.Core.Scanning;

namespace JsonToolbox.App.ViewModels;

/// <summary>
/// The document shown as one flat list of visible rows.
/// </summary>
/// <remarks>
/// <para>
/// A nested tree control has to guess how tall the branches it has not built are, and it
/// guesses by averaging the rows it has built. Expanding one node breaks that average — a row
/// twelve times taller than its neighbours drags it up by a third — and the control then
/// believes it is scrolled somewhere else entirely. Opening item 264 of a long array would
/// leave you looking at item 182, with the node you opened nowhere in sight.
/// </para>
/// <para>
/// Flattening removes the guess. Every row is one line tall, the list knows exactly how many
/// there are, and expanding a node inserts its children into the middle of the list without
/// changing the height of anything above them. Indentation is drawn from each row's depth.
/// This is how tree views over large data are built, and it is why they stay still.
/// </para>
/// </remarks>
public sealed class JsonTree
{
    /// <summary>How many children are read from the document when a node is expanded.</summary>
    private const int IndexLimit = 50_000;

    /// <summary>How many of them become rows at a time.</summary>
    private const int PageSize = 500;

    /// <summary>
    /// Nodes at most this large are read on the calling thread rather than on a worker: a
    /// quarter of a megabyte comes out of a memory mapping in well under a millisecond, which
    /// is less than the cost of scheduling the work elsewhere and coming back.
    /// </summary>
    private const long SynchronousReadLimit = 256 * 1024;

    private readonly Dictionary<JsonNodeViewModel, ChildState> _states = [];

    /// <summary>How this view reads the document; relaxed once, if the document demands it.</summary>
    private JsonScanOptions _options = new();

    public JsonTree(
        IndexedJsonDocument document,
        SearchHighlight highlight,
        PinnedKeys pinned,
        JsonMemberOrder order = JsonMemberOrder.FileOrder)
    {
        Document = document;
        Highlight = highlight;
        Pinned = pinned;
        Order = order;

        Root = new JsonNodeViewModel(this, document.Root, depth: 0);
        Rows.Add(Root);
    }

    public IndexedJsonDocument Document { get; }

    public SearchHighlight Highlight { get; }

    public PinnedKeys Pinned { get; }

    /// <summary>The order this view lists the properties of an object in.</summary>
    /// <remarks>
    /// Fixed for the life of the tree. Choosing a different order is a different view of the
    /// same document, and the session builds a new tree for it exactly as it does after an
    /// edit — which is what puts the open nodes and the selection back where they were.
    /// </remarks>
    public JsonMemberOrder Order { get; }

    public JsonNodeViewModel Root { get; }

    /// <summary>True once the document turned out not to be portable JSON.</summary>
    public bool IsLenient => _options.Strictness == JsonScanStrictness.Lenient;

    /// <summary>Something the user should be told, in the words to tell them.</summary>
    public event Action<string>? Notice;

    /// <summary>Every row currently visible, in document order.</summary>
    public ObservableRangeCollection<JsonNodeViewModel> Rows { get; } = [];

    public Task ToggleAsync(JsonNodeViewModel node, CancellationToken cancellationToken = default) =>
        node.IsExpanded ? CollapseTask(node) : ExpandAsync(node, cancellationToken);

    private Task CollapseTask(JsonNodeViewModel node)
    {
        Collapse(node);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Opens a node, reading its children the first time and inserting the first page of them
    /// directly below it.
    /// </summary>
    public async Task ExpandAsync(JsonNodeViewModel node, CancellationToken cancellationToken = default)
    {
        if (node.IsExpanded || !node.CanExpand)
        {
            return;
        }

        ChildState state = await LoadAsync(node, cancellationToken).ConfigureAwait(true);

        node.IsExpanded = true;
        state.Reset();
        InsertPage(node, state);
    }

    /// <summary>Closes a node by removing every row that sits beneath it.</summary>
    public void Collapse(JsonNodeViewModel node)
    {
        if (!node.IsExpanded)
        {
            return;
        }

        int start = Rows.IndexOf(node);
        if (start < 0)
        {
            return;
        }

        // Everything deeper than the node and immediately after it belongs to it, however
        // many levels of its own were open.
        int end = start + 1;
        while (end < Rows.Count && Rows[end].Depth > node.Depth)
        {
            JsonNodeViewModel row = Rows[end];
            row.IsExpanded = false;

            // The rows are going away, so whatever was recorded about them has to go with
            // them, or reopening the node would try to reuse rows that are no longer there.
            if (_states.TryGetValue(row, out ChildState? nested))
            {
                nested.Reset();
            }

            end++;
        }

        Rows.RemoveRange(start + 1, end - start - 1);
        node.IsExpanded = false;
        _states[node].Reset();
    }

    /// <summary>Adds the next page of an open node's already-read children.</summary>
    public void ShowMore(JsonNodeViewModel node)
    {
        if (_states.TryGetValue(node, out ChildState? state) && node.IsExpanded)
        {
            InsertPage(node, state);
        }
    }

    /// <summary>
    /// Opens the chain of nodes leading to a byte offset and returns the row holding it.
    /// </summary>
    /// <remarks>
    /// Navigation works on offsets rather than on parsed paths because an offset is
    /// unambiguous: a property name can contain dots and brackets, so a path string would have
    /// to be parsed back with exactly the same rules that produced it. Every node knows the
    /// byte range it covers, so the walk is a containment test at each level.
    /// </remarks>
    public async Task<JsonNodeViewModel?> RevealAsync(long offset, CancellationToken cancellationToken = default)
    {
        JsonNodeViewModel current = Root;
        if (!current.Node.Contains(offset))
        {
            return null;
        }

        while (current.CanExpand)
        {
            ChildState state = await LoadAsync(current, cancellationToken).ConfigureAwait(true);

            int index = state.Children.FindIndex(child => child.Contains(offset));
            if (index < 0)
            {
                return current;
            }

            if (!current.IsExpanded)
            {
                await ExpandAsync(current, cancellationToken).ConfigureAwait(true);
            }

            // The target may be past the page that is shown, so more rows are added until it
            // is reachable rather than reporting that it is not there.
            while (state.Shown <= index && state.Shown < state.Children.Count)
            {
                InsertPage(current, state);
            }

            if (state.Rows.Count <= index)
            {
                return current;
            }

            current = state.Rows[index];
        }

        return current;
    }

    /// <summary>
    /// The paths of every node that is currently open, in document order.
    /// </summary>
    /// <remarks>
    /// An edit moves every byte offset behind it, so the tree has to be built again from the
    /// changed document. Paths survive that where offsets do not, which is what lets the view
    /// come back looking as it did rather than collapsed to the root.
    /// </remarks>
    public List<IReadOnlyList<JsonPathSegment>> ExpandedPaths(int limit = 500) =>
    [
        .. Rows
            .Where(row => row is { IsMoreRow: false, IsExpanded: true })
            .Take(limit)
            .Select(row => row.BuildPath())
    ];

    /// <summary>
    /// Opens the chain of nodes named by a path and returns the row it ends at.
    /// </summary>
    /// <param name="expandTarget">
    /// Whether to open the node the path ends at, as well as the ones leading to it.
    /// </param>
    /// <remarks>
    /// The distinction matters because the two callers want opposite things. Putting the open
    /// nodes back after the document was rebuilt means opening the node each path names, or the
    /// deepest level would close every time. Putting the selection back means walking to a value
    /// and leaving it as it was — selecting a container has never opened it, and rebuilding the
    /// tree is not the moment to start.
    /// </remarks>
    public async Task<JsonNodeViewModel?> ExpandPathAsync(
        IReadOnlyList<JsonPathSegment> path,
        bool expandTarget = false,
        CancellationToken cancellationToken = default)
    {
        JsonNodeViewModel current = Root;

        foreach (JsonPathSegment segment in path)
        {
            if (!current.CanExpand)
            {
                return current;
            }

            ChildState state = await LoadAsync(current, cancellationToken).ConfigureAwait(true);

            int index = segment.IsIndex
                ? segment.Index
                : state.Children.FindIndex(child => child.Name == segment.Name);

            if (index < 0 || index >= state.Children.Count)
            {
                return current;
            }

            if (!current.IsExpanded)
            {
                await ExpandAsync(current, cancellationToken).ConfigureAwait(true);
            }

            while (state.Shown <= index && state.Shown < state.Children.Count)
            {
                InsertPage(current, state);
            }

            if (index >= state.Rows.Count)
            {
                return current;
            }

            current = state.Rows[index];
        }

        if (expandTarget && current is { CanExpand: true, IsExpanded: false })
        {
            await ExpandAsync(current, cancellationToken).ConfigureAwait(true);
        }

        return current;
    }

    /// <summary>
    /// Reads the pinned values again for every open node and updates the rows in place.
    /// </summary>
    /// <remarks>
    /// Only containers that have actually been opened are re-read, and each costs one scan of
    /// bytes the operating system has already paged in. The rows themselves are kept, so the
    /// tree does not collapse under the user when a pin is added.
    /// </remarks>
    public async Task RefreshPinsAsync(CancellationToken cancellationToken = default)
    {
        foreach (JsonNodeViewModel node in _states.Keys.ToList())
        {
            ChildState state = _states[node];

            JsonChildIndexer indexer;
            try
            {
                indexer = await Document
                    .IndexChildrenAsync(
                        node.Node,
                        maxChildren: IndexLimit,
                        pinnedKeys: Pinned.Keys,
                        options: _options,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(true);
            }
            catch (JsonScanException)
            {
                // A branch that could not be read has no pinned values to refresh either. It
                // already says why it is empty, so there is nothing to add.
                continue;
            }

            state.Children.Clear();
            state.Children.AddRange(Arrange(indexer.Children));

            var byStart = indexer.Children.ToDictionary(child => child.Start);
            foreach (JsonNodeViewModel row in state.Rows)
            {
                if (byStart.TryGetValue(row.Node.Start, out JsonNodeInfo updated))
                {
                    row.Node = updated;
                }
            }
        }

        foreach (JsonNodeViewModel row in Rows)
        {
            row.RefreshPinState();
        }
    }

    /// <summary>
    /// Puts a container's children in the order this view shows them.
    /// </summary>
    /// <remarks>
    /// Sorting happens here, once, where the children are read — not when the rows are built.
    /// Everything downstream, from paging to revealing a search hit, then walks a single list
    /// whose order is already settled, and cannot disagree with what is on screen.
    /// </remarks>
    private IReadOnlyList<JsonNodeInfo> Arrange(IReadOnlyList<JsonNodeInfo> children) =>
        JsonMemberOrdering.Arrange(children, child => child.Name, Order);

    private async Task<ChildState> LoadAsync(JsonNodeViewModel node, CancellationToken cancellationToken)
    {
        if (_states.TryGetValue(node, out ChildState? existing))
        {
            return existing;
        }

        ChildState state;
        try
        {
            JsonChildIndexer indexer = await IndexAsync(node, cancellationToken).ConfigureAwait(true);
            state = new ChildState
            {
                Children = [.. Arrange(indexer.Children)],
                Truncated = indexer.Truncated,
            };
        }
        catch (JsonScanException ex)
        {
            // A document that is not JSON is a thing to be told about, not a thing to fall
            // over. The branch opens onto the explanation, and the rest of the tree — every
            // part of the file that does parse — stays usable.
            state = new ChildState
            {
                Children = [],
                Truncated = false,
                Error = $"Line {ex.LineNumber}, column {ex.BytePositionInLine + 1}: {ex.Message}",
            };

            Notice?.Invoke($"{Document.DisplayName} stops being JSON at line {ex.LineNumber}: {ex.Message}");
        }

        _states[node] = state;
        return state;
    }

    /// <summary>
    /// Reads a container's children, giving up on portable JSON if that is what it takes.
    /// </summary>
    /// <remarks>
    /// Configuration files written by hand are full of comments and trailing commas, and a
    /// reader that refuses them leaves the user staring at a document they can plainly see is
    /// there. So the first refusal switches this view to reading leniently and tries again —
    /// once, for the whole tree — and says so. Inspection stays strict, because reporting that
    /// the file is not portable JSON is exactly its job.
    /// </remarks>
    private async Task<JsonChildIndexer> IndexAsync(JsonNodeViewModel node, CancellationToken cancellationToken)
    {
        try
        {
            return await ReadAsync(node, cancellationToken).ConfigureAwait(true);
        }
        catch (JsonScanException) when (_options.Strictness == JsonScanStrictness.Strict)
        {
            _options = _options with { Strictness = JsonScanStrictness.Lenient };

            JsonChildIndexer indexer = await ReadAsync(node, cancellationToken).ConfigureAwait(true);
            Notice?.Invoke(
                $"{Document.DisplayName} is not portable JSON, so it is being read with comments and "
                + "trailing commas allowed. Run Inspect to see what is in it.");

            return indexer;
        }
    }

    private Task<JsonChildIndexer> ReadAsync(JsonNodeViewModel node, CancellationToken cancellationToken) =>
        // A small node is read on the spot: going through a worker would cost more than the
        // read itself, and it would split the expansion into two layout passes.
        node.Node.HasKnownExtent && node.Node.ByteLength <= SynchronousReadLimit
            ? Task.FromResult(Document.IndexChildren(
                node.Node,
                maxChildren: IndexLimit,
                pinnedKeys: Pinned.Keys,
                options: _options,
                cancellationToken: cancellationToken))
            : Document.IndexChildrenAsync(
                node.Node,
                maxChildren: IndexLimit,
                pinnedKeys: Pinned.Keys,
                options: _options,
                cancellationToken: cancellationToken);

    /// <summary>
    /// Inserts the next page of a node's children directly below the rows it already has.
    /// </summary>
    private void InsertPage(JsonNodeViewModel node, ChildState state)
    {
        int anchor = Rows.IndexOf(node);
        if (anchor < 0)
        {
            return;
        }

        // The insertion point is after the node's own rows, which end where the depth drops
        // back to the node's level.
        int insertAt = anchor + 1;
        while (insertAt < Rows.Count && Rows[insertAt].Depth > node.Depth)
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

        int take = Math.Min(PageSize, state.Children.Count - state.Shown);
        List<JsonNodeViewModel> page = [.. state.Children
            .GetRange(state.Shown, take)
            .Select(child => new JsonNodeViewModel(this, child, node.Depth + 1, node))];

        state.Rows.AddRange(page);
        state.Shown += take;

        List<JsonNodeViewModel> toInsert = [.. page];
        if (CreateMoreRow(node, state) is { } moreRow)
        {
            state.MoreRow = moreRow;
            toInsert.Add(moreRow);
        }

        Rows.InsertRange(insertAt, toInsert);
    }

    private JsonNodeViewModel? CreateMoreRow(JsonNodeViewModel node, ChildState state)
    {
        int remaining = state.Children.Count - state.Shown;

        // The reason a branch has nothing under it belongs under that branch, where whoever
        // opened it is looking, rather than only in a status bar they have already read past.
        if (state.Error is { } error)
        {
            return JsonNodeViewModel.CreateMoreRow(this, node, error, canShowMore: false);
        }

        if (remaining > 0)
        {
            return JsonNodeViewModel.CreateMoreRow(
                this,
                node,
                $"Show {Math.Min(PageSize, remaining):N0} more · {remaining:N0} left",
                canShowMore: true);
        }

        return state.Truncated
            ? JsonNodeViewModel.CreateMoreRow(
                this,
                node,
                $"Only the first {state.Children.Count:N0} children were read",
                canShowMore: false)
            : null;
    }

    /// <summary>What is known about one container's children, once it has been read.</summary>
    private sealed class ChildState
    {
        public required List<JsonNodeInfo> Children { get; init; }

        public required bool Truncated { get; init; }

        /// <summary>Why there are no children, when the reason is that the document is broken.</summary>
        public string? Error { get; init; }

        /// <summary>How many children have been turned into rows.</summary>
        public int Shown { get; set; }

        /// <summary>Those rows, in order, so a reveal can index straight into them.</summary>
        public List<JsonNodeViewModel> Rows { get; } = [];

        public JsonNodeViewModel? MoreRow { get; set; }

        /// <summary>
        /// Forgets the rows that were built from these children, keeping the children
        /// themselves. Reading a container is worth doing once; building rows from it is not.
        /// </summary>
        public void Reset()
        {
            Shown = 0;
            Rows.Clear();
            MoreRow = null;
        }
    }
}
