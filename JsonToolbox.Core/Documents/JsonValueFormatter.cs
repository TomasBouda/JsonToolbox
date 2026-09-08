using System.Text;
using System.Text.Json;
using JsonToolbox.Core.Model;

namespace JsonToolbox.Core.Documents;

/// <summary>
/// Renders values the way they are written in the document.
/// </summary>
/// <remarks>
/// Strings keep their quotes and containers collapse to <c>{…}</c> and <c>[…]</c>, which is
/// what every JSON viewer does and for a good reason: without the quotes there is no way to
/// tell the number <c>2006</c> from the string <c>"2006"</c>, and that difference is exactly
/// the kind of thing this application exists to make visible.
/// </remarks>
public static class JsonValueFormatter
{
    /// <summary>
    /// Values longer than this are previewed from a truncated slice rather than decoded in
    /// full, so a row holding an embedded base64 image stays cheap to render.
    /// </summary>
    public const int MaxPreviewBytes = 256;

    public static string Collapsed(JsonKind kind) => kind == JsonKind.Object ? "{…}" : "[…]";

    /// <summary>Reads the token under the reader as display text, quoting strings.</summary>
    public static string Scalar(ref Utf8JsonReader reader)
    {
        ReadOnlySpan<byte> raw = reader.ValueSpan;

        if (reader.TokenType != JsonTokenType.String)
        {
            return Encoding.UTF8.GetString(raw[..Math.Min(raw.Length, MaxPreviewBytes)]);
        }

        // Decoding resolves escape sequences, which is what the user wants to read — but only
        // for values short enough that decoding them is free.
        if (raw.Length <= MaxPreviewBytes)
        {
            return $"\"{reader.GetString()}\"";
        }

        ReadOnlySpan<byte> head = TrimToCharBoundary(raw[..MaxPreviewBytes]);
        return $"\"{Encoding.UTF8.GetString(head)}…\"";
    }

    /// <summary>
    /// Drops a trailing partial UTF-8 sequence, so truncating a preview mid-character does
    /// not produce a replacement glyph.
    /// </summary>
    private static ReadOnlySpan<byte> TrimToCharBoundary(ReadOnlySpan<byte> bytes)
    {
        for (int i = bytes.Length - 1; i >= 0 && i >= bytes.Length - 4; i--)
        {
            byte b = bytes[i];
            if ((b & 0x80) == 0)
            {
                return bytes[..(i + 1)];
            }

            if ((b & 0xC0) == 0xC0)
            {
                // A leading byte: everything from here on is an incomplete character.
                return bytes[..i];
            }
        }

        return bytes;
    }
}
