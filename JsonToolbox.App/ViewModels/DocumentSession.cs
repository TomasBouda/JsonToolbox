using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JsonToolbox.Core.Diagnostics;
using JsonToolbox.Core.Documents;
using JsonToolbox.Core.Editing;
using JsonToolbox.Core.Model;
using JsonToolbox.Core.Scanning;
using JsonToolbox.Core.Search;

namespace JsonToolbox.App.ViewModels;

/// <summary>
/// What the window can do for a document that the document cannot do for itself.
/// </summary>
/// <remarks>
/// A session owns a file and everything derived from it, and knows nothing about windows,
/// dialogs or clipboards. Handing it these few callbacks is what keeps it that way — and what
/// lets several of them exist side by side without fighting over one window's state.
/// </remarks>
/// <param name="PickFile">Asks the user for a file, or <c>null</c> if they decline.</param>
/// <param name="Copy">Puts text on the clipboard.</param>
/// <param name="AskForText">Asks a one-line question, or <c>null</c> if they decline.</param>
/// <param name="Report">Says something in the status bar.</param>
/// <param name="SetBusy">Shows or hides the progress indicator.</param>
/// <param name="SetProgress">Moves the progress indicator.</param>
/// <param name="Activate">Brings this document to the front.</param>
/// <param name="Close">Closes this document.</param>
public sealed record SessionServices(
    Func<Task<string?>>? PickFile,
    Func<string, Task>? Copy,
    Func<string, string, Task<string?>>? AskForText,
    Action<string> Report,
    Action<bool> SetBusy,
    Action<double> SetProgress,
    Action<DocumentSession> Activate,
    Action<DocumentSession> Close);

/// <summary>
/// One open document and everything the window shows about it.
/// </summary>
/// <remarks>
/// Everything that belongs to a file lives here — its tree, its search, its findings, its
/// pending edits — so opening a second file is opening a second session rather than swapping
/// the contents of one. That is what makes the tabs real: switching between them changes which
/// session is on screen and nothing else, and neither loses its place.
/// </remarks>
public sealed partial class DocumentSession : ObservableObject, IDisposable
{
    /// <summary>Values larger than this are shown truncated, and so cannot be edited as text.</summary>
    private const int EditableValueLimit = 64 * 1024;

    private readonly SessionServices _services;

    private IndexedJsonDocument _document;
    private JsonDocumentEditor _editor;
    private CancellationTokenSource? _searchCancellation;

    /// <summary>
    /// True when this is the document on screen.
    /// </summary>
    /// <remarks>
    /// Set by the window rather than worked out here, because it depends on something a
    /// session cannot see: whether the comparison tab is the one being shown.
    /// </remarks>
    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private JsonTree? _tree;

    [ObservableProperty]
    private JsonNodeViewModel? _selectedNode;

    [ObservableProperty]
    private string _selectedNodeDetail = string.Empty;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _searchKeys = true;

    [ObservableProperty]
    private bool _searchValues = true;

    [ObservableProperty]
    private bool _caseSensitive;

    [ObservableProperty]
    private bool _useRegex;

    [ObservableProperty]
    private bool _wholeWord;

    [ObservableProperty]
    private string _searchSummary = string.Empty;

    [ObservableProperty]
    private SearchHitRowViewModel? _selectedHit;

    [ObservableProperty]
    private DiagnosticRowViewModel? _selectedDiagnostic;

    [ObservableProperty]
    private string _inspectionSummary = string.Empty;

    [ObservableProperty]
    private bool _hasInspected;

    [ObservableProperty]
    private string _documentSize = string.Empty;

    [ObservableProperty]
    private bool _hasSelection;

    [ObservableProperty]
    private string _selectedPath = string.Empty;

    [ObservableProperty]
    private string _selectedPointer = string.Empty;

    [ObservableProperty]
    private string _selectedType = string.Empty;

    [ObservableProperty]
    private string _selectedSize = string.Empty;

    [ObservableProperty]
    private string _selectedOffset = string.Empty;

    [ObservableProperty]
    private string _selectedLine = string.Empty;

    [ObservableProperty]
    private string _selectedChildren = string.Empty;

    /// <summary>The value as it stands in the editing box, which may not have been applied yet.</summary>
    [ObservableProperty]
    private string _editedValue = string.Empty;

    [ObservableProperty]
    private string _editError = string.Empty;

    /// <summary>
    /// Whether the selected value is small enough to edit as text.
    /// </summary>
    /// <remarks>
    /// The pane shows a truncated rendering of a large value, and writing a truncated value
    /// back would delete the rest of it. Anything past the limit is read-only.
    /// </remarks>
    [ObservableProperty]
    private bool _canEditValue;

    public DocumentSession(string path, SessionServices services)
    {
        _services = services;

        var editable = new EditableJsonSource(JsonSource.FromFile(path));
        _editor = new JsonDocumentEditor(editable);
        _document = IndexedJsonDocument.Open(editable);

        Name = _document.DisplayName;
        DocumentSize = ByteSize.Format(_document.Length);

        Diagnostics.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasDiagnostics));
            OnPropertyChanged(nameof(DiagnosticCountText));
        };

        Hits.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasHits));
            OnPropertyChanged(nameof(HitCountText));
        };

        // Pinned values are collected by the scan that lists a container's children, so
        // changing the set means re-reading whatever is currently open.
        Pinned.Changed += (_, _) => _ = RefreshPinsAsync();

        // Said before the first read rather than after it, so that anything the read has to
        // report — a document that is not portable JSON, most of all — is what the user is
        // left looking at instead of being overwritten by the greeting.
        _services.Report($"Root is {_document.Root.Kind.ToDisplayName()}. Run Inspect to analyse the whole document.");

        Tree = Build();
        _ = Tree.ExpandAsync(Tree.Root);
    }

    /// <summary>
    /// A tree over the document as it now stands, wired to say what it finds.
    /// </summary>
    /// <remarks>
    /// Every tree is built here so that none of them can be built without the notices
    /// connected — the message that a document is not portable JSON is the one thing a reader
    /// most needs and would otherwise never see.
    /// </remarks>
    private JsonTree Build()
    {
        var tree = new JsonTree(_document, Highlight, Pinned, MemberOrder);

        tree.Notice += message =>
        {
            _services.Report(message);
            OnPropertyChanged(nameof(IsReadLeniently));
        };

        return tree;
    }

    /// <summary>True when the document had to be read with comments and trailing commas allowed.</summary>
    public bool IsReadLeniently => Tree?.IsLenient == true;

    public string Name { get; }

    public IndexedJsonDocument Document => _document;

    public ObservableCollection<SearchHitRowViewModel> Hits { get; } = [];

    public ObservableCollection<DiagnosticRowViewModel> Diagnostics { get; } = [];

    public ObservableCollection<PathRowViewModel> Profile { get; } = [];

    /// <summary>The term the last search ran with, handed to every control that shows text.</summary>
    public SearchHighlight Highlight { get; } = new();

    /// <summary>Property names pinned so their values show on the parent row.</summary>
    public PinnedKeys Pinned { get; } = new();

    public bool HasDiagnostics => Diagnostics.Count > 0;

    public bool HasHits => Hits.Count > 0;

    public string DiagnosticCountText => Diagnostics.Count.ToString("N0");

    public string HitCountText => Hits.Count.ToString("N0");

    public bool IsModified => _editor.IsModified;

    public bool CanUndo => _editor.CanUndo;

    public bool CanRedo => _editor.CanRedo;

    /// <summary>The name as the tab shows it, with a mark when there is unsaved work.</summary>
    public string TabTitle => IsModified ? $"• {Name}" : Name;

    // The tab's own buttons act on the session it represents, so its bindings are to itself
    // rather than reaching up the visual tree for the window that owns it.
    [RelayCommand]
    private void Activate() => _services.Activate(this);

    [RelayCommand]
    private void Close() => _services.Close(this);

    // ---- Inspecting ------------------------------------------------------------------

    [RelayCommand]
    private async Task InspectAsync()
    {
        _services.SetBusy(true);
        _services.SetProgress(0);
        _services.Report("Inspecting…");

        try
        {
            var progress = new Progress<double>(_services.SetProgress);
            InspectionReport report = await JsonInspection
                .InspectAsync(_document.Source, progress: progress)
                .ConfigureAwait(true);

            Diagnostics.Clear();
            foreach (JsonDiagnostic diagnostic in report.Diagnostics)
            {
                Diagnostics.Add(new DiagnosticRowViewModel(diagnostic));
            }

            // The heaviest path other than the document root sets the scale for the bars; the
            // root always holds everything and would otherwise flatten every other row.
            List<PathStats> byWeight = [.. report.Profile.ByWeight().Take(2_000)];
            long largest = byWeight.Where(s => s.ParentPath is not null).Select(s => s.TotalBytes).DefaultIfEmpty(0).Max();

            Profile.Clear();
            foreach (PathStats stats in byWeight)
            {
                Profile.Add(new PathRowViewModel(stats, largest));
            }

            HasInspected = true;
            InspectionSummary =
                $"{report.Stats.Values:N0} values · max depth {report.Stats.MaxDepth} · " +
                $"{report.Profile.Paths.Count:N0} distinct paths";

            _services.Report(report.Completed
                ? $"Inspection finished: {report.ErrorCount} errors, {report.WarningCount} warnings."
                : "Inspection stopped at a syntax error; everything before it was still analysed.");
        }
        finally
        {
            _services.SetBusy(false);
            _services.SetProgress(0);
        }
    }

    // ---- Searching -------------------------------------------------------------------

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (string.IsNullOrEmpty(SearchText))
        {
            return;
        }

        await _searchCancellation.CancelAndDisposeAsync().ConfigureAwait(true);
        _searchCancellation = new CancellationTokenSource();
        CancellationToken token = _searchCancellation.Token;

        SearchScope scope = (SearchKeys ? SearchScope.Keys : 0) | (SearchValues ? SearchScope.Values : 0);
        if (scope == 0)
        {
            SearchSummary = "Select keys, values, or both.";
            return;
        }

        var query = new SearchQuery
        {
            Text = SearchText,
            Scope = scope,
            CaseSensitive = CaseSensitive,
            UseRegex = UseRegex,
            WholeWord = WholeWord,
        };

        _services.SetBusy(true);
        Hits.Clear();

        // Set before the scan runs, so the rows already on screen mark up the term while the
        // rest of the document is still being read.
        Highlight.Set(query.Text, query.CaseSensitive, query.UseRegex);

        try
        {
            var progress = new Progress<double>(_services.SetProgress);
            SearchResult result = await JsonSearchEngine
                .SearchAsync(_document.Source, query, progress: progress, cancellationToken: token)
                .ConfigureAwait(true);

            foreach (SearchHit hit in result.Hits)
            {
                Hits.Add(new SearchHitRowViewModel(hit, Highlight));
            }

            SearchSummary = result.Truncated
                ? $"Stopped at {result.Hits.Count:N0} hits after {result.Duration.TotalSeconds:F1} s — narrow the query to see the rest."
                : $"{result.Hits.Count:N0} hits in {result.Duration.TotalSeconds:F1} s.";
        }
        catch (ArgumentException ex)
        {
            SearchSummary = $"Invalid pattern: {ex.Message}";
        }
        catch (JsonScanException ex)
        {
            // Searching reads the whole file, so it meets a syntax error the tree never
            // reached. What was found before it is still worth showing.
            SearchSummary = $"Stopped at line {ex.LineNumber}: {ex.Message}";
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer search; its results are the ones worth showing.
        }
        finally
        {
            _services.SetBusy(false);
            _services.SetProgress(0);
        }
    }

    // ---- Editing ---------------------------------------------------------------------

    [RelayCommand]
    private async Task ApplyValueAsync()
    {
        if (SelectedNode is { } row)
        {
            await ApplyEditAsync(() => _editor.ReplaceValue(row.Node, EditedValue)).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void RevertValue()
    {
        EditedValue = SelectedNodeDetail;
        EditError = string.Empty;
    }

    [RelayCommand]
    private async Task RenameSelectedAsync()
    {
        if (SelectedNode is not { } row || _services.AskForText is null)
        {
            return;
        }

        string? name = await _services.AskForText("Rename property", row.Node.Name ?? string.Empty).ConfigureAwait(true);
        if (name is not null)
        {
            await ApplyEditAsync(() => _editor.RenameProperty(row.Node, name)).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        if (SelectedNode is { } row)
        {
            await ApplyEditAsync(() => _editor.Delete(row.Node)).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task AddChildAsync()
    {
        if (SelectedNode is not { Node.IsContainer: true } row || _services.AskForText is null)
        {
            return;
        }

        bool isObject = row.Node.Kind == JsonKind.Object;
        string? name = isObject
            ? await _services.AskForText("Name of the new property", "newProperty").ConfigureAwait(true)
            : string.Empty;

        if (name is null)
        {
            return;
        }

        string? json = await _services.AskForText("Value, as JSON", "null").ConfigureAwait(true);
        if (json is not null)
        {
            await ApplyEditAsync(() => _editor.Insert(row.Node, isObject ? name : null, json)).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task UndoAsync()
    {
        if (_editor.Undo())
        {
            await RebuildAfterEditAsync().ConfigureAwait(true);
            _services.Report("Undone.");
        }
    }

    [RelayCommand]
    private async Task RedoAsync()
    {
        if (_editor.Redo())
        {
            await RebuildAfterEditAsync().ConfigureAwait(true);
            _services.Report("Redone.");
        }
    }

    /// <summary>
    /// Writes the document back over the file it came from.
    /// </summary>
    /// <remarks>
    /// The file is read through a memory mapping, and Windows will not let a mapped file be
    /// replaced. So the new content is written beside it first, the mapping is released, the
    /// file is moved into place, and the document is opened again from it. That last step is
    /// also why the undo history stops here: it points into a file that no longer exists.
    /// </remarks>
    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_document.Source.FilePath is not { } path || !IsModified)
        {
            return;
        }

        string temporary = Path.Combine(
            Path.GetDirectoryName(path) ?? ".",
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                _editor.WriteTo(stream);
            }

            _editor.Source.Dispose();
            File.Move(temporary, path, overwrite: true);

            await ReopenAsync(path).ConfigureAwait(true);
            _services.Report($"Saved to {Path.GetFileName(path)}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }

            _services.Report($"Could not save: {ex.Message}");
        }
    }

    [RelayCommand]
    private void ClearPins() => Pinned.Clear();

    // ---- The table ---------------------------------------------------------------------

    /// <summary>Which panel is on screen, so the table is only built when it is being looked at.</summary>
    [ObservableProperty]
    private int _selectedPanel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTable))]
    private JsonTableViewModel? _table;

    [ObservableProperty]
    private string _tableMessage = "Select an array of records in the tree to read it as a table.";

    public bool HasTable => Table is not null;

    /// <summary>The index of the table panel among the tabs beside the tree.</summary>
    private const int TablePanelIndex = 1;

    partial void OnSelectedPanelChanged(int value) => _ = RefreshTableAsync();

    /// <summary>
    /// Builds the table for whatever array the selection is in.
    /// </summary>
    /// <remarks>
    /// Only while the table is the panel on screen. Reading an array of eight million records
    /// costs a pass over the file, and paying that on every click in the tree — for a panel
    /// nobody is looking at — would make the tree unusable to save a tab switch.
    /// </remarks>
    private async Task RefreshTableAsync()
    {
        if (SelectedPanel != TablePanelIndex)
        {
            return;
        }

        if (NearestArray(SelectedNode) is not { } row)
        {
            Table = null;
            TableMessage = SelectedNode is null
                ? "Select an array of records in the tree to read it as a table."
                : "This value is not inside an array of records.";
            return;
        }

        // Already showing this array: rebuilding it would throw away the scroll position to
        // arrive at the same table.
        if (Table is { } current && current.Path == JsonPathFormatter.ToJsonPath(row.BuildPath()))
        {
            return;
        }

        _services.SetBusy(true);

        try
        {
            string path = JsonPathFormatter.ToJsonPath(row.BuildPath());
            Table = await JsonTableViewModel.BuildAsync(_document, row.Node, path).ConfigureAwait(true);

            TableMessage = Table is null
                ? "The elements of this array are not records with the same fields, so there are no columns to draw."
                : string.Empty;
        }
        catch (JsonScanException ex)
        {
            Table = null;
            TableMessage = $"Line {ex.LineNumber}: {ex.Message}";
        }
        finally
        {
            _services.SetBusy(false);
        }
    }

    /// <summary>
    /// The selected array, or the one the selection sits in.
    /// </summary>
    /// <remarks>
    /// Clicking a field of a record and asking for a table means the table of those records,
    /// not an error. Walking up from the selection is what turns the one into the other.
    /// </remarks>
    private static JsonNodeViewModel? NearestArray(JsonNodeViewModel? from)
    {
        for (JsonNodeViewModel? row = from; row is not null; row = row.Parent)
        {
            if (row is { IsMoreRow: false } && row.Node.Kind == JsonKind.Array)
            {
                return row;
            }
        }

        return null;
    }

    /// <summary>Selects the record a table row stands for, so the tree follows the table.</summary>
    [RelayCommand]
    private async Task RevealTableRowAsync(TableRowViewModel? row)
    {
        if (row is null || Tree is not { } tree)
        {
            return;
        }

        if (await tree.RevealAsync(row.Node.Start).ConfigureAwait(true) is { } found)
        {
            SelectedNode = found;
        }
    }

    // ---- The order properties are listed in ---------------------------------------------

    /// <summary>
    /// How the tree lists the properties of an object.
    /// </summary>
    /// <remarks>
    /// This is a way of looking at the document, not a change to it: the file keeps the order it
    /// was written in until <see cref="ApplyOrderAsync"/> is asked for. Reading a record whose
    /// forty keys arrive in the order a serialiser happened to emit them is the case this
    /// exists for, and wanting to read it that way is not the same as wanting to rewrite it.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFileOrder))]
    [NotifyPropertyChangedFor(nameof(IsAscending))]
    [NotifyPropertyChangedFor(nameof(IsDescending))]
    [NotifyPropertyChangedFor(nameof(CanApplyOrder))]
    private JsonMemberOrder _memberOrder = JsonMemberOrder.FileOrder;

    // The three chips are one choice, so each is bound to a property that can only be turned
    // on: clicking the chip that is already lit sets false, which is answered by saying the
    // value is still true. That is the behaviour of a radio group, without a radio group's
    // glyph to style away.
    public bool IsFileOrder
    {
        get => MemberOrder == JsonMemberOrder.FileOrder;
        set => Choose(value, JsonMemberOrder.FileOrder);
    }

    public bool IsAscending
    {
        get => MemberOrder == JsonMemberOrder.Ascending;
        set => Choose(value, JsonMemberOrder.Ascending);
    }

    public bool IsDescending
    {
        get => MemberOrder == JsonMemberOrder.Descending;
        set => Choose(value, JsonMemberOrder.Descending);
    }

    /// <summary>There is nothing to write into the document while the view follows the file.</summary>
    public bool CanApplyOrder => MemberOrder.IsSorted();

    private void Choose(bool chosen, JsonMemberOrder order)
    {
        if (chosen)
        {
            MemberOrder = order;
        }
        else
        {
            // Turning the current choice off would leave no choice at all, so the chip is told
            // it is still on.
            OnPropertyChanged(nameof(IsFileOrder));
            OnPropertyChanged(nameof(IsAscending));
            OnPropertyChanged(nameof(IsDescending));
        }
    }

    partial void OnMemberOrderChanged(JsonMemberOrder value) => _ = RebuildTreeAsync();

    /// <summary>
    /// Writes the order the tree is showing into the document itself.
    /// </summary>
    /// <param name="node">
    /// The value to reorder, or <c>null</c> for the whole document.
    /// </param>
    /// <remarks>
    /// It is an edit like any other — one step of undo, nothing on disk until Save — because a
    /// rewrite that reaches every object in the file is exactly the change somebody wants to be
    /// able to take back.
    /// </remarks>
    [RelayCommand]
    private async Task ApplyOrderAsync(JsonNodeViewModel? node)
    {
        if (!CanApplyOrder)
        {
            _services.Report("Choose A→Z or Z→A first; there is nothing to apply while the view follows the file.");
            return;
        }

        JsonNodeInfo target = node?.Node ?? _document.Root;
        string what = node is null ? "the document" : node.Header;

        _services.SetBusy(true);
        _services.Report($"Sorting the properties of {what}…");

        try
        {
            EditResult result = await Task
                .Run(() => _editor.SortMembers(_document, target, MemberOrder))
                .ConfigureAwait(true);

            if (!result.Applied)
            {
                EditError = result.Message ?? "The order could not be applied.";
                _services.Report(EditError);
                return;
            }

            EditError = string.Empty;
            await RebuildAfterEditAsync().ConfigureAwait(true);
            _services.Report($"Sorted the properties of {what}. Save to write it to the file.");
        }
        finally
        {
            _services.SetBusy(false);
        }
    }

    private async Task ApplyEditAsync(Func<EditResult> edit)
    {
        EditResult result = edit();

        if (!result.Applied)
        {
            EditError = result.Message ?? "The edit was refused.";
            _services.Report(EditError);
            return;
        }

        EditError = string.Empty;
        await RebuildAfterEditAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Builds the tree again from the changed document, and puts it back the way it was.
    /// </summary>
    /// <remarks>
    /// An edit moves every byte offset behind it, so every cached position is stale and the
    /// index has to be read again. Which nodes were open is remembered as paths rather than
    /// offsets, because a path is what survives the document changing underneath it.
    /// </remarks>
    private async Task RebuildAfterEditAsync()
    {
        _document = IndexedJsonDocument.Open(_editor.Source);
        await RebuildTreeAsync().ConfigureAwait(true);
    }

    private async Task ReopenAsync(string path)
    {
        var editable = new EditableJsonSource(JsonSource.FromFile(path));
        _editor = new JsonDocumentEditor(editable);
        _document = IndexedJsonDocument.Open(editable);
        await RebuildTreeAsync().ConfigureAwait(true);
    }

    private async Task RebuildTreeAsync()
    {
        List<IReadOnlyList<JsonPathSegment>> open = Tree?.ExpandedPaths() ?? [];
        IReadOnlyList<JsonPathSegment>? selected = SelectedNode?.BuildPath();

        Tree = Build();
        await Tree.ExpandAsync(Tree.Root).ConfigureAwait(true);

        foreach (IReadOnlyList<JsonPathSegment> segments in open)
        {
            await Tree.ExpandPathAsync(segments, expandTarget: true).ConfigureAwait(true);
        }

        if (selected is not null)
        {
            SelectedNode = await Tree.ExpandPathAsync(selected).ConfigureAwait(true);
        }

        DocumentSize = ByteSize.Format(_document.Length);
        RefreshEditState();
    }

    private void RefreshEditState()
    {
        OnPropertyChanged(nameof(IsModified));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(TabTitle));
    }

    private async Task RefreshPinsAsync()
    {
        if (Tree is { } tree)
        {
            await tree.RefreshPinsAsync().ConfigureAwait(true);
        }
    }

    // ---- Selection -------------------------------------------------------------------

    partial void OnSelectedNodeChanged(JsonNodeViewModel? value)
    {
        if (value is null || value.IsMoreRow)
        {
            HasSelection = false;
            CanEditValue = false;
            SelectedPath = string.Empty;
            SelectedPointer = string.Empty;
            SelectedNodeDetail = string.Empty;
            return;
        }

        JsonNodeInfo node = value.Node;
        IReadOnlyList<JsonPathSegment> segments = value.BuildPath();

        HasSelection = true;
        SelectedPath = JsonPathFormatter.ToJsonPath(segments);
        SelectedPointer = JsonPathFormatter.ToJsonPointer(segments) is { Length: > 0 } pointer ? pointer : "/";
        SelectedType = node.Kind.ToDisplayName();
        SelectedSize = node.HasKnownExtent ? ByteSize.Format(node.ByteLength) : "still reading";
        SelectedOffset = $"{node.Start:N0}";
        SelectedChildren = node.IsContainer
            ? node.ChildCount >= 0 ? node.ChildCount.ToString("N0") : "not counted yet"
            : "—";
        SelectedNodeDetail = _document.ReadRaw(node, maxBytes: EditableValueLimit);
        EditedValue = SelectedNodeDetail;
        EditError = string.Empty;

        // A truncated rendering must not be writable: applying it would delete the rest.
        CanEditValue = node.HasKnownExtent && node.ByteLength <= EditableValueLimit;

        _ = RefreshTableAsync();

        _ = UpdateLineNumberAsync(node.Start);
    }

    /// <summary>
    /// Works out the line a value sits on without blocking the selection.
    /// </summary>
    /// <remarks>
    /// The line number is the one fact about a value that cannot be read straight out of the
    /// index: it means counting newlines from the start of the file. Doing that inline made
    /// selecting a row in a large document stutter, so the row updates immediately and the
    /// line fills in a moment later.
    /// </remarks>
    private async Task UpdateLineNumberAsync(long offset)
    {
        IndexedJsonDocument document = _document;
        SelectedLine = "…";
        long line = await Task.Run(() => document.GetLineNumber(offset)).ConfigureAwait(true);

        // A later selection may have overtaken this one, in which case its own update wins.
        if (SelectedNode is { } selected && selected.Node.Start == offset)
        {
            SelectedLine = line < 0 ? "too far in to count" : line.ToString("N0");
        }
    }

    partial void OnSelectedHitChanged(SearchHitRowViewModel? value)
    {
        if (value is not null)
        {
            _ = RevealAsync(value.ByteOffset);
        }
    }

    partial void OnSelectedDiagnosticChanged(DiagnosticRowViewModel? value)
    {
        if (value is { ByteOffset: > 0 })
        {
            _ = RevealAsync(value.ByteOffset);
        }
    }

    public async Task RevealAsync(long offset)
    {
        if (Tree is not { } tree)
        {
            return;
        }

        JsonNodeViewModel? target = await tree.RevealAsync(offset).ConfigureAwait(true);
        if (target is not null)
        {
            SelectedNode = target;
        }
    }

    // ---- Copying ---------------------------------------------------------------------

    [RelayCommand]
    private async Task CopyPathAsync() => await CopyAsync(SelectedPath).ConfigureAwait(true);

    [RelayCommand]
    private async Task CopyPointerAsync() => await CopyAsync(SelectedPointer).ConfigureAwait(true);

    [RelayCommand]
    private async Task CopyValueAsync() => await CopyAsync(SelectedNodeDetail).ConfigureAwait(true);

    private async Task CopyAsync(string text)
    {
        if (_services.Copy is null || string.IsNullOrEmpty(text))
        {
            return;
        }

        await _services.Copy(text).ConfigureAwait(true);
        _services.Report("Copied to the clipboard.");
    }

    public void Dispose()
    {
        _searchCancellation?.Dispose();
        _editor.Source.Dispose();
    }
}

internal static class CancellationExtensions
{
    /// <summary>
    /// Cancels and disposes a token source, tolerating one that has already been disposed.
    /// </summary>
    public static async Task CancelAndDisposeAsync(this CancellationTokenSource? source)
    {
        if (source is null)
        {
            return;
        }

        try
        {
            await source.CancelAsync().ConfigureAwait(true);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        source.Dispose();
    }
}
