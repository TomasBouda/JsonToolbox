using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JsonToolbox.Core.Documents;
using JsonToolbox.Core.Model;

namespace JsonToolbox.App.ViewModels;

/// <summary>One column of a table: a property name, and how wide to draw it.</summary>
public sealed class TableColumnViewModel(string name, double width)
{
    public string Name { get; } = name;

    /// <summary>Fixed, so the header and every row line up without a shared grid to keep.</summary>
    public double Width { get; } = width;
}

/// <summary>One cell: a value as the document writes it, coloured by what it is.</summary>
public sealed class TableCellViewModel(string? text, JsonKind kind, double width)
{
    public string Text { get; } = text ?? string.Empty;

    public double Width { get; } = width;

    /// <summary>A record that simply has no such property, which is not the same as null.</summary>
    public bool IsMissing { get; } = text is null;

    public bool IsString { get; } = kind == JsonKind.String;

    public bool IsNumber { get; } = kind.IsNumber();

    public bool IsBoolean { get; } = kind.IsBoolean();

    public bool IsNull { get; } = kind == JsonKind.Null;

    public bool IsContainerKind { get; } = kind.IsContainer();
}

/// <summary>One row: a record, and its cells in column order.</summary>
public sealed class TableRowViewModel(JsonNodeInfo node, string label, IReadOnlyList<TableCellViewModel> cells)
{
    public JsonNodeInfo Node { get; } = node;

    /// <summary>The element's position, which is the one thing a table row cannot infer.</summary>
    public string Label { get; } = label;

    public IReadOnlyList<TableCellViewModel> Cells { get; } = cells;
}

/// <summary>
/// An array of like-shaped records, drawn as a grid.
/// </summary>
/// <remarks>
/// <para>
/// Reading a record at a time is browsing; reading a column at a time is what somebody wants
/// from an array of eight million of them. The tree stays the way around the document and this
/// is the way to read one part of it, which is why it sits in the panel beside the tree rather
/// than replacing it.
/// </para>
/// <para>
/// What makes it affordable is the scan that already exists. Listing a container's children is
/// a pass that goes one level further down to count each child's contents, and it can pick up
/// named values while it is there — the same mechanism that shows a pinned key on every row of
/// the tree. So the columns are inferred from the first fifty records, and then every cell of
/// every row comes out of one further pass over the array. Nothing is read twice and nothing is
/// held that is not on screen.
/// </para>
/// </remarks>
public sealed partial class JsonTableViewModel : ObservableObject
{
    /// <summary>
    /// How many records are read from the document.
    /// </summary>
    /// <remarks>
    /// The same limit the tree uses, for the same reason and one more. Nobody scrolls through
    /// fifty thousand rows; and unlike the tree, every row here keeps a handful of values read
    /// out of the file, so the limit is what stops a table over a million records from being the
    /// one thing in this application that holds a document in memory.
    /// </remarks>
    private const int RowLimit = 50_000;

    /// <summary>How many of them become rows at a time.</summary>
    private const int PageSize = 500;

    private readonly IndexedJsonDocument _document;
    private readonly JsonNodeInfo _array;
    private readonly List<JsonNodeInfo> _records = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMore))]
    [NotifyPropertyChangedFor(nameof(MoreText))]
    private int _shown;

    [ObservableProperty]
    private TableRowViewModel? _selectedRow;

    [ObservableProperty]
    private bool _truncated;

    private JsonTableViewModel(IndexedJsonDocument document, JsonNodeInfo array, string path)
    {
        _document = document;
        _array = array;
        Path = path;
    }

    /// <summary>Where the array sits in the document, so the table says what it is a table of.</summary>
    public string Path { get; }

    public List<TableColumnViewModel> Columns { get; } = [];

    public ObservableRangeCollection<TableRowViewModel> Rows { get; } = [];

    public int RecordCount => _records.Count;

    public string Summary => Truncated
        ? $"the first {RecordCount:N0} records · {Columns.Count} columns"
        : $"{RecordCount:N0} records · {Columns.Count} columns";

    public bool HasMore => Shown < _records.Count;

    public string MoreText => $"Show {Math.Min(PageSize, _records.Count - Shown):N0} more · {_records.Count - Shown:N0} left";

    /// <summary>
    /// Builds the table for an array, or returns null when the array is not one.
    /// </summary>
    public static async Task<JsonTableViewModel?> BuildAsync(
        IndexedJsonDocument document,
        JsonNodeInfo array,
        string path,
        CancellationToken cancellationToken = default)
    {
        JsonTableShape shape = await Task
            .Run(() => JsonTableInference.Infer(document, array, cancellationToken: cancellationToken), cancellationToken)
            .ConfigureAwait(true);

        if (!shape.IsTabular)
        {
            return null;
        }

        var table = new JsonTableViewModel(document, array, path);
        var wanted = shape.Columns.ToHashSet(StringComparer.Ordinal);

        JsonChildIndexer indexer = await document
            .IndexChildrenAsync(array, maxChildren: RowLimit, pinnedKeys: wanted, cancellationToken: cancellationToken)
            .ConfigureAwait(true);

        table._records.AddRange(indexer.Children);
        table.Truncated = indexer.Truncated;

        // Column widths come from what the first page actually holds. A table of identifiers
        // and a table of sentences want very different widths, and the values say which is
        // which better than any default would.
        foreach (string column in shape.Columns)
        {
            table.Columns.Add(new TableColumnViewModel(column, WidthFor(column, table._records)));
        }

        table.ShowMore();
        return table;
    }

    [RelayCommand]
    public void ShowMore()
    {
        int take = Math.Min(PageSize, _records.Count - Shown);
        if (take <= 0)
        {
            return;
        }

        List<TableRowViewModel> page = [.. Enumerable
            .Range(Shown, take)
            .Select(index => BuildRow(_records[index], index))];

        Rows.AddRange(page);
        Shown += take;

        OnPropertyChanged(nameof(HasMore));
        OnPropertyChanged(nameof(MoreText));
    }

    private TableRowViewModel BuildRow(JsonNodeInfo record, int index)
    {
        List<TableCellViewModel> cells = [];

        foreach (TableColumnViewModel column in Columns)
        {
            string? value = record.PinnedValue(column.Name);
            cells.Add(new TableCellViewModel(value, KindOf(value), column.Width));
        }

        return new TableRowViewModel(record, $"[{index:N0}]", cells);
    }

    /// <summary>
    /// What a cell holds, read back from how it was written.
    /// </summary>
    /// <remarks>
    /// The scan hands back the value as the document writes it — quotes and all — rather than
    /// its type, because that rendering is what the tree shows too. The first character says
    /// which it was, which is enough to colour it and cheaper than carrying a second field
    /// through every record of a large file.
    /// </remarks>
    private static JsonKind KindOf(string? value) => value switch
    {
        null or "" => JsonKind.Undefined,
        ['"', ..] => JsonKind.String,
        ['{', ..] => JsonKind.Object,
        ['[', ..] => JsonKind.Array,
        "true" or "false" => JsonKind.True,
        "null" => JsonKind.Null,
        [>= '0' and <= '9', ..] or ['-', ..] => JsonKind.Integer,
        _ => JsonKind.Undefined,
    };

    private static double WidthFor(string column, List<JsonNodeInfo> records)
    {
        int widest = column.Length;

        foreach (JsonNodeInfo record in records.Take(PageSize))
        {
            if (record.PinnedValue(column) is { } value && value.Length > widest)
            {
                widest = value.Length;
            }
        }

        // Roughly the width of a character in the monospaced face the values are drawn in,
        // between a column that would be unreadable and one that would push the rest off screen.
        return Math.Clamp(widest * 7.6 + 18, 64, 340);
    }
}
