using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Composition;

namespace FtdHullGenerator.Geometry.Smoothing;

/// <summary>
/// Bevels beam-width steps in each horizontal X/Z plane. Each port column's
/// bow/stern contour is compared with its inboard neighbor at the same height.
/// </summary>
/// <remarks>
/// Emit one 1–4m bow slope or 2–4m stern slope where the inboard column extends
/// farther toward the hull end. A supported 1m transom notch uses a transverse
/// wedge instead. Shape V2 also checks the ends of disjoint longitudinal runs,
/// where MinZ/MaxZ alone would hide the internal gap, and caps each supported
/// return rather than packing the interval. Mirror port rotations 18/19/23 to
/// starboard 16/17/22. Derived from hand-corrected references.
/// </remarks>
public sealed class HorizontalSlopeFillPass : ISmoothingPass
{
    public SmoothingMethod Method => SmoothingMethod.HorizontalSlopeFill;

    public GeneratedHull Apply(GeneratedHull hull)
    {
        ArgumentNullException.ThrowIfNull(hull);
        var occupied = hull.Blocks.SelectMany(block => block.OccupiedCells).ToHashSet();
        var claimed = new HashSet<(int X, int Y, int Z)>();
        var additions = new List<BlockPlacement>();
        foreach (var slope in CollectCandidates(hull))
            SlopeFillPlacement.TryAddMirrored(hull, occupied, claimed, additions, slope,
                allowVerticalSupport: hull.Parameters.Shape is not null);
        return SlopeFillPlacement.Merge(hull, additions);
    }

    /// <summary>Returns raw port-side candidates before footprint conflicts are resolved.</summary>
    internal static IReadOnlyList<BlockPlacement> CollectCandidates(GeneratedHull hull)
    {
        ArgumentNullException.ThrowIfNull(hull);
        // A Shape V2 internal longitudinal return must occupy genuine exterior space. The row
        // contour alone cannot tell a surface notch from the hollow interior: both are a gap with
        // the same inboard/lower support. The hull carries the original analytic role of every cell
        // captured before packing, so the guard never reconstructs the hull from this placement list.
        if (hull.Parameters.Shape is not null && hull.RoleClassifier is null)
            throw new InvalidOperationException(
                "A Shape V2 horizontal fill requires the hull's original analytic cell-role classifier so " +
                "internal longitudinal returns cannot be placed inside the hollow cavity.");
        var roleClassifier = hull.RoleClassifier;
        var occupied = hull.Blocks.SelectMany(block => block.OccupiedCells).ToHashSet();
        var rowCells = occupied.GroupBy(cell => (cell.X, cell.Y)).ToDictionary(
            group => group.Key, group => group.Select(cell => cell.Z).ToHashSet());
        var rows = rowCells.ToDictionary(pair => pair.Key,
            pair => (MinZ: pair.Value.Min(), MaxZ: pair.Value.Max()));
        var candidates = new List<BlockPlacement>();
        foreach (var row in rows.Where(row => 2 * row.Key.X < hull.MinX + hull.MaxX)
                     .OrderBy(row => row.Key.X).ThenBy(row => row.Key.Y))
        {
            if (!rows.TryGetValue((row.Key.X + 1, row.Key.Y), out var inboard))
                continue;
            AddRun(row.Value.MaxZ + 1, inboard.MaxZ - row.Value.MaxZ, 18, minimumLength: 1);
            AddRun(row.Value.MinZ - 1, row.Value.MinZ - inboard.MinZ, 19, minimumLength: 2);
            if (hull.Parameters.Shape is not null)
                AddInternalRuns(rowCells[row.Key], rowCells[(row.Key.X + 1, row.Key.Y)]);

            void AddRun(int anchorZ, int length, int rotation, int minimumLength)
            {
                if (length < minimumLength)
                    return;
                var slope = new BlockPlacement(SlopeFillPlacement.ShapeForLength(Math.Min(length, 4)),
                    hull.Parameters.SurfaceMaterial, row.Key.X, row.Key.Y, anchorZ, rotation)
                {
                    Origin = BlockOrigin.Smoothing,
                };
                candidates.Add(slope);
            }

            void AddInternalRuns(HashSet<int> current, HashSet<int> support)
            {
                var z = row.Value.MinZ + 1;
                while (z < row.Value.MaxZ)
                {
                    if (current.Contains(z))
                    {
                        z++;
                        continue;
                    }

                    var start = z;
                    while (z < row.Value.MaxZ && !current.Contains(z))
                        z++;
                    var length = z - start;
                    if (!current.Contains(start - 1) || !current.Contains(z))
                        continue;

                    var forwardLength = !occupied.Contains((row.Key.X - 1, row.Key.Y, start - 1))
                        ? SupportLength(start, 1, length) : 0;
                    var backwardLength = !occupied.Contains((row.Key.X - 1, row.Key.Y, z))
                        ? SupportLength(z - 1, -1, length) : 0;

                    if (forwardLength >= 2)
                        AddInternal(start, forwardLength, 0);
                    if (backwardLength >= 2 && forwardLength + backwardLength <= length)
                        AddInternal(z - 1, backwardLength, 2);

                    void AddInternal(int anchorZ, int slopeLength, int rotation)
                    {
                        var slope = new BlockPlacement(SlopeFillPlacement.ShapeForLength(slopeLength),
                            hull.Parameters.SurfaceMaterial, row.Key.X, row.Key.Y, anchorZ, rotation)
                        {
                            Origin = BlockOrigin.Smoothing,
                        };
                        // A genuine surface notch is capped from the unbounded exterior. The
                        // hollow hull interior is an equally supported longitudinal gap, so the
                        // original analytic role, not the row contour, must decide. Only the
                        // Shape V2 internal-return path is guarded: the bow/stern contour runs
                        // and the transom wedge above keep their established behavior.
                        if (hull.Parameters.Shape is not null &&
                            !slope.OccupiedCells.All(cell =>
                                roleClassifier!(cell.X, cell.Y, cell.Z) == HullCellRole.Outside))
                            return;
                        candidates.Add(slope);
                    }

                    int SupportLength(int supportZ, int direction, int available)
                    {
                        // An internal deck-edge return has support on only one side
                        // of its top face. Keep that cap to the hand-verified 2 m
                        // footprint; side rows may use the full supported 4 m part.
                        var limit = Math.Min(available, row.Key.Y == hull.MaxY ? 2 : 4);
                        var inboardLength = 0;
                        while (inboardLength < limit && support.Contains(supportZ + direction * inboardLength))
                            inboardLength++;
                        if (inboardLength >= 2)
                            return inboardLength;

                        var lowerLength = 0;
                        while (lowerLength < limit &&
                               occupied.Contains((row.Key.X, row.Key.Y - 1,
                                   supportZ + direction * lowerLength)))
                            lowerLength++;
                        return lowerLength;
                    }
                }
            }
        }

        // At the stern, a one-cell width step can turn across X rather than run
        // along Z. Require original inboard, upper, and forward support; this
        // distinguishes a connected transom corner from unsupported empty space.
        var mirrorSum = hull.MinX + hull.MaxX;
        var sternZ = hull.MinZ;
        for (var y = hull.MinY; y < hull.MaxY; y++)
        for (var x = hull.MinX; 2 * x < mirrorSum; x++)
        {
            var cell = (x, y, sternZ);
            if (occupied.Contains(cell) ||
                !occupied.Contains((x + 1, y, sternZ)) ||
                !occupied.Contains((x, y + 1, sternZ)) ||
                !occupied.Contains((x, y, sternZ + 1)))
                continue;
            var slope = new BlockPlacement(BlockShape.Slope1, hull.Parameters.SurfaceMaterial,
                x, y, sternZ, 23) { Origin = BlockOrigin.Smoothing };
            candidates.Add(slope);
        }
        return candidates;
    }
}
