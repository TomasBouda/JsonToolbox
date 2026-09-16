using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using JsonToolbox.App.Controls;
using JsonToolbox.App.ViewModels;

namespace JsonToolbox.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        WindowChrome.FollowTheme(this);
        TreeRowAnchor.Install();
        ScrollMemory.Install();
        TextRowReveal.Install();
        TabDragging.Attach(this.FindControl<ItemsControl>("DocumentTabs")!);

        // Tunnelled, so the palette gets Up, Down, Enter and Escape before its text box does.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        PaletteList.PointerReleased += OnPaletteItemPointerReleased;

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

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    // ---- Keyboard ----------------------------------------------------------------------

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        if (viewModel.Palette.IsOpen)
        {
            switch (e.Key)
            {
                case Key.Escape:
                    viewModel.Palette.Close();
                    e.Handled = true;
                    return;
                case Key.Down:
                    viewModel.Palette.MoveSelection(1);
                    e.Handled = true;
                    return;
                case Key.Up:
                    viewModel.Palette.MoveSelection(-1);
                    e.Handled = true;
                    return;
                case Key.Enter:
                    viewModel.Palette.RunSelected();
                    e.Handled = true;
                    return;
            }
        }

        if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.K)
        {
            OpenPalette();
            e.Handled = true;
        }
        else if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.F)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }

    // ---- Command palette ---------------------------------------------------------------

    private void OpenPalette()
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        viewModel.Palette.Open();
        Dispatcher.UIThread.Post(() =>
        {
            PaletteBox.Focus();
            PaletteBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void OnPaletteButtonClick(object? sender, RoutedEventArgs e) => OpenPalette();

    private void OnScrimPressed(object? sender, PointerPressedEventArgs e) => ViewModel?.Palette.Close();

    private void OnPaletteItemPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.Source is Visual source && source.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not null)
        {
            ViewModel?.Palette.RunSelected();
        }
    }

    // ---- The header as a title bar -----------------------------------------------------

    /// <summary>Dragging the header moves the window; double-clicking it toggles maximised.</summary>
    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is not null)
        {
            return;
        }

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }
        else
        {
            BeginMoveDrag(e);
        }
    }

    // ---- Services the view owns --------------------------------------------------------

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
