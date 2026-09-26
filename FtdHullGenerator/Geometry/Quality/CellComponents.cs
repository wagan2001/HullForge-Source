namespace FtdHullGenerator.Geometry;

/// <summary>
/// Shared component helpers for the evidence-based detectors. Components are built from a sorted work
/// set, so the returned order and each component's cell order are independent of the input order.
/// </summary>
internal static class CellComponents
{
    public static List<List<(int X, int Y, int Z)>> Build(IEnumerable<(int X, int Y, int Z)> cells)
    {
        var remaining = new SortedSet<(int X, int Y, int Z)>(cells);
        var components = new List<List<(int X, int Y, int Z)>>();
        var queue = new Queue<(int X, int Y, int Z)>();
        while (remaining.Count > 0)
        {
            var start = remaining.Min;
            remaining.Remove(start);
            queue.Enqueue(start);
            var component = new List<(int X, int Y, int Z)> { start };
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                foreach (var direction in Domain.BlockRotations.AxisDirections)
                {
                    var next = (current.X + direction.X, current.Y + direction.Y, current.Z + direction.Z);
                    if (!remaining.Remove(next)) continue;
                    component.Add(next);
                    queue.Enqueue(next);
                }
            }

            component.Sort();
            components.Add(component);
        }

        return components;
    }

    /// <summary>
    /// Merges components that are exact X-mirror images about the hull centreline. A self-mirrored
    /// component is left alone. The merged cells are the union of the mirror pair, ordered by X then Y
    /// then Z, so symmetric port/starboard evidence yields one deterministic component.
    /// </summary>
    public static List<List<(int X, int Y, int Z)>> MergeMirrored(
        IEnumerable<List<(int X, int Y, int Z)>> components, int mirrorSum)
    {
        var groups = new Dictionary<string, List<(int X, int Y, int Z)>>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var component in components)
        {
            var mirrored = component.Select(cell => (X: mirrorSum - cell.X, cell.Y, cell.Z)).ToList();
            mirrored.Sort();
            var key = string.CompareOrdinal(Key(component), Key(mirrored)) <= 0 ? Key(component) : Key(mirrored);
            if (!groups.TryGetValue(key, out var group))
            {
                group = [];
                groups[key] = group;
                order.Add(key);
            }

            group.AddRange(component);
        }

        return order
            .Select(key => groups[key].Distinct()
                .OrderBy(cell => cell.X).ThenBy(cell => cell.Y).ThenBy(cell => cell.Z).ToList())
            .ToList();
    }

    private static string Key(IEnumerable<(int X, int Y, int Z)> cells) =>
        string.Join(";", cells.Select(cell => $"{cell.X},{cell.Y},{cell.Z}"));
}
