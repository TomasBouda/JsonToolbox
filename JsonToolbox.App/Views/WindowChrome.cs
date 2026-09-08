using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Styling;

namespace JsonToolbox.App.Views;

/// <summary>
/// Keeps the window's native title bar in step with the application theme on Windows.
/// </summary>
/// <remarks>
/// The title bar is drawn by the operating system, not by the toolkit, so a dark application
/// under a light title bar is the default outcome and it looks like a bug. Windows exposes
/// this as a per-window attribute; everywhere else the platform already follows the system
/// theme and there is nothing to do.
/// </remarks>
internal static class WindowChrome
{
    /// <summary>Attribute id on Windows 10 1903 and later.</summary>
    private const int UseImmersiveDarkMode = 20;

    /// <summary>The id the same attribute had on Windows 10 builds before 19041.</summary>
    private const int UseImmersiveDarkModeLegacy = 19;

    /// <summary>
    /// Applies the current theme to the title bar, and keeps applying it whenever the theme
    /// changes for as long as the window lives.
    /// </summary>
    public static void FollowTheme(Window window)
    {
        // The native handle does not exist until the window is opened, so the first
        // application has to wait for that rather than happening in the constructor.
        window.Opened += (_, _) => Apply(window);
        window.ActualThemeVariantChanged += (_, _) => Apply(window);
    }

    private static void Apply(Window window)
    {
        // Guarded here rather than at subscription, so the check sits directly around the
        // call the platform analyzer cares about.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (window.TryGetPlatformHandle() is not { } handle || handle.Handle == IntPtr.Zero)
        {
            return;
        }

        int dark = window.ActualThemeVariant == ThemeVariant.Dark ? 1 : 0;

        // The attribute was renumbered during the Windows 10 lifetime, and setting the one
        // this build does not know is harmless, so the older id is tried as a fallback.
        if (DwmSetWindowAttribute(handle.Handle, UseImmersiveDarkMode, ref dark, sizeof(int)) != 0)
        {
            _ = DwmSetWindowAttribute(handle.Handle, UseImmersiveDarkModeLegacy, ref dark, sizeof(int));
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
