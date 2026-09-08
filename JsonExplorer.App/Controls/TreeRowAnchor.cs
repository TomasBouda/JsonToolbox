using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace JsonExplorer.App.Controls;

/// <summary>
/// Keeps the row you just opened where you can see it.
/// </summary>
/// <remarks>
/// <para>
/// A virtualising panel does not know how tall the rows it has not built are, so it works out
/// where it is scrolled to by dividing the pixel offset by the average height of the rows it
/// has built. Expanding one row breaks that average: a row that was 24 pixels tall becomes
/// twelve rows tall, the average jumps by a third, and the panel concludes it is looking at a
/// completely different part of the list. Opening item 264 of a long array lands you at item
/// 182, with the node you opened nowhere in sight.
/// </para>
/// <para>
/// Rather than fight the estimate, this puts the view back on the row that was clicked, once
/// the new children have been laid out. It is the answer the user wanted anyway: opening
/// something should leave you looking at it.
/// </para>
/// </remarks>
internal static class TreeRowAnchor
{
    private static bool _installed;

    /// <summary>
    /// Starts watching every tree row in the application for expansion. Registering one class
    /// handler is what keeps this free of per-row subscriptions, which would otherwise have to
    /// be attached and detached as rows are recycled.
    /// </summary>
    public static void Install()
    {
        if (_installed)
        {
            return;
        }

        _installed = true;

        TreeViewItem.IsExpandedProperty.Changed.AddClassHandler<TreeViewItem>((item, args) =>
        {
            if (args.NewValue is not true)
            {
                return;
            }

            // Queued behind layout: the children have not been measured yet at the moment the
            // property changes, so scrolling now would aim at the old geometry.
            Dispatcher.UIThread.Post(item.BringIntoView, DispatcherPriority.Loaded);
        });
    }
}
