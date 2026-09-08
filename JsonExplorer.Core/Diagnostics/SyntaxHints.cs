using System.Text;
using JsonExplorer.Core.Documents;
using JsonExplorer.Core.Scanning;

namespace JsonExplorer.Core.Diagnostics;

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

        if (after[0] == ',' && after.AsSpan(1).TrimStart() is { Length: > 0 } rest && (rest[0] == '}' || rest[0] == ']'))
        {
            return $"A comma precedes the closing '{rest[0]}'. JSON does not allow a trailing comma, unlike JavaScript and JSON5.";
        }

        if (after[0] == '\'')
        {
            return "Strings appear to be quoted with apostrophes. JSON requires double quotes.";
        }

        if (after.StartsWith("//", StringComparison.Ordinal) || after.StartsWith("/*", StringComparison.Ordinal))
        {
            return "The document contains comments, which JSON does not allow. Open it in lenient mode to browse it anyway.";
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
