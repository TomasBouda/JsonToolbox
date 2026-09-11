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
/// One tree control serves every tab: switching hands it another session's rows, and a list
/// given new rows starts at the top. Which nodes are open is the session's own and survives,
/// but the scroll position is the control's, so it is copied out to the session being left
/// and copied back in for the one being shown.
/// </para>
/// <para>
/// Copying back waits for layout. The virtualising panel measures the new rows before it
/// knows how far the list extends, and an offset set before that is clamped to the extent of
/// an empty list, which is zero.
/// </para>
/// </remarks>
internal static class TreeScrollMemory
{
    /// <summary>Set on a list whose position should follow its session.</summary>
    public static readonly AttachedProperty<bool> KeepProperty =
        AvaloniaProperty.RegisterAttached<ListBox, bool>("Keep", typeof(TreeScrollMemory));

    public static bool GetKeep(ListBox list) => list.GetValue(KeepProperty);

    public static void SetKeep(ListBox list, bool value) => list.SetValue(KeepProperty, value);

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
            if (!GetKeep(list))
            {
                return;
            }

            if (args.OldValue is DocumentSession left && list.Scroll is { } scroll)
            {
                left.TreeScroll = scroll.Offset;
            }

            if (args.NewValue is DocumentSession shown)
            {
                Vector target = shown.TreeScroll;
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
