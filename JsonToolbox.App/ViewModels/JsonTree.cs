using JsonToolbox.Core.Model;
using JsonToolbox.Core.Documents;

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

    public JsonTree(IndexedJsonDocument document, SearchHighlight highlight, PinnedKeys pinned)
    {
        Document = document;
        Highlight = highlight;
        Pinned = pinned;

        Root = new JsonNodeViewModel(this, document.Root, depth: 0);
        Rows.Add(Root);
    }

    public IndexedJsonDocument Document { get; }

    public SearchHighlight Highlight { get; }

    public PinnedKeys Pinned { get; }

    public JsonNodeViewModel Root { get; }

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
    public async Task<JsonNodeViewModel?> ExpandPathAsync(
        IReadOnlyList<JsonPathSegment> path,
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

            JsonChildIndexer indexer = await Document
                .IndexChildrenAsync(
                    node.Node,
                    maxChildren: IndexLimit,
                    pinnedKeys: Pinned.Keys,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(true);

            state.Children.Clear();
            state.Children.AddRange(indexer.Children);

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

    private async Task<ChildState> LoadAsync(JsonNodeViewModel node, CancellationToken cancellationToken)
    {
        if (_states.TryGetValue(node, out ChildState? existing))
        {
            return existing;
        }

        // A small node is read on the spot: going through a worker would cost more than the
        // read itself, and it would split the expansion into two layout passes.
        JsonChildIndexer indexer = node.Node.HasKnownExtent && node.Node.ByteLength <= SynchronousReadLimit
            ? Document.IndexChildren(node.Node, maxChildren: IndexLimit, pinnedKeys: Pinned.Keys, cancellationToken: cancellationToken)
            : await Document
                .IndexChildrenAsync(node.Node, maxChildren: IndexLimit, pinnedKeys: Pinned.Keys, cancellationToken: cancellationToken)
                .ConfigureAwait(true);

        var state = new ChildState
        {
            Children = [.. indexer.Children],
            Truncated = indexer.Truncated,
        };

        _states[node] = state;
        return state;
    }

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
