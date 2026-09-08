using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using JsonExplorer.Core.Diff;

namespace JsonExplorer.App.Controls;

/// <summary>
/// Shows one side of a changed value with the part that actually changed marked.
/// </summary>
/// <remarks>
/// Two values side by side answer "did this change"; they do not answer "what changed". For
/// <c>"Sahakar Nagar"</c> against <c>"Sahakar Nagars"</c> the difference is one letter, and
/// finding it by reading both strings is work the view should have done. Marking the shared
/// beginning and end away leaves the edit itself standing out.
/// </remarks>
public class DiffValueTextBlock : TextBlock
{
    /// <summary>
    /// Values longer than this are shown unmarked. Comparing them costs little, but a mark
    /// somewhere off the right-hand edge of a truncated row helps nobody.
    /// </summary>
    private const int MaxMarkedLength = 4_000;

    public static readonly StyledProperty<string?> CounterpartProperty =
        AvaloniaProperty.Register<DiffValueTextBlock, string?>(nameof(Counterpart));

    public static readonly StyledProperty<IBrush?> MarkBackgroundProperty =
        AvaloniaProperty.Register<DiffValueTextBlock, IBrush?>(nameof(MarkBackground));

    static DiffValueTextBlock()
    {
        TextProperty.Changed.AddClassHandler<DiffValueTextBlock>((c, _) => c.Rebuild());
        CounterpartProperty.Changed.AddClassHandler<DiffValueTextBlock>((c, _) => c.Rebuild());
        MarkBackgroundProperty.Changed.AddClassHandler<DiffValueTextBlock>((c, _) => c.Rebuild());
    }

    /// <summary>The same value as the other document has it, or <c>null</c> when it has none.</summary>
    public string? Counterpart
    {
        get => GetValue(CounterpartProperty);
        set => SetValue(CounterpartProperty, value);
    }

    /// <summary>The wash drawn behind the changed part. Green on the right, red on the left.</summary>
    public IBrush? MarkBackground
    {
        get => GetValue(MarkBackgroundProperty);
        set => SetValue(MarkBackgroundProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Rebuild();
    }

    private void Rebuild()
    {
        string text = Text ?? string.Empty;
        string? other = Counterpart;

        if (text.Length == 0 || string.IsNullOrEmpty(other) || other == text
            || text.Length > MaxMarkedLength || other.Length > MaxMarkedLength)
        {
            // Clearing the inlines lets the base class render the plain string, which is both
            // faster and correctly selectable.
            Inlines?.Clear();
            return;
        }

        (int start, int length) = TextDiff.Compare(text, other).MiddleOf(text);

        if (length <= 0)
        {
            // This side only lost characters; there is nothing of its own to mark.
            Inlines?.Clear();
            return;
        }

        var inlines = new InlineCollection();

        if (start > 0)
        {
            inlines.Add(new Run(text[..start]));
        }

        inlines.Add(new Run(text.Substring(start, length)) { Background = MarkBackground });

        int after = start + length;
        if (after < text.Length)
        {
            inlines.Add(new Run(text[after..]));
        }

        Inlines = inlines;
    }
}
