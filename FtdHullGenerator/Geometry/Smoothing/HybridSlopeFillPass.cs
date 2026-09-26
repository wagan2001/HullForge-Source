using FtdHullGenerator.Domain;

namespace FtdHullGenerator.Geometry.Smoothing;

/// <summary>
/// Uses vertical fill below a configurable upper band and horizontal fill inside
/// that band. Both families measure the same unchanged hull.
/// </summary>
/// <remarks>
/// The transition row is <c>MaxY - offset + 1</c>. Candidates must fit wholly on
/// their side of that boundary; crossing vertical slopes are rejected rather than
/// shortened. Horizontal candidates claim their complete mirrored footprints first.
/// </remarks>
public sealed class HybridSlopeFillPass : ISmoothingPass
{
    public SmoothingMethod Method => SmoothingMethod.HybridSlopeFill;

    public GeneratedHull Apply(GeneratedHull hull)
    {
        ArgumentNullException.ThrowIfNull(hull);
        var offset = Math.Clamp(hull.Parameters.HybridFillOffset, 1, Math.Max(1, hull.OccupiedHeight - 1));
        var transitionY = hull.MaxY - offset + 1;
        var horizontal = HorizontalSlopeFillPass.CollectCandidates(hull)
            .Where(candidate => candidate.OccupiedCells.All(cell => cell.Y >= transitionY));
        var vertical = VerticalSlopeFillPass.CollectCandidates(hull)
            .Where(candidate => candidate.OccupiedCells.All(cell => cell.Y < transitionY));
        return ResolveCandidates(hull, horizontal, vertical);
    }

    /// <summary>
    /// Resolves already partitioned raw candidates. Horizontal candidates always
    /// claim first; vertical candidates retain the independent pass's ordering.
    /// </summary>
    internal static GeneratedHull ResolveCandidates(
        GeneratedHull hull,
        IEnumerable<BlockPlacement> horizontalCandidates,
        IEnumerable<BlockPlacement> verticalCandidates)
    {
        ArgumentNullException.ThrowIfNull(hull);
        ArgumentNullException.ThrowIfNull(horizontalCandidates);
        ArgumentNullException.ThrowIfNull(verticalCandidates);

        var occupied = hull.Blocks.SelectMany(block => block.OccupiedCells).ToHashSet();
        var claimed = new HashSet<(int X, int Y, int Z)>();
        var additions = new List<BlockPlacement>();

        foreach (var candidate in horizontalCandidates
                     .DistinctBy(CandidateKey))
        {
            SlopeFillPlacement.TryAddMirrored(hull, occupied, claimed, additions, candidate,
                allowVerticalSupport: hull.Parameters.Shape is not null);
        }

        foreach (var candidate in verticalCandidates
                     .DistinctBy(CandidateKey)
                     .OrderByDescending(block => block.CellLength)
                     .ThenBy(block => block.Rotation is 0 or 2 or 12 or 14 ? 0 : 1)
                     .ThenBy(block => block.Y)
                     .ThenBy(block => block.X)
                     .ThenBy(block => block.Z)
                     .ThenBy(block => block.Rotation))
        {
            SlopeFillPlacement.TryAddMirrored(
                hull, occupied, claimed, additions, candidate, allowVerticalSupport: true);
        }

        return SlopeFillPlacement.Merge(hull, additions);
    }

    private static (BlockShape Shape, (int X, int Y, int Z) Position, int Rotation) CandidateKey(
        BlockPlacement candidate) => (candidate.Shape, candidate.Position, candidate.Rotation);
}
