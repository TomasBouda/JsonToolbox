using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using JsonExplorer.App.Controls;
using JsonExplorer.App.ViewModels;

namespace JsonExplorer.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        WindowChrome.FollowTheme(this);
        TreeRowAnchor.Install();

        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                // The picker and the clipboard belong to the window, not to the view model,
                // so the view model stays testable without a UI thread.
                viewModel.PickFileAsync = PickFileAsync;
                viewModel.CopyAsync = CopyAsync;
                viewModel.AskForTextAsync = AskForTextAsync;
            }
        };
    }

    private async Task<string?> PickFileAsync()
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open a JSON document",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("JSON")
                {
                    Patterns = ["*.json", "*.jsonl", "*.ndjson", "*.geojson", "*.har"],
                },
                FilePickerFileTypes.All,
            ],
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private Task<string?> AskForTextAsync(string question, string initial) =>
        TextPrompt.ShowAsync(this, question, initial);

    private async Task CopyAsync(string text)
    {
        if (Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    private static void OnDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        if (e.DataTransfer.TryGetFiles()?.FirstOrDefault()?.TryGetLocalPath() is { } path)
        {
            viewModel.OpenPath(path);
        }
    }
}
