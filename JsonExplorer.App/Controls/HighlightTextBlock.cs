using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using JsonExplorer.App.ViewModels;

namespace JsonExplorer.App.Controls;

/// <summary>
/// A text block that marks up every occurrence of the current search term.
/// </summary>
/// <remarks>
/// <para>
/// Finding a hit and then having to hunt for it inside a long value is most of the work a
/// search was supposed to save, so the term is marked up wherever the text is shown: in the
/// tree, in the result list, and in the value pane.
/// </para>
/// <para>
/// The control listens to the shared <see cref="SearchHighlight"/> only while it is attached
/// to the tree. Rows are recycled as the user scrolls, and a subscription that outlived its
/// row would keep the whole row alive with it.
/// </para>
/// </remarks>
/// <remarks>
/// Plain rather than selectable on purpose. A selectable text block takes the pointer press
/// for itself to start a text selection, and inside a list row that means clicking a value
/// never selects the row it belongs to — the row highlights on hover and then does nothing.
/// Selecting the row is what a reader wants here; copying the text is what the value pane and
/// its Copy button are for.
/// </remarks>
public class HighlightTextBlock : TextBlock
{
    /// <summary>
    /// Longer text is shown without markup. Splitting a very long value into runs costs more
    /// than the marks are worth, and no one reads a match that far down a single row.
    /// </summary>
    private const int MaxHighlightedLength = 200_000;

    public static readonly StyledProperty<SearchHighlight?> HighlightProperty =
        AvaloniaProperty.Register<HighlightTextBlock, SearchHighlight?>(nameof(Highlight));

    public static readonly StyledProperty<IBrush?> MatchBackgroundProperty =
        AvaloniaProperty.Register<HighlightTextBlock, IBrush?>(nameof(MatchBackground));

    public static readonly StyledProperty<IBrush?> MatchForegroundProperty =
        AvaloniaProperty.Register<HighlightTextBlock, IBrush?>(nameof(MatchForeground));

    private SearchHighlight? _subscribed;

    static HighlightTextBlock()
    {
        TextProperty.Changed.AddClassHandler<HighlightTextBlock>((c, _) => c.Rebuild());
        HighlightProperty.Changed.AddClassHandler<HighlightTextBlock>((c, e) => c.OnHighlightChanged(e));
        MatchBackgroundProperty.Changed.AddClassHandler<HighlightTextBlock>((c, _) => c.Rebuild());
        MatchForegroundProperty.Changed.AddClassHandler<HighlightTextBlock>((c, _) => c.Rebuild());
    }

    /// <summary>The shared search term. Set once from the view model that owns the search.</summary>
    public SearchHighlight? Highlight
    {
        get => GetValue(HighlightProperty);
        set => SetValue(HighlightProperty, value);
    }

    public IBrush? MatchBackground
    {
        get => GetValue(MatchBackgroundProperty);
        set => SetValue(MatchBackgroundProperty, value);
    }

    public IBrush? MatchForeground
    {
        get => GetValue(MatchForegroundProperty);
        set => SetValue(MatchForegroundProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Subscribe(Highlight);
        Rebuild();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Subscribe(null);
        base.OnDetachedFromVisualTree(e);
    }

    private void OnHighlightChanged(AvaloniaPropertyChangedEventArgs e)
    {
        Subscribe(e.NewValue as SearchHighlight);
        Rebuild();
    }

    private void Subscribe(SearchHighlight? highlight)
    {
        if (ReferenceEquals(_subscribed, highlight))
        {
            return;
        }

        if (_subscribed is not null)
        {
            _subscribed.PropertyChanged -= OnHighlightPropertyChanged;
        }

        _subscribed = highlight;

        if (_subscribed is not null)
        {
            _subscribed.PropertyChanged += OnHighlightPropertyChanged;
        }
    }

    private void OnHighlightPropertyChanged(object? sender, PropertyChangedEventArgs e) => Rebuild();

    private void Rebuild()
    {
        string text = Text ?? string.Empty;

        IReadOnlyList<Range> matches = text.Length <= MaxHighlightedLength
            ? Highlight?.Find(text) ?? []
            : [];

        if (matches.Count == 0)
        {
            // Clearing the inlines lets the base class render the plain string, which is both
            // faster and correctly selectable.
            Inlines?.Clear();
            return;
        }

        var inlines = new InlineCollection();
        int cursor = 0;

        foreach (Range match in matches)
        {
            (int start, int length) = match.GetOffsetAndLength(text.Length);

            if (start > cursor)
            {
                inlines.Add(new Run(text[cursor..start]));
            }

            inlines.Add(new Run(text.Substring(start, length))
            {
                Background = MatchBackground,
                Foreground = MatchForeground ?? Foreground,
                FontWeight = FontWeight.SemiBold,
            });

            cursor = start + length;
        }

        if (cursor < text.Length)
        {
            inlines.Add(new Run(text[cursor..]));
        }

        Inlines = inlines;
    }
}
