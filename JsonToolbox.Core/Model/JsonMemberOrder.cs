namespace JsonToolbox.Core.Model;

/// <summary>The order an object's properties are listed in.</summary>
public enum JsonMemberOrder
{
    /// <summary>As the document writes them.</summary>
    FileOrder,

    /// <summary>By property name, A to Z.</summary>
    Ascending,

    /// <summary>By property name, Z to A.</summary>
    Descending,
}

/// <summary>How property names are put in order.</summary>
public static class JsonMemberOrdering
{
    /// <summary>True for an order that rearranges anything.</summary>
    public static bool IsSorted(this JsonMemberOrder order) => order != JsonMemberOrder.FileOrder;

    /// <summary>
    /// Compares two property names the way somebody looking for one would.
    /// </summary>
    /// <remarks>
    /// Case is ignored first, so <c>Name</c> and <c>name</c> sit together instead of being
    /// separated by every lowercase key in the object — an ordinal sort puts the whole of the
    /// uppercase alphabet before <c>a</c>, which is an order nobody scans a list in. Ties are
    /// then broken ordinally so that the result is total and does not depend on the culture the
    /// application happens to be running under.
    /// </remarks>
    public static int Compare(string? left, string? right)
    {
        int byLetter = string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
        return byLetter != 0 ? byLetter : string.CompareOrdinal(left, right);
    }

    /// <summary>Orders a container's members, leaving array elements where they are.</summary>
    /// <remarks>
    /// Only objects have properties to sort. Array elements are identified by their position,
    /// so reordering them would not be a different view of the same data — it would be
    /// different data, and every index in every path pointing into it would be wrong.
    /// </remarks>
    public static IReadOnlyList<T> Arrange<T>(
        IReadOnlyList<T> members,
        Func<T, string?> nameOf,
        JsonMemberOrder order)
    {
        if (!order.IsSorted() || members.Count < 2 || nameOf(members[0]) is null)
        {
            return members;
        }

        var comparer = Comparer<string?>.Create(Compare);

        // Both sorts are stable, so members sharing a name — which a JSON object is allowed to
        // have, however badly it reads — keep the order the document gave them.
        return order == JsonMemberOrder.Ascending
            ? [.. members.OrderBy(nameOf, comparer)]
            : [.. members.OrderByDescending(nameOf, comparer)];
    }
}
