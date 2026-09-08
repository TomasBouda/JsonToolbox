using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JsonExplorer.Core.Diff;
using JsonExplorer.Core.Documents;

namespace JsonExplorer.App.ViewModels;

/// <summary>
/// Something that can be put on one side of a comparison.
/// </summary>
/// <remarks>
/// A choice is either a document that is already open — in which case it follows that
/// document, edits and all — or a file picked from disk for the comparison alone. The
/// difference matters: comparing against an open document should compare against what is on
/// screen, including unsaved work, not against what its file used to say.
/// </remarks>
public sealed class ComparisonChoice
{
    private readonly DocumentSession? _session;
    private readonly JsonExplorerDocument? _owned;

    private ComparisonChoice(string name, DocumentSession? session, JsonExplorerDocument? owned, bool isOpenDocument)
    {
        Name = name;
        _session = session;
        _owned = owned;
        IsOpenDocument = isOpenDocument;
    }

    public string Name { get; }

    /// <summary>True for a document that is open in a tab, rather than a file read for this alone.</summary>
    public bool IsOpenDocument { get; }

    /// <summary>The tab this choice follows, or null for a file opened for the comparison alone.</summary>
    public DocumentSession? Session => _session;

    /// <summary>Resolved when the comparison runs, so an open document contributes its edits.</summary>
    public JsonExplorerDocument Document => _session?.Document ?? _owned!;

    public static ComparisonChoice ForSession(DocumentSession session) =>
        new(session.Name, session, null, isOpenDocument: true);

    public static ComparisonChoice ForFile(string path) =>
        new(Path.GetFileName(path), null, JsonExplorerDocument.OpenFile(path), isOpenDocument: false);

    /// <summary>Releases a file that was opened for the comparison; open documents are left alone.</summary>
    public void Release() => _owned?.Dispose();

    public override string ToString() => Name;
}

/// <summary>
/// The comparison: what is on each side, and the result of putting them together.
/// </summary>
/// <remarks>
/// The two sides are chosen rather than assumed. Comparing used to mean "this document against
/// a file you are about to pick", which is one of the four things a person might want; now each
/// side is picked from what is already open or from disk, and either can be changed without
/// starting again.
/// </remarks>
public sealed partial class ComparisonViewModel : ObservableObject
{
    private readonly Func<Task<string?>>? _pickFile;
    private readonly Action<string> _report;
    private readonly Action<bool> _setBusy;
    private readonly Action<double> _setProgress;
    private readonly List<ComparisonChoice> _owned = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun))]
    [NotifyPropertyChangedFor(nameof(TabTitle))]
    private ComparisonChoice? _left;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun))]
    [NotifyPropertyChangedFor(nameof(TabTitle))]
    private ComparisonChoice? _right;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    private JsonDiffTree? _tree;

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private DiffRowViewModel? _selectedDifference;

    public ComparisonViewModel(
        Func<Task<string?>>? pickFile,
        Action<string> report,
        Action<bool> setBusy,
        Action<double> setProgress)
    {
        _pickFile = pickFile;
        _report = report;
        _setBusy = setBusy;
        _setProgress = setProgress;

        Differences.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasDifferences));
            OnPropertyChanged(nameof(DifferenceCountText));
        };
    }

    /// <summary>Everything either side can be set to: the open documents, then any picked files.</summary>
    public ObservableCollection<ComparisonChoice> Choices { get; } = [];

    public ObservableCollection<DiffRowViewModel> Differences { get; } = [];

    public bool HasDifferences => Differences.Count > 0;

    public string DifferenceCountText => Differences.Count.ToString("N0");

    public bool CanRun => Left is not null && Right is not null;

    public bool HasResult => Tree is not null;

    /// <summary>What the tab says: the two sides once they are chosen, and an invitation before.</summary>
    public string TabTitle => (Left, Right) switch
    {
        ({ } left, { } right) => $"{left.Name} ↔ {right.Name}",
        _ => "Compare",
    };

    /// <summary>
    /// Keeps the list of candidates in step with the documents that are open.
    /// </summary>
    /// <remarks>
    /// A file picked from disk stays on the list once it is there, so a comparison can be
    /// re-pointed at it without picking it again. Choices that are still valid keep the very
    /// same instance, so opening or closing some other document leaves the two sides — and the
    /// result they produced — untouched rather than silently comparing everything again.
    /// </remarks>
    public void SyncChoices(IEnumerable<DocumentSession> documents)
    {
        var open = documents.ToList();

        for (int i = Choices.Count - 1; i >= 0; i--)
        {
            if (Choices[i].Session is { } session && !open.Contains(session))
            {
                Choices.RemoveAt(i);
            }
        }

        // New documents go after the ones already listed and before any picked files, so the
        // list reads in the order the tabs do.
        int insertAt = 0;
        foreach (DocumentSession session in open)
        {
            if (Choices.FirstOrDefault(c => c.Session == session) is { } existing)
            {
                insertAt = Choices.IndexOf(existing) + 1;
            }
            else
            {
                Choices.Insert(insertAt, ComparisonChoice.ForSession(session));
                insertAt++;
            }
        }

        // A side that pointed at a document which has since been closed is cleared rather than
        // left pointing at nothing.
        if (Left is { } left && !Choices.Contains(left))
        {
            Left = null;
        }

        if (Right is { } right && !Choices.Contains(right))
        {
            Right = null;
        }
    }

    [RelayCommand]
    private async Task BrowseLeftAsync() => Left = await BrowseAsync().ConfigureAwait(true) ?? Left;

    [RelayCommand]
    private async Task BrowseRightAsync() => Right = await BrowseAsync().ConfigureAwait(true) ?? Right;

    private async Task<ComparisonChoice?> BrowseAsync()
    {
        if (_pickFile is null)
        {
            return null;
        }

        string? path = await _pickFile().ConfigureAwait(true);
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        try
        {
            ComparisonChoice choice = ComparisonChoice.ForFile(path);
            _owned.Add(choice);
            Choices.Add(choice);
            return choice;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _report($"Could not open {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }

    partial void OnLeftChanged(ComparisonChoice? value) => _ = RunAsync();

    partial void OnRightChanged(ComparisonChoice? value) => _ = RunAsync();

    /// <summary>Compares the two sides, as soon as there are two.</summary>
    [RelayCommand]
    public async Task RunAsync()
    {
        if (Left is not { } left || Right is not { } right)
        {
            // A result outlives its sides only as something to misread, so losing a side clears
            // it and the panel goes back to asking for two documents.
            Differences.Clear();
            Tree = null;
            Summary = string.Empty;
            return;
        }

        _setBusy(true);
        _setProgress(0);
        _report($"Comparing {left.Name} against {right.Name}…");

        try
        {
            var progress = new Progress<double>(_setProgress);
            DiffResult result = await JsonDiffEngine
                .CompareAsync(left.Document, right.Document, progress: progress)
                .ConfigureAwait(true);

            Differences.Clear();
            foreach (JsonDifference difference in result.Differences)
            {
                Differences.Add(new DiffRowViewModel(difference));
            }

            Tree = new JsonDiffTree(left.Document, right.Document);
            await Tree.ExpandAsync(Tree.Root).ConfigureAwait(true);
            await Tree.ExpandChangesAsync().ConfigureAwait(true);

            Summary = Describe(result);
            _report(result.Identical
                ? $"{left.Name} and {right.Name} are structurally identical."
                : $"{result.Differences.Count:N0} differences between {left.Name} and {right.Name}.");
        }
        finally
        {
            _setBusy(false);
            _setProgress(0);
        }
    }

    private static string Describe(DiffResult result)
    {
        if (result.Identical)
        {
            return "Structurally identical: the same values, whatever the key order or formatting.";
        }

        var parts = new List<string>(6)
        {
            $"{result.AddedCount:N0} added",
            $"{result.RemovedCount:N0} removed",
            $"{result.ChangedCount:N0} changed",
            $"in {result.Duration.TotalSeconds:F1} s",
        };

        if (result.Truncated)
        {
            parts.Add("stopped at the reporting limit");
        }

        if (result.Incomplete)
        {
            parts.Add("some containers were too large to compare in full");
        }

        if (result.Approximate)
        {
            parts.Add("an array was too long to align, so its elements were matched by position");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// Empties the comparison and lets go of every file it opened for itself.
    /// </summary>
    /// <remarks>
    /// The object outlives the tab, so closing the tab has to leave it as it started; otherwise
    /// the next comparison would open showing the previous one's result.
    /// </remarks>
    public void Reset()
    {
        Left = null;
        Right = null;

        foreach (ComparisonChoice choice in _owned)
        {
            Choices.Remove(choice);
            choice.Release();
        }

        _owned.Clear();
    }
}
