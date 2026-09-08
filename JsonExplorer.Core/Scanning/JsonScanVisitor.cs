using System.Text.Json;

namespace JsonExplorer.Core.Scanning;

/// <summary>
/// Receives every token the <see cref="JsonScanner"/> reads.
/// </summary>
/// <remarks>
/// The reader is handed over by reference rather than copied into an event object, so a
/// visitor can look at the raw UTF-8 bytes of a token without the scanner allocating a
/// string for tokens the visitor does not care about. On a multi-gigabyte file that
/// difference decides whether a scan takes seconds or minutes.
/// </remarks>
public abstract class JsonScanVisitor
{
    /// <summary>Called once before the first token.</summary>
    public virtual void OnScanStarting(long totalBytes)
    {
    }

    /// <summary>
    /// Called for every token in document order.
    /// </summary>
    /// <param name="reader">
    /// The reader positioned on the token. Do not advance it; the scanner owns the cursor.
    /// </param>
    /// <param name="start">Absolute byte offset of the first byte of the token in the document.</param>
    /// <param name="end">Absolute byte offset just past the last byte of the token.</param>
    public abstract void OnToken(ref Utf8JsonReader reader, long start, long end);

    /// <summary>Called once after the last token, or after a scan was cut short.</summary>
    public virtual void OnScanFinished(long bytesRead, bool completed)
    {
    }

    /// <summary>
    /// Lets a visitor stop the scan early — for example once a search has collected
    /// as many hits as it is willing to show.
    /// </summary>
    public virtual bool WantsMoreTokens => true;
}
