using System.Text;
using JsonToolbox.Core.Documents;
using JsonToolbox.Core.Scanning;

namespace JsonToolbox.Core.Diagnostics;

/// <summary>
/// Turns a parser's "unexpected character" into an explanation of what the author probably
/// wrote instead.
/// </summary>
/// <remarks>
/// A strict JSON parser reports where it gave up, which is rarely where the mistake reads
/// from. The classic case is a trailing comma: the parser complains about the closing brace,
/// while the thing to point at is the comma before it. These heuristics look at the text that
/// follows the last token which parsed cleanly and name the likely cause — the difference
/// between a message the user can act on and one they have to decode.
/// </remarks>
public static class SyntaxHints
{
    private const int LookAhead = 256;

    /// <summary>Values JavaScript accepts and JSON does not.</summary>
    private static readonly string[] NonJsonLiterals = ["NaN", "-Infinity", "Infinity", "undefined"];

    public static string? Explain(JsonSource source, JsonScanException error)
    {
        int length = (int)Math.Min(LookAhead, source.Length - error.ByteOffset);
        if (length <= 0)
        {
            return null;
        }

        string after = Encoding.UTF8.GetString(source.ReadSlice(error.ByteOffset, length)).TrimStart();
        if (after.Length == 0)
        {
            return "The document ends before the last object or array is closed.";
        }

        // The last token that parsed cleanly is a value, so what follows it is usually the
        // comma that separates it from the next one. Stepping over that comma is what lets the
        // rules below see the actual mistake: `"a": 1, // why` fails on the comment, not on the
        // comma, and reporting the comma would send the reader to the wrong character.
        if (after[0] == ',')
        {
            string beyond = after.AsSpan(1).TrimStart().ToString();

            if (beyond.Length > 0 && beyond[0] is '}' or ']')
            {
                return $"A comma precedes the closing '{beyond[0]}'. JSON does not allow a trailing comma, unlike JavaScript and JSON5.";
            }

            after = beyond;
        }

        if (after.Length == 0)
        {
            return "The document ends just after a comma, with the value that should follow it missing.";
        }

        if (after[0] == '\'')
        {
            return "Strings appear to be quoted with apostrophes. JSON requires double quotes.";
        }

        if (after.StartsWith("//", StringComparison.Ordinal) || after.StartsWith("/*", StringComparison.Ordinal))
        {
            return "The document contains comments, which JSON does not allow. The tree reads it anyway, "
                + "with comments and trailing commas skipped, but anything consuming it as strict JSON will refuse it.";
        }

        foreach (string literal in NonJsonLiterals)
        {
            if (after.StartsWith(literal, StringComparison.Ordinal))
            {
                return $"'{literal}' is not a JSON value. It usually comes from a serializer that did not guard against non-finite numbers or undefined fields.";
            }
        }

        if (LooksLikeUnquotedKey(after))
        {
            return "An object key appears not to be quoted. JSON requires every key to be a double-quoted string.";
        }

        return null;
    }

    private static bool LooksLikeUnquotedKey(string after)
    {
        if (!(char.IsLetter(after[0]) || after[0] == '_' || after[0] == '$'))
        {
            return false;
        }

        int i = 0;
        while (i < after.Length && (char.IsLetterOrDigit(after[i]) || after[i] is '_' or '$'))
        {
            i++;
        }

        while (i < after.Length && char.IsWhiteSpace(after[i]))
        {
            i++;
        }

        return i < after.Length && after[i] == ':';
    }
}
