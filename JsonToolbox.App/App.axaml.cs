using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using JsonToolbox.App.ViewModels;
using JsonToolbox.App.Views;

namespace JsonToolbox.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new MainWindowViewModel();

            // Last resort. Every operation that can fail reports its own failure, but a bug
            // that gets past one of them should cost the user a message and not the document
            // they had open, with whatever they had not saved in it.
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                e.Handled = true;
                viewModel.StatusText = $"Something went wrong: {e.Exception.Message}";
            };

            desktop.MainWindow = new MainWindow { DataContext = viewModel };

            Start(viewModel, StartupOptions.Parse(desktop.Args));
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Acts on the command line: opens what it named, and compares if it asked to.
    /// </summary>
    /// <remarks>
    /// A file that is not there is reported rather than skipped in silence. Whatever the command
    /// line got wrong is said last, so it is what the status bar is left showing instead of being
    /// buried under the greeting from the last document that did open.
    /// </remarks>
    private static void Start(MainWindowViewModel viewModel, StartupOptions options)
    {
        List<string> missing = [];

        foreach (string path in options.Paths)
        {
            if (File.Exists(path))
            {
                viewModel.OpenPath(path);
            }
            else
            {
                missing.Add(Path.GetFileName(path));
            }
        }

        if (options.Compare)
        {
            viewModel.CompareOpenDocuments();
        }

        if (options.Problem is { } problem)
        {
            viewModel.StatusText = problem;
        }

        if (missing.Count > 0)
        {
            viewModel.StatusText = $"No such file: {string.Join(", ", missing)}.";
        }
    }
}
