using JsonToolbox.Core.Documents;
using JsonToolbox.Core.Model;

namespace JsonToolbox.Core.Editing;

/// <summary>
/// Rewrites a value with the properties of every object inside it in a chosen order.
/// </summary>
/// <remarks>
/// <para>
/// The rewrite is a permutation of bytes the document already contains, not a re-serialisation:
/// each member is emitted as the exact slice of the original that holds its name, its colon and
/// its value. Numbers keep the digits they were written with, strings keep their escapes, and a
/// nested value nobody reordered comes out byte for byte as it went in.
/// </para>
/// <para>
/// The separators are the part worth explaining. What sits between two members — the comma, the
/// newline, the indentation of the line the next member starts on — stays where it is, and only
/// the members move between them. So a pretty-printed document keeps its shape, a minified one
/// stays minified, and the member that ends up last gets the no-comma ending that position
/// always had. Carrying each member's own trailing text instead would move the commas around
/// with the members and produce a document that no longer parses.
/// </para>
/// </remarks>
internal static class JsonMemberSorter
{
    /// <summary>
    /// The largest value that can be sorted in one operation.
    /// </summary>
    /// <remarks>
    /// Reordering means holding the rewritten value in memory, which is the one thing the rest
    /// of the toolbox never does. The limit keeps that deliberate: sorting a section of a large
    /// document is offered, sorting a gigabyte in one go is refused with a reason rather than
    /// attempted and paid for in swap.
    /// </remarks>
    public const long MaxBytes = 64L * 1024 * 1024;

    /// <summary>How many members of one container can be reordered.</summary>
    public const int MaxChildren = 500_000;

    /// <summary>
    /// How deep the rewrite goes. Beyond this a value is copied as it stands, which matches
    /// the reader's own nesting limit rather than inventing a second one.
    /// </summary>
    private const int MaxDepth = 64;

    /// <summary>
    /// Produces the bytes <paramref name="node"/> should be replaced with.
    /// </summary>
    /// <exception cref="MemberSortRefusedException">
    /// The value is too large, or holds more members than can be reordered.
    /// </exception>
    public static byte[] Build(
        IndexedJsonDocument document,
        JsonNodeInfo node,
        JsonMemberOrder order,
        CancellationToken cancellationToken = default)
    {
        long end = EndOf(document, node);
        if (end - node.Start > MaxBytes)
        {
            throw new MemberSortRefusedException(
                $"This value is {ByteSize.Format(end - node.Start)}; sorting rewrites it in memory, "
                + $"which is only offered up to {ByteSize.Format(MaxBytes)}. Sort one of the objects inside it instead.");
        }

        var writer = new MemoryStream();
        Write(writer, document, node, order, depth: 0, cancellationToken);
        return writer.ToArray();
    }

    private static void Write(
        Stream destination,
        IndexedJsonDocument document,
        JsonNodeInfo node,
        JsonMemberOrder order,
        int depth,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        long start = node.Start;
        long end = EndOf(document, node);

        if (!node.IsContainer || depth >= MaxDepth)
        {
            Copy(destination, document, start, end);
            return;
        }

        JsonChildIndexer indexer = document.IndexChildren(node, maxChildren: MaxChildren, cancellationToken: cancellationToken);
        IReadOnlyList<JsonNodeInfo> members = indexer.Children;

        if (indexer.Truncated)
        {
            throw new MemberSortRefusedException(
                $"This container has more than {MaxChildren:N0} members, which is more than can be reordered at once.");
        }

        if (members.Count == 0)
        {
            Copy(destination, document, start, end);
            return;
        }

        // The opening bracket, and whatever whitespace follows it.
        Copy(destination, document, start, MemberStart(members[0]));

        IReadOnlyList<JsonNodeInfo> arranged = JsonMemberOrdering.Arrange(members, member => member.Name, order);

        for (int slot = 0; slot < members.Count; slot++)
        {
            JsonNodeInfo member = arranged[slot];

            // The name and the colon, verbatim; an array element contributes nothing here.
            Copy(destination, document, MemberStart(member), member.Start);
            Write(destination, document, member, order, depth + 1, cancellationToken);

            // The separator belongs to the slot, not to the member that moved into it: this is
            // the comma, the newline and the indentation that were written after slot number
            // `slot` before anything was rearranged. The last one carries the closing bracket.
            long from = EndOf(document, members[slot]);
            long to = slot + 1 < members.Count ? MemberStart(members[slot + 1]) : end;
            Copy(destination, document, from, to);
        }
    }

    private static long MemberStart(JsonNodeInfo member) =>
        member.HasNamePosition ? member.NameStart : member.Start;

    private static long EndOf(IndexedJsonDocument document, JsonNodeInfo node) =>
        node.HasKnownExtent ? node.End : document.Length;

    private static void Copy(Stream destination, IndexedJsonDocument document, long from, long to)
    {
        if (to <= from)
        {
            return;
        }

        using Stream source = document.Source.OpenRange(from, to - from);
        source.CopyTo(destination);
    }
}

/// <summary>Raised when a value cannot be reordered, with the reason to show the user.</summary>
internal sealed class MemberSortRefusedException(string message) : Exception(message);
