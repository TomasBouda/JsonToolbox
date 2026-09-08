using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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
            desktop.MainWindow = new MainWindow { DataContext = viewModel };

            // Paths on the command line open straight away, so the toolbox can be wired up as
            // the "Open with" handler for .json files. Every path is opened rather than only the
            // first, so selecting two files and comparing them is one step.
            foreach (string path in desktop.Args ?? [])
            {
                if (File.Exists(path))
                {
                    viewModel.OpenPath(path);
                }
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
