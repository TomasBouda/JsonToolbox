using System.Text;

namespace JsonToolbox.Core.Model;

/// <summary>
/// One step from a parent value to a child value: either a property name or an array index.
/// </summary>
public readonly record struct JsonPathSegment
{
    private JsonPathSegment(string? name, int index)
    {
        Name = name;
        Index = index;
    }

    /// <summary>The property name, or <c>null</c> when this step indexes into an array.</summary>
    public string? Name { get; }

    /// <summary>The zero-based array index, or <c>-1</c> when this step names a property.</summary>
    public int Index { get; }

    public bool IsIndex => Name is null;

    public static JsonPathSegment Property(string name) => new(name, -1);

    public static JsonPathSegment Element(int index) => new(null, index);

    public override string ToString() => IsIndex ? $"[{Index}]" : Name!;
}

/// <summary>
/// Formats a chain of <see cref="JsonPathSegment"/> into the notations the user can paste
/// somewhere else.
/// </summary>
/// <remarks>
/// Three notations are supported on purpose. JSONPath is what most query tools accept,
/// JSON Pointer (RFC 6901) is what JSON Schema validators report errors in, and the
/// normalized path collapses every array index to <c>[]</c> so that all elements of a
/// collection share one identity — that is what makes the structure profile possible.
/// </remarks>
public static class JsonPathFormatter
{
    public static string ToJsonPath(IReadOnlyList<JsonPathSegment> segments)
    {
        var builder = new StringBuilder("$");
        foreach (JsonPathSegment segment in segments)
        {
            if (segment.IsIndex)
            {
                builder.Append('[').Append(segment.Index).Append(']');
            }
            else if (IsBareIdentifier(segment.Name!))
            {
                builder.Append('.').Append(segment.Name);
            }
            else
            {
                builder.Append("['").Append(segment.Name!.Replace("\\", "\\\\").Replace("'", "\'")).Append("']");
            }
        }

        return builder.ToString();
    }

    public static string ToJsonPointer(IReadOnlyList<JsonPathSegment> segments)
    {
        if (segments.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (JsonPathSegment segment in segments)
        {
            builder.Append('/');
            if (segment.IsIndex)
            {
                builder.Append(segment.Index);
            }
            else
            {
                // RFC 6901 escaping: '~' becomes "~0" and '/' becomes "~1", in that order.
                builder.Append(segment.Name!.Replace("~", "~0").Replace("/", "~1"));
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Builds the path with every array index replaced by <c>[]</c>, so that
    /// <c>$.items[0].price</c> and <c>$.items[999].price</c> map to the same key.
    /// </summary>
    public static string ToNormalizedPath(IReadOnlyList<JsonPathSegment> segments)
    {
        var builder = new StringBuilder("$");
        foreach (JsonPathSegment segment in segments)
        {
            if (segment.IsIndex)
            {
                builder.Append("[]");
            }
            else
            {
                builder.Append('.').Append(segment.Name);
            }
        }

        return builder.ToString();
    }

    private static bool IsBareIdentifier(string name)
    {
        if (name.Length == 0)
        {
            return false;
        }

        if (!char.IsLetter(name[0]) && name[0] != '_' && name[0] != '$')
        {
            return false;
        }

        foreach (char c in name)
        {
            if (!char.IsLetterOrDigit(c) && c != '_' && c != '$')
            {
                return false;
            }
        }

        return true;
    }
}
