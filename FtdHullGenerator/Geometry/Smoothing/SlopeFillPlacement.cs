using FtdHullGenerator.Domain;

namespace FtdHullGenerator.Geometry.Smoothing;

/// <summary>Shared footprint checks and mirrored placement for additive side slopes.</summary>
internal static class SlopeFillPlacement
{
    public static void TryAddMirrored(GeneratedHull hull, HashSet<(int X, int Y, int Z)> occupied,
        HashSet<(int X, int Y, int Z)> claimed, List<BlockPlacement> additions, BlockPlacement slope,
        bool allowVerticalSupport = false)
    {
        var mirrorSum = hull.MinX + hull.MaxX;
        var mirror = slope with
        {
            X = mirrorSum - slope.X,
            Shape = HullGeometryValidator.MirrorShape(slope.Shape),
            Rotation = HullGeometryValidator.MirrorRotation(slope.Rotation),
        };
        var selfMirrored = slope.Position == mirror.Position &&
                           slope.Shape == mirror.Shape && slope.Rotation == mirror.Rotation;
        // Only symmetric longitudinal wedges can live on an odd-width centerline.
        if (slope.X == mirror.X && !selfMirrored)
            return;
        var cells = (selfMirrored ? slope.OccupiedCells : slope.OccupiedCells.Concat(mirror.OccupiedCells)).ToArray();
        // Check both complete footprints before claiming either. Side-fill cells
        // normally need original inboard shell support. A longitudinal vertical-fill
        // tread is also fully supported by the unchanged contour row directly above;
        // accepting that support prevents valid treads from disappearing where the
        // horizontal taper briefly outruns the next inboard side column. Shape V2
        // risers can likewise use unchanged longitudinal support toward their end,
        // and internal horizontal returns can sit on the unchanged row below.
        if (cells.Any(cell => cell.X < hull.MinX || cell.X > hull.MaxX ||
                              cell.Y < hull.MinY || cell.Y > hull.MaxY ||
                              cell.Z < hull.MinZ || cell.Z > hull.MaxZ ||
                              occupied.Contains(cell) || claimed.Contains(cell) ||
                              !HasOriginalSupport(cell)))
            return;
        claimed.UnionWith(cells);
        additions.Add(slope);
        if (!selfMirrored)
            additions.Add(mirror);

        bool HasOriginalSupport((int X, int Y, int Z) cell)
        {
            var inboardX = cell.X + (2 * cell.X < mirrorSum ? 1 : -1);
            return occupied.Contains((inboardX, cell.Y, cell.Z)) ||
                   allowVerticalSupport &&
                   (slope.Rotation is 12 or 14 && occupied.Contains((cell.X, cell.Y + 1, cell.Z)) ||
                    slope.Rotation is 0 or 2 && occupied.Contains((cell.X, cell.Y - 1, cell.Z)) ||
                    hull.Parameters.Shape is not null &&
                    (slope.Rotation is 4 or 8 && occupied.Contains((cell.X, cell.Y, cell.Z - 1)) ||
                     slope.Rotation is 6 or 10 && occupied.Contains((cell.X, cell.Y, cell.Z + 1))));
        }
    }

    public static GeneratedHull Merge(GeneratedHull hull, List<BlockPlacement> additions)
    {
        if (additions.Count == 0)
            return hull;
        if (!hull.Parameters.EffectiveBottomArmor.Equals(hull.Parameters.HullArmor))
        {
            var owners = hull.Blocks.SelectMany(block => block.OccupiedCells.Select(cell => (cell, block.Material)))
                .ToDictionary(pair => pair.cell, pair => pair.Material);
            for (var index = 0; index < additions.Count; index++)
            {
                var slope = additions[index];
                foreach (var cell in slope.OccupiedCells)
                {
                    var inboard = cell.X + (2 * cell.X < hull.MinX + hull.MaxX ? 1 : -1);
                    if (owners.TryGetValue((inboard, cell.Y, cell.Z), out var material) ||
                        owners.TryGetValue((cell.X, cell.Y + (slope.Rotation is 0 or 2 ? -1 : 1), cell.Z), out material))
                    {
                        additions[index] = slope with { Material = material };
                        break;
                    }
                }
            }
        }
        return hull with
        {
            Blocks = hull.Blocks.Concat(additions).OrderBy(block => block.Z)
                .ThenBy(block => block.Y).ThenBy(block => block.X).ToArray(),
        };
    }

    public static BlockShape ShapeForLength(int length) => length switch
    {
        1 => BlockShape.Slope1,
        2 => BlockShape.Slope2,
        3 => BlockShape.Slope3,
        4 => BlockShape.Slope4,
        _ => throw new ArgumentOutOfRangeException(nameof(length)),
    };
}
