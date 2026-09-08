namespace JsonToolbox.Core.Scanning;

/// <summary>
/// A syntax error found while scanning, carrying a position the UI can navigate to.
/// </summary>
public sealed class JsonScanException : Exception
{
    public JsonScanException(string message, long byteOffset, long lineNumber, long bytePositionInLine, Exception? inner = null)
        : base(message, inner)
    {
        ByteOffset = byteOffset;
        LineNumber = lineNumber;
        BytePositionInLine = bytePositionInLine;
    }

    /// <summary>
    /// Absolute byte offset just past the last token that parsed cleanly.
    /// </summary>
    /// <remarks>
    /// This is where the good part of the document ends, which is a more useful anchor than
    /// where the parser happened to give up: for a trailing comma those are several
    /// characters apart, and only the former lets an explanation see the comma at all.
    /// </remarks>
    public long ByteOffset { get; }

    /// <summary>One-based line number.</summary>
    public long LineNumber { get; }

    /// <summary>Zero-based byte position within the line.</summary>
    public long BytePositionInLine { get; }
}
