using FtdHullGenerator.Domain;

namespace FtdHullGenerator.Geometry;

/// <summary>
/// Performs geometry-independent validation of an exported hull lattice.
/// The validator checks the guarantees represented by <see cref="GeneratedHull" />:
/// unique cells, bounds, valid game rotations, material consistency, side-to-side
/// symmetry, and face-connected block occupancy.
/// </summary>
public static class HullGeometryValidator
{
    /// <summary>
    /// A reduced-face native armor member cannot make positive-area contact across
    /// an unequal-layout ownership seam.
    /// </summary>
    public const string NativeArmorContactDiagnosticCode = "ARM001";

    /// <summary>
    /// Validates a generated hull without changing it.
    /// </summary>
    /// <param name="hull">The hull to inspect.</param>
    /// <returns>A list of human-readable failures. An empty list means the lattice is valid.</returns>
    public static IReadOnlyList<string> Validate(GeneratedHull hull)
    {
        ArgumentNullException.ThrowIfNull(hull);

        var errors = new List<string>();
        if (hull.Blocks is null || hull.Blocks.Count == 0)
        {
            errors.Add("The hull does not contain any blocks.");
            return errors;
        }

        if (hull.MinX > hull.MaxX || hull.MinY > hull.MaxY || hull.MinZ > hull.MaxZ)
            errors.Add("The hull bounds are inverted.");

        // Cells are keyed by occupancy rather than by anchor so that a multi-cell beam
        // is checked over its whole footprint. Symmetry stays anchor-keyed below.
        var cellOwners = new Dictionary<(int X, int Y, int Z), BlockPlacement>(hull.OccupiedCellCount);
        var anchors = new Dictionary<(int X, int Y, int Z), BlockPlacement>(hull.Blocks.Count);
        var actualMinX = int.MaxValue;
        var actualMaxX = int.MinValue;
        var actualMinY = int.MaxValue;
        var actualMaxY = int.MinValue;
        var actualMinZ = int.MaxValue;
        var actualMaxZ = int.MinValue;
        var configuredMaterials = hull.Parameters.HullArmor.Layers
            .Concat(hull.Parameters.EffectiveBottomArmor.Layers)
            .Concat(hull.Parameters.DeckArmor?.Layers ?? [])
            .Select(layer => layer.Material)
            .OfType<MaterialKind>()
            .ToHashSet();
        if (hull.Parameters.EffectiveSuperstructure.Enabled)
            configuredMaterials.Add(hull.Parameters.EffectiveSuperstructure.Material);

        foreach (var block in hull.Blocks)
        {
            if (!anchors.TryAdd(block.Position, block))
                errors.Add($"More than one block is anchored at ({block.X}, {block.Y}, {block.Z}).");

            if (!configuredMaterials.Contains(block.Material))
                errors.Add($"Block at ({block.X}, {block.Y}, {block.Z}) uses a material absent from the armor layouts.");

            // The footprint expansion reads the rotation table, so an out-of-range
            // rotation must be reported and skipped rather than allowed to throw.
            if (!BlockRotations.IsValid(block.Rotation))
            {
                errors.Add($"Block at ({block.X}, {block.Y}, {block.Z}) uses invalid rotation {block.Rotation}.");
                continue;
            }

            // Every registered multi-cell structural shape has a verified linear +Z
            // footprint. The game's 24 rotations transform that footprint exactly;
            // family-specific historical rotation lists are no longer appropriate.
            foreach (var cell in block.OccupiedCells)
            {
                if (!cellOwners.TryAdd(cell, block))
                    errors.Add($"More than one block occupies ({cell.X}, {cell.Y}, {cell.Z}).");

                actualMinX = Math.Min(actualMinX, cell.X);
                actualMaxX = Math.Max(actualMaxX, cell.X);
                actualMinY = Math.Min(actualMinY, cell.Y);
                actualMaxY = Math.Max(actualMaxY, cell.Y);
                actualMinZ = Math.Min(actualMinZ, cell.Z);
                actualMaxZ = Math.Max(actualMaxZ, cell.Z);
            }
        }

        if (cellOwners.Count == 0)
        {
            errors.Add("The hull does not occupy any lattice cells.");
            return errors;
        }

        if (actualMinX != hull.MinX || actualMaxX != hull.MaxX ||
            actualMinY != hull.MinY || actualMaxY != hull.MaxY ||
            actualMinZ != hull.MinZ || actualMaxZ != hull.MaxZ)
        {
            errors.Add("The declared hull bounds do not match the placed blocks.");
        }

        if (!cellOwners.Keys.Any(position => position.X == hull.MinX) ||
            !cellOwners.Keys.Any(position => position.X == hull.MaxX) ||
            !cellOwners.Keys.Any(position => position.Y == hull.MinY) ||
            !cellOwners.Keys.Any(position => position.Y == hull.MaxY) ||
            !cellOwners.Keys.Any(position => position.Z == hull.MinZ) ||
            !cellOwners.Keys.Any(position => position.Z == hull.MaxZ))
        {
            errors.Add("The hull does not occupy every declared outer extent.");
        }

        ValidateSymmetry(hull, anchors, errors);
        foreach (var block in FindUnsupportedNativeArmorContacts(hull, cellOwners))
        {
            errors.Add(
                $"{NativeArmorContactDiagnosticCode}: Native armor member {block.Shape} " +
                $"at ({block.X}, {block.Y}, {block.Z}) " +
                $"has no positive-area physical contact; use a full-volume member at this seam.");
        }
        if (!hull.Parameters.HullArmor.HasInternalAirGap &&
            !hull.Parameters.EffectiveBottomArmor.HasInternalAirGap &&
            hull.Parameters.DeckArmor?.HasInternalAirGap != true)
        {
            ValidateConnectivity(cellOwners.Keys, errors);
        }
        return errors;
    }

    /// <summary>
    /// Finds reduced-face armor members at unequal-layout ownership seams that are
    /// voxel-adjacent but do not share a positive-area analytic face with any seam
    /// neighbor. This is the physical-contact check used by generation's local
    /// full-volume fallback and by validation's ARM001 diagnostic.
    /// </summary>
    internal static IReadOnlyList<BlockPlacement> FindUnsupportedNativeArmorContacts(GeneratedHull hull)
    {
        ArgumentNullException.ThrowIfNull(hull);
        var cellOwners = new Dictionary<(int X, int Y, int Z), BlockPlacement>();
        foreach (var block in hull.Blocks)
        {
            if (!BlockRotations.IsValid(block.Rotation))
                continue;
            foreach (var cell in block.OccupiedCells)
                cellOwners.TryAdd(cell, block);
        }
        return FindUnsupportedNativeArmorContacts(hull, cellOwners);
    }

    private static IReadOnlyList<BlockPlacement> FindUnsupportedNativeArmorContacts(
        GeneratedHull hull,
        IReadOnlyDictionary<(int X, int Y, int Z), BlockPlacement> cellOwners)
    {
        var unsupported = new List<BlockPlacement>();
        foreach (var block in hull.Blocks)
        {
            if (!RequiresNativeArmorContact(hull, block) || !BlockRotations.IsValid(block.Rotation))
                continue;

            var neighbours = new HashSet<BlockPlacement>();
            foreach (var cell in block.OccupiedCells)
            {
                Add(cell.X - 1, cell.Y, cell.Z);
                Add(cell.X + 1, cell.Y, cell.Z);
                Add(cell.X, cell.Y - 1, cell.Z);
                Add(cell.X, cell.Y + 1, cell.Z);
                Add(cell.X, cell.Y, cell.Z - 1);
                Add(cell.X, cell.Y, cell.Z + 1);
            }

            var unequalSeamNeighbours = neighbours.Where(neighbour =>
                neighbour.Origin == BlockOrigin.Shell &&
                neighbour.ArmorRegion != block.ArmorRegion &&
                !IsBehindReservedAir(hull, neighbour) &&
                UsesUnequalLayouts(hull, block.ArmorRegion, neighbour.ArmorRegion)).ToArray();
            if (unequalSeamNeighbours.Length > 0 && !unequalSeamNeighbours.Any(neighbour =>
                    BlockRotations.IsValid(neighbour.Rotation) &&
                    StructuralShapeGeometry.TouchingArea(block, neighbour) > 1e-5))
            {
                unsupported.Add(block);
            }
            continue;

            void Add(int x, int y, int z)
            {
                if (cellOwners.TryGetValue((x, y, z), out var neighbour) && neighbour != block)
                    neighbours.Add(neighbour);
            }
        }
        return unsupported;
    }

    private static bool UsesUnequalLayouts(GeneratedHull hull, ArmorRegion first, ArmorRegion second)
    {
        ArmorLayout? Layout(ArmorRegion region) => region switch
        {
            ArmorRegion.Side => hull.Parameters.HullArmor,
            ArmorRegion.Bottom => hull.Parameters.EffectiveBottomArmor,
            ArmorRegion.Deck => hull.Parameters.DeckArmor,
            _ => null,
        };
        var firstLayout = Layout(first);
        var secondLayout = Layout(second);
        return firstLayout is not null && secondLayout is not null && !firstLayout.Equals(secondLayout);
    }

    private static bool RequiresNativeArmorContact(GeneratedHull hull, BlockPlacement block)
    {
        if (block.Origin != BlockOrigin.Shell || !block.KeepsFittedShape)
            return false;
        var family = BlockShapeMetadata.Get(block.Shape).Family;
        if (family is not (StructuralFamily.Pole or StructuralFamily.BeamSlope))
            return false;

        return !IsBehindReservedAir(hull, block);
    }

    private static bool IsBehindReservedAir(GeneratedHull hull, BlockPlacement block)
    {
        var layout = block.ArmorRegion switch
        {
            ArmorRegion.Side => hull.Parameters.HullArmor,
            ArmorRegion.Bottom => hull.Parameters.EffectiveBottomArmor,
            ArmorRegion.Deck => hull.Parameters.DeckArmor,
            _ => null,
        };
        // An internal layer behind deliberately reserved armor air is allowed to be
        // a separate physical skin. Replacing its member cannot create contact without
        // filling that reservation, which this validator must never suggest.
        return layout is not null &&
               layout.Layers.Take(Math.Min(block.ArmorDepth, layout.Thickness)).Any(layer => layer.IsAir);
    }

    /// <summary>
    /// Throws <see cref="HullGenerationException" /> when <paramref name="hull" />
    /// violates any lattice invariant.
    /// </summary>
    public static void EnsureValid(GeneratedHull hull)
    {
        var errors = Validate(hull);
        if (errors.Count > 0)
            throw new HullGenerationException(errors);
    }

    /// <summary>
    /// Returns the From the Depths rotation index obtained by reflecting a block
    /// across the hull's longitudinal X/Z symmetry plane. The mapping is derived
    /// from the game's 24 forward/up orientations.
    /// </summary>
    /// <param name="rotation">A valid From the Depths rotation index from 0 through 23.</param>
    /// <returns>The rotation of the reflected block.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the rotation is outside 0 through 23.</exception>
    public static int MirrorRotation(int rotation) => rotation switch
    {
        0 => 0,
        1 => 3,
        2 => 2,
        3 => 1,
        4 => 4,
        5 => 7,
        6 => 6,
        7 => 5,
        8 => 8,
        9 => 11,
        10 => 10,
        11 => 9,
        12 => 12,
        13 => 15,
        14 => 14,
        15 => 13,
        16 => 18,
        17 => 19,
        18 => 16,
        19 => 17,
        20 => 21,
        21 => 20,
        22 => 23,
        23 => 22,
        _ => throw new ArgumentOutOfRangeException(nameof(rotation), rotation, "Rotation must be between 0 and 23."),
    };

    private static void ValidateSymmetry(
        GeneratedHull hull,
        IReadOnlyDictionary<(int X, int Y, int Z), BlockPlacement> blocksByPosition,
        ICollection<string> errors)
    {
        foreach (var (position, block) in blocksByPosition)
        {
            var mirrorPosition = (hull.MinX + hull.MaxX - position.X, position.Y, position.Z);
            if (!blocksByPosition.TryGetValue(mirrorPosition, out var mirror))
            {
                errors.Add($"Block at ({position.X}, {position.Y}, {position.Z}) has no mirrored counterpart.");
                continue;
            }

            if (MirrorShape(block.Shape) != mirror.Shape || block.Material != mirror.Material)
            {
                errors.Add($"Block at ({position.X}, {position.Y}, {position.Z}) does not match its mirrored counterpart.");
                continue;
            }

            if (block.Rotation is >= 0 and <= 23 &&
                mirror.Rotation is >= 0 and <= 23 &&
                MirrorRotation(block.Rotation) != mirror.Rotation)
            {
                errors.Add($"Block at ({position.X}, {position.Y}, {position.Z}) has an asymmetric rotation.");
            }

            if (block.Shape is BlockShape.Beam2 or BlockShape.Beam3 or BlockShape.Beam4 &&
                BlockRotations.IsValid(block.Rotation) && BlockRotations.IsValid(mirror.Rotation) &&
                BlockRotations.GetRotationAxes(block.Rotation).Forward is { Z: 0 })
            {
                var reflectedCells = block.OccupiedCells
                    .Select(cell => (X: hull.MinX + hull.MaxX - cell.X, cell.Y, cell.Z))
                    .ToHashSet();
                if (!reflectedCells.SetEquals(mirror.OccupiedCells))
                    errors.Add($"Beam at ({position.X}, {position.Y}, {position.Z}) has an asymmetric occupied footprint.");
            }
        }
    }

    /// <summary>
    /// Returns the shape variant required after reflecting a block across the hull
    /// centre plane. Beams and poles are self-mirroring shapes; crosswise beams also
    /// mirror their anchors and rotations so their occupied footprints match.
    /// </summary>
    public static BlockShape MirrorShape(BlockShape shape) => BlockShapeMetadata.MirrorShape(shape);

    private static void ValidateConnectivity(
        IEnumerable<(int X, int Y, int Z)> positions,
        ICollection<string> errors)
    {
        var occupied = positions.ToHashSet();
        if (occupied.Count == 0)
            return;

        var reached = new HashSet<(int X, int Y, int Z)> { occupied.First() };
        var frontier = new Queue<(int X, int Y, int Z)>();
        frontier.Enqueue(occupied.First());

        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();
            Visit(current.X - 1, current.Y, current.Z);
            Visit(current.X + 1, current.Y, current.Z);
            Visit(current.X, current.Y - 1, current.Z);
            Visit(current.X, current.Y + 1, current.Z);
            Visit(current.X, current.Y, current.Z - 1);
            Visit(current.X, current.Y, current.Z + 1);
        }

        if (reached.Count != occupied.Count)
            errors.Add("The hull shell is not face-connected.");

        return;

        void Visit(int x, int y, int z)
        {
            var neighbour = (x, y, z);
            if (occupied.Contains(neighbour) && reached.Add(neighbour))
                frontier.Enqueue(neighbour);
        }
    }
}
