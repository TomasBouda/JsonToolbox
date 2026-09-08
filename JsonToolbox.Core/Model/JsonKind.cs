namespace JsonToolbox.Core.Model;

/// <summary>
/// The kind of a JSON value, as the toolbox presents it to the user.
/// </summary>
/// <remarks>
/// This mirrors <see cref="System.Text.Json.JsonValueKind"/> but splits numbers into
/// integers and floating point values, because the two carry very different risks:
/// a large integer silently loses precision in consumers that decode JSON numbers as
/// IEEE 754 doubles, while a floating point value never promised exactness to begin with.
/// </remarks>
public enum JsonKind : byte
{
    Undefined = 0,
    Object,
    Array,
    String,
    Integer,
    Float,
    True,
    False,
    Null,
}

public static class JsonKindExtensions
{
    /// <summary>Objects and arrays are the only kinds that can have children.</summary>
    public static bool IsContainer(this JsonKind kind) => kind is JsonKind.Object or JsonKind.Array;

    public static bool IsNumber(this JsonKind kind) => kind is JsonKind.Integer or JsonKind.Float;

    public static bool IsBoolean(this JsonKind kind) => kind is JsonKind.True or JsonKind.False;

    /// <summary>
    /// Collapses the kinds that are interchangeable when comparing the shape of two values,
    /// so that <c>true</c> and <c>false</c> do not read as a type mismatch.
    /// </summary>
    public static JsonKind Normalize(this JsonKind kind) => kind switch
    {
        JsonKind.False => JsonKind.True,
        _ => kind,
    };

    public static string ToDisplayName(this JsonKind kind) => kind switch
    {
        JsonKind.Object => "object",
        JsonKind.Array => "array",
        JsonKind.String => "string",
        JsonKind.Integer => "integer",
        JsonKind.Float => "number",
        JsonKind.True or JsonKind.False => "boolean",
        JsonKind.Null => "null",
        _ => "unknown",
    };
}
