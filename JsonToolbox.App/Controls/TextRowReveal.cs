using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;
using JsonToolbox.App.ViewModels;

namespace JsonToolbox.App.Controls;

/// <summary>
/// Keeps the text and the tree pointing at the same value: a click in the text selects the
/// value under the pointer in the tree, and a value selected in the tree is scrolled to here.
/// </summary>
/// <remarks>
/// <para>
/// The character under the pointer is asked of the text's own layout, and its byte offset is
/// the row's start plus the UTF-8 length of the characters before it. A click beside the text
/// rather than on it — in the margin, or past the end of a short row — counts as the first
/// thing on the row, which for a pretty-printed file is the member that line holds.
/// </para>
/// <para>
/// The list's own scrolling to a selected row is turned off in favour of this, because it
/// brings the row barely into view: a value selected in the tree would land on the bottom
/// edge with everything it contains out of sight. A row that is already on screen is left
/// where it is, which is what a click on it wants; one that is not is placed a third of the
/// way down, so what follows it is what the reader sees.
/// </para>
/// </remarks>
internal static class TextRowReveal
{
    /// <summary>The height of a row before any has been laid out, matching the style.</summary>
    private const double DefaultRowHeight = 22;

    private static bool _installed;

    public static void Install()
    {
        if (_installed)
        {
            return;
        }

        _installed = true;

        // Handled events too: the list has already taken the press to select the row by the
        // time it bubbles here, and that is no reason not to select the value as well.
        InputElement.PointerPressedEvent.AddClassHandler<ListBox>(OnPressed, handledEventsToo: true);
        SelectingItemsControl.SelectedItemProperty.Changed.AddClassHandler<ListBox>(OnSelected);
    }

    private static void OnPressed(ListBox list, PointerPressedEventArgs e)
    {
        if (!IsText(list) || !e.GetCurrentPoint(list).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.Source is not Visual source || RowOf(source) is not { } row)
        {
            return;
        }

        string content = row.Text;
        int index = 0;

        if (source.FindAncestorOfType<HighlightTextBlock>(includeSelf: true) is { } text)
        {
            index = text.TextLayout.HitTestPoint(e.GetPosition(text)).TextPosition;
        }

        // Beside the text, or on the indentation before it, means the first thing on the row:
        // the member a pretty-printed line holds, rather than the whitespace around it, which
        // belongs to whatever contains the member.
        if (index < 0 || index >= content.Length)
        {
            index = 0;
        }

        while (index < content.Length && char.IsWhiteSpace(content[index]))
        {
            index++;
        }

        row.Owner.Reveal(row.Start + Encoding.UTF8.GetByteCount(content.AsSpan(0, index)));
    }

    private static void OnSelected(ListBox list, AvaloniaPropertyChangedEventArgs e)
    {
        if (!IsText(list) || e.NewValue is not TextRowViewModel row || list.Scroll is not ScrollViewer scroll)
        {
            return;
        }

        int index = (int)row.Index;

        if (list.ContainerFromIndex(index) is { } container && IsWithin(container, scroll))
        {
            return;
        }

        double rowHeight = list.GetRealizedContainers().FirstOrDefault()?.Bounds.Height ?? DefaultRowHeight;
        double target = index * rowHeight - scroll.Viewport.Height / 3;

        scroll.Offset = new Vector(scroll.Offset.X, Math.Max(0, target));
    }

    private static bool IsText(ListBox list) => list.Classes.Contains("text");

    private static bool IsWithin(Control container, ScrollViewer scroll)
    {
        if (container.TranslatePoint(new Point(0, 0), scroll) is not { } top)
        {
            return false;
        }

        return top.Y >= 0 && top.Y + container.Bounds.Height <= scroll.Viewport.Height;
    }

    private static TextRowViewModel? RowOf(Visual source) =>
        source.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as TextRowViewModel;
}
