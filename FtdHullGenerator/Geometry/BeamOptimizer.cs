using FtdHullGenerator.Domain;

namespace FtdHullGenerator.Geometry;

/// <summary>
/// Replaces runs of full cube placements with native structural members.
/// </summary>
/// <remarks>
/// <para>
/// The first pass merges along the hull's longitudinal Z axis. Poles and beam slopes
/// stay longitudinal; remaining solid one-cell hull armor can then merge across X or Y.
/// A downward beam slope is rolled 180° about its longitudinal axis
/// so its exposed face looks down and outboard. Rotation 12 is that roll: forward is
/// still +Z, so the footprint, the anchor and the occupied cells are untouched, and
/// <c>MirrorRotation(12)</c> is 12, so a Z-run still reflects across the port/starboard
/// plane onto an identical Z-run and the hull stays symmetric without a special case.
/// </para>
/// <para>
/// The pass is a pure repartitioning: it never adds, removes, or moves an occupied
/// cell. Bounds and lattice occupancy are therefore identical before and after. A fitted
/// shell candidate is merged through rather than left as a blocker when the caller asks
/// for it, because the exporter writes such a candidate as a full cube anyway; leaving
/// it as a blocker fragmented straight exterior runs into isolated one-metre cubes.
/// Real smoothing pieces and poles keep their own catalog shape and still break runs.
/// </para>
/// </remarks>
public static class BeamOptimizer
{
    /// <summary>
    /// The roll that turns a downward slope over. Rotation 12 is the 180° roll about the
    /// block's own length axis: forward stays +Z, so the footprint and the occupied cells do
    /// not move, while right and up both invert and the exposed diagonal ends up facing down
    /// and outboard instead of up and inboard.
    /// </summary>
    private const int DownwardSlopeRotation = 12;

    /// <summary>
    /// Returns a hull whose cube runs have been merged into beams or poles. The lattice bounds
    /// are carried over unchanged because the occupied cell set does not change.
    /// </summary>
    /// <param name="hull">The generated hull to repartition.</param>
    /// <param name="mergeFlattenedShellCandidates">
    /// When true, a one-metre shell slope or corner the exporter will flatten to a cube joins the
    /// run instead of blocking it. Shape V2 passes true; the frozen pre-V2 regression sampler
    /// passes false so its historical beam grouping is reproduced exactly.
    /// </param>
    public static GeneratedHull Optimize(GeneratedHull hull, bool mergeFlattenedShellCandidates = false)
    {
        ArgumentNullException.ThrowIfNull(hull);

        // A fresh hull rather than a `with` expression: GeneratedHull.OccupiedCellCount
        // is computed, but constructing explicitly keeps that a local decision.
        var longitudinal = Merge(hull.Blocks, mergeFlattenedShellCandidates);
        var packed = PackRemainingSolidCells(longitudinal, hull.MinX, hull.MaxX);
        return new GeneratedHull(
            hull.Parameters,
            packed,
            hull.MinX,
            hull.MaxX,
            hull.MinY,
            hull.MaxY,
            hull.MinZ,
            hull.MaxZ) { ConstructionNotes = hull.ConstructionNotes };
    }

    /// <summary>
    /// Merges longitudinal runs of cubes within each (x, y, material) column.
    /// The result is ordered by Z, then Y, then X, matching the generator's contract.
    /// </summary>
    internal static IReadOnlyList<BlockPlacement> Merge(
        IReadOnlyList<BlockPlacement> placements,
        bool mergeFlattenedShellCandidates = false)
    {
        ArgumentNullException.ThrowIfNull(placements);
        if (placements.Count == 0)
            return placements;
        var minX = placements.Min(block => block.X);
        var maxX = placements.Max(block => block.X);

        var columns = new Dictionary<(int X, int Y, MaterialKind Material,
            ArmorConstruction Construction, ArmorRegion Region), List<BlockPlacement>>();
        foreach (var placement in placements)
        {
            var construction = placement.UsePoles ? ArmorConstruction.Pole : placement.Construction;
            var region = construction == ArmorConstruction.BeamSlopeSpike
                ? placement.ArmorRegion
                : ArmorRegion.Side;
            var key = (placement.X, placement.Y, placement.Material, construction, region);
            if (!columns.TryGetValue(key, out var column))
                columns[key] = column = new List<BlockPlacement>();
            column.Add(placement);
        }

        var merged = new List<BlockPlacement>(placements.Count);
        var runLengths = new List<int>();
        foreach (var column in columns.Values)
        {
            // Generation already emits Z-ascending per column, but that ordering is an
            // implementation detail of VoxelSolid.Cells rather than a stated contract.
            column.Sort(static (left, right) => left.Z.CompareTo(right.Z));

            var index = 0;
            while (index < column.Count)
            {
                var placement = column[index];
                if (!IsMergeable(placement, mergeFlattenedShellCandidates))
                {
                    merged.Add(placement);
                    index++;
                    continue;
                }

                var runEnd = index + 1;
                while (runEnd < column.Count &&
                       IsMergeable(column[runEnd], mergeFlattenedShellCandidates) &&
                       column[runEnd].Z == column[runEnd - 1].Z + 1)
                {
                    runEnd++;
                }

                runLengths.Clear();
                AppendRunLengths(runEnd - index, runLengths);

                // Run lengths arrive stern to bow, matching this ascending walk in +Z.
                // A run is keyed by material alone, so it may pass from an exposed cell
                // into internal armor where the hull tapers; the member takes the
                // shallowest depth it covers so the preview treats it as shell.
                var z = placement.Z;
                var runStart = index;
                foreach (var length in runLengths)
                {
                    var depth = int.MaxValue;
                    for (var cellIndex = runStart; cellIndex < runStart + length; cellIndex++)
                        depth = Math.Min(depth, column[cellIndex].ArmorDepth);
                    var (shape, rotation) = StructuralMemberFor(length, placement, minX, maxX);
                    merged.Add(placement with
                    {
                        Shape = shape,
                        Z = z,
                        Rotation = rotation,
                        ArmorDepth = depth,
                    });
                    z += length;
                    runStart += length;
                }

                index = runEnd;
            }
        }

        merged.Sort(static (left, right) =>
        {
            var comparison = left.Z.CompareTo(right.Z);
            if (comparison != 0)
                return comparison;
            comparison = left.Y.CompareTo(right.Y);
            return comparison != 0 ? comparison : left.X.CompareTo(right.X);
        });

        var occupied = 0;
        foreach (var placement in merged)
            occupied += placement.CellLength;
        var sourceCellCount = placements.Sum(placement => placement.CellLength);
        if (occupied != sourceCellCount)
        {
            throw new InvalidOperationException(
                $"Beam merging changed the occupied cell count from {sourceCellCount} to {occupied}.");
        }

        return merged;
    }

    /// <summary>
    /// Packs only solid one-cell hull armor left by the established longitudinal pass.
    /// Crosswise runs stop at the center plane, so the port and starboard native anchors
    /// and rotations are exact mirrors. Component and fitted families stay on their
    /// existing packing paths.
    /// </summary>
    internal static IReadOnlyList<BlockPlacement> PackRemainingSolidCells(
        IReadOnlyList<BlockPlacement> placements, int minX, int maxX)
    {
        var crosswise = PackCrosswise(placements, minX, maxX);
        var vertical = PackVertical(crosswise);
        if (vertical.Sum(block => block.CellLength) != placements.Sum(block => block.CellLength))
            throw new InvalidOperationException("Multi-axis beam packing changed the occupied cell count.");
        return vertical.OrderBy(block => block.Z).ThenBy(block => block.Y)
            .ThenBy(block => block.X).ToArray();
    }

    private static bool CanPackAcross(BlockPlacement block) =>
        block.Shape == BlockShape.Cube && block.Origin == BlockOrigin.Shell &&
        block.Construction == ArmorConstruction.Solid && !block.UsePoles;

    private static IReadOnlyList<BlockPlacement> PackCrosswise(
        IReadOnlyList<BlockPlacement> placements, int minX, int maxX)
    {
        var result = placements.Where(block => !CanPackAcross(block) ||
            2 * block.X == minX + maxX).ToList();
        var groups = placements.Where(block => CanPackAcross(block) &&
                2 * block.X != minX + maxX)
            .GroupBy(block => (block.Material, block.Origin, block.ArmorRegion,
                block.ArmorDepth, block.Construction, block.Y, block.Z,
                Side: Math.Sign(2 * block.X - minX - maxX)));
        foreach (var group in groups)
        {
            var ordered = group.Key.Side < 0
                ? group.OrderBy(block => block.X).ToArray()
                : group.OrderByDescending(block => block.X).ToArray();
            PackRuns(ordered, block => block.X, group.Key.Side < 0 ? 1 : -1,
                group.Key.Side < 0 ? 1 : 3, result);
        }
        return result;
    }

    private static IReadOnlyList<BlockPlacement> PackVertical(IReadOnlyList<BlockPlacement> placements)
    {
        var result = placements.Where(block => !CanPackAcross(block)).ToList();
        var groups = placements.Where(CanPackAcross)
            .GroupBy(block => (block.Material, block.Origin, block.ArmorRegion,
                block.ArmorDepth, block.Construction, block.X, block.Z));
        foreach (var group in groups)
            PackRuns(group.OrderBy(block => block.Y).ToArray(), block => block.Y, 1, 8, result);
        return result;
    }

    private static void PackRuns(BlockPlacement[] ordered, Func<BlockPlacement, int> coordinate,
        int direction, int rotation, List<BlockPlacement> result)
    {
        var lengths = new List<int>();
        for (var start = 0; start < ordered.Length;)
        {
            var end = start + 1;
            while (end < ordered.Length &&
                   coordinate(ordered[end]) == coordinate(ordered[end - 1]) + direction)
                end++;
            lengths.Clear();
            AppendRunLengths(end - start, lengths);
            var index = start;
            foreach (var length in lengths)
            {
                var shape = length switch
                {
                    1 => BlockShape.Cube,
                    2 => BlockShape.Beam2,
                    3 => BlockShape.Beam3,
                    4 => BlockShape.Beam4,
                    _ => throw UnsupportedLength(length),
                };
                result.Add(ordered[index] with
                {
                    Shape = shape,
                    Rotation = length == 1 ? 0 : rotation,
                });
                index += length;
            }
            start = end;
        }
    }

    /// <summary>
    /// Splits a run of <paramref name="runLength" /> cells into beam lengths, in stern to
    /// bow order. The split uses the fewest pieces possible, ceil(length / 4), and never
    /// leaves a stray one metre cube beside a beam: a run of 5 becomes 2 + 3, not 4 + 1.
    /// </summary>
    /// <remarks>
    /// Lengths come out non-decreasing, which biases the long beams towards the bow: a run
    /// of 15 becomes 3 + 4 + 4 + 4, so the three 4 m beams carry the forward part of the
    /// run and the odd 3 m remainder sits at the stern. The bow is where a hull's runs get
    /// chopped short by the taper, so leaving the remainder aft keeps the fragmentation
    /// away from the fine end of the shell. The split still depends only on the run
    /// length, which is what keeps mirrored columns identical.
    /// </remarks>
    internal static void AppendRunLengths(int runLength, List<int> lengths)
    {
        ArgumentNullException.ThrowIfNull(lengths);
        ArgumentOutOfRangeException.ThrowIfLessThan(runLength, 1);

        // Take out the 4 m beams first, then emit what is left at the stern end before
        // them. Stopping at 6 rather than 4 is what avoids a trailing one metre cube.
        var fourMetreBeams = 0;
        var remaining = runLength;
        while (remaining >= 6)
        {
            fourMetreBeams++;
            remaining -= 4;
        }

        if (remaining == 5)
        {
            lengths.Add(2);
            lengths.Add(3);
        }
        else
        {
            lengths.Add(remaining);
        }

        for (var index = 0; index < fourMetreBeams; index++)
            lengths.Add(4);
    }

    /// <summary>
    /// Gets whether a placement can take part in a merged longitudinal run. An ordinary
    /// one-metre cube always can. On the Shape V2 path a fitted shell candidate can too:
    /// the shell classifier's one-metre slope and corner candidates are written as full
    /// cubes by <c>BlueprintExporter</c> and drawn as cubes by the preview, so leaving them
    /// as blockers fragmented straight exterior runs. Smoothing pieces, poles, and any
    /// multi-cell member keep their own catalog shape and remain blockers.
    /// </summary>
    private static bool IsMergeable(BlockPlacement placement, bool mergeFlattenedShellCandidates) =>
        placement.Shape == BlockShape.Cube ||
        (mergeFlattenedShellCandidates && !placement.KeepsFittedShape);

    /// <summary>
    /// The native member for one run, with the rotation it is placed at. Only the downward
    /// slope leaves rotation 0: everything else keeps the longitudinal orientation the
    /// validated cube-only export already proved.
    /// </summary>
    private static (BlockShape Shape, int Rotation) StructuralMemberFor(
        int cellLength,
        BlockPlacement placement,
        int minX,
        int maxX)
    {
        var construction = placement.UsePoles ? ArmorConstruction.Pole : placement.Construction;
        if (construction == ArmorConstruction.Pole)
        {
            return (cellLength switch
            {
                1 => BlockShape.Pole1,
                2 => BlockShape.Pole2,
                3 => BlockShape.Pole3,
                4 => BlockShape.Pole4,
                _ => throw UnsupportedLength(cellLength),
            }, 0);
        }

        if (construction == ArmorConstruction.Solid || cellLength == 1)
        {
            return (cellLength switch
            {
                1 => BlockShape.Cube,
                2 => BlockShape.Beam2,
                3 => BlockShape.Beam3,
                4 => BlockShape.Beam4,
                _ => throw UnsupportedLength(cellLength),
            }, 0);
        }

        var side = 2 * placement.X - minX - maxX;
        // A chiral beam slope cannot mirror onto itself on an odd-width centreline.
        // Keep that run solid rather than making the complete hull asymmetric.
        if (side == 0)
        {
            return (cellLength switch
            {
                2 => BlockShape.Beam2,
                3 => BlockShape.Beam3,
                4 => BlockShape.Beam4,
                _ => BlockShape.Cube,
            }, 0);
        }

        var upward = construction switch
        {
            ArmorConstruction.BeamSlopeUp => true,
            ArmorConstruction.BeamSlopeDown => false,
            ArmorConstruction.BeamSlopeSpike => SpikeRowIsUp(placement, minX, maxX),
            _ => throw new ArgumentOutOfRangeException(nameof(placement.Construction)),
        };
        // The two beam slopes are a lateral mirror pair, so the starboard and port halves
        // must take opposite members of the pair or the hull stops being symmetric. That
        // leaves exactly one free choice — which member counts as the upward one — and it is
        // the one the labels are written against: an up run keeps its material against the
        // inboard face and cuts the top outboard corner away, so its exposed slope faces up
        // and outboard, while a down run takes the same member rolled over so it keeps its
        // material against the inboard face at the top and cuts the bottom outboard corner,
        // facing down and outboard.
        var mirrored = side > 0 ? upward : !upward;
        var shape = (cellLength, mirrored) switch
        {
            (2, false) => BlockShape.BeamSlope2,
            (3, false) => BlockShape.BeamSlope3,
            (4, false) => BlockShape.BeamSlope4,
            (2, true) => BlockShape.BeamSlopeMirrored2,
            (3, true) => BlockShape.BeamSlopeMirrored3,
            (4, true) => BlockShape.BeamSlopeMirrored4,
            _ => throw UnsupportedLength(cellLength),
        };
        return (shape, upward ? 0 : DownwardSlopeRotation);
    }

    private static bool SpikeRowIsUp(BlockPlacement placement, int minX, int maxX)
    {
        var row = placement.ArmorRegion == ArmorRegion.Side
            ? placement.Y
            : Math.Abs(2 * placement.X - minX - maxX) / 2;
        return (row & 1) == 0;
    }

    private static ArgumentOutOfRangeException UnsupportedLength(int length) =>
        new(nameof(length), length, "The game only supplies 1–4 m structural members.");
}
