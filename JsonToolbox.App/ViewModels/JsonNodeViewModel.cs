using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JsonToolbox.Core.Documents;
using JsonToolbox.Core.Model;

namespace JsonToolbox.App.ViewModels;

/// <summary>
/// One row of the tree.
/// </summary>
/// <remarks>
/// A row carries its own depth and nothing else about its position: the rows live in one flat
/// list owned by <see cref="JsonTree"/>, and expanding a node inserts its children into that
/// list rather than into a collection of its own. Indentation is drawn from the depth, which
/// is why a nested structure can be shown by a control that only knows about a list.
/// </remarks>
public sealed partial class JsonNodeViewModel : ObservableObject
{
    private const double IndentPerLevel = 16;

    private readonly JsonTree? _tree;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KindName))]
    [NotifyPropertyChangedFor(nameof(Preview))]
    [NotifyPropertyChangedFor(nameof(CountBadge))]
    [NotifyPropertyChangedFor(nameof(IsString))]
    [NotifyPropertyChangedFor(nameof(IsNumber))]
    [NotifyPropertyChangedFor(nameof(IsBoolean))]
    [NotifyPropertyChangedFor(nameof(IsNull))]
    [NotifyPropertyChangedFor(nameof(IsContainerKind))]
    [NotifyPropertyChangedFor(nameof(PinnedSummary))]
    [NotifyPropertyChangedFor(nameof(HasPinnedSummary))]
    private JsonNodeInfo _node;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsPreview))]
    private bool _isExpanded;

    /// <summary>Creates the trailing row of a container that is only partly shown.</summary>
    private JsonNodeViewModel(JsonTree tree, JsonNodeViewModel owner, string text, bool canShowMore)
    {
        _tree = tree;
        Depth = owner.Depth + 1;
        Header = string.Empty;
        Owner = owner;
        MoreText = text;
        IsMoreRow = true;
        CanShowMore = canShowMore;
    }

    public JsonNodeViewModel(JsonTree tree, JsonNodeInfo node, int depth, JsonNodeViewModel? parent = null)
    {
        _tree = tree;
        _node = node;
        Depth = depth;
        Parent = parent;

        Header = node.Name ?? (parent is null ? "$" : $"[{node.Index}]");
    }

    /// <summary>How deep in the document this row sits, counted from the root at zero.</summary>
    public int Depth { get; }

    public Thickness IndentMargin => new(Depth * IndentPerLevel, 0, 0, 0);

    /// <summary>The key, or the index for an array element.</summary>
    public string Header { get; }

    /// <summary>
    /// The row this one hangs from, or <c>null</c> for the document root.
    /// </summary>
    /// <remarks>
    /// Kept so a selected row can name itself. The path is walked upwards on demand rather
    /// than stored on each node, because storing it would mean holding a string for every row
    /// in a tree that can have millions of them.
    /// </remarks>
    public JsonNodeViewModel? Parent { get; }

    public SearchHighlight? Highlight => _tree?.Highlight;

    public PinnedKeys? Pinned => _tree?.Pinned;

    // ---- The "show more" row --------------------------------------------------------

    /// <summary>True for the row at the end of a partly shown container.</summary>
    public bool IsMoreRow { get; }

    /// <summary>The container this row belongs to, for a "show more" row.</summary>
    public JsonNodeViewModel? Owner { get; }

    /// <summary>What the "show more" row says.</summary>
    public string? MoreText { get; }

    /// <summary>
    /// False when the trailing row is only reporting that the document holds more children
    /// than were read, which is a statement rather than an offer.
    /// </summary>
    public bool CanShowMore { get; }

    internal static JsonNodeViewModel CreateMoreRow(JsonTree tree, JsonNodeViewModel owner, string text, bool canShowMore) =>
        new(tree, owner, text, canShowMore);

    // ---- What the row shows ---------------------------------------------------------

    public string KindName => Node.Kind.ToDisplayName();

    public string? Preview => Node.Preview;

    public bool CanExpand => !IsMoreRow && Node.CanExpand;

    /// <summary>
    /// The child count shown at the end of a container row, once it is known.
    /// </summary>
    /// <remarks>
    /// Absent rather than zero while the container is still being read: a row that says "0"
    /// and then changes to "8 million" is worse than one that says nothing until it knows.
    /// </remarks>
    public string? CountBadge => Node.IsContainer && Node.ChildCount >= 0
        ? Node.ChildCount.ToString("N0")
        : null;

    /// <summary>
    /// Whether the collapsed rendering of a value is worth showing.
    /// </summary>
    /// <remarks>
    /// An expanded container already shows its contents on the rows below it, so repeating
    /// "{…}" beside its key is noise. Scalars always show, because that text is the value.
    /// </remarks>
    public bool ShowsPreview => !string.IsNullOrEmpty(Node.Preview) && !(Node.IsContainer && IsExpanded);

    // Exposed as booleans so the row can switch style classes, which keeps the actual colours
    // in the theme and lets them follow the light and dark variants without any code.
    public bool IsString => Node.Kind == JsonKind.String;

    public bool IsNumber => Node.Kind.IsNumber();

    public bool IsBoolean => Node.Kind.IsBoolean();

    public bool IsNull => Node.Kind == JsonKind.Null;

    public bool IsContainerKind => Node.IsContainer;

    // ---- Pinning --------------------------------------------------------------------

    /// <summary>The pinned properties of this container, shown without expanding it.</summary>
    public string? PinnedSummary => Node.PinnedSummary;

    public bool HasPinnedSummary => !string.IsNullOrEmpty(Node.PinnedSummary);

    /// <summary>
    /// True when this row names a property that can be pinned. Array elements have no name to
    /// pin, and neither does the document root.
    /// </summary>
    public bool CanPin => !IsMoreRow && Node.Name is { Length: > 0 };

    public bool IsPinned => Node.Name is { } name && Pinned?.Contains(name) == true;

    /// <summary>The label on the context menu, which flips with the state of the pin.</summary>
    public string PinMenuHeader => Node.Name is { } name
        ? IsPinned ? $"Unpin \"{name}\"" : $"Pin \"{name}\""
        : "Pin";

    /// <summary>Pins or unpins this row's property name everywhere it occurs.</summary>
    [RelayCommand]
    private void TogglePin()
    {
        if (Node.Name is { Length: > 0 } name)
        {
            Pinned?.Toggle(name);
        }
    }

    /// <summary>Re-reads whether this row's own key is pinned, for the context menu.</summary>
    internal void RefreshPinState()
    {
        OnPropertyChanged(nameof(IsPinned));
        OnPropertyChanged(nameof(PinMenuHeader));
    }

    // ---- Commands -------------------------------------------------------------------

    [RelayCommand]
    private Task ToggleAsync() => _tree?.ToggleAsync(this) ?? Task.CompletedTask;

    [RelayCommand]
    private void ShowMoreChildren()
    {
        if (Owner is { } owner)
        {
            _tree?.ShowMore(owner);
        }
    }

    /// <summary>Builds the chain of steps from the document root down to this row.</summary>
    public IReadOnlyList<JsonPathSegment> BuildPath()
    {
        var segments = new List<JsonPathSegment>();
        for (JsonNodeViewModel? current = this; current?.Parent is not null; current = current.Parent)
        {
            segments.Add(current.Node.ToPathSegment());
        }

        segments.Reverse();
        return segments;
    }
}
