using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JsonToolbox.Core.Documents;
using JsonToolbox.Core.Model;

namespace JsonToolbox.App.ViewModels;

/// <summary>One column of a table: a property name, how wide to draw it, and how it is sorted.</summary>
public sealed partial class TableColumnViewModel(JsonTableViewModel table, string name, double width)
    : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSorted))]
    [NotifyPropertyChangedFor(nameof(Marker))]
    private TableSort _sort = TableSort.None;

    public string Name { get; } = name;

    /// <summary>Fixed, so the header and every row line up without a shared grid to keep.</summary>
    public double Width { get; } = width;

    public bool IsSorted => Sort != TableSort.None;

    /// <summary>The arrow in the heading, which is the only thing that says a table is sorted.</summary>
    public string Marker => Sort switch
    {
        TableSort.Ascending => " ↑",
        TableSort.Descending => " ↓",
        _ => string.Empty,
    };

    /// <summary>
    /// Cycles this column between the two orders and back to the order of the file.
    /// </summary>
    /// <remarks>
    /// Three states rather than two, because the order the records are written in is a real
    /// answer — for a log it is the only one that means anything — and a table that cannot be
    /// put back has taken it away.
    /// </remarks>
    [RelayCommand]
    private void CycleSort() => table.SortBy(this, Sort switch
    {
        TableSort.None => TableSort.Ascending,
        TableSort.Ascending => TableSort.Descending,
        _ => TableSort.None,
    });
}

/// <summary>How a column is ordered.</summary>
public enum TableSort
{
    /// <summary>As the file writes the records.</summary>
    None,
    Ascending,
    Descending,
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

    /// <summary>
    /// Narrows the table to the records mentioning this text, in any of their columns.
    /// </summary>
    /// <remarks>
    /// A filter over the records that were read, not a search of the file: what it can hide is
    /// what the table holds, which is the set somebody is looking through at this point. The
    /// toolbar's search reads the whole document and is the other question.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFilter))]
    private string _filter = string.Empty;

    /// <summary>The records after sorting and filtering, which is what the rows are built from.</summary>
    private List<JsonNodeInfo> _visible = [];

    public bool HasFilter => !string.IsNullOrWhiteSpace(Filter);

    partial void OnFilterChanged(string value) => Arrange();

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

    public string Summary
    {
        get
        {
            string read = Truncated ? $"the first {RecordCount:N0} records" : $"{RecordCount:N0} records";
            string shown = HasFilter ? $"{_visible.Count:N0} of {read}" : read;
            return $"{shown} · {Columns.Count} columns";
        }
    }

    public bool HasMore => Shown < _visible.Count;

    public string MoreText => $"Show {Math.Min(PageSize, _visible.Count - Shown):N0} more · {_visible.Count - Shown:N0} left";

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
            table.Columns.Add(new TableColumnViewModel(table, column, WidthFor(column, table._records)));
        }

        table.Arrange();
        return table;
    }

    /// <summary>Orders the table by one column, and by no other.</summary>
    /// <remarks>
    /// One at a time. Sorting by three columns is a query, and a table that answers queries is a
    /// different feature from a table that can be read; the arrow in one heading says plainly
    /// what the order is, where three of them would not.
    /// </remarks>
    public void SortBy(TableColumnViewModel column, TableSort sort)
    {
        foreach (TableColumnViewModel other in Columns)
        {
            other.Sort = other == column ? sort : TableSort.None;
        }

        Arrange();
    }

    /// <summary>
    /// Decides which records are shown and in what order, then starts the rows again.
    /// </summary>
    /// <remarks>
    /// The row number stays the record's position in the file rather than its position on
    /// screen, because that is what it is for: after sorting, a row labelled [4 812] is still
    /// the four-thousand-eight-hundred-and-twelfth record, and looking it up in the tree finds
    /// the same thing.
    /// </remarks>
    private void Arrange()
    {
        string needle = Filter.Trim();

        IEnumerable<JsonNodeInfo> records = _records;

        if (needle.Length > 0)
        {
            records = records.Where(record => Mentions(record, needle));
        }

        if (Columns.FirstOrDefault(c => c.IsSorted) is { } sorted)
        {
            // Records without the property are set aside and put back at the end, rather than
            // left to the comparer: reversing the order would otherwise bring them to the top,
            // and an absence is not a value that belongs at either end of a column.
            List<JsonNodeInfo> held = [.. records];
            var present = held.Where(record => record.PinnedValue(sorted.Name) is not null);
            var missing = held.Where(record => record.PinnedValue(sorted.Name) is null);

            records = (sorted.Sort == TableSort.Ascending
                    ? present.OrderBy(record => record.PinnedValue(sorted.Name), TableValueComparer.Instance)
                    : present.OrderByDescending(record => record.PinnedValue(sorted.Name), TableValueComparer.Instance))
                .Concat(missing);
        }

        _visible = [.. records];

        Rows.Clear();
        Shown = 0;
        ShowMore();

        OnPropertyChanged(nameof(Summary));
    }

    private bool Mentions(JsonNodeInfo record, string needle)
    {
        foreach (TableColumnViewModel column in Columns)
        {
            if (record.PinnedValue(column.Name) is { } value
                && value.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    [RelayCommand]
    public void ShowMore()
    {
        int take = Math.Min(PageSize, _visible.Count - Shown);
        if (take <= 0)
        {
            OnPropertyChanged(nameof(HasMore));
            OnPropertyChanged(nameof(MoreText));
            return;
        }

        List<TableRowViewModel> page = [.. _visible
            .GetRange(Shown, take)
            .Select(BuildRow)];

        Rows.AddRange(page);
        Shown += take;

        OnPropertyChanged(nameof(HasMore));
        OnPropertyChanged(nameof(MoreText));
    }

    private TableRowViewModel BuildRow(JsonNodeInfo record)
    {
        List<TableCellViewModel> cells = [];

        foreach (TableColumnViewModel column in Columns)
        {
            string? value = record.PinnedValue(column.Name);
            cells.Add(new TableCellViewModel(value, KindOf(value), column.Width));
        }

        return new TableRowViewModel(record, $"[{record.Index:N0}]", cells);
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

    /// <summary>
    /// Puts cell values in order: numbers as numbers, everything else as text.
    /// </summary>
    /// <remarks>
    /// A column of `1, 2, 10` sorted as text reads `1, 10, 2`, which is wrong in the way that
    /// makes somebody stop trusting a table. A column is not declared to hold numbers, so what
    /// decides is whether both values being compared parse as one; where they do not, the
    /// rendering the document wrote is compared ordinally. Records without the property never
    /// reach here — they are put back at the end, both ways round.
    /// </remarks>
    private sealed class TableValueComparer : IComparer<string?>
    {
        public static TableValueComparer Instance { get; } = new();

        public int Compare(string? left, string? right)
        {
            if (left is null)
            {
                return right is null ? 0 : 1;
            }

            if (right is null)
            {
                return -1;
            }

            if (double.TryParse(left, NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                && double.TryParse(right, NumberStyles.Float, CultureInfo.InvariantCulture, out double y))
            {
                return x.CompareTo(y);
            }

            return string.CompareOrdinal(left, right);
        }
    }

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
