using System.Diagnostics;
using JsonExplorer.Core.Documents;
using JsonExplorer.Core.Model;

namespace JsonExplorer.Core.Diff;

/// <summary>
/// Walks two documents that have been lined up by <see cref="JsonDiffPairing"/> and reports
/// what differs.
/// </summary>
/// <remarks>
/// The pairing decides what is compared with what; this only decides what is worth saying
/// about each pair. Keeping the two apart is what lets a side-by-side view and a list of
/// findings agree with each other — they are the same comparison, read two ways.
/// </remarks>
public static class JsonDiffEngine
{
    public static DiffResult Compare(
        JsonExplorerDocument left,
        JsonExplorerDocument right,
        DiffOptions? options = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        DiffOptions effective = options ?? new DiffOptions();
        var pairing = new JsonDiffPairing(left, right, effective, cancellationToken);
        var walker = new Walker(pairing, effective, progress);

        long start = Stopwatch.GetTimestamp();
        walker.Compare(left.Root, right.Root, []);

        return new DiffResult
        {
            Differences = walker.Differences,
            Truncated = walker.Truncated,
            Incomplete = pairing.Incomplete,
            Approximate = pairing.Approximate,
            Duration = Stopwatch.GetElapsedTime(start),
        };
    }

    public static Task<DiffResult> CompareAsync(
        JsonExplorerDocument left,
        JsonExplorerDocument right,
        DiffOptions? options = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Compare(left, right, options, progress, cancellationToken), cancellationToken);

    private sealed class Walker(JsonDiffPairing pairing, DiffOptions options, IProgress<double>? progress)
    {
        private readonly List<JsonDifference> _differences = [];

        public IReadOnlyList<JsonDifference> Differences => _differences;

        public bool Truncated { get; private set; }

        public void Compare(JsonNodeInfo leftNode, JsonNodeInfo rightNode, List<JsonPathSegment> path)
        {
            if (Truncated || pairing.BytesEqual(leftNode, rightNode))
            {
                return;
            }

            // true and false are the same type disagreeing about a value, not two types.
            if (leftNode.Kind.Normalize() != rightNode.Kind.Normalize())
            {
                Report(JsonDifferenceKind.TypeChanged, path, leftNode, rightNode);
                return;
            }

            if (!leftNode.IsContainer)
            {
                Report(JsonDifferenceKind.ValueChanged, path, leftNode, rightNode);
                return;
            }

            List<JsonDiffPair> pairs = pairing.PairChildren(leftNode, rightNode);
            bool atRoot = path.Count == 0;

            for (int i = 0; i < pairs.Count && !Truncated; i++)
            {
                JsonDiffPair pair = pairs[i];
                path.Add(pair.Name is { } name ? JsonPathSegment.Property(name) : JsonPathSegment.Element(pair.Index));

                switch (pair)
                {
                    case { Left: { } l, Right: { } r }:
                        Compare(l, r, path);
                        break;

                    case { Left: { } l }:
                        Report(JsonDifferenceKind.Removed, path, l, null);
                        break;

                    case { Right: { } r }:
                        Report(JsonDifferenceKind.Added, path, null, r);
                        break;
                }

                path.RemoveAt(path.Count - 1);

                // Only the top level says anything about how far along the walk is; below it
                // the counts do not relate to how much of the document is left.
                if (atRoot && progress is not null && pairs.Count > 0)
                {
                    progress.Report(Math.Clamp((i + 1) / (double)pairs.Count, 0, 1));
                }
            }
        }

        private void Report(JsonDifferenceKind kind, List<JsonPathSegment> path, JsonNodeInfo? leftNode, JsonNodeInfo? rightNode)
        {
            if (_differences.Count >= options.MaxDifferences)
            {
                Truncated = true;
                return;
            }

            _differences.Add(new JsonDifference(
                kind,
                JsonPathFormatter.ToJsonPath(path),
                leftNode?.Kind ?? JsonKind.Undefined,
                rightNode?.Kind ?? JsonKind.Undefined,
                leftNode is { } l ? Describe(pairing.Left, l) : null,
                rightNode is { } r ? Describe(pairing.Right, r) : null,
                leftNode?.Start ?? -1,
                rightNode?.Start ?? -1));
        }

        /// <summary>
        /// Renders a value for the report. Containers show as <c>{…}</c> rather than as their
        /// contents: a whole subtree that was added is one finding, not a wall of text.
        /// </summary>
        private string Describe(JsonExplorerDocument document, JsonNodeInfo node) =>
            node.IsContainer
                ? JsonValueFormatter.Collapsed(node.Kind)
                : node.Preview ?? document.ReadRaw(node, options.MaxValueLength);
    }
}
