using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace JsonToolbox.App.ViewModels;

/// <summary>One entry of the command palette: a document to jump to, a panel, or an action.</summary>
public sealed record PaletteItem(string Group, string Icon, string Title, string Hint, string Shortcut, Action Run);

/// <summary>
/// The Ctrl+K palette: a fuzzy search across the open documents, the panels beside the tree,
/// and the actions on whatever is on screen.
/// </summary>
/// <remarks>
/// The entries are rebuilt every time the palette opens, so they reflect what is open and
/// what the active document can do right now — an action that would do nothing is not
/// offered. Matching is deliberately simple: a prefix beats a substring beats a subsequence,
/// and the hint counts half as much as the title.
/// </remarks>
public sealed partial class CommandPaletteViewModel : ObservableObject
{
    /// <summary>The panels beside the tree, in the order the tab strip shows them.</summary>
    private static readonly string[] Panels = ["Value", "Text", "Table", "Problems", "Structure", "Diff", "Search"];

    private readonly MainWindowViewModel _window;
    private readonly List<PaletteItem> _all = [];

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private ObservableCollection<PaletteItem> _items = [];

    [ObservableProperty]
    private PaletteItem? _selectedItem;

    private string _query = string.Empty;

    public CommandPaletteViewModel(MainWindowViewModel window)
    {
        _window = window;
    }

    public string Query
    {
        get => _query;
        set
        {
            if (SetProperty(ref _query, value))
            {
                Filter();
            }
        }
    }

    public void Open()
    {
        Build();
        Query = string.Empty;
        Filter();
        IsOpen = true;
    }

    public void Close() => IsOpen = false;

    public void MoveSelection(int delta)
    {
        if (Items.Count == 0)
        {
            return;
        }

        int index = SelectedItem is null ? -1 : Items.IndexOf(SelectedItem);
        SelectedItem = Items[Math.Clamp(index + delta, 0, Items.Count - 1)];
    }

    public void RunSelected()
    {
        if ((SelectedItem ?? Items.FirstOrDefault()) is not { } item)
        {
            return;
        }

        Close();
        item.Run();
    }

    private void Build()
    {
        _all.Clear();
        DocumentSession? active = _window.IsDiffOpen ? null : _window.ActiveDocument;
        string name = active?.Name ?? string.Empty;

        if (active is not null)
        {
            _all.Add(new PaletteItem("Actions", "fa-solid fa-wand-magic-sparkles", "Inspect", $"{name} · problems and structure", "", () => active.InspectCommand.Execute(null)));
            _all.Add(new PaletteItem("Actions", "fa-solid fa-code-compare", "Compare…", "choose two documents", "", () => _window.CompareCommand.Execute(null)));

            if (active.IsModified)
            {
                _all.Add(new PaletteItem("Actions", "fa-solid fa-floppy-disk", "Save", name, "Ctrl+S", () => active.SaveCommand.Execute(null)));
            }

            if (active.CanUndo)
            {
                _all.Add(new PaletteItem("Actions", "fa-solid fa-rotate-left", "Undo", name, "Ctrl+Z", () => active.UndoCommand.Execute(null)));
            }

            if (active.CanRedo)
            {
                _all.Add(new PaletteItem("Actions", "fa-solid fa-rotate-right", "Redo", name, "Ctrl+Y", () => active.RedoCommand.Execute(null)));
            }

            if (active.HasSelection)
            {
                _all.Add(new PaletteItem("Actions", "fa-solid fa-copy", "Copy path", active.SelectedPath, "", () => active.CopyPathCommand.Execute(null)));
                _all.Add(new PaletteItem("Actions", "fa-solid fa-copy", "Copy value", active.SelectedPath, "", () => active.CopyValueCommand.Execute(null)));
            }

            if (!active.Pinned.IsEmpty)
            {
                _all.Add(new PaletteItem("Actions", "fa-solid fa-thumbtack", "Clear pins", string.Join(", ", active.Pinned.Ordered), "", () => active.ClearPinsCommand.Execute(null)));
            }

            _all.Add(new PaletteItem("Actions", "fa-solid fa-xmark", "Close document", name, "Ctrl+W", () => active.CloseCommand.Execute(null)));

            for (int i = 0; i < Panels.Length; i++)
            {
                int index = i;
                _all.Add(new PaletteItem("Jump to", "fa-solid fa-table-columns", Panels[i], name, "", () =>
                {
                    _window.IsDiffOpen = false;
                    active.SelectedPanel = index;
                }));
            }
        }

        foreach (DocumentSession session in _window.Documents)
        {
            DocumentSession captured = session;
            string state = session.IsModified ? "unsaved changes" : session.DocumentSize;
            _all.Add(new PaletteItem("Jump to", "fa-regular fa-file-lines", session.Name, state, "", () => captured.ActivateCommand.Execute(null)));
        }

        if (_window.HasComparison)
        {
            _all.Add(new PaletteItem("Jump to", "fa-solid fa-code-compare", "Comparison", _window.Comparison.TabTitle, "", () => _window.ShowComparisonCommand.Execute(null)));
        }

        _all.Add(new PaletteItem("App", "fa-solid fa-folder-open", "Open a file…", "any size", "Ctrl+O", () => _window.OpenCommand.Execute(null)));
        _all.Add(new PaletteItem("App", "fa-solid fa-clock-rotate-left", "Reopen closed document", "the one closed last", "Ctrl+Shift+T", () => _window.ReopenClosedCommand.Execute(null)));
        _all.Add(new PaletteItem("App", "fa-solid fa-circle-half-stroke", "Toggle light / dark", "remembered for next start", "Ctrl+Shift+L", () => _window.ToggleThemeCommand.Execute(null)));
    }

    private void Filter()
    {
        string query = Query.Trim();
        IEnumerable<PaletteItem> result = query.Length == 0
            ? _all
            : _all.Select(item => (Item: item, Score: Score(item, query)))
                  .Where(scored => scored.Score > 0)
                  .OrderByDescending(scored => scored.Score)
                  .Select(scored => scored.Item);

        Items = new ObservableCollection<PaletteItem>(result.Take(12));
        SelectedItem = Items.FirstOrDefault();
    }

    private static int Score(PaletteItem item, string query) =>
        Math.Max(Score(item.Title, query), Score(item.Hint, query) / 2);

    private static int Score(string text, string query)
    {
        if (text.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 300;
        }

        if (text.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return 200;
        }

        int matched = 0;
        foreach (char c in text)
        {
            if (matched < query.Length && char.ToLowerInvariant(c) == char.ToLowerInvariant(query[matched]))
            {
                matched++;
            }
        }

        return matched == query.Length ? 100 : 0;
    }
}
