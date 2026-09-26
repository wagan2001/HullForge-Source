using FtdHullGenerator.Domain;

namespace FtdHullGenerator.Geometry.Smoothing;

/// <summary>
/// Bevels vertical gaps between adjacent side columns in each transverse X/Y
/// cross-section. The unchanged shell supplies every measurement.
/// </summary>
/// <remarks>
/// Kevin's pre-built hull collection establishes the Y-axis orientation and strongly
/// supports the dominant hands: port rotations 11 (+Y/-X) and 7 (-Y/-X), mirrored
/// to starboard 9 and 5.
/// It is a finished structural skin rather than an additive baseline. This conservative
/// hypothesis therefore emits only supported 2-4m additions and leaves singletons bare.
/// </remarks>
public sealed class CrossSectionSlopeFillPass : ISmoothingPass
{
    private const int MinimumSlopeLength = 2;
    private const int MaximumSlopeLength = 4;

    public SmoothingMethod Method => SmoothingMethod.CrossSectionSlopeFill;

    public GeneratedHull Apply(GeneratedHull hull)
    {
        ArgumentNullException.ThrowIfNull(hull);
        var occupied = hull.Blocks.SelectMany(block => block.OccupiedCells).ToHashSet();
        var claimed = new HashSet<(int X, int Y, int Z)>();
        var additions = new List<BlockPlacement>();
        foreach (var slope in CollectCandidates(hull))
            SlopeFillPlacement.TryAddMirrored(hull, occupied, claimed, additions, slope);
        return SlopeFillPlacement.Merge(hull, additions);
    }

    /// <summary>Returns raw port-side candidates before footprint conflicts are resolved.</summary>
    internal static IReadOnlyList<BlockPlacement> CollectCandidates(GeneratedHull hull)
    {
        ArgumentNullException.ThrowIfNull(hull);
        var occupied = hull.Blocks.SelectMany(block => block.OccupiedCells).ToHashSet();
        var columns = occupied.GroupBy(cell => (cell.X, cell.Z)).ToDictionary(
            group => group.Key,
            group => (MinY: group.Min(cell => cell.Y), MaxY: group.Max(cell => cell.Y)));
        var mirrorSum = hull.MinX + hull.MaxX;
        var candidates = new List<BlockPlacement>();

        foreach (var column in columns.Where(column => 2 * column.Key.X < mirrorSum)
                     .OrderBy(column => column.Key.Z).ThenBy(column => column.Key.X))
        {
            if (!columns.TryGetValue((column.Key.X + 1, column.Key.Z), out var inboard))
                continue;

            AddRun(
                column.Value.MaxY + 1,
                inboard.MaxY - column.Value.MaxY,
                rotation: 11);
            AddRun(
                column.Value.MinY - 1,
                column.Value.MinY - inboard.MinY,
                rotation: 7);

            void AddRun(int anchorY, int length, int rotation)
            {
                if (length < MinimumSlopeLength)
                    return;

                var slope = new BlockPlacement(
                    SlopeFillPlacement.ShapeForLength(Math.Min(length, MaximumSlopeLength)),
                    hull.Parameters.SurfaceMaterial,
                    column.Key.X,
                    anchorY,
                    column.Key.Z,
                    rotation)
                {
                    Origin = BlockOrigin.Smoothing,
                };
                candidates.Add(slope);
            }
        }

        return candidates;
    }
}
