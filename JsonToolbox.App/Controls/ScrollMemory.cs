using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using JsonToolbox.App.ViewModels;

namespace JsonToolbox.App.Controls;

/// <summary>
/// Brings a tab back to the rows it was showing when it was left.
/// </summary>
/// <remarks>
/// <para>
/// One tree control and one text control serve every tab: switching hands them another
/// session's rows, and a list given new rows starts at the top. Which nodes are open is the
/// session's own and survives, but the scroll position is the control's, so it is copied out
/// to the session being left and copied back in for the one being shown, under a name that
/// says which list it belongs to.
/// </para>
/// <para>
/// Copying back waits for layout. The virtualising panel measures the new rows before it
/// knows how far the list extends, and an offset set before that is clamped to the extent of
/// an empty list, which is zero.
/// </para>
/// </remarks>
internal static class ScrollMemory
{
    /// <summary>Set on a list whose position should follow its session, to the name it is kept under.</summary>
    public static readonly AttachedProperty<string?> KeepProperty =
        AvaloniaProperty.RegisterAttached<ListBox, string?>("Keep", typeof(ScrollMemory));

    public static string? GetKeep(ListBox list) => list.GetValue(KeepProperty);

    public static void SetKeep(ListBox list, string? value) => list.SetValue(KeepProperty, value);

    private static bool _installed;

    /// <summary>
    /// Starts following every marked list in the application. One class handler serves them
    /// all, so a list needs no subscription of its own to be attached and released.
    /// </summary>
    public static void Install()
    {
        if (_installed)
        {
            return;
        }

        _installed = true;

        StyledElement.DataContextProperty.Changed.AddClassHandler<ListBox>((list, args) =>
        {
            if (GetKeep(list) is not { } key)
            {
                return;
            }

            if (args.OldValue is DocumentSession left && list.Scroll is { } scroll)
            {
                left.ScrollPositions[key] = scroll.Offset;
            }

            if (args.NewValue is DocumentSession shown)
            {
                Vector target = shown.ScrollPositions.GetValueOrDefault(key);
                Dispatcher.UIThread.Post(() =>
                {
                    if (list.DataContext == shown && list.Scroll is { } restored)
                    {
                        restored.Offset = target;
                    }
                }, DispatcherPriority.Loaded);
            }
        });
    }
}
