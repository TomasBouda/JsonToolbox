using System.Text.Json;
using JsonToolbox.Core.Model;
using JsonToolbox.Core.Scanning;

namespace JsonToolbox.Core.Documents;

/// <summary>
/// Collects the direct children of one container, plus the child count of each of them and,
/// when asked, the values of pinned keys one level further down.
/// </summary>
/// <remarks>
/// <para>
/// The scan is always started at the container's own first byte, so the container is the
/// root of what the reader sees: its direct children sit at depth 1 and its grandchildren at
/// depth 2. Grandchildren are counted but not described, which is exactly what a tree row
/// needs — the expander and the "12 items" label — for free in the same pass.
/// </para>
/// <para>
/// Pinned keys ride along on that same pass. Showing one field of every record without
/// expanding them is only worth having if it is free, and it is: the scan is already inside
/// each record at depth 2, so reading the value next to a pinned name costs nothing beyond
/// the string it produces.
/// </para>
/// <para>
/// Children are handed to the caller as they are found rather than at the end, so a tree can
/// start filling in immediately even when the container is a top-level array with two million
/// elements.
/// </para>
/// </remarks>
public sealed class JsonChildIndexer : JsonScanVisitor
{
    private const int ChildDepth = 1;
    private const int GrandchildDepth = 2;

    private readonly List<JsonNodeInfo> _children = [];
    private readonly Action<JsonNodeInfo>? _onChildFound;
    private readonly Action<JsonNodeInfo>? _onChildCompleted;
    private readonly int _maxChildren;
    private readonly IReadOnlySet<string>? _pinnedKeys;

    private string? _pendingName;
    private long _pendingNameStart = -1;
    private long _pendingNameEnd = -1;
    private int _nextIndex;
    private int _emitted;

    // State of the child currently being read, when that child is a container.
    private bool _inContainerChild;
    private JsonKind _openKind;
    private string? _openName;
    private int _openIndex;
    private long _openStart;
    private long _openNameStart = -1;
    private long _openNameEnd = -1;
    private int _openChildCount;
    private string? _pendingPinnedKey;
    private List<string>? _openPinned;

    /// <param name="onChildFound">
    /// Called the moment a child is discovered. For a container this happens at its opening
    /// bracket, so the node arrives with <see cref="JsonNodeInfo.End"/> and
    /// <see cref="JsonNodeInfo.ChildCount"/> still unknown.
    /// </param>
    /// <param name="onChildCompleted">
    /// Called once a container child's closing bracket has been read and its size and child
    /// count are settled. Scalars are complete when found and are never reported here.
    /// </param>
    /// <param name="pinnedKeys">
    /// Property names to read out of each container child, so the parent row can show them
    /// without being expanded.
    /// </param>
    public JsonChildIndexer(
        Action<JsonNodeInfo>? onChildFound = null,
        int maxChildren = int.MaxValue,
        Action<JsonNodeInfo>? onChildCompleted = null,
        IReadOnlySet<string>? pinnedKeys = null)
    {
        _onChildFound = onChildFound;
        _onChildCompleted = onChildCompleted;
        _maxChildren = maxChildren;
        _pinnedKeys = pinnedKeys is { Count: > 0 } ? pinnedKeys : null;
    }

    public IReadOnlyList<JsonNodeInfo> Children => _children;

    /// <summary>True when the scan stopped because <c>maxChildren</c> was reached.</summary>
    public bool Truncated { get; private set; }

    /// <summary>True once the container being indexed has been read to its closing bracket.</summary>
    /// <remarks>
    /// This is what lets a node be expanded without knowing where it ends: the scan is given
    /// the rest of the file and stops itself at the right place, rather than being handed a
    /// boundary that has not been discovered yet.
    /// </remarks>
    public bool ContainerClosed { get; private set; }

    public override bool WantsMoreTokens => !Truncated && !ContainerClosed;

    public override void OnToken(ref Utf8JsonReader reader, long start, long end)
    {
        int depth = reader.CurrentDepth;

        if (depth == 0 && reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray)
        {
            ContainerClosed = true;
            return;
        }

        if (depth == GrandchildDepth && _inContainerChild)
        {
            OnGrandchildToken(ref reader);
            return;
        }

        if (depth != ChildDepth)
        {
            return;
        }

        switch (reader.TokenType)
        {
            case JsonTokenType.PropertyName:
                _pendingName = reader.GetString();

                // Where the name is written, not just what it says: renaming a key and
                // deleting a property both need the name's own byte range.
                //
                // The end is worked out from the raw name rather than taken from the token,
                // because the reader counts the colon after the name as consumed — and a
                // rename that took the colon with it would break the document.
                _pendingNameStart = start;
                _pendingNameEnd = start + reader.ValueSpan.Length + 2;
                break;

            case JsonTokenType.StartObject:
            case JsonTokenType.StartArray:
                BeginContainerChild(reader.TokenType == JsonTokenType.StartObject ? JsonKind.Object : JsonKind.Array, start);
                break;

            case JsonTokenType.EndObject:
            case JsonTokenType.EndArray:
                EndContainerChild(end);
                break;

            default:
                if (IsValueStart(reader.TokenType))
                {
                    AddChild(new JsonNodeInfo(
                        ToKind(ref reader),
                        _pendingName,
                        _nextIndex++,
                        start,
                        end,
                        ChildCount: 0,
                        Preview: JsonValueFormatter.Scalar(ref reader))
                    {
                        NameStart = _pendingNameStart,
                        NameEnd = _pendingNameEnd,
                    });
                    ClearPendingName();
                }

                break;
        }
    }

    /// <summary>
    /// Counts one level down, and picks up pinned values on the way past.
    /// </summary>
    private void OnGrandchildToken(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.PropertyName)
        {
            if (_pinnedKeys is not null)
            {
                string name = reader.GetString() ?? string.Empty;
                _pendingPinnedKey = _pinnedKeys.Contains(name) ? name : null;
            }

            return;
        }

        if (!IsValueStart(reader.TokenType))
        {
            return;
        }

        // Every property has exactly one value and every array element is one value, so
        // counting value starts counts children for objects and arrays alike.
        _openChildCount++;

        if (_pendingPinnedKey is not { } key)
        {
            return;
        }

        _pendingPinnedKey = null;
        _openPinned ??= [];
        _openPinned.Add($"{key}: {Describe(ref reader)}");
    }

    private static string Describe(ref Utf8JsonReader reader) => reader.TokenType switch
    {
        // A pinned container is summarised rather than expanded: the point of pinning is to
        // avoid opening things, so showing its contents here would defeat it.
        JsonTokenType.StartObject => JsonValueFormatter.Collapsed(JsonKind.Object),
        JsonTokenType.StartArray => JsonValueFormatter.Collapsed(JsonKind.Array),
        _ => JsonValueFormatter.Scalar(ref reader),
    };

    private void BeginContainerChild(JsonKind kind, long start)
    {
        _inContainerChild = true;
        _openKind = kind;
        _openName = _pendingName;
        _openIndex = _nextIndex++;
        _openStart = start;
        _openNameStart = _pendingNameStart;
        _openNameEnd = _pendingNameEnd;
        _openChildCount = 0;
        _openPinned = null;
        _pendingPinnedKey = null;
        ClearPendingName();

        // Announced immediately, with its extent still unknown. Waiting for the closing
        // bracket would mean that the last child of a large document only appears after the
        // whole file has been read — which is the one case where waiting is unbearable.
        Emit(new JsonNodeInfo(kind, _openName, _openIndex, start, End: -1, ChildCount: -1, Preview: JsonValueFormatter.Collapsed(kind)));
    }

    private void EndContainerChild(long end)
    {
        if (!_inContainerChild)
        {
            return;
        }

        _inContainerChild = false;
        var completed = new JsonNodeInfo(
            _openKind,
            _openName,
            _openIndex,
            _openStart,
            end,
            _openChildCount,
            Preview: JsonValueFormatter.Collapsed(_openKind))
        {
            NameStart = _openNameStart,
            NameEnd = _openNameEnd,
            PinnedSummary = _openPinned is { Count: > 0 } ? string.Join("   ", _openPinned) : null,
        };

        _children.Add(completed);
        _onChildCompleted?.Invoke(completed);
    }

    private void ClearPendingName()
    {
        _pendingName = null;
        _pendingNameStart = -1;
        _pendingNameEnd = -1;
    }

    private void AddChild(JsonNodeInfo child)
    {
        _children.Add(child);
        Emit(child);
    }

    private void Emit(JsonNodeInfo child)
    {
        _onChildFound?.Invoke(child);

        if (++_emitted >= _maxChildren)
        {
            Truncated = true;
        }
    }

    public override void OnScanFinished(long bytesRead, bool completed)
    {
        // A container child that was announced but never closed — because the scan stopped at
        // the child limit — still belongs in the list, or the rows already shown to the user
        // would not match it.
        if (_inContainerChild)
        {
            _inContainerChild = false;
            _children.Add(new JsonNodeInfo(
                _openKind,
                _openName,
                _openIndex,
                _openStart,
                End: -1,
                ChildCount: -1,
                Preview: JsonValueFormatter.Collapsed(_openKind))
            {
                NameStart = _openNameStart,
                NameEnd = _openNameEnd,
            });
        }
    }

    private static bool IsValueStart(JsonTokenType type) => type is
        JsonTokenType.StartObject or
        JsonTokenType.StartArray or
        JsonTokenType.String or
        JsonTokenType.Number or
        JsonTokenType.True or
        JsonTokenType.False or
        JsonTokenType.Null;

    private static JsonKind ToKind(ref Utf8JsonReader reader) => reader.TokenType switch
    {
        JsonTokenType.StartObject => JsonKind.Object,
        JsonTokenType.StartArray => JsonKind.Array,
        JsonTokenType.String => JsonKind.String,
        JsonTokenType.True => JsonKind.True,
        JsonTokenType.False => JsonKind.False,
        JsonTokenType.Null => JsonKind.Null,
        JsonTokenType.Number => LooksIntegral(reader.ValueSpan) ? JsonKind.Integer : JsonKind.Float,
        _ => JsonKind.Undefined,
    };

    /// <summary>
    /// Classifies a number by how it was written rather than by its magnitude, because that
    /// is what the author meant: <c>1.0</c> is a measurement, <c>1</c> is a count.
    /// </summary>
    private static bool LooksIntegral(ReadOnlySpan<byte> raw) =>
        raw.IndexOfAny((byte)'.', (byte)'e', (byte)'E') < 0;
}
