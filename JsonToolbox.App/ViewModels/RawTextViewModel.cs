using System.Collections;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using JsonToolbox.Core.Documents;

namespace JsonToolbox.App.ViewModels;

/// <summary>
/// The byte range the text view marks as the selected value, shared by every row that draws it.
/// </summary>
/// <remarks>
/// One object for the same reason the search term is one object: the rows on screen all have
/// to redraw when the selection moves, and a single notification reaching each of them is
/// cheaper than a property on each row that has to be found and set.
/// </remarks>
public sealed partial class TextMark : ObservableObject
{
    [ObservableProperty]
    private long _start = -1;

    [ObservableProperty]
    private long _end = -1;

    public bool IsSet => Start >= 0 && End > Start;

    public void Set(long start, long end)
    {
        Start = start;
        End = end;
    }

    public void Clear() => Set(-1, -1);
}

/// <summary>
/// One row of the text, read when it is first shown.
/// </summary>
public sealed class TextRowViewModel(RawTextViewModel owner, TextRow row)
{
    private string? _text;

    public RawTextViewModel Owner { get; } = owner;

    public TextRow Row { get; } = row;

    public long Index => Row.Index;

    public long Start => Row.Start;

    /// <summary>The line number, on the first row of a line; a continuation row shows none.</summary>
    public string LineLabel => Row.IsContinuation ? string.Empty : Row.Line.ToString("N0");

    public string Text => _text ??= Owner.Read(Row);

    // Rows are handed out afresh each time the list is asked for one, so two objects for the
    // same row have to count as the same row, or the list could not find the one it selected.
    public override bool Equals(object? obj) => obj is TextRowViewModel other && other.Index == Index && ReferenceEquals(other.Owner, Owner);

    public override int GetHashCode() => Index.GetHashCode();
}

/// <summary>
/// The document as text, beside the tree.
/// </summary>
/// <remarks>
/// <para>
/// The rows come from a <see cref="TextRowIndex"/> that is built in the background the first
/// time the text is looked at, and the list grows as the count proceeds, so the start of a
/// large file is readable before its end has been reached. The list is virtual: a row is
/// created when the view asks for it and read from the file at that moment, which is what
/// lets a gigabyte be shown without being loaded.
/// </para>
/// <para>
/// Selecting a value in the tree marks its bytes here and scrolls to them; clicking here
/// selects the value under the pointer in the tree. Both go through byte offsets, which is the
/// one coordinate the tree and the text share.
/// </para>
/// </remarks>
public sealed partial class RawTextViewModel : ObservableObject, IDisposable
{
    private readonly TextRowIndex _index;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Action<long> _reveal;
    private long _pendingOffset = -1;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private bool _isBuilding;

    [ObservableProperty]
    private TextRowViewModel? _selectedRow;

    public RawTextViewModel(IndexedJsonDocument document, SearchHighlight highlight, Action<long> reveal)
    {
        _index = new TextRowIndex(document.Source);
        _reveal = reveal;
        Highlight = highlight;
        Rows = new RowList(this, _index);
    }

    /// <summary>The term to mark up, shared with the rest of the window.</summary>
    public SearchHighlight Highlight { get; }

    /// <summary>The bytes of the value selected in the tree.</summary>
    public TextMark Mark { get; } = new();

    /// <summary>Every row found so far, created on demand.</summary>
    public RowList Rows { get; }

    /// <summary>
    /// Counts the rows, on a worker, and tells the list as they arrive.
    /// </summary>
    public async Task BuildAsync()
    {
        if (IsBuilding || _index.IsComplete)
        {
            return;
        }

        IsBuilding = true;
        Status = "Reading…";

        // Progress<T> reports on the thread that created it, which here is the one the view
        // is bound on, so the list can be told directly.
        IProgress<long> progress = new Progress<long>(rows =>
        {
            Status = $"Reading… {rows:N0} rows so far";
            Rows.Refresh();
            ShowPending();
        });

        try
        {
            CancellationToken token = _cancellation.Token;
            await Task.Run(() => _index.Build(progress.Report, token), token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        Rows.Refresh();
        Status = string.Empty;
        IsBuilding = false;
        ShowPending();
    }

    /// <summary>
    /// Marks a value's bytes and scrolls to where they start. A value not yet reached by the
    /// count is shown when it is.
    /// </summary>
    public void Show(long start, long end)
    {
        Mark.Set(start, end);
        _pendingOffset = start;
        ShowPending();
    }

    public void ClearSelection()
    {
        Mark.Clear();
        _pendingOffset = -1;
        SelectedRow = null;
    }

    /// <summary>Selects, in the tree, the value at a byte offset that was clicked here.</summary>
    public void Reveal(long offset) => _reveal(offset);

    internal string Read(TextRow row) => _index.Read(row);

    private void ShowPending()
    {
        if (_pendingOffset < 0 || _index.RowCount == 0)
        {
            return;
        }

        TextRow row = _index.RowAt(_pendingOffset);
        if (!row.Contains(_pendingOffset) && !_index.IsComplete)
        {
            return;
        }

        _pendingOffset = -1;

        // Cleared first: a row equal to the one already selected would not count as a change,
        // and the view must scroll to it again whether or not it moved.
        SelectedRow = null;
        SelectedRow = new TextRowViewModel(this, row);
    }

    public void Dispose() => _cancellation.Cancel();

    /// <summary>
    /// The rows as a list the view can virtualise over: it has a count and an indexer, and
    /// nothing is created until a row is asked for.
    /// </summary>
    /// <remarks>
    /// Only the members a virtualising panel uses are real. The rest of the list interface
    /// exists because that is the interface the panel asks for, and throwing from the members
    /// that would mean loading the file is better than quietly doing so.
    /// </remarks>
    public sealed class RowList(RawTextViewModel owner, TextRowIndex index) : IList, INotifyCollectionChanged
    {
        public event NotifyCollectionChangedEventHandler? CollectionChanged;

        public int Count => (int)Math.Min(index.RowCount, int.MaxValue);

        public object? this[int i]
        {
            get => new TextRowViewModel(owner, index.Row(i));
            set => throw new NotSupportedException();
        }

        /// <summary>Tells the view the count has changed.</summary>
        public void Refresh() =>
            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));

        public int IndexOf(object? value) =>
            value is TextRowViewModel row && ReferenceEquals(row.Owner, owner) && row.Index < Count ? (int)row.Index : -1;

        public bool Contains(object? value) => IndexOf(value) >= 0;

        public IEnumerator GetEnumerator()
        {
            int count = Count;
            for (int i = 0; i < count; i++)
            {
                yield return this[i];
            }
        }

        public bool IsFixedSize => false;

        public bool IsReadOnly => true;

        public bool IsSynchronized => false;

        public object SyncRoot => this;

        public int Add(object? value) => throw new NotSupportedException();

        public void Clear() => throw new NotSupportedException();

        public void Insert(int index, object? value) => throw new NotSupportedException();

        public void Remove(object? value) => throw new NotSupportedException();

        public void RemoveAt(int index) => throw new NotSupportedException();

        public void CopyTo(Array array, int index) => throw new NotSupportedException();
    }
}
