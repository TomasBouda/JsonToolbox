using JsonToolbox.Core.Model;
using JsonToolbox.Core.Scanning;

namespace JsonToolbox.Core.Documents;

/// <summary>
/// The columns an array of records has, worked out from the first few of them.
/// </summary>
/// <param name="Columns">Property names, in the order the records first mention them.</param>
/// <param name="Sampled">How many elements were looked at to decide.</param>
/// <param name="Uniform">
/// True when every sampled element was an object. An array of mixed or scalar values has no
/// columns worth drawing, and saying so is better than drawing an empty grid.
/// </param>
public readonly record struct JsonTableShape(
    IReadOnlyList<string> Columns,
    int Sampled,
    bool Uniform)
{
    public bool IsTabular => Uniform && Columns.Count > 0;

    public static JsonTableShape None => new([], 0, Uniform: false);
}

/// <summary>
/// Works out what a table over an array would look like.
/// </summary>
/// <remarks>
/// <para>
/// Reading a record at a time is browsing; reading a column at a time is what somebody wants
/// from an array of eight million of them. The columns are the property names, and they are
/// inferred rather than declared, because JSON has nothing to declare them with.
/// </para>
/// <para>
/// Only the first few records are looked at. Every record would be the whole file, for an
/// answer that the first fifty already give: a serialiser that writes a field on record eight
/// million and on none before it is describing an exception, not a column. What that costs is
/// bounded by the sample and not by the document, so a table over a gigabyte opens as quickly
/// as one over a kilobyte.
/// </para>
/// </remarks>
public static class JsonTableInference
{
    /// <summary>How many elements are looked at before deciding what the columns are.</summary>
    public const int SampleSize = 50;

    /// <summary>Beyond this many columns a table stops being a table.</summary>
    public const int MaxColumns = 60;

    public static JsonTableShape Infer(
        IndexedJsonDocument document,
        JsonNodeInfo array,
        int sampleSize = SampleSize,
        JsonScanOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (array.Kind != JsonKind.Array)
        {
            return JsonTableShape.None;
        }

        // The sample stops the scan after this many elements, so the cost is the first few
        // records rather than the array.
        IReadOnlyList<JsonNodeInfo> sample = document
            .IndexChildren(array, maxChildren: sampleSize, options: options, cancellationToken: cancellationToken)
            .Children;

        if (sample.Count == 0)
        {
            return JsonTableShape.None;
        }

        List<string> columns = [];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        bool uniform = true;

        foreach (JsonNodeInfo element in sample)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (element.Kind != JsonKind.Object)
            {
                uniform = false;
                break;
            }

            foreach (JsonNodeInfo property in document
                .IndexChildren(element, options: options, cancellationToken: cancellationToken)
                .Children)
            {
                if (property.Name is { Length: > 0 } name && seen.Add(name) && columns.Count < MaxColumns)
                {
                    columns.Add(name);
                }
            }
        }

        return new JsonTableShape(columns, sample.Count, uniform);
    }
}
