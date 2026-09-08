using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;

namespace JsonToolbox.App.Views;

/// <summary>
/// A one-line question with a text box, for the few edits that need a value typed before they
/// can be made.
/// </summary>
/// <remarks>
/// Built in code rather than as a window of its own: it is three controls and a pair of
/// buttons, and a whole XAML file plus its view model would be more ceremony than the thing
/// deserves.
/// </remarks>
internal static class TextPrompt
{
    public static async Task<string?> ShowAsync(Window owner, string question, string initial)
    {
        var box = new TextBox
        {
            Text = initial,
            FontFamily = new("Cascadia Mono,Consolas,monospace"),
            MinWidth = 360,
        };

        var accept = new Button { Content = "OK", IsDefault = true, MinWidth = 80 };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };

        var dialog = new Window
        {
            Title = question,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
            Content = new StackPanel
            {
                Margin = new(20),
                Spacing = 14,
                Children =
                {
                    new TextBlock { Text = question },
                    box,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { cancel, accept },
                    },
                },
            },
        };

        string? result = null;
        accept.Click += (_, _) =>
        {
            result = box.Text ?? string.Empty;
            dialog.Close();
        };

        cancel.Click += (_, _) => dialog.Close();

        // Escape cancels even when the focus is in the box, which is where it starts.
        dialog.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                dialog.Close();
            }
        };

        dialog.Opened += (_, _) =>
        {
            box.SelectAll();
            box.Focus();
        };

        await dialog.ShowDialog(owner);
        return result;
    }
}
