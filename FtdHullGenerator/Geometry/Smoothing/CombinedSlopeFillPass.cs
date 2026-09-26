using FtdHullGenerator.Domain;

namespace FtdHullGenerator.Geometry.Smoothing;

/// <summary>
/// Resolves the vertical, horizontal, and cross-section fill methods against one
/// unchanged base hull. Candidate footprints are claimed as complete mirrored pairs.
/// </summary>
/// <remarks>
/// Longer complete footprints win first. For equal lengths, the hand-corrected
/// vertical and horizontal rules retain priority over the orientation-derived
/// cross-section rule. Each source pass measures the same input rather than seeing another pass's
/// additions, and contributes its raw pre-collision set so a runner-up remains
/// available after a cross-family conflict. Pass order cannot alter the contours.
/// </remarks>
public sealed class CombinedSlopeFillPass : ISmoothingPass
{
    public SmoothingMethod Method => SmoothingMethod.CombinedSlopeFill;

    public GeneratedHull Apply(GeneratedHull hull)
    {
        ArgumentNullException.ThrowIfNull(hull);
        return ResolveCandidates(hull, CollectCandidates(hull));
    }

    /// <summary>
    /// Collects every raw port-side candidate with its evidence tie-break tier.
    /// No source-local footprint winner is discarded before global arbitration.
    /// </summary>
    internal static IEnumerable<(int Priority, BlockPlacement Placement)> CollectCandidates(
        GeneratedHull hull)
    {
        ArgumentNullException.ThrowIfNull(hull);
        var mirrorSum = hull.MinX + hull.MaxX;
        return CandidateSets(hull)
            .SelectMany(source => source.Placements
                .Where(block => 2 * block.X <= mirrorSum)
                .Select(block => (source.Priority, Placement: block)));
    }

    /// <summary>
    /// Claims a deterministic sequence of port-side candidates. This is separated
    /// from contour discovery so every method shares one collision set and no half
    /// of a mirrored pair can survive a conflict.
    /// </summary>
    internal static GeneratedHull ResolveCandidates(
        GeneratedHull hull,
        IEnumerable<(int Priority, BlockPlacement Placement)> candidates)
    {
        var occupied = hull.Blocks.SelectMany(block => block.OccupiedCells).ToHashSet();
        var claimed = new HashSet<(int X, int Y, int Z)>();
        var additions = new List<BlockPlacement>();

        foreach (var candidate in candidates
                     .DistinctBy(candidate => (candidate.Priority, candidate.Placement.Shape,
                         candidate.Placement.Position, candidate.Placement.Rotation))
                     .OrderByDescending(candidate => candidate.Placement.CellLength)
                     .ThenBy(candidate => candidate.Priority)
                     .ThenBy(candidate => candidate.Placement.Rotation is 0 or 2 or 12 or 14 ? 0 : 1)
                     .ThenBy(candidate => candidate.Placement.Z)
                     .ThenBy(candidate => candidate.Placement.Y)
                     .ThenBy(candidate => candidate.Placement.X)
                     .ThenBy(candidate => candidate.Placement.Rotation))
        {
            SlopeFillPlacement.TryAddMirrored(
                hull,
                occupied,
                claimed,
                additions,
                candidate.Placement,
                allowVerticalSupport: candidate.Priority == 0);
        }

        return SlopeFillPlacement.Merge(hull, additions);
    }

    private static IEnumerable<(int Priority, IReadOnlyList<BlockPlacement> Placements)> CandidateSets(
        GeneratedHull hull)
    {
        yield return (0, VerticalSlopeFillPass.CollectCandidates(hull));
        yield return (1, HorizontalSlopeFillPass.CollectCandidates(hull));
        yield return (2, CrossSectionSlopeFillPass.CollectCandidates(hull));
    }
}
