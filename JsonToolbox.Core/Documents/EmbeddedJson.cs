using System.Text;
using System.Text.Json;

namespace JsonToolbox.Core.Documents;

/// <summary>
/// A string whose content is itself a JSON document.
/// </summary>
/// <remarks>
/// <para>
/// Double-encoded JSON usually means a serialiser ran twice: an object was turned into a string
/// and that string was put into another object, which was serialised again. It reads as
/// <c>"payload": "{\"id\":42}"</c>, and a consumer expecting <c>payload.id</c> gets a string.
/// </para>
/// <para>
/// Deciding whether a string is one of these is done in two steps, because the first is nearly
/// free and rejects almost everything. A document in a string still shows its brackets at each
/// end even while its quotes are escaped, so a value that is not bracketed is dismissed without
/// being unescaped. Only what survives that is actually parsed — and the parse is what matters:
/// <c>"{DatabaseCNN}"</c> is bracketed and is a placeholder in a template, not a document, and
/// calling it one is the kind of false finding that teaches people to ignore findings.
/// </para>
/// </remarks>
public static class EmbeddedJson
{
    /// <summary>
    /// The largest string examined.
    /// </summary>
    /// <remarks>
    /// Confirming one means holding its text, which is the one thing this application otherwise
    /// avoids. A megabyte is far more than the encoded payloads this is for and small enough
    /// that meeting a lot of them costs nothing worth measuring.
    /// </remarks>
    public const int MaxBytes = 1024 * 1024;

    /// <summary>Whether the bytes could be a document, judged without unescaping them.</summary>
    /// <param name="raw">The string's bytes as the file writes them, escapes and all.</param>
    public static bool MayBeEmbedded(ReadOnlySpan<byte> raw) =>
        raw.Length >= 2
        && raw.Length <= MaxBytes
        && ((raw[0] == (byte)'{' && raw[^1] == (byte)'}')
            || (raw[0] == (byte)'[' && raw[^1] == (byte)']'));

    /// <summary>Whether the text is exactly one JSON value.</summary>
    public static bool IsDocument(string text)
    {
        if (text.Length is < 2 or > MaxBytes)
        {
            return false;
        }

        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(text));

            if (!reader.Read())
            {
                return false;
            }

            reader.Skip();

            // Anything after the first value means the string holds something else that happens
            // to start with a document.
            return !reader.Read();
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Rewrites an embedded document the way it would be written if it were not embedded.
    /// </summary>
    /// <returns>The indented document, or <c>null</c> when the text is not one.</returns>
    public static string? Format(string text)
    {
        if (!IsDocument(text))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            using var buffer = new MemoryStream();

            // Written through the element rather than handed to JsonSerializer: serialising is a
            // reflection-based API the trimmer cannot prove safe, and this is a job it does not
            // need — the document is already parsed and knows how to write itself out.
            using (var writer = new Utf8JsonWriter(buffer, IndentedAndUnescaped))
            {
                document.RootElement.WriteTo(writer);
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Decodes a string value as the file writes it — quotes, escapes and all — into the
    /// document it holds.
    /// </summary>
    /// <param name="literal">The value exactly as it appears in the document.</param>
    /// <returns>The document written out indented, or <c>null</c> when the string holds none.</returns>
    public static string? FromLiteral(string literal)
    {
        if (literal.Length is < 4 or > MaxBytes || literal[0] != '"')
        {
            return null;
        }

        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(literal));

            return reader.Read() && reader.TokenType == JsonTokenType.String
                ? Format(reader.GetString() ?? string.Empty)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// How a decoded document is written out.
    /// </summary>
    /// <remarks>
    /// Indented, because the point of decoding is to read it; and with the relaxed encoder,
    /// because the default one turns every non-Latin character into a <c>\uXXXX</c> escape,
    /// which is correct and defeats the purpose.
    /// </remarks>
    private static readonly JsonWriterOptions IndentedAndUnescaped = new()
    {
        Indented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
