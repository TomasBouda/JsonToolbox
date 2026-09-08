using System.Text;
using JsonToolbox.Core.Documents;

namespace JsonToolbox.Core.Tests;

public class LineNumberTests
{
    [Fact]
    public void Counts_the_lines_before_an_offset()
    {
        const string json = "{\n  \"a\": 1,\n  \"b\": 2\n}";
        using IndexedJsonDocument document = IndexedJsonDocument.OpenText(json);

        int offsetOfB = json.IndexOf("\"b\"", StringComparison.Ordinal);

        Assert.Equal(1, document.GetLineNumber(0));
        Assert.Equal(3, document.GetLineNumber(offsetOfB));
    }

    [Fact]
    public void Counts_across_the_internal_chunk_boundary()
    {
        // The counter reads in 64 kB chunks, so a document larger than that is what proves
        // newlines are not lost or double counted where two chunks meet.
        var builder = new StringBuilder("[\n");
        for (int i = 0; i < 20_000; i++)
        {
            builder.Append("  ").Append(i).Append(",\n");
        }

        builder.Append("  0\n]");
        string json = builder.ToString();

        using IndexedJsonDocument document = IndexedJsonDocument.OpenText(json);

        Assert.Equal(20_002, document.GetLineNumber(json.Length - 2));
    }

    [Fact]
    public void Refuses_to_count_further_than_it_was_allowed()
    {
        using IndexedJsonDocument document = IndexedJsonDocument.OpenText("[1, 2, 3]");

        // Past the limit the byte offset is the honest answer and the line number is not, so
        // the caller is told rather than made to wait.
        Assert.Equal(-1, document.GetLineNumber(offset: 5, maxBytesToScan: 4));
    }

    [Fact]
    public void A_minified_document_is_all_one_line()
    {
        const string json = """{"a":1,"b":[2,3],"c":{"d":4}}""";
        using IndexedJsonDocument document = IndexedJsonDocument.OpenText(json);

        Assert.Equal(1, document.GetLineNumber(json.Length - 1));
    }
}
