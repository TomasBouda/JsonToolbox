using JsonToolbox.Core.Documents;

namespace JsonToolbox.Core.Tests;

public class EmbeddedJsonTests
{
    [Fact]
    public void A_document_serialised_into_a_string_is_recognised()
    {
        Assert.True(EmbeddedJson.IsDocument("""{"id":42,"name":"Alice"}"""));
        Assert.True(EmbeddedJson.IsDocument("""[1,2,3]"""));
    }

    [Fact]
    public void A_placeholder_that_merely_looks_bracketed_is_not_a_document()
    {
        // Template files are full of these, and calling every one of them double-encoded JSON is
        // the kind of false finding that teaches people to ignore findings.
        Assert.False(EmbeddedJson.IsDocument("{DatabaseCNN}"));
        Assert.False(EmbeddedJson.IsDocument("{CustomerID}_{InstanceID}"));
        Assert.False(EmbeddedJson.IsDocument("[Drama]"));
    }

    [Fact]
    public void Text_that_starts_with_a_document_and_carries_on_is_not_one()
    {
        Assert.False(EmbeddedJson.IsDocument("""{"a":1} and then some"""));
    }

    [Fact]
    public void The_cheap_check_rejects_what_it_can_without_unescaping()
    {
        Assert.True(EmbeddedJson.MayBeEmbedded("{}"u8));
        Assert.True(EmbeddedJson.MayBeEmbedded("[]"u8));
        Assert.False(EmbeddedJson.MayBeEmbedded("hello"u8));
        Assert.False(EmbeddedJson.MayBeEmbedded("{unclosed"u8));
    }

    [Fact]
    public void A_string_value_is_decoded_into_the_document_it_holds()
    {
        // The literal as the file writes it: quotes around it, quotes inside it escaped.
        string? decoded = EmbeddedJson.FromLiteral("\"{\\\"id\\\":42}\"");

        Assert.NotNull(decoded);
        Assert.Contains("\"id\": 42", decoded);
    }

    [Fact]
    public void A_decoded_document_is_written_out_indented()
    {
        string? decoded = EmbeddedJson.FromLiteral("\"{\\\"a\\\":1,\\\"b\\\":2}\"");

        Assert.NotNull(decoded);
        Assert.Contains("\n", decoded);
    }

    [Fact]
    public void Decoding_keeps_the_letters_the_document_was_written_with()
    {
        // Escaping these back into \uXXXX would be valid and would defeat the point of decoding.
        string? decoded = EmbeddedJson.FromLiteral("\"{\\\"mesto\\\":\\\"Plzeň\\\"}\"");

        Assert.NotNull(decoded);
        Assert.Contains("Plzeň", decoded);
    }

    [Fact]
    public void An_ordinary_string_decodes_to_nothing()
    {
        Assert.Null(EmbeddedJson.FromLiteral("\"just text\""));
        Assert.Null(EmbeddedJson.FromLiteral("\"{DatabaseCNN}\""));
    }

    [Fact]
    public void A_value_that_is_not_a_string_decodes_to_nothing()
    {
        Assert.Null(EmbeddedJson.FromLiteral("""{"id":42}"""));
        Assert.Null(EmbeddedJson.FromLiteral("42"));
    }
}
