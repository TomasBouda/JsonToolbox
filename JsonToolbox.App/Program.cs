using Avalonia;

namespace JsonToolbox.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Asking what the arguments are is answered before anything is built: opening a window
        // to explain how to avoid opening a window would be a strange thing to do.
        if (StartupOptions.Parse(args).Help)
        {
            ConsoleOutput.WriteLine(StartupOptions.HelpText);
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>Also used by the Avalonia design-time tooling, which requires this exact name.</summary>
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
