using FtdHullGenerator.Domain;

namespace FtdHullGenerator.Geometry.Smoothing;

/// <summary>
/// Bevels the bow and stern contours in each port-side Y/Z plane, then mirrors
/// the additions to starboard. The unchanged shell supplies every measurement.
/// </summary>
/// <remarks>
/// At a fixed X, each occupied row has its own bow and stern extent. Changes in
/// that extent give longitudinal treads; consecutive rows with the same extent
/// give vertical risers. Longer candidates win shared corner cells. This follows
/// the local cascade, including its termination at the transom or deck, without
/// treating the parallel mid-body's longitudinal chines as taper steps.
/// Emits 12/14 treads with 4/6 risers on ascending runs and 0/2 treads with 8/10
/// risers on descending ones; this pass, not any prose summary, is the authority
/// for that set.
/// Hand-built smoothing fixtures in the self-tests protect the accepted placements.
/// </remarks>
public sealed class VerticalSlopeFillPass : ISmoothingPass
{
    private const int MaxSlopeLength = 4;
    public SmoothingMethod Method => SmoothingMethod.VerticalSlopeFill;

    public GeneratedHull Apply(GeneratedHull hull)
    {
        ArgumentNullException.ThrowIfNull(hull);
        var occupied = hull.Blocks.SelectMany(block => block.OccupiedCells).ToHashSet();
        var claimed = new HashSet<(int X, int Y, int Z)>();
        var additions = new List<BlockPlacement>();
        foreach (var slope in CollectCandidates(hull)
                     .OrderByDescending(block => block.CellLength)
                     .ThenBy(block => block.Rotation is 0 or 2 or 12 or 14 ? 0 : 1)
                     .ThenBy(block => block.Y).ThenBy(block => block.X)
                     .ThenBy(block => block.Z).ThenBy(block => block.Rotation))
        {
            SlopeFillPlacement.TryAddMirrored(
                hull, occupied, claimed, additions, slope, allowVerticalSupport: true);
        }
        return SlopeFillPlacement.Merge(hull, additions);
    }

    /// <summary>
    /// Returns every raw port-side contour candidate before collisions are resolved.
    /// Combined fill consumes this set so a runner-up can survive when another
    /// smoothing family displaces the independent pass's first choice.
    /// </summary>
    internal static IReadOnlyList<BlockPlacement> CollectCandidates(GeneratedHull hull)
    {
        ArgumentNullException.ThrowIfNull(hull);
        var occupied = hull.Blocks.SelectMany(block => block.OccupiedCells).ToHashSet();
        var mirrorSum = hull.MinX + hull.MaxX;
        var candidates = new List<BlockPlacement>();

        foreach (var plane in occupied.Where(cell => 2 * cell.X <= mirrorSum).GroupBy(cell => cell.X))
        {
            var rows = plane.GroupBy(cell => cell.Y).ToDictionary(
                row => row.Key, row => (MinZ: row.Min(cell => cell.Z), MaxZ: row.Max(cell => cell.Z)));
            var ys = rows.Keys.OrderBy(y => y).ToArray();
            CollectEnd(true);
            CollectEnd(false);

            void CollectEnd(bool bow)
            {
                var direction = bow ? 1 : -1;
                var treadRotation = bow ? 12 : 14;
                int Edge(int y) => bow ? rows[y].MaxZ : rows[y].MinZ;

                foreach (var y in ys)
                {
                    if (!rows.ContainsKey(y + 1))
                        continue;
                    var runLength = direction * (Edge(y + 1) - Edge(y));
                    if (runLength >= 0)
                        AddCandidate(y, Edge(y) + direction, runLength, treadRotation);
                    else
                        AddCandidate(y + 1, Edge(y + 1) + direction, -runLength, bow ? 0 : 2);
                }

                for (var start = 0; start < ys.Length;)
                {
                    var end = start + 1;
                    while (end < ys.Length && ys[end] == ys[end - 1] + 1 && Edge(ys[end]) == Edge(ys[start]))
                        end++;
                    var runLength = end - start;
                    var bottomY = ys[start];
                    var topY = ys[end - 1];
                    var belowTrend = rows.ContainsKey(bottomY - 1)
                        ? Math.Sign(direction * (Edge(bottomY) - Edge(bottomY - 1))) : 0;
                    var aboveTrend = rows.ContainsKey(topY + 1)
                        ? Math.Sign(direction * (Edge(topY + 1) - Edge(topY))) : 0;
                    // At a crest or trough the adjacent treads own the turn. A
                    // single riser orientation cannot describe both branches.
                    if (belowTrend * aboveTrend < 0)
                    {
                        start = end;
                        continue;
                    }
                    var descending = belowTrend < 0 || aboveTrend < 0;
                    // A singleton at the foot of a longer tread belongs to
                    // that tread. Do not leave an orphan 1m bevel when the
                    // straight-edge length limit rejects the tread.
                    var hasLongTread = runLength == 1 && (descending
                        ? rows.ContainsKey(bottomY - 1) && direction * (Edge(bottomY - 1) - Edge(bottomY)) > 1
                        : rows.ContainsKey(topY + 1) && direction * (Edge(topY + 1) - Edge(topY)) > 1);
                    // Singleton risers use their equivalent tread orientation.
                    // Descending risers anchor at the bottom and extend upward;
                    // ascending risers anchor at the top and extend downward.
                    var riserRotation = descending ? bow ? 8 : 10 : bow ? 4 : 6;
                    var singleRotation = descending ? bow ? 0 : 2 : treadRotation;
                    if (!hasLongTread)
                        AddCandidate(descending ? bottomY : topY, Edge(bottomY) + direction, runLength,
                            runLength == 1 ? singleRotation : riserRotation);
                    start = end;
                }
            }

            void AddCandidate(int y, int z, int runLength, int rotation)
            {
                if (runLength < 1)
                    return;
                candidates.Add(new BlockPlacement(SlopeFillPlacement.ShapeForLength(Math.Min(runLength, MaxSlopeLength)),
                    hull.Parameters.SurfaceMaterial, plane.Key, y, z, rotation) { Origin = BlockOrigin.Smoothing });
            }
        }

        return candidates;
    }
}
