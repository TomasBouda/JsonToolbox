using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JsonToolbox.Core.Diff;
using JsonToolbox.Core.Documents;
using JsonToolbox.Core.Model;

namespace JsonToolbox.App.ViewModels;

/// <summary>
/// The window: the documents that are open, which one is on screen, and the comparison.
/// </summary>
/// <remarks>
/// Everything that belongs to a file lives in its <see cref="DocumentSession"/>, so this holds
/// only what is genuinely about the window — the list of tabs, the theme, and the comparison
/// that sits beside them. Opening a second file therefore costs nothing but a second session,
/// and switching tabs changes which one is shown rather than reloading anything.
/// </remarks>
public sealed partial class MainWindowViewModel : ObservableObject
{
    [ObservableProperty]
    private string _statusText = "Open a JSON file to begin.";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDocument))]
    [NotifyPropertyChangedFor(nameof(Title))]
    private DocumentSession? _activeDocument;

    /// <summary>True while the window shows the two documents side by side.</summary>
    [ObservableProperty]
    private bool _isDiffOpen;

    /// <summary>True once the comparison has a tab, whether or not it is the one being shown.</summary>
    [ObservableProperty]
    private bool _hasComparison;

    /// <summary>Every file that is open, in the order it was opened.</summary>
    public ObservableCollection<DocumentSession> Documents { get; } = [];

    /// <summary>
    /// The comparison. It exists from the start, empty, rather than being created when it is
    /// first opened, so nothing in the window has to bind through a null.
    /// </summary>
    public ComparisonViewModel Comparison { get; }

    public MainWindowViewModel()
    {
        // The picker is resolved at the moment it is needed rather than captured here, because
        // the view supplies it after this object exists.
        Comparison = new ComparisonViewModel(
            () => PickFileAsync?.Invoke() ?? Task.FromResult<string?>(null),
            message => StatusText = message,
            busy => IsBusy = busy,
            value => Progress = value);
    }

    partial void OnActiveDocumentChanged(DocumentSession? value) => RefreshTabs();

    partial void OnIsDiffOpenChanged(bool value) => RefreshTabs();

    /// <summary>
    /// Tells each session whether it is the one on screen.
    /// </summary>
    /// <remarks>
    /// A document tab is only highlighted when its document is both the active one and the
    /// comparison is not what is being shown — which is why the sessions are told rather than
    /// left to work it out.
    /// </remarks>
    private void RefreshTabs()
    {
        foreach (DocumentSession session in Documents)
        {
            session.IsActive = session == ActiveDocument && !IsDiffOpen;
        }
    }

    public bool HasDocument => ActiveDocument is not null;

    public string Version => AppVersion.Current;

    public string Title => ActiveDocument is { } session
        ? $"{session.TabTitle} — JSON Toolbox"
        : "JSON Toolbox";

    /// <summary>Set by the view, which owns the file picker.</summary>
    public Func<Task<string?>>? PickFileAsync { get; set; }

    /// <summary>Set by the view, which owns the clipboard.</summary>
    public Func<string, Task>? CopyAsync { get; set; }

    /// <summary>Set by the view, which owns the dialogs.</summary>
    public Func<string, string, Task<string?>>? AskForTextAsync { get; set; }

    // ---- Opening and closing documents ------------------------------------------------

    [RelayCommand]
    private async Task OpenAsync()
    {
        if (PickFileAsync is null)
        {
            return;
        }

        string? path = await PickFileAsync().ConfigureAwait(true);
        if (!string.IsNullOrEmpty(path))
        {
            OpenPath(path);
        }
    }

    public void OpenPath(string path)
    {
        // A file that is already open is shown rather than opened twice.
        if (Documents.FirstOrDefault(d => PathsMatch(d, path)) is { } existing)
        {
            ActiveDocument = existing;
            IsDiffOpen = false;
            return;
        }

        DocumentSession session;
        try
        {
            session = new DocumentSession(path, CreateServices());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"Could not open {Path.GetFileName(path)}: {ex.Message}";
            return;
        }

        session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DocumentSession.TabTitle))
            {
                OnPropertyChanged(nameof(Title));
            }
        };

        Documents.Add(session);
        Comparison.SyncChoices(Documents);
        ActiveDocument = session;
        IsDiffOpen = false;
    }

    [RelayCommand]
    private void CloseDocument(DocumentSession? session)
    {
        if (session is null)
        {
            return;
        }

        int index = Documents.IndexOf(session);
        Documents.Remove(session);
        Comparison.SyncChoices(Documents);
        session.Dispose();

        ActiveDocument = Documents.Count == 0
            ? null
            : Documents[Math.Min(index, Documents.Count - 1)];

        StatusText = Documents.Count == 0 ? "Open a JSON file to begin." : $"Closed {session.Name}.";
    }

    [RelayCommand]
    private void ShowDocument(DocumentSession? session)
    {
        if (session is not null)
        {
            ActiveDocument = session;
        }

        IsDiffOpen = false;
    }

    /// <summary>Moves to the next tab, wrapping round, with the comparison last.</summary>
    [RelayCommand]
    private void SwitchView()
    {
        if (HasComparison && !IsDiffOpen && ActiveDocument == Documents.LastOrDefault())
        {
            IsDiffOpen = true;
            return;
        }

        if (IsDiffOpen)
        {
            IsDiffOpen = false;
            ActiveDocument = Documents.FirstOrDefault();
            return;
        }

        if (ActiveDocument is { } current && Documents.Count > 1)
        {
            int next = (Documents.IndexOf(current) + 1) % Documents.Count;
            ActiveDocument = Documents[next];
        }
    }

    private static bool PathsMatch(DocumentSession session, string path) =>
        session.Document.Source.FilePath is { } existing
        && string.Equals(Path.GetFullPath(existing), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);

    private SessionServices CreateServices() => new(
        PickFile: PickFileAsync,
        Copy: CopyAsync,
        AskForText: AskForTextAsync,
        Report: message => StatusText = message,
        SetBusy: busy => IsBusy = busy,
        SetProgress: value => Progress = value,
        Activate: ShowDocument,
        Close: CloseDocument);

    // ---- The comparison ---------------------------------------------------------------

    /// <summary>
    /// Opens the comparison, with the document on screen already on the left.
    /// </summary>
    /// <remarks>
    /// A comparison is a second view of the same work, not a mode to be entered and left. It
    /// gets a tab beside the documents and stays there until it is closed, so switching is a
    /// click on the thing you want rather than dismissing the thing you do not.
    /// <para>
    /// Comparing used to mean "this document against a file you are about to pick", which is one
    /// of the several things a person might want. Now the button opens the panel and both sides
    /// are chosen there — from what is already open, or from disk.
    /// </para>
    /// </remarks>
    [RelayCommand]
    private void Compare()
    {
        Comparison.SyncChoices(Documents);

        // The document being looked at is the obvious left-hand side, so on the first opening it
        // is filled in and only the other side is left to choose. Re-opening leaves the sides
        // alone, because they were chosen deliberately.
        if (!HasComparison && ActiveDocument is { } active)
        {
            Comparison.Left = Comparison.Choices.FirstOrDefault(c => c.Session == active);
        }

        HasComparison = true;
        IsDiffOpen = true;
    }

    [RelayCommand]
    private void ShowComparison()
    {
        if (HasComparison)
        {
            IsDiffOpen = true;
        }
    }

    [RelayCommand]
    private void CloseComparison()
    {
        IsDiffOpen = false;
        HasComparison = false;
        Comparison.Reset();
    }

    // ---- The window itself -------------------------------------------------------------

    /// <summary>
    /// Cycles the application between following the system theme and being pinned to light or
    /// dark, in that order.
    /// </summary>
    [RelayCommand]
    private static void ToggleTheme()
    {
        if (Application.Current is not { } app)
        {
            return;
        }

        app.RequestedThemeVariant = app.RequestedThemeVariant switch
        {
            var v when v == ThemeVariant.Light => ThemeVariant.Dark,
            var v when v == ThemeVariant.Dark => ThemeVariant.Default,
            _ => ThemeVariant.Light,
        };
    }
}
