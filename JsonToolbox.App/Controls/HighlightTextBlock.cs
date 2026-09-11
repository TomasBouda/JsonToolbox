using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using JsonToolbox.App.ViewModels;

namespace JsonToolbox.App.Controls;

/// <summary>
/// A text block that marks up every occurrence of the current search term.
/// </summary>
/// <remarks>
/// <para>
/// Finding a hit and then having to hunt for it inside a long value is most of the work a
/// search was supposed to save, so the term is marked up wherever the text is shown: in the
/// tree, in the result list, in the value pane, and in the text of the file.
/// </para>
/// <para>
/// Text that is a slice of the document can also mark a byte range — the value selected in
/// the tree — given where in the document it begins. The two markings are drawn together,
/// with a match showing through the mark, so a hit inside the selected value is still a hit.
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

    /// <summary>
    /// A byte range to mark as the selected value, for text that is a slice of the document.
    /// </summary>
    public static readonly StyledProperty<TextMark?> MarkProperty =
        AvaloniaProperty.Register<HighlightTextBlock, TextMark?>(nameof(Mark));

    /// <summary>Where in the document this text begins, so the mark can be placed in it.</summary>
    public static readonly StyledProperty<long> ByteOffsetProperty =
        AvaloniaProperty.Register<HighlightTextBlock, long>(nameof(ByteOffset), -1);

    public static readonly StyledProperty<IBrush?> MarkBackgroundProperty =
        AvaloniaProperty.Register<HighlightTextBlock, IBrush?>(nameof(MarkBackground));

    private SearchHighlight? _subscribed;
    private TextMark? _subscribedMark;

    static HighlightTextBlock()
    {
        TextProperty.Changed.AddClassHandler<HighlightTextBlock>((c, _) => c.Rebuild());
        HighlightProperty.Changed.AddClassHandler<HighlightTextBlock>((c, e) => c.OnHighlightChanged(e));
        MatchBackgroundProperty.Changed.AddClassHandler<HighlightTextBlock>((c, _) => c.Rebuild());
        MatchForegroundProperty.Changed.AddClassHandler<HighlightTextBlock>((c, _) => c.Rebuild());
        MarkProperty.Changed.AddClassHandler<HighlightTextBlock>((c, e) => c.OnMarkChanged(e));
        ByteOffsetProperty.Changed.AddClassHandler<HighlightTextBlock>((c, _) => c.Rebuild());
        MarkBackgroundProperty.Changed.AddClassHandler<HighlightTextBlock>((c, _) => c.Rebuild());
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

    public TextMark? Mark
    {
        get => GetValue(MarkProperty);
        set => SetValue(MarkProperty, value);
    }

    public long ByteOffset
    {
        get => GetValue(ByteOffsetProperty);
        set => SetValue(ByteOffsetProperty, value);
    }

    public IBrush? MarkBackground
    {
        get => GetValue(MarkBackgroundProperty);
        set => SetValue(MarkBackgroundProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Subscribe(Highlight);
        SubscribeMark(Mark);
        Rebuild();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Subscribe(null);
        SubscribeMark(null);
        base.OnDetachedFromVisualTree(e);
    }

    private void OnHighlightChanged(AvaloniaPropertyChangedEventArgs e)
    {
        Subscribe(e.NewValue as SearchHighlight);
        Rebuild();
    }

    private void OnMarkChanged(AvaloniaPropertyChangedEventArgs e)
    {
        SubscribeMark(e.NewValue as TextMark);
        Rebuild();
    }

    private void SubscribeMark(TextMark? mark)
    {
        if (ReferenceEquals(_subscribedMark, mark))
        {
            return;
        }

        if (_subscribedMark is not null)
        {
            _subscribedMark.PropertyChanged -= OnHighlightPropertyChanged;
        }

        _subscribedMark = mark;

        if (_subscribedMark is not null)
        {
            _subscribedMark.PropertyChanged += OnHighlightPropertyChanged;
        }
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
        (int markStart, int markEnd) = MarkedChars(text);

        if (matches.Count == 0 && markStart >= markEnd)
        {
            // Clearing the inlines lets the base class render the plain string, which is both
            // faster and correctly selectable.
            Inlines?.Clear();
            return;
        }

        // Every place the styling can change, in order: the mark's ends and each match's ends.
        // Walking them gives runs that are each uniformly styled, however the two overlap.
        var edges = new SortedSet<int> { 0, text.Length };
        if (markStart < markEnd)
        {
            edges.Add(markStart);
            edges.Add(markEnd);
        }

        foreach (Range match in matches)
        {
            (int start, int length) = match.GetOffsetAndLength(text.Length);
            edges.Add(start);
            edges.Add(start + length);
        }

        var inlines = new InlineCollection();
        int cursor = 0;
        int nextMatch = 0;

        foreach (int edge in edges)
        {
            if (edge <= cursor)
            {
                continue;
            }

            while (nextMatch < matches.Count && matches[nextMatch].End.GetOffset(text.Length) <= cursor)
            {
                nextMatch++;
            }

            bool inMatch = nextMatch < matches.Count && matches[nextMatch].Start.GetOffset(text.Length) <= cursor;
            bool inMark = cursor >= markStart && cursor < markEnd;
            var run = new Run(text[cursor..edge]);

            if (inMatch)
            {
                run.Background = MatchBackground;
                run.Foreground = MatchForeground ?? Foreground;
                run.FontWeight = FontWeight.SemiBold;
            }
            else if (inMark)
            {
                run.Background = MarkBackground;
            }

            inlines.Add(run);
            cursor = edge;
        }

        Inlines = inlines;
    }

    /// <summary>
    /// The characters of the text that the mark covers, found by walking the text's UTF-8
    /// length up to each end of the byte range.
    /// </summary>
    private (int Start, int End) MarkedChars(string text)
    {
        if (Mark is not { IsSet: true } mark || ByteOffset < 0 || text.Length == 0)
        {
            return (0, 0);
        }

        long start = mark.Start - ByteOffset;
        long end = mark.End - ByteOffset;

        if (end <= 0)
        {
            return (0, 0);
        }

        int charStart = -1;
        int charEnd = text.Length;
        long bytes = 0;

        for (int i = 0; i < text.Length; i++)
        {
            if (charStart < 0 && bytes >= start)
            {
                charStart = i;
            }

            if (bytes >= end)
            {
                charEnd = i;
                break;
            }

            char c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                bytes += 4;
                i++;
            }
            else
            {
                bytes += c < 0x80 ? 1 : c < 0x800 ? 2 : 3;
            }
        }

        if (charStart < 0)
        {
            // The range starts past this text, or exactly at its end, so none of it is marked.
            return (0, 0);
        }

        return (charStart, charEnd);
    }
}
