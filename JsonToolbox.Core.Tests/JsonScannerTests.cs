using System.Text;
using System.Text.Json;
using JsonToolbox.Core.Scanning;

namespace JsonToolbox.Core.Tests;

public class JsonScannerTests
{
    /// <summary>Records every token so two scans can be compared token for token.</summary>
    private sealed class RecordingVisitor : JsonScanVisitor
    {
        public List<(JsonTokenType Type, long Start, long End, int Depth)> Tokens { get; } = [];

        public override void OnToken(ref Utf8JsonReader reader, long start, long end) =>
            Tokens.Add((reader.TokenType, start, end, reader.CurrentDepth));
    }

    private static List<(JsonTokenType Type, long Start, long End, int Depth)> Scan(string json, int bufferSize = 128 * 1024)
    {
        var visitor = new RecordingVisitor();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        JsonScanner.Scan(stream, visitor, options: new JsonScanOptions { BufferSize = bufferSize });
        return visitor.Tokens;
    }

    [Fact]
    public void Reports_absolute_offsets_of_each_token()
    {
        const string json = """{"a":12,"b":"xy"}""";

        var tokens = Scan(json);

        Assert.Equal(JsonTokenType.StartObject, tokens[0].Type);
        Assert.Equal(0, tokens[0].Start);

        // The number 12 sits at index 5..7 of the document.
        (JsonTokenType Type, long Start, long End, int Depth) number = tokens.Single(t => t.Type == JsonTokenType.Number);
        Assert.Equal(5, number.Start);
        Assert.Equal(7, number.End);
        Assert.Equal("12", json[(int)number.Start..(int)number.End]);
    }

    [Fact]
    public void Chunked_reading_produces_the_same_tokens_as_a_single_buffer()
    {
        // Every buffer size exercises a different set of token boundaries, which is where the
        // chunked reading protocol is easiest to get wrong.
        string json = BuildDocument(recordCount: 200);
        var reference = Scan(json);

        foreach (int bufferSize in new[] { 4096, 5000, 8192, 9973 })
        {
            var chunked = Scan(json, bufferSize);
            Assert.Equal(reference, chunked);
        }
    }

    [Fact]
    public void Grows_the_buffer_for_a_token_larger_than_it()
    {
        string blob = new('x', 20_000);
        string json = $$"""{"blob":"{{blob}}"}""";

        var tokens = Scan(json, bufferSize: 4096);

        (JsonTokenType Type, long Start, long End, int Depth) value = tokens.Last(t => t.Type == JsonTokenType.String);
        Assert.Equal(blob.Length + 2, value.End - value.Start);
    }

    [Fact]
    public void Skips_a_byte_order_mark()
    {
        byte[] withBom = [.. Encoding.UTF8.Preamble, .. "{\"a\":1}"u8];
        var visitor = new RecordingVisitor();

        using var stream = new MemoryStream(withBom);
        JsonScanner.Scan(stream, visitor);

        Assert.Equal(JsonTokenType.StartObject, visitor.Tokens[0].Type);
        Assert.Equal(3, visitor.Tokens[0].Start);
    }

    [Fact]
    public void Reports_the_position_of_a_syntax_error()
    {
        const string json = """
            {
              "a": 1,
            }
            """;

        var visitor = new RecordingVisitor();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        JsonScanException error = Assert.Throws<JsonScanException>(() => JsonScanner.Scan(stream, visitor));
        Assert.Equal(3, error.LineNumber);
    }

    [Fact]
    public void Lenient_mode_accepts_trailing_commas_and_comments()
    {
        const string json = """
            {
              // a comment
              "a": 1,
            }
            """;

        var visitor = new RecordingVisitor();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        JsonScanner.Scan(stream, visitor, options: new JsonScanOptions { Strictness = JsonScanStrictness.Lenient });

        Assert.Contains(visitor.Tokens, t => t.Type == JsonTokenType.Number);
    }

    [Fact]
    public void Stops_early_when_the_visitor_has_seen_enough()
    {
        var visitor = new StoppingVisitor(stopAfter: 3);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(BuildDocument(recordCount: 100)));

        JsonScanner.Scan(stream, visitor);

        Assert.Equal(3, visitor.Count);
    }

    private sealed class StoppingVisitor(int stopAfter) : JsonScanVisitor
    {
        public int Count { get; private set; }

        public override bool WantsMoreTokens => Count < stopAfter;

        public override void OnToken(ref Utf8JsonReader reader, long start, long end) => Count++;
    }

    internal static string BuildDocument(int recordCount)
    {
        var builder = new StringBuilder("""{"records":[""");
        for (int i = 0; i < recordCount; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append($$"""{"id":{{i}},"name":"item {{i}}","tags":["a","b"],"active":{{(i % 2 == 0).ToString().ToLowerInvariant()}}}""");
        }

        return builder.Append("]}").ToString();
    }
}
