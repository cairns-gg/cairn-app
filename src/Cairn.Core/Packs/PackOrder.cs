namespace Cairn.Core.Packs;

/// <summary>
/// The order the launcher lists packs in: the one somebody arranged by dragging, with
/// anything they have not placed after it (cairns-gg/cairn-app#7).
///
/// Kept as a list of ids in <see cref="CairnSettings.PackOrder"/> rather than as a number on
/// each pack. The order is this machine's arrangement of its own sidebar, and a pack's files
/// are the part that gets shared — a position written into pack.json would arrive with an
/// import and mean nothing on the other end.
/// </summary>
public static class PackOrder
{
    /// <summary>
    /// <paramref name="ids"/>, in the saved order where there is one. A pack the saved order
    /// does not mention — created or imported since, or from before there was an order —
    /// keeps its place among the rest, after the ones somebody arranged: new arrivals land at
    /// the bottom rather than shuffling the list somebody made. An id saved for a pack that is
    /// gone is ignored.
    /// </summary>
    public static IReadOnlyList<string> Arrange(IEnumerable<string> ids, IReadOnlyList<string>? saved)
    {
        var present = ids.ToList();
        if (saved is null || saved.Count == 0) return present;

        // Each saved id as the pack spells it, so the list hands back the ids that exist.
        var placed = saved
            .Select(id => present.FirstOrDefault(p => string.Equals(p, id, StringComparison.OrdinalIgnoreCase)))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rest = present.Except(placed, StringComparer.OrdinalIgnoreCase);
        return [.. placed, .. rest];
    }
}
