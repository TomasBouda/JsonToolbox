using System.Buffers;
using JsonExplorer.Core.Documents;
using JsonExplorer.Core.Model;

namespace JsonExplorer.Core.Diff;

public enum DiffState
{
    /// <summary>The same value on both sides.</summary>
    Unchanged,

    /// <summary>On the right only.</summary>
    Added,

    /// <summary>On the left only.</summary>
    Removed,

    /// <summary>On both sides, but not the same.</summary>
    Changed,
}

/// <summary>
/// One value of the left document lined up with its counterpart on the right.
/// </summary>
/// <param name="Left">The value on the left, or <c>null</c> when it is only on the right.</param>
/// <param name="Right">The value on the right, or <c>null</c> when it is only on the left.</param>
/// <param name="Name">The property name, or <c>null</c> for an array element.</param>
/// <param name="Index">The position to show for an array element.</param>
public readonly record struct JsonDiffPair(
    JsonNodeInfo? Left,
    JsonNodeInfo? Right,
    DiffState State,
    string? Name,
    int Index)
{
    public bool HasLeft => Left is not null;

    public bool HasRight => Right is not null;

    /// <summary>True when both sides are containers worth opening.</summary>
    public bool CanExpand =>
        (Left?.IsContainer ?? false) || (Right?.IsContainer ?? false);
}

/// <summary>
/// Works out which value on one side corresponds to which value on the other.
/// </summary>
/// <remarks>
/// This is the part of a diff that decides what is being compared with what, and it is kept
/// separate from the part that reports findings because both a list of differences and a
/// side-by-side view need exactly the same answer. Objects are matched by key; arrays are
/// matched by identifier where the records carry one, by longest common subsequence where they
/// do not, and by position only when the arrays are too long for either.
/// </remarks>
public sealed class JsonDiffPairing(
    JsonExplorerDocument left,
    JsonExplorerDocument right,
    DiffOptions? options = null,
    CancellationToken cancellationToken = default)
{
    private static readonly string[] IdentityCandidates =
        ["id", "_id", "uuid", "guid", "key", "code", "sku", "name", "email"];

    private readonly DiffOptions _options = options ?? new DiffOptions();

    public JsonExplorerDocument Left => left;

    public JsonExplorerDocument Right => right;

    /// <summary>True when some container held more children than the pairing would read.</summary>
    public bool Incomplete { get; private set; }

    /// <summary>True when an array had to be matched by position because it was too long to align.</summary>
    public bool Approximate { get; private set; }

    /// <summary>
    /// Compares the bytes of two values. Identical bytes mean an identical subtree, however
    /// large, and differing ones almost always differ near the start.
    /// </summary>
    public bool BytesEqual(JsonNodeInfo leftNode, JsonNodeInfo rightNode) =>
        JsonByteComparer.RangesEqual(
            left.Source, leftNode.Start, Extent(left, leftNode),
            right.Source, rightNode.Start, Extent(right, rightNode),
            cancellationToken);

    public DiffState Classify(JsonNodeInfo? leftNode, JsonNodeInfo? rightNode) => (leftNode, rightNode) switch
    {
        (null, not null) => DiffState.Added,
        (not null, null) => DiffState.Removed,
        ({ } l, { } r) => BytesEqual(l, r) ? DiffState.Unchanged : DiffState.Changed,
        _ => DiffState.Unchanged,
    };

    /// <summary>
    /// Lines up the direct children of two containers.
    /// </summary>
    public List<JsonDiffPair> PairChildren(JsonNodeInfo? leftNode, JsonNodeInfo? rightNode)
    {
        List<JsonNodeInfo> leftChildren = leftNode is { IsContainer: true } l ? Children(left, l) : [];
        List<JsonNodeInfo> rightChildren = rightNode is { IsContainer: true } r ? Children(right, r) : [];

        // One side missing, or the two are different kinds of container: there is nothing to
        // match, so each side stands alone.
        JsonKind? kind = leftNode?.Kind ?? rightNode?.Kind;
        if (leftNode is null || rightNode is null || leftNode.Value.Kind != rightNode.Value.Kind)
        {
            return Unmatched(leftChildren, rightChildren);
        }

        return kind == JsonKind.Object
            ? PairObjects(leftChildren, rightChildren)
            : PairArrays(leftNode.Value, rightNode.Value, leftChildren, rightChildren);
    }

    private List<JsonDiffPair> Unmatched(List<JsonNodeInfo> leftChildren, List<JsonNodeInfo> rightChildren)
    {
        var pairs = new List<JsonDiffPair>(leftChildren.Count + rightChildren.Count);
        pairs.AddRange(leftChildren.Select(c => new JsonDiffPair(c, null, DiffState.Removed, c.Name, c.Index)));
        pairs.AddRange(rightChildren.Select(c => new JsonDiffPair(null, c, DiffState.Added, c.Name, c.Index)));
        return pairs;
    }

    private List<JsonDiffPair> PairObjects(List<JsonNodeInfo> leftChildren, List<JsonNodeInfo> rightChildren)
    {
        var byName = new Dictionary<string, JsonNodeInfo>(rightChildren.Count, StringComparer.Ordinal);
        foreach (JsonNodeInfo child in rightChildren)
        {
            // A duplicate key is a defect the inspector reports; here the first one wins,
            // which is what most parsers do with the document as a whole.
            if (child.Name is { } name)
            {
                byName.TryAdd(name, child);
            }
        }

        var matched = new HashSet<string>(StringComparer.Ordinal);
        var pairs = new List<JsonDiffPair>(Math.Max(leftChildren.Count, rightChildren.Count));

        foreach (JsonNodeInfo child in leftChildren)
        {
            if (child.Name is not { } name)
            {
                continue;
            }

            if (byName.TryGetValue(name, out JsonNodeInfo counterpart))
            {
                matched.Add(name);
                pairs.Add(new JsonDiffPair(child, counterpart, Classify(child, counterpart), name, child.Index));
            }
            else
            {
                pairs.Add(new JsonDiffPair(child, null, DiffState.Removed, name, child.Index));
            }
        }

        foreach (JsonNodeInfo child in rightChildren)
        {
            if (child.Name is { } name && !matched.Contains(name))
            {
                pairs.Add(new JsonDiffPair(null, child, DiffState.Added, name, child.Index));
            }
        }

        return pairs;
    }

    private List<JsonDiffPair> PairArrays(
        JsonNodeInfo leftNode,
        JsonNodeInfo rightNode,
        List<JsonNodeInfo> leftChildren,
        List<JsonNodeInfo> rightChildren)
    {
        var pairs = new List<JsonDiffPair>(Math.Max(leftChildren.Count, rightChildren.Count));

        // The head and tail the two arrays already agree on are matched straight across. This
        // is the cheap part and settles most comparisons on its own.
        int head = 0;
        while (head < leftChildren.Count && head < rightChildren.Count &&
               BytesEqual(leftChildren[head], rightChildren[head]))
        {
            pairs.Add(Same(leftChildren[head], rightChildren[head], head));
            head++;
        }

        int lastLeft = leftChildren.Count - 1;
        int lastRight = rightChildren.Count - 1;
        var tail = new List<JsonDiffPair>();
        while (lastLeft >= head && lastRight >= head &&
               BytesEqual(leftChildren[lastLeft], rightChildren[lastRight]))
        {
            tail.Add(Same(leftChildren[lastLeft], rightChildren[lastRight], lastLeft));
            lastLeft--;
            lastRight--;
        }

        tail.Reverse();

        int leftCount = lastLeft - head + 1;
        int rightCount = lastRight - head + 1;

        if (leftCount > 0 || rightCount > 0)
        {
            pairs.AddRange(PairWindow(leftNode, rightNode, leftChildren, rightChildren, head, leftCount, rightCount));
        }

        pairs.AddRange(tail);
        return pairs;
    }

    private List<JsonDiffPair> PairWindow(
        JsonNodeInfo leftNode,
        JsonNodeInfo rightNode,
        List<JsonNodeInfo> leftChildren,
        List<JsonNodeInfo> rightChildren,
        int head,
        int leftCount,
        int rightCount)
    {
        if (leftCount <= 0 || rightCount <= 0)
        {
            return Unmatched(
                leftChildren.GetRange(head, Math.Max(leftCount, 0)),
                rightChildren.GetRange(head, Math.Max(rightCount, 0)));
        }

        // Identity first: it is the only pairing that survives every record having been edited,
        // which is the case content matching gets most wrong.
        if (FindIdentity(leftNode, rightNode, leftChildren, rightChildren, head, leftCount, rightCount) is { } identity)
        {
            return PairByIdentity(leftChildren, rightChildren, identity.Left, identity.Right, head);
        }

        if (JsonArrayAligner.CanAlign(leftCount, rightCount))
        {
            return PairByAlignment(leftChildren, rightChildren, head, leftCount, rightCount);
        }

        Approximate = true;
        return PairByPosition(leftChildren, rightChildren, head, leftCount, rightCount);
    }

    private List<JsonDiffPair> PairByIdentity(
        List<JsonNodeInfo> leftChildren,
        List<JsonNodeInfo> rightChildren,
        string?[] leftIds,
        string?[] rightIds,
        int head)
    {
        var rightByIdentity = new Dictionary<string, int>(rightIds.Length, StringComparer.Ordinal);
        for (int i = 0; i < rightIds.Length; i++)
        {
            if (rightIds[i] is { } id)
            {
                rightByIdentity[id] = i;
            }
        }

        var matched = new bool[rightIds.Length];
        var pairs = new List<JsonDiffPair>(Math.Max(leftIds.Length, rightIds.Length));

        for (int i = 0; i < leftIds.Length; i++)
        {
            JsonNodeInfo child = leftChildren[head + i];

            if (leftIds[i] is { } id && rightByIdentity.TryGetValue(id, out int j))
            {
                matched[j] = true;
                JsonNodeInfo counterpart = rightChildren[head + j];
                pairs.Add(new JsonDiffPair(child, counterpart, Classify(child, counterpart), null, head + i));
            }
            else
            {
                pairs.Add(new JsonDiffPair(child, null, DiffState.Removed, null, head + i));
            }
        }

        for (int j = 0; j < rightIds.Length; j++)
        {
            if (!matched[j])
            {
                pairs.Add(new JsonDiffPair(null, rightChildren[head + j], DiffState.Added, null, head + j));
            }
        }

        return pairs;
    }

    private List<JsonDiffPair> PairByAlignment(
        List<JsonNodeInfo> leftChildren,
        List<JsonNodeInfo> rightChildren,
        int head,
        int leftCount,
        int rightCount)
    {
        ulong[] leftHashes = HashRange(left, leftChildren, head, leftCount);
        ulong[] rightHashes = HashRange(right, rightChildren, head, rightCount);

        List<AlignmentStep> steps = JsonArrayAligner.Align(leftHashes, rightHashes);
        var pairs = new List<JsonDiffPair>(steps.Count);

        for (int i = 0; i < steps.Count;)
        {
            if (steps[i].Op == AlignmentOp.Match)
            {
                JsonNodeInfo l = leftChildren[head + steps[i].LeftIndex];
                JsonNodeInfo r = rightChildren[head + steps[i].RightIndex];

                // A match by hash still gets its bytes compared: that settles any collision.
                pairs.Add(new JsonDiffPair(l, r, Classify(l, r), null, head + steps[i].LeftIndex));
                i++;
                continue;
            }

            int deleteStart = i;
            while (i < steps.Count && steps[i].Op == AlignmentOp.Delete)
            {
                i++;
            }

            int deleted = i - deleteStart;

            int insertStart = i;
            while (i < steps.Count && steps[i].Op == AlignmentOp.Insert)
            {
                i++;
            }

            int inserted = i - insertStart;
            int paired = Math.Min(deleted, inserted);

            // A run removed and a run inserted in the same place is nearly always a record
            // that was edited, so the two runs are paired rather than reported as a swap.
            for (int k = 0; k < paired; k++)
            {
                JsonNodeInfo l = leftChildren[head + steps[deleteStart + k].LeftIndex];
                JsonNodeInfo r = rightChildren[head + steps[insertStart + k].RightIndex];
                pairs.Add(new JsonDiffPair(l, r, Classify(l, r), null, head + steps[deleteStart + k].LeftIndex));
            }

            for (int k = paired; k < deleted; k++)
            {
                int index = steps[deleteStart + k].LeftIndex;
                pairs.Add(new JsonDiffPair(leftChildren[head + index], null, DiffState.Removed, null, head + index));
            }

            for (int k = paired; k < inserted; k++)
            {
                int index = steps[insertStart + k].RightIndex;
                pairs.Add(new JsonDiffPair(null, rightChildren[head + index], DiffState.Added, null, head + index));
            }
        }

        return pairs;
    }

    private List<JsonDiffPair> PairByPosition(
        List<JsonNodeInfo> leftChildren,
        List<JsonNodeInfo> rightChildren,
        int head,
        int leftCount,
        int rightCount)
    {
        int paired = Math.Min(leftCount, rightCount);
        var pairs = new List<JsonDiffPair>(Math.Max(leftCount, rightCount));

        for (int i = 0; i < paired; i++)
        {
            JsonNodeInfo l = leftChildren[head + i];
            JsonNodeInfo r = rightChildren[head + i];
            pairs.Add(new JsonDiffPair(l, r, Classify(l, r), null, head + i));
        }

        for (int i = paired; i < leftCount; i++)
        {
            pairs.Add(new JsonDiffPair(leftChildren[head + i], null, DiffState.Removed, null, head + i));
        }

        for (int i = paired; i < rightCount; i++)
        {
            pairs.Add(new JsonDiffPair(null, rightChildren[head + i], DiffState.Added, null, head + i));
        }

        return pairs;
    }

    private JsonDiffPair Same(JsonNodeInfo l, JsonNodeInfo r, int index) =>
        new(l, r, DiffState.Unchanged, l.Name, index);

    private List<JsonNodeInfo> Children(JsonExplorerDocument document, JsonNodeInfo node)
    {
        JsonChildIndexer indexer = document.IndexChildren(
            node,
            maxChildren: _options.MaxChildrenPerLevel,
            cancellationToken: cancellationToken);

        if (indexer.Truncated)
        {
            Incomplete = true;
        }

        return [.. indexer.Children];
    }

    private static long Extent(JsonExplorerDocument document, JsonNodeInfo node) =>
        node.HasKnownExtent ? node.ByteLength : document.Length - node.Start;

    // ---- Identity -------------------------------------------------------------------

    /// <summary>
    /// Whether a property name reads as an identifier.
    /// </summary>
    /// <remarks>
    /// Names ending in "_id" or "Id" are accepted as well as the bare candidates, because real
    /// records call the field <c>employee_id</c> or <c>orderId</c> far more often than they
    /// call it <c>id</c>. A bare "id" suffix is not enough on its own — that would take "valid"
    /// and "paid" with it.
    /// </remarks>
    private static bool LooksLikeIdentity(string name)
    {
        if (IdentityCandidates.Contains(name.ToLowerInvariant()))
        {
            return true;
        }

        if (name.EndsWith("_id", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // "orderId" and "orderID" have a capital where "valid" and "paid" have a letter of the
        // word they end.
        return name.Length > 2
            && name.EndsWith("id", StringComparison.OrdinalIgnoreCase)
            && char.IsUpper(name[^2]);
    }

    private (string Key, string?[] Left, string?[] Right)? FindIdentity(
        JsonNodeInfo leftNode,
        JsonNodeInfo rightNode,
        List<JsonNodeInfo> leftChildren,
        List<JsonNodeInfo> rightChildren,
        int head,
        int leftCount,
        int rightCount)
    {
        // Only a list of records has anything to identify.
        if (leftChildren[head].Kind != JsonKind.Object || rightChildren[head].Kind != JsonKind.Object)
        {
            return null;
        }

        foreach (string candidate in NamesOf(left, leftChildren[head]))
        {
            if (!LooksLikeIdentity(candidate))
            {
                continue;
            }

            string?[]? leftValues = IdentityValues(left, leftNode, candidate, head, leftCount);
            string?[]? rightValues = leftValues is null ? null : IdentityValues(right, rightNode, candidate, head, rightCount);

            if (leftValues is not null && rightValues is not null)
            {
                return (candidate, leftValues, rightValues);
            }
        }

        return null;
    }

    private IEnumerable<string> NamesOf(JsonExplorerDocument document, JsonNodeInfo node) =>
        document
            .IndexChildren(node, maxChildren: 64, cancellationToken: cancellationToken)
            .Children
            .Select(child => child.Name)
            .OfType<string>();

    /// <summary>
    /// Reads one property out of every element of an array, in a single scan, and rejects the
    /// property if any element lacks it or two elements share a value.
    /// </summary>
    private string?[]? IdentityValues(
        JsonExplorerDocument document,
        JsonNodeInfo arrayNode,
        string key,
        int head,
        int count)
    {
        IReadOnlyList<JsonNodeInfo> children = document
            .IndexChildren(
                arrayNode,
                maxChildren: _options.MaxChildrenPerLevel,
                pinnedKeys: new HashSet<string>(StringComparer.Ordinal) { key },
                cancellationToken: cancellationToken)
            .Children;

        var values = new string?[count];
        var seen = new HashSet<string>(count, StringComparer.Ordinal);

        for (int i = 0; i < count; i++)
        {
            if (head + i >= children.Count || children[head + i].PinnedSummary is not { } summary || !seen.Add(summary))
            {
                return null;
            }

            values[i] = summary;
        }

        return values;
    }

    /// <summary>
    /// Reduces a run of elements to hashes, so the alignment can compare them cheaply.
    /// </summary>
    /// <remarks>
    /// Only the first stretch of each element is hashed. Two elements that agree that far and
    /// differ later hash the same, which makes the alignment pair them — and pairing them is
    /// right, because they are a record and its edited self.
    /// </remarks>
    private ulong[] HashRange(JsonExplorerDocument document, List<JsonNodeInfo> children, int start, int count)
    {
        const int maxHashedBytes = 8 * 1024;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(maxHashedBytes);

        try
        {
            ulong[] hashes = new ulong[count];
            for (int i = 0; i < count; i++)
            {
                JsonNodeInfo node = children[start + i];
                long length = Extent(document, node);
                int read = document.Source.ReadInto(node.Start, buffer, (int)Math.Min(length, maxHashedBytes));

                // Fowler–Noll–Vo, seeded with the length so that a value which is a prefix of
                // another does not hash the same as it.
                ulong hash = 14695981039346656037UL ^ (ulong)length;
                for (int b = 0; b < read; b++)
                {
                    hash = (hash ^ buffer[b]) * 1099511628211UL;
                }

                hashes[i] = hash;
            }

            return hashes;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
