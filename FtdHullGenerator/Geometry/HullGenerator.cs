using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Composition;
using FtdHullGenerator.Geometry.Composition;

namespace FtdHullGenerator.Geometry;

/// <summary>
/// Generates a deterministic, one-metre lattice approximation of a conventional
/// ship hull. The hull is symmetric across its longitudinal centre plane, has its
/// stern facing negative Z and its bow facing positive Z, and takes the shape of
/// each end from the selected <see cref="BowStyle" /> and <see cref="SternStyle" />.
/// </summary>
/// <remarks>
/// The continuous profile is sampled before any blocks are chosen. Bow entrance
/// and stern run lengths delimit a full-beam body region; overlapping requested
/// lengths are shortened proportionally so their transitions stay ordered. A
/// shared section evaluator blends bottom fullness, flare or tumblehome, and chine
/// character toward the end controls. Per-station keel and deck elevations add
/// profile rise while preserving at least two occupied rows. The final shell is
/// selected from the exterior cells of that analytic solid.
/// </remarks>
public sealed class HullGenerator
{

    /// <summary>
    /// Generates a hollow, inward-layered armor shell from the requested dimensions and profile.
    /// The returned placement list is ordered by Z, then Y, then X, and is stable
    /// for identical input parameters.
    /// </summary>
    /// <param name="parameters">The requested dimensions, profile controls, armor layouts, and construction options.</param>
    /// <returns>A face-connected hull ready for block catalog resolution and blueprint serialization.</returns>
    /// <exception cref="HullGenerationException">
    /// Thrown when parameters are invalid or the requested shape cannot retain a usable cavity.
    /// </exception>
    public GeneratedHull Generate(HullParameters parameters, CancellationToken cancellationToken = default)
        => GenerateCore(parameters, useLegacyShape: false, cancellationToken);

    /// <summary>
    /// Reproduces the pre-Shape-V2 analytic solid for frozen smoothing fixtures.
    /// This is intentionally internal: new UI and export work always uses the
    /// regional sampler through <see cref="Generate" />.
    /// </summary>
    internal GeneratedHull GenerateLegacyRegression(
        HullParameters parameters,
        CancellationToken cancellationToken = default)
        => GenerateCore(parameters, useLegacyShape: true, cancellationToken);

    private static GeneratedHull GenerateCore(
        HullParameters parameters,
        bool useLegacyShape,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        cancellationToken.ThrowIfCancellationRequested();

        var parameterErrors = CollectParameterErrors(parameters);
        if (parameterErrors.Count > 0)
            throw new HullGenerationException(parameterErrors);

        var solid = useLegacyShape
            ? VoxelSolid.CreateLegacy(parameters, cancellationToken)
            : VoxelSolid.Create(parameters, cancellationToken).FillShortLongitudinalNotches(3);
        GeneratedHull hull;
        HullCellRoleClassifier? roleClassifier;
        if (!useLegacyShape && parameters.Smoothing == SmoothingMethod.InvertedTriangleFill)
        {
            hull = BuildCoordinatedHull(solid, parameters, cancellationToken);
            roleClassifier = null;
        }
        else
        {
            hull = BuildUnmergedHull(solid, parameters, useLegacyShape, false, cancellationToken,
                out roleClassifier);
        }

        return CompleteGeneratedHull(hull, parameters, useLegacyShape, roleClassifier, cancellationToken);
    }

    private static GeneratedHull CompleteGeneratedHull(
        GeneratedHull hull,
        HullParameters parameters,
        bool useLegacyShape,
        HullCellRoleClassifier? roleClassifier,
        CancellationToken cancellationToken)
    {
        if (parameters.Beamify)
        {
            // Shape V2 merges through the fitted shell candidates the exporter flattens,
            // so a straight exterior run becomes native beams instead of fragmenting into
            // one-metre cubes. The frozen pre-V2 regression sampler keeps its historical
            // grouping, where any non-cube shape segments the run.
            hull = BeamOptimizer.Optimize(hull, mergeFlattenedShellCandidates: !useLegacyShape);
            hull = PreserveNativeArmorContact(hull);
            HullGeometryValidator.EnsureValid(hull);
        }

        // The original analytic role classification is attached after packing so every downstream
        // Shape V2 consumer — including a direct smoothing-pass call on this returned hull — can
        // tell genuine exterior space from the hollow cavity without rebuilding the hull.
        if (parameters.Shape is not null && roleClassifier is not null)
            hull = hull with { RoleClassifier = roleClassifier };

        // Other methods keep their established additive, post-beamification contract.
        if (parameters.Smoothing is not SmoothingMethod.None and not SmoothingMethod.InvertedTriangleFill)
        {
            hull = Smoothing.SmoothingPipeline.Apply(hull, parameters.Smoothing);
            HullGeometryValidator.EnsureValid(hull);
        }
        if (parameters.EffectiveSuperstructure.Enabled)
            hull = SuperstructureGenerator.Apply(hull, cancellationToken);
        return hull;
    }

    /// <summary>
    /// Replaces only native pole/beam-slope members whose analytic solid has no
    /// positive-area contact with any neighboring placement. Their occupied cells,
    /// material and ownership stay unchanged; a full-volume cube/beam is the safe
    /// fallback at a quantized boundary between unequal armor layouts.
    /// </summary>
    /// <remarks>
    /// Occupied-cell adjacency is insufficient for these reduced-face families: a
    /// pole can touch a lateral block only along a line, and complementary slope cuts
    /// can leave two neighboring reserved cells with no shared face. This correction
    /// is deliberately post-packing and local. It does not fill an empty lattice cell,
    /// cross reserved armor air, or rewrite a supported native run or equal-layout join.
    /// </remarks>
    private static GeneratedHull PreserveNativeArmorContact(GeneratedHull hull)
    {
        var unsupported = HullGeometryValidator.FindUnsupportedNativeArmorContacts(hull);
        if (unsupported.Count == 0)
            return hull;

        var fallbackSet = unsupported.ToHashSet();

        var blocks = hull.Blocks.Select(block => fallbackSet.Contains(block)
            ? FullVolumeFallback(block)
            : block).ToArray();
        var orderedFallbacks = fallbackSet.OrderBy(block => block.Z).ThenBy(block => block.Y).ThenBy(block => block.X).ToArray();
        var locations = string.Join(", ", orderedFallbacks.Take(6)
            .Select(block => $"({block.X}, {block.Y}, {block.Z})"));
        if (orderedFallbacks.Length > 6)
            locations += $", and {orderedFallbacks.Length - 6} more";
        var note = $"{HullGeometryValidator.NativeArmorContactDiagnosticCode} contact fallback: " +
                   $"replaced {orderedFallbacks.Length} native armor " +
                   $"member(s) at {locations} with full-volume members; occupied cells, materials, " +
                   "armor air and cavity were unchanged.";

        return new GeneratedHull(
            hull.Parameters,
            Array.AsReadOnly(blocks),
            hull.MinX,
            hull.MaxX,
            hull.MinY,
            hull.MaxY,
            hull.MinZ,
            hull.MaxZ)
        {
            ConstructionNotes = hull.ConstructionNotes.Append(note).ToArray(),
        };

        static BlockPlacement FullVolumeFallback(BlockPlacement block) => block with
        {
            Shape = block.CellLength switch
            {
                1 => BlockShape.Cube,
                2 => BlockShape.Beam2,
                3 => BlockShape.Beam3,
                4 => BlockShape.Beam4,
                _ => throw new InvalidOperationException(
                    $"Native armor member {block.Shape} has unsupported length {block.CellLength}."),
            },
            Rotation = 0,
            UsePoles = false,
            Construction = ArmorConstruction.Solid,
        };

    }

    private static GeneratedHull BuildCoordinatedHull(
        VoxelSolid original, HullParameters parameters, CancellationToken cancellationToken)
    {
        var plan = Smoothing.InvertedTriangleConstruction.Plan(
            original.ToSurfaceGrid(), parameters, cancellationToken);
        // An outward span edit must not erase an old wall or a connectivity
        // bridge just because it becomes interior to the revised voxel solid.
        // Keep that original boundary as armor seeds; fitted replacements must
        // independently retain every face bordering the original cavity.
        var originalShell = ExtractShell(original, parameters.HasDeck, cancellationToken);
        var originalSurfaceRows = originalShell.Select(cell => (cell.Y, cell.Z)).ToHashSet();
        var notes = new List<string>();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var revised = original.WithSurfaceGrid(plan.ModifiedGrid);
                var fittedSurfaceCells = plan.Regions.SelectMany(region => region.Placements)
                    .SelectMany(block => block.OccupiedCells)
                    .Concat(plan.Regions.SelectMany(region => region.RequiredBacking))
                    .Concat(originalShell.Select(cell => (cell.X, cell.Y, cell.Z))).Distinct().ToArray();
                var baseHull = BuildUnmergedHull(revised, parameters, false, true, cancellationToken,
                    out _, fittedSurfaceCells, parameters.HasDeck ? null : originalSurfaceRows);
                var fitted = plan.TryFit(baseHull, cancellationToken);
                if (fitted.Succeeded)
                {
                    HullGeometryValidator.EnsureValid(fitted.Hull);
                    return fitted.Hull with
                    {
                        ConstructionNotes = notes.Concat(fitted.Diagnostics).Distinct().ToArray(),
                    };
                }
                notes.Add("Some fitted regions were restored because their armor or surface joins did not fit.");
                plan = plan.WithoutRegions(fitted.FailedRegionIds);
            }
            catch (HullGenerationException)
            {
                // A changed solid can fail the existing armor/cavity checks before
                // individual fitted regions can be reconciled. Restore the original
                // instead of masking invalid user parameters or returning partial armor.
                notes.Add("The fitted contour could not retain the requested armor and cavity; the original contour was restored.");
                break;
            }
        }
        var fallback = BuildUnmergedHull(original, parameters, false, true, cancellationToken, out _);
        return fallback with
        {
            ConstructionNotes = notes.Append("Inverted construction could not complete this hull; its original construction was retained.")
                .Distinct().ToArray(),
        };
    }

    private static GeneratedHull BuildUnmergedHull(
        VoxelSolid solid, HullParameters parameters, bool useLegacyShape,
        bool cubeSurface, CancellationToken cancellationToken,
        out HullCellRoleClassifier? roleClassifier,
        IReadOnlyList<(int X, int Y, int Z)>? fittedSurfaceCells = null,
        IReadOnlySet<(int Y, int Z)>? originalSurfaceRows = null)
    {
        var (outerShell, armor, armorErrors) = BuildShellAndArmor(
            solid, parameters, useLegacyShape, cancellationToken, fittedSurfaceCells, originalSurfaceRows);
        var topologyErrors = armorErrors.Concat(ValidateTopology(
            solid, armor.Keys.ToHashSet(), parameters.HasDeck, cancellationToken)).ToList();
        roleClassifier = CreateRoleClassifier(solid, armor);
        return MaterializeEvaluatedHull(
            solid, outerShell, armor, topologyErrors, parameters,
            useLegacyShape, cubeSurface, cancellationToken);
    }

    /// <summary>
    /// Materializes one already-evaluated shell/armor stage. Both the ordinary generator and
    /// the feature-free composition adapter enter here, which makes the adapter consume the
    /// context's captured evaluation instead of approximating it through public samples.
    /// </summary>
    private static GeneratedHull MaterializeEvaluatedHull(
        VoxelSolid solid,
        HashSet<Cell> outerShell,
        IReadOnlyDictionary<Cell, ArmorCell> armor,
        IReadOnlyList<string> errors,
        HullParameters parameters,
        bool useLegacyShape,
        bool cubeSurface,
        CancellationToken cancellationToken)
    {
        if (errors.Count > 0)
            throw new HullGenerationException(errors);

        var blocks = CreatePlacements(
            solid, outerShell, armor, cancellationToken, useLegacyShape, cubeSurface);
        var bounds = OccupiedBounds(blocks);
        parameters = parameters with
        {
            HybridFillOffset = Math.Clamp(parameters.HybridFillOffset, 1, Math.Max(1, bounds.MaxY - bounds.MinY)),
        };
        var hull = new GeneratedHull(parameters, blocks, bounds.MinX, bounds.MaxX,
            bounds.MinY, bounds.MaxY, bounds.MinZ, bounds.MaxZ);
        HullGeometryValidator.EnsureValid(hull);
        return hull;
    }

    /// <summary>
    /// Captures the original analytic cell-role classification of one evaluated shell/armor stage.
    /// It is the same solid, armor intent and deck-interior test the hull-context
    /// <c>RoleAt</c> query reports. The captured solid and armor are read-only after this point, so
    /// the closure shares them rather than copying the evaluated hull.
    /// </summary>
    private static HullCellRoleClassifier CreateRoleClassifier(
        VoxelSolid solid,
        IReadOnlyDictionary<Cell, ArmorCell> armor) =>
        (x, y, z) =>
        {
            if (!solid.Contains(x, y, z))
                return HullCellRole.Outside;
            if (armor.TryGetValue(new Cell(x, y, z), out var armorCell))
                return RoleForArmor(armorCell);
            return IsDeckInterior(solid, new Cell(x, y, z)) ? HullCellRole.Outside : HullCellRole.Cavity;
        };

    /// <summary>
    /// Builds the exterior shell and its armor layers for one evaluated solid. This is the
    /// single shared implementation behind <see cref="GenerateCore" /> and
    /// <see cref="CreateContext" />, so the shell and armor a context reports are the same ones the
    /// generator builds for the same solid.
    /// </summary>
    /// <remarks>
    /// Scope: the caller decides which solid this runs on. <see cref="CreateContext" /> evaluates the
    /// feature-free base solid with the historical fitted/replaced-row arguments null, so a context
    /// describes the pre-materialization hull. The deferred inverted-construction route revises the
    /// solid first, and beam merging, the additive smoothing passes and the superstructure stage run
    /// after this point, so a context is not a description of the final placement list.
    /// </remarks>
    private static (HashSet<Cell> OuterShell, Dictionary<Cell, ArmorCell> Armor, IReadOnlyList<string> ArmorErrors)
        BuildShellAndArmor(
            VoxelSolid solid,
            HullParameters parameters,
            bool useLegacyShape,
            CancellationToken cancellationToken,
            IReadOnlyList<(int X, int Y, int Z)>? fittedSurfaceCells = null,
            IReadOnlySet<(int Y, int Z)>? originalSurfaceRows = null)
    {
        var outerShell = ExtractShell(solid, parameters.HasDeck, cancellationToken);
        // Interlocking native panels can expose two adjacent lattice cells in a
        // row. Their cut faces make the inboard piece part of the skin, even when
        // the enclosing voxel solid would classify it as a second armor layer.
        // The planner reserves verified surface footprints and their original
        // exposed cube caps; seed armor from both before propagating its inward
        // material/air layers. Adjacent outward panels must not swallow a cap.
        if (fittedSurfaceCells is not null)
        {
            foreach (var cell in fittedSurfaceCells)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!solid.Contains(cell.X, cell.Y, cell.Z))
                    throw new HullGenerationException(["A fitted surface extends outside its planned contour."]);
                outerShell.Add(new Cell(cell.X, cell.Y, cell.Z));
            }
        }
        // Deckless construction deliberately discards isolated top-only fragments.
        // A new panel must not reconnect them and create an originally empty row.
        // Missing required caps will reject the complete region during fitting.
        if (originalSurfaceRows is not null)
            outerShell.RemoveWhere(cell => !originalSurfaceRows.Contains((cell.Y, cell.Z)));
        var canJoinThroughArmor = !useLegacyShape && Math.Max(
            Math.Max(parameters.HullArmor.Thickness, parameters.EffectiveBottomArmor.Thickness),
            parameters.DeckArmor?.Thickness ?? 0) > 1;
        if (useLegacyShape)
            ConnectShell(solid, outerShell, cancellationToken);
        else if (!canJoinThroughArmor)
            ConnectRegionalShell(solid, outerShell, parameters.HasDeck, cancellationToken);
        if (originalSurfaceRows is not null)
            outerShell.RemoveWhere(cell => !originalSurfaceRows.Contains((cell.Y, cell.Z)));
        // Reserved air participates in armor depth and cavity validation, but is
        // filtered before placements are emitted.
        var (armor, armorErrors) = BuildArmor(solid, outerShell, parameters, cancellationToken);
        if (canJoinThroughArmor)
            FillShortLongitudinalArmorGaps(solid, armor, 3, cancellationToken);
        var structuralArmor = armor.Where(pair => !pair.Value.Layer.IsAir)
            .Select(pair => pair.Key).ToHashSet();
        if (canJoinThroughArmor && structuralArmor.Count > 0 && !IsFaceConnected(structuralArmor))
        {
            ConnectRegionalShell(solid, outerShell, parameters.HasDeck, cancellationToken);
            (armor, armorErrors) = BuildArmor(solid, outerShell, parameters, cancellationToken);
            FillShortLongitudinalArmorGaps(solid, armor, 3, cancellationToken);
        }

        return (outerShell, armor, armorErrors);
    }

    /// <summary>
    /// Evaluates the same analytic solid and armor stage the real generator uses and exposes
    /// them as an immutable, read-only <see cref="HullBuildContext" /> for composition features.
    /// The occupancy, deck-interior and deck/floor queries are delegates over the private
    /// <c>VoxelSolid</c>; no voxel data is copied and no second approximation is built.
    /// </summary>
    /// <param name="parameters">The requested dimensions, profile controls and armor layouts.</param>
    /// <param name="deckOpenings">The deck cells a feature is authorized to open, or <c>null</c> for none.</param>
    /// <param name="cancellationToken">Cancels the context build. A cancelled build returns no context.</param>
    /// <exception cref="HullGenerationException">Thrown when the parameters are invalid.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the token is cancelled before the context is returned.</exception>
    public static HullBuildContext CreateContext(
        HullParameters parameters,
        DeckOpeningMask? deckOpenings = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        cancellationToken.ThrowIfCancellationRequested();

        var parameterErrors = CollectParameterErrors(parameters);
        if (parameterErrors.Count > 0)
            throw new HullGenerationException(parameterErrors);

        // The feature-free evaluation path. Inverted construction is deferred experimental
        // work, so the context describes the base solid and armor it is built on.
        var solid = VoxelSolid.Create(parameters, cancellationToken).FillShortLongitudinalNotches(3);
        var (outerShell, armor, armorErrors) = BuildShellAndArmor(
            solid, parameters, useLegacyShape: false, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var topologyErrors = ValidateTopology(
            solid, armor.Keys.ToHashSet(), parameters.HasDeck, cancellationToken);

        GeneratedHull ComposeFeatureFree(CancellationToken token)
        {
            if (parameters.Smoothing == SmoothingMethod.InvertedTriangleFill)
            {
                throw new HullGenerationException(
                ["Feature-free composition does not include deferred inverted construction; use the legacy generator route."]);
            }

            var errors = armorErrors.Concat(topologyErrors).ToList();
            var unmerged = MaterializeEvaluatedHull(
                solid, outerShell, armor, errors, parameters,
                useLegacyShape: false, cubeSurface: false, token);
            // The captured evaluation is the same one the context answers RoleAt from, so the
            // feature-free composition path smooths against the hull's real exterior too.
            return CompleteGeneratedHull(unmerged, parameters, useLegacyShape: false,
                CreateRoleClassifier(solid, armor), token);
        }

        var armorIntents = new Dictionary<HullCell, HullCellIntent>(armor.Count);
        var protectedShell = new HashSet<HullCell>();
        foreach (var (cell, armorCell) in armor)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hullCell = new HullCell(cell.X, cell.Y, cell.Z);
            armorIntents[hullCell] = new HullCellIntent(
                hullCell,
                RoleForArmor(armorCell),
                armorCell.Layer.Material,
                armorCell.Region,
                armorCell.Depth,
                armorCell.Layer.Construction);
            if (IsProtectedShellArmor(armorCell))
                protectedShell.Add(hullCell);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new HullBuildContext(
            solid.MinX, solid.MaxX, solid.MinY, solid.MaxY, solid.MinZ, solid.MaxZ,
            parameters.Height - 1,
            solid.Contains,
            (x, y, z) => IsDeckInterior(solid, new Cell(x, y, z)),
            solid.DeckYAt,
            solid.FloorYAt,
            armorIntents,
            protectedShell,
            deckOpenings ?? DeckOpeningMask.None,
            armorErrors,
            topologyErrors,
            ComposeFeatureFree);
    }

    /// <summary>
    /// Classifies one armor-stage cell. The exposed layer takes its surface family; deeper
    /// structural layers are internal armor; a null material is a deliberate air reservation.
    /// </summary>
    private static HullCellRole RoleForArmor(ArmorCell armorCell)
    {
        if (armorCell.Layer.IsAir)
            return HullCellRole.ReservedArmorAir;
        if (armorCell.Depth > 0)
            return HullCellRole.InternalArmor;
        return armorCell.Region switch
        {
            ArmorRegion.Side => HullCellRole.SideArmor,
            ArmorRegion.Bottom => HullCellRole.BottomArmor,
            ArmorRegion.Deck => HullCellRole.DeckArmor,
            _ => HullCellRole.InternalArmor,
        };
    }

    /// <summary>
    /// True for the exposed side, bottom and rim armor cells a feature cut must never remove.
    /// The deck is excluded because an authorized deck aperture is meant to open it.
    /// </summary>
    private static bool IsProtectedShellArmor(ArmorCell armorCell) =>
        armorCell.Depth == 0 &&
        !armorCell.Layer.IsAir &&
        armorCell.Region is ArmorRegion.Side or ArmorRegion.Bottom;

    private static (int MinX, int MaxX, int MinY, int MaxY, int MinZ, int MaxZ) OccupiedBounds(
        IReadOnlyList<BlockPlacement> blocks)
    {
        var minX = int.MaxValue;
        var maxX = int.MinValue;
        var minY = int.MaxValue;
        var maxY = int.MinValue;
        var minZ = int.MaxValue;
        var maxZ = int.MinValue;
        foreach (var block in blocks)
        foreach (var cell in block.OccupiedCells)
        {
            minX = Math.Min(minX, cell.X);
            maxX = Math.Max(maxX, cell.X);
            minY = Math.Min(minY, cell.Y);
            maxY = Math.Max(maxY, cell.Y);
            minZ = Math.Min(minZ, cell.Z);
            maxZ = Math.Max(maxZ, cell.Z);
        }

        return (minX, maxX, minY, maxY, minZ, maxZ);
    }

    private static List<string> CollectParameterErrors(HullParameters parameters)
    {
        var errors = parameters.Validate().ToList();
        if (!double.IsFinite(parameters.BowFullness) ||
            !double.IsFinite(parameters.SternFullness) ||
            !double.IsFinite(parameters.CrossSectionCurve))
        {
            errors.Add("Shape controls must be finite numbers.");
        }

        if (parameters.HullArmor is not null &&
            parameters.HullArmor.Layers.Any(layer => layer.Material is { } material && !Enum.IsDefined(material)))
        {
            errors.Add("The selected hull armor material is not supported.");
        }
        if (parameters.DeckArmor is not null &&
            parameters.DeckArmor.Layers.Any(layer => layer.Material is { } material && !Enum.IsDefined(material)))
        {
            errors.Add("The selected deck armor material is not supported.");
        }

        return errors;
    }

    /// <summary>One armor cell: which layer fills it and how many metres inward it sits.</summary>
    private readonly record struct ArmorCell(ArmorLayer Layer, int Depth, ArmorRegion Region);

    private static (Dictionary<Cell, ArmorCell> Armor, IReadOnlyList<string> Errors) BuildArmor(
        VoxelSolid solid,
        HashSet<Cell> outerShell,
        HullParameters parameters,
        CancellationToken cancellationToken)
    {
        if (!parameters.EffectiveBottomArmor.Equals(parameters.HullArmor))
            return BuildSeparateArmor(solid, outerShell, parameters, cancellationToken);
        var armor = new Dictionary<Cell, ArmorCell>();
        var errors = new List<string>();
        var hullSeeds = parameters.HasDeck
            ? outerShell.Where(cell => !IsDeckInterior(solid, cell)).ToHashSet()
            : outerShell.ToHashSet();
        var distances = new Dictionary<Cell, int>();
        var regions = new Dictionary<Cell, ArmorRegion>();
        var frontier = new Queue<Cell>();

        foreach (var seed in hullSeeds.OrderBy(cell => cell.Z).ThenBy(cell => cell.Y).ThenBy(cell => cell.X))
        {
            distances[seed] = 0;
            regions[seed] = !solid.Contains(seed.X, seed.Y - 1, seed.Z)
                ? ArmorRegion.Bottom
                : ArmorRegion.Side;
            frontier.Enqueue(seed);
        }

        var hullLayerCounts = new int[parameters.HullArmor.Thickness];
        while (frontier.Count > 0)
        {
            if ((frontier.Count & 0xFFF) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            var cell = frontier.Dequeue();
            var depth = distances[cell];
            if (depth >= parameters.HullArmor.Thickness)
                continue;

            armor[cell] = new ArmorCell(parameters.HullArmor.Layers[depth], depth, regions[cell]);
            hullLayerCounts[depth]++;
            if (depth + 1 >= parameters.HullArmor.Thickness)
                continue;

            foreach (var neighbour in Neighbours(cell))
            {
                // Hull armor grows from the sides, keel, bow, and stern. The top
                // interior is reserved for the independent deck layout.
                if (!solid.Contains(neighbour.X, neighbour.Y, neighbour.Z) ||
                    solid.IsDeckSurface(neighbour) && !hullSeeds.Contains(neighbour) ||
                    distances.ContainsKey(neighbour))
                {
                    continue;
                }

                distances[neighbour] = depth + 1;
                regions[neighbour] = regions[cell];
                frontier.Enqueue(neighbour);
            }
        }

        for (var index = 0; index < hullLayerCounts.Length; index++)
        {
            if (hullLayerCounts[index] == 0)
                errors.Add($"Hull armor layer {index + 1} does not fit inside this hull profile.");
        }

        if (parameters.DeckArmor is not null)
        {
            for (var index = 0; index < parameters.DeckArmor.Thickness; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = 0;
                for (var z = solid.MinZ; z <= solid.MaxZ; z++)
                {
                    var deckY = solid.DeckYAt(z);
                    if (deckY < solid.MinY)
                        continue;
                    var y = deckY - index;
                    if (y < solid.FloorYAt(z))
                        continue;
                    foreach (var cell in solid.CellsInRow(y, z))
                    {
                        // The hull layout owns intersections so side armor remains
                        // continuous through the deck rim and inward corners.
                        if (armor.TryAdd(cell, new ArmorCell(parameters.DeckArmor.Layers[index], index, ArmorRegion.Deck)))
                            count++;
                    }
                }

                if (count == 0)
                    errors.Add($"Deck armor layer {index + 1} does not fit inside the hull armor.");
            }
        }

        return (armor, errors);
    }

    /// <summary>
    /// Closes brief interruptions inside one otherwise continuous longitudinal
    /// armor run. The enclosing voxel row must exist throughout. Equal anchors are
    /// copied exactly; at a side/bottom ownership seam the shallower structural
    /// wave continues across the gap. Deliberate air layers are never bridged.
    /// </summary>
    private static void FillShortLongitudinalArmorGaps(
        VoxelSolid solid,
        Dictionary<Cell, ArmorCell> armor,
        int maximumLength,
        CancellationToken cancellationToken)
    {
        var additions = new List<(Cell Cell, ArmorCell Armor)>();
        for (var x = solid.MinX; x <= solid.MaxX; x++)
        for (var y = solid.MinY; y <= solid.MaxY; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var z = solid.MinZ + 1;
            while (z < solid.MaxZ)
            {
                var startCell = new Cell(x, y, z - 1);
                if (!armor.TryGetValue(startCell, out var before) ||
                    armor.ContainsKey(new Cell(x, y, z)))
                {
                    z++;
                    continue;
                }

                var start = z;
                while (z < solid.MaxZ && !armor.ContainsKey(new Cell(x, y, z)))
                    z++;
                var afterCell = new Cell(x, y, z);
                if (z - start > maximumLength || !armor.TryGetValue(afterCell, out var after))
                    continue;

                ArmorCell bridge;
                if (before == after)
                {
                    bridge = before;
                }
                else if (before.Region != after.Region && !before.Layer.IsAir && !after.Layer.IsAir)
                {
                    // At a side/bottom ownership seam the two independent waves can
                    // pass one another without meeting. Continue the shallower wave;
                    // equal depths keep the established side-wins tie rule.
                    bridge = before.Depth < after.Depth ? before :
                        after.Depth < before.Depth ? after :
                        before.Region == ArmorRegion.Side ? before : after;
                }
                else
                {
                    continue;
                }

                var valid = true;
                for (var fillZ = start; fillZ < z && valid; fillZ++)
                    valid = solid.Contains(x, y, fillZ);
                if (!valid)
                    continue;

                for (var fillZ = start; fillZ < z; fillZ++)
                    additions.Add((new Cell(x, y, fillZ), bridge));
            }
        }

        foreach (var addition in additions)
            armor.TryAdd(addition.Cell, addition.Armor);
    }

    private static (Dictionary<Cell, ArmorCell> Armor, IReadOnlyList<string> Errors) BuildSeparateArmor(
        VoxelSolid solid, HashSet<Cell> outerShell, HullParameters parameters, CancellationToken cancellationToken)
    {
        var armor = new Dictionary<Cell, ArmorCell>();
        var regions = new Dictionary<Cell, string>();
        var errors = new List<string>();
        var seeds = outerShell.Where(cell => !parameters.HasDeck || !IsDeckInterior(solid, cell)).ToHashSet();
        var bottomSeeds = seeds.Where(cell => !solid.Contains(cell.X, cell.Y - 1, cell.Z)).ToHashSet();
        Grow(parameters.HullArmor, seeds.Except(bottomSeeds), "Side");
        Grow(parameters.EffectiveBottomArmor, bottomSeeds, "Bottom");
        foreach (var (label, layout) in new[] { ("Side", parameters.HullArmor), ("Bottom", parameters.EffectiveBottomArmor) })
        for (var depth = 0; depth < layout.Thickness; depth++)
            if (!regions.Any(pair => pair.Value == label && armor[pair.Key].Depth == depth))
                errors.Add($"{label} armor layer {depth + 1} does not fit inside this hull profile.");

        if (parameters.DeckArmor is { } deck)
        for (var depth = 0; depth < deck.Thickness; depth++)
        {
            var count = 0;
            for (var z = solid.MinZ; z <= solid.MaxZ; z++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var deckY = solid.DeckYAt(z);
                if (deckY == int.MinValue)
                    continue;
                foreach (var cell in solid.CellsInRow(deckY - depth, z))
                    if (armor.TryAdd(cell, new ArmorCell(deck.Layers[depth], depth, ArmorRegion.Deck)))
                        count++;
            }
            if (count == 0)
                errors.Add($"Deck armor layer {depth + 1} does not fit inside the hull armor.");
        }
        return (armor, errors);

        void Grow(ArmorLayout layout, IEnumerable<Cell> surface, string label)
        {
            var distances = new Dictionary<Cell, int>();
            var frontier = new Queue<Cell>();
            foreach (var seed in surface.OrderBy(c => c.Z).ThenBy(c => c.Y).ThenBy(c => c.X))
            {
                distances[seed] = 0;
                frontier.Enqueue(seed);
            }
            while (frontier.TryDequeue(out var cell))
            {
                if ((frontier.Count & 0xFFF) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                var depth = distances[cell];
                // Nearest exposed surface owns the intersection; sides win ties.
                if (!armor.TryGetValue(cell, out var existing) || depth < existing.Depth)
                {
                    armor[cell] = new ArmorCell(layout.Layers[depth], depth,
                        label == "Bottom" ? ArmorRegion.Bottom : ArmorRegion.Side);
                    regions[cell] = label;
                }
                if (depth + 1 == layout.Thickness)
                    continue;
                foreach (var neighbour in Neighbours(cell))
                {
                    if (!solid.Contains(neighbour.X, neighbour.Y, neighbour.Z) ||
                        solid.IsDeckSurface(neighbour) && !seeds.Contains(neighbour) ||
                        !distances.TryAdd(neighbour, depth + 1))
                        continue;
                    frontier.Enqueue(neighbour);
                }
            }
        }
    }

    private static HashSet<Cell> ExtractShell(
        VoxelSolid solid,
        bool hasDeck,
        CancellationToken cancellationToken)
    {
        var shell = solid.ExteriorCells(cancellationToken);

        if (!hasDeck)
        {
            // Open the deck in one pass. ConnectShell repairs any rare component split
            // below the opening afterward; this stays linear for very large decks.
            var removable = shell
                .Where(cell => IsDeckInterior(solid, cell))
                .ToArray();

            foreach (var cell in removable)
                shell.Remove(cell);
        }

        return shell;
    }

    private static bool IsDeckInterior(VoxelSolid solid, Cell cell) =>
        solid.IsDeckSurface(cell) &&
        solid.Contains(cell.X, cell.Y - 1, cell.Z) &&
        solid.Contains(cell.X - 1, cell.Y, cell.Z) &&
        solid.Contains(cell.X + 1, cell.Y, cell.Z);

    private static List<string> ValidateTopology(
        VoxelSolid solid,
        HashSet<Cell> shell,
        bool hasDeck,
        CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        if (shell.Count == 0)
        {
            errors.Add("The requested profile did not produce an exterior shell.");
            return errors;
        }

        var cavityCells = solid.TotalCellCount - shell.Count;
        var belowDeckShellCells = shell.Count(cell => !solid.IsDeckSurface(cell));
        var belowDeckCavityCells = solid.CellCountBelowDeck - belowDeckShellCells;
        var topOpeningCells = solid.DeckSurfaceCellCount - shell.Count(solid.IsDeckSurface);

        foreach (var cell in solid.ExteriorCells(cancellationToken))
        {
            if (shell.Contains(cell))
                continue;
            if (hasDeck || !solid.IsDeckSurface(cell))
            {
                errors.Add($"The shell has an unintended opening at ({cell.X}, {cell.Y}, {cell.Z}).");
                break;
            }
        }

        if (cavityCells == 0 || belowDeckCavityCells == 0)
            errors.Add("The requested dimensions and profile cannot retain a usable hollow cavity.");

        if (hasDeck)
        {
            foreach (var cell in solid.DeckSurfaceCells())
            {
                if (!shell.Contains(cell))
                {
                    errors.Add("The requested deck is not fully closed.");
                    break;
                }
            }
        }
        else if (topOpeningCells == 0)
        {
            errors.Add("The deckless hull did not produce a top opening.");
        }

        if (!IsFaceConnected(shell))
            errors.Add("The generated shell is not face-connected.");

        return errors;
    }

    private static bool IsFaceConnected(HashSet<Cell> shell)
    {
        var reached = new HashSet<Cell> { shell.First() };
        var frontier = new Queue<Cell>();
        frontier.Enqueue(shell.First());

        while (frontier.Count > 0)
        {
            var cell = frontier.Dequeue();
            Visit(cell.X - 1, cell.Y, cell.Z);
            Visit(cell.X + 1, cell.Y, cell.Z);
            Visit(cell.X, cell.Y - 1, cell.Z);
            Visit(cell.X, cell.Y + 1, cell.Z);
            Visit(cell.X, cell.Y, cell.Z - 1);
            Visit(cell.X, cell.Y, cell.Z + 1);
        }

        return reached.Count == shell.Count;

        void Visit(int x, int y, int z)
        {
            var neighbour = new Cell(x, y, z);
            if (shell.Contains(neighbour) && reached.Add(neighbour))
                frontier.Enqueue(neighbour);
        }
    }

    /// <summary>
    /// Connects the regional sampler in three overlapping longitudinal slabs.
    /// This is a fallback for profiles whose completed armor is still disconnected;
    /// ordinarily the armor layers themselves join edge-touching surface samples,
    /// avoiding unnecessary shortest paths through the cavity.
    /// </summary>
    private static void ConnectRegionalShell(
        VoxelSolid solid,
        HashSet<Cell> shell,
        bool hasDeck,
        CancellationToken cancellationToken)
    {
        var rawShell = shell.ToHashSet();
        ConnectSlab(solid.MinZ, solid.SternEndZ);
        ConnectSlab(solid.SternEndZ, solid.BowStartZ);
        ConnectSlab(solid.BowStartZ, solid.MaxZ);

        if (!hasDeck)
        {
            // Plan-view steps can leave a few top-surface cells isolated after
            // the deck interior is opened. Joining those fragments would require
            // rebuilding the removed deck, so omit deck-only components instead.
            foreach (var component in GetConnectedComponents(shell, cancellationToken))
            {
                if (component.All(solid.IsDeckSurface))
                    shell.ExceptWith(component);
            }
        }

        if (!IsFaceConnected(shell))
        {
            throw new HullGenerationException([
                hasDeck
                    ? "The regional shell could not be connected inside its bow, body, and stern boundaries."
                    : "The deckless regional shell could not be connected without closing its top opening.",
            ]);
        }

        void ConnectSlab(int minZ, int maxZ)
        {
            var slabShell = rawShell.Where(cell => cell.Z >= minZ && cell.Z <= maxZ).ToHashSet();
            var components = GetConnectedComponents(slabShell, cancellationToken)
                .OrderByDescending(component => component.Count)
                .ThenBy(component => component.Min(cell => cell.Z))
                .ThenBy(component => component.Min(cell => cell.Y))
                .ThenBy(component => component.Min(cell => cell.X))
                .ToArray();
            if (components.Length <= 1)
            {
                shell.UnionWith(slabShell);
                return;
            }

            // Grow from every surface component at once. A root-only search made
            // long spokes across the cavity to nearby disconnected surface steps,
            // producing a transverse wall at the region boundary.
            var parent = Enumerable.Range(0, components.Length).ToArray();
            var owner = new Dictionary<Cell, int>();
            var previous = new Dictionary<Cell, Cell>();
            var frontier = new Queue<Cell>();
            for (var index = 0; index < components.Length; index++)
            foreach (var cell in components[index].OrderBy(c => c.Z).ThenBy(c => c.Y).ThenBy(c => c.X))
            {
                owner[cell] = index;
                previous[cell] = cell;
                frontier.Enqueue(cell);
            }

            var remaining = components.Length;
            var inspected = 0;
            while (frontier.Count > 0 && remaining > 1)
            {
                if ((++inspected & 0xFFF) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                var current = frontier.Dequeue();
                foreach (var neighbour in Neighbours(current))
                {
                    if (neighbour.Z < minZ || neighbour.Z > maxZ ||
                        !solid.Contains(neighbour.X, neighbour.Y, neighbour.Z) ||
                        !hasDeck && IsDeckInterior(solid, neighbour))
                        continue;

                    if (!owner.TryGetValue(neighbour, out var other))
                    {
                        owner[neighbour] = owner[current];
                        previous[neighbour] = current;
                        frontier.Enqueue(neighbour);
                        continue;
                    }

                    var firstRoot = Root(owner[current]);
                    var secondRoot = Root(other);
                    if (firstRoot == secondRoot)
                        continue;
                    parent[secondRoot] = firstRoot;
                    remaining--;
                    AddBridge(current);
                    AddBridge(neighbour);
                }
            }

            int Root(int index)
            {
                while (parent[index] != index)
                {
                    parent[index] = parent[parent[index]];
                    index = parent[index];
                }
                return index;
            }

            void AddBridge(Cell cell)
            {
                while (!slabShell.Contains(cell))
                {
                    slabShell.Add(cell);
                    slabShell.Add(new Cell(solid.MirrorX(cell.X), cell.Y, cell.Z));
                    cell = previous[cell];
                }
            }
            shell.UnionWith(slabShell);
        }
    }

    /// <summary>
    /// A discrete sample of an aggressively concave analytic surface can touch
    /// only at edges. Retain the shortest occupied-cell bridges in those cases so
    /// the exported armor always forms one face-connected construct. The bridges
    /// are inside the requested solid and leave the normal cavity intact.
    /// </summary>
    private static void ConnectShell(
        VoxelSolid solid,
        HashSet<Cell> shell,
        CancellationToken cancellationToken)
    {
        var components = GetConnectedComponents(shell, cancellationToken)
            .OrderByDescending(component => component.Count)
            .ThenBy(component => component.Min(cell => cell.Z))
            .ThenBy(component => component.Min(cell => cell.Y))
            .ThenBy(component => component.Min(cell => cell.X))
            .ToArray();
        if (components.Length <= 1)
            return;
        if (components.Length <= 256)
        {
            // Preserve the fixture-established nearest-component bridge order for
            // ordinary hulls. Large profiles use the single-pass spanning search
            // below so hundreds of discrete surface components remain interactive.
            ConnectShellLegacy(solid, shell, cancellationToken);
            return;
        }

        var connected = components[0];
        var remaining = Enumerable.Range(1, components.Length - 1).ToHashSet();
        var componentByCell = new Dictionary<Cell, int>(shell.Count - connected.Count);
        foreach (var componentIndex in remaining)
        foreach (var cell in components[componentIndex])
            componentByCell[cell] = componentIndex;

        var previous = new Dictionary<Cell, Cell>(connected.Count);
        var frontier = new Queue<Cell>();
        foreach (var cell in connected.OrderBy(cell => cell.Z).ThenBy(cell => cell.Y).ThenBy(cell => cell.X))
        {
            previous[cell] = cell;
            frontier.Enqueue(cell);
        }

        while (frontier.Count > 0 && remaining.Count > 0)
        {
            if ((frontier.Count & 0xFFF) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            var current = frontier.Dequeue();
            foreach (var neighbour in Neighbours(current))
            {
                if (!solid.Contains(neighbour.X, neighbour.Y, neighbour.Z))
                    continue;

                if (componentByCell.TryGetValue(neighbour, out var componentIndex) &&
                    remaining.Remove(componentIndex))
                {
                    previous.TryAdd(neighbour, current);
                    var bridge = neighbour;
                    while (!connected.Contains(bridge))
                    {
                        shell.Add(bridge);
                        connected.Add(bridge);
                        bridge = previous[bridge];
                    }

                    foreach (var componentCell in components[componentIndex]
                                 .OrderBy(cell => cell.Z)
                                 .ThenBy(cell => cell.Y)
                                 .ThenBy(cell => cell.X))
                    {
                        componentByCell.Remove(componentCell);
                        connected.Add(componentCell);
                        if (previous.TryAdd(componentCell, componentCell))
                            frontier.Enqueue(componentCell);
                    }
                    continue;
                }

                if (previous.TryAdd(neighbour, current))
                    frontier.Enqueue(neighbour);
            }
        }

        // The spanning search may reach one of a mirrored component pair first.
        // Reflect every selected bridge cell so material assignment and later beam
        // splitting start from an explicitly symmetric occupied-cell set.
        foreach (var cell in shell.ToArray())
            shell.Add(new Cell(solid.MirrorX(cell.X), cell.Y, cell.Z));
    }

    private static void ConnectShellLegacy(
        VoxelSolid solid,
        HashSet<Cell> shell,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var connected = GetConnectedComponent(shell);
            if (connected.Count == shell.Count)
                return;

            var previous = new Dictionary<Cell, Cell>(connected.Count);
            var frontier = new Queue<Cell>();
            foreach (var cell in connected)
            {
                previous[cell] = cell;
                frontier.Enqueue(cell);
            }

            Cell? target = null;
            while (frontier.Count > 0 && target is null)
            {
                if ((frontier.Count & 0xFFF) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                var current = frontier.Dequeue();
                foreach (var neighbour in Neighbours(current))
                {
                    if (!solid.Contains(neighbour.X, neighbour.Y, neighbour.Z) ||
                        !previous.TryAdd(neighbour, current))
                    {
                        continue;
                    }

                    if (shell.Contains(neighbour) && !connected.Contains(neighbour))
                    {
                        target = neighbour;
                        break;
                    }
                    frontier.Enqueue(neighbour);
                }
            }

            if (target is null)
                return;

            // A bridge is one shortest path, so it may run up one side only. Reflect it:
            // an overhang's underside spans both sides, so one bridge would otherwise
            // leave the hull asymmetric. On a hull whose islands sit port and starboard
            // separately this only pre-empts the mirrored bridge the next pass would find.
            var bridge = target.Value;
            while (!connected.Contains(bridge))
            {
                shell.Add(bridge);
                shell.Add(new Cell(solid.MirrorX(bridge.X), bridge.Y, bridge.Z));
                bridge = previous[bridge];
            }
        }
    }

    private static HashSet<Cell> GetConnectedComponent(HashSet<Cell> shell)
    {
        var first = shell.First();
        var reached = new HashSet<Cell> { first };
        var frontier = new Queue<Cell>();
        frontier.Enqueue(first);
        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();
            foreach (var neighbour in Neighbours(current))
            {
                if (shell.Contains(neighbour) && reached.Add(neighbour))
                    frontier.Enqueue(neighbour);
            }
        }
        return reached;
    }

    private static IReadOnlyList<HashSet<Cell>> GetConnectedComponents(
        HashSet<Cell> shell,
        CancellationToken cancellationToken)
    {
        var unseen = shell.ToHashSet();
        var components = new List<HashSet<Cell>>();
        while (unseen.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var first = unseen.First();
            var component = new HashSet<Cell> { first };
            var frontier = new Queue<Cell>();
            frontier.Enqueue(first);
            unseen.Remove(first);
            while (frontier.Count > 0)
            {
                var current = frontier.Dequeue();
                foreach (var neighbour in Neighbours(current))
                {
                    if (unseen.Remove(neighbour))
                    {
                        component.Add(neighbour);
                        frontier.Enqueue(neighbour);
                    }
                }
            }
            components.Add(component);
        }
        return components;
    }

    private static IEnumerable<Cell> Neighbours(Cell cell)
    {
        yield return new Cell(cell.X - 1, cell.Y, cell.Z);
        yield return new Cell(cell.X + 1, cell.Y, cell.Z);
        yield return new Cell(cell.X, cell.Y - 1, cell.Z);
        yield return new Cell(cell.X, cell.Y + 1, cell.Z);
        yield return new Cell(cell.X, cell.Y, cell.Z - 1);
        yield return new Cell(cell.X, cell.Y, cell.Z + 1);
    }

    private static IReadOnlyList<BlockPlacement> CreatePlacements(
        VoxelSolid solid,
        HashSet<Cell> outerShell,
        IReadOnlyDictionary<Cell, ArmorCell> armor,
        CancellationToken cancellationToken,
        bool legacyCornerOrientation = false,
        bool cubeSurface = false)
    {
        var corners = cubeSurface ? new Dictionary<Cell, CornerPlacement>()
            : ClassifySafeCorners(solid, outerShell, cancellationToken, legacyCornerOrientation);
        var slopes = cubeSurface ? new Dictionary<Cell, int>()
            : ClassifySafeSlopes(solid, outerShell, corners.Keys, cancellationToken);
        var placements = new List<BlockPlacement>(armor.Count);
        var inspected = 0;
        foreach (var cell in armor.Keys
                     .OrderBy(cell => cell.Z)
                     .ThenBy(cell => cell.Y)
                     .ThenBy(cell => cell.X))
        {
            if ((++inspected & 0xFFF) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            var (layer, depth, region) = armor[cell];
            if (layer.IsAir)
                continue;
            var material = layer.Material!.Value;

            if (layer.Construction != ArmorConstruction.Solid)
            {
                placements.Add(new BlockPlacement(BlockShape.Cube, material, cell.X, cell.Y, cell.Z, Rotation: 0)
                {
                    UsePoles = layer.UsePoles,
                    Construction = layer.Construction,
                    ArmorRegion = region,
                    ArmorDepth = depth,
                });
                continue;
            }

            if (outerShell.Contains(cell) && corners.TryGetValue(cell, out var corner))
            {
                placements.Add(new BlockPlacement(corner.Shape, material, cell.X, cell.Y, cell.Z, corner.Rotation)
                    { ArmorDepth = depth, Construction = layer.Construction, ArmorRegion = region });
            }
            else if (outerShell.Contains(cell) && slopes.TryGetValue(cell, out var rotation))
            {
                placements.Add(new BlockPlacement(BlockShape.Slope1, material, cell.X, cell.Y, cell.Z, rotation)
                    { ArmorDepth = depth, Construction = layer.Construction, ArmorRegion = region });
            }
            else
            {
                placements.Add(new BlockPlacement(BlockShape.Cube, material, cell.X, cell.Y, cell.Z, Rotation: 0)
                    { ArmorDepth = depth, Construction = layer.Construction, ArmorRegion = region });
            }
        }

        return placements.AsReadOnly();
    }

    /// <summary>
    /// Fits a triangle corner only at a true three-axis convex lattice corner.
    /// The game supplies left- and right-handed meshes, so both the shape and its
    /// rotation are resolved from the three interior attachment faces, then checked
    /// against the mirrored placement before either side is accepted.
    /// </summary>
    private static Dictionary<Cell, CornerPlacement> ClassifySafeCorners(
        VoxelSolid solid,
        HashSet<Cell> shell,
        CancellationToken cancellationToken,
        bool legacyCornerOrientation = false)
    {
        var corners = new Dictionary<Cell, CornerPlacement>();
        var inspected = 0;
        foreach (var cell in shell)
        {
            if ((++inspected & 0xFFF) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            var mirror = new Cell(solid.MirrorX(cell.X), cell.Y, cell.Z);
            if (cell.X >= mirror.X || !shell.Contains(cell) || !shell.Contains(mirror) ||
                !TryGetSafeCorner(solid, shell, cell, out var placement, legacyCornerOrientation) ||
                !TryGetSafeCorner(solid, shell, mirror, out var mirroredPlacement, legacyCornerOrientation))
            {
                continue;
            }

            if (HullGeometryValidator.MirrorShape(placement.Shape) != mirroredPlacement.Shape ||
                HullGeometryValidator.MirrorRotation(placement.Rotation) != mirroredPlacement.Rotation)
            {
                continue;
            }

            corners[cell] = placement;
            corners[mirror] = mirroredPlacement;
        }
        return corners;
    }

    private static bool TryGetSafeCorner(
        VoxelSolid solid,
        HashSet<Cell> shell,
        Cell cell,
        out CornerPlacement placement,
        bool legacyCornerOrientation = false)
    {
        placement = default;
        var innerFaces = new HashSet<AxisDirection>();
        foreach (var direction in BlockRotations.AxisDirections)
        {
            var neighbour = new Cell(cell.X + direction.X, cell.Y + direction.Y, cell.Z + direction.Z);
            if (solid.Contains(neighbour.X, neighbour.Y, neighbour.Z) && shell.Contains(neighbour))
                innerFaces.Add(direction);
        }

        if (innerFaces.Count != 3 || !HasOneFacePerAxis(innerFaces))
            return false;

        for (var rotation = 0; rotation < 24; rotation++)
        {
            var axes = BlockRotations.GetRotationAxes(rotation);
            // OBJ import reflects X: the imported left corner's solid vertex
            // meets -X/-Y/-Z. Its axis faces are triangular, not full squares.
            // Preserve the historical classifier only for frozen regression inputs.
            var leftSide = legacyCornerOrientation ? axes.Right : axes.Right.Negate();
            if (innerFaces.SetEquals([leftSide, axes.Up.Negate(), axes.Forward.Negate()]))
            {
                placement = new CornerPlacement(BlockShape.CornerLeft, rotation);
                return true;
            }
            if (innerFaces.SetEquals([leftSide.Negate(), axes.Up.Negate(), axes.Forward.Negate()]))
            {
                placement = new CornerPlacement(BlockShape.CornerRight, rotation);
                return true;
            }
        }

        return false;
    }

    private static bool HasOneFacePerAxis(IEnumerable<AxisDirection> directions)
    {
        var axes = directions.Select(direction =>
            direction.X != 0 ? 0 : direction.Y != 0 ? 1 : 2).ToHashSet();
        return axes.Count == 3;
    }

    private static Dictionary<Cell, int> ClassifySafeSlopes(
        VoxelSolid solid,
        HashSet<Cell> shell,
        ICollection<Cell> cornerCells,
        CancellationToken cancellationToken)
    {
        var slopes = new Dictionary<Cell, int>();
        var inspected = 0;
        foreach (var cell in shell)
        {
            if ((++inspected & 0xFFF) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            var mirror = new Cell(solid.MirrorX(cell.X), cell.Y, cell.Z);
            if (cell.X >= mirror.X || !shell.Contains(cell) || !shell.Contains(mirror) ||
                cornerCells.Contains(cell) || cornerCells.Contains(mirror))
                continue;

            if (!TryGetSafeSlopeRotation(solid, cell, out var rotation))
                continue;

            slopes[cell] = rotation;
            slopes[mirror] = HullGeometryValidator.MirrorRotation(rotation);
        }

        return slopes;
    }

    /// <summary>
    /// Uses a one-metre slope only when a cell belongs to a 45-degree, two-dimensional
    /// staircase. A simultaneous vertical and longitudinal step would require a
    /// handed corner block, so that case deliberately remains a cube until the
    /// catalog contract can name a verified mirrored corner pair.
    /// </summary>
    private static bool TryGetSafeSlopeRotation(VoxelSolid solid, Cell cell, out int rotation)
    {
        rotation = 0;
        if (cell.Y <= solid.FloorYAt(cell.Z) || cell.Y >= solid.DeckYAt(cell.Z))
            return false;

        var current = solid.SpanAt(cell.Y, cell.Z);
        var isPortSide = cell.X == current.MinX;
        var isStarboardSide = cell.X == current.MaxX;
        if (isPortSide == isStarboardSide)
            return false;

        var verticalStep = IsOneCellCrossSectionStep(solid, cell, isPortSide);
        var bowStep = IsOneCellBowStep(solid, cell, isPortSide);
        if (verticalStep == bowStep)
            return false;

        if (verticalStep)
        {
            // A side slope occupies the Y/X plane. Rotation 11 is +Y/-X
            // (port) and rotation 9 is its +Y/+X mirror (starboard).
            if (!solid.Contains(cell.X, cell.Y, cell.Z - 1) ||
                !solid.Contains(cell.X, cell.Y, cell.Z + 1))
            {
                return false;
            }

            rotation = isPortSide ? 11 : 9;
            return true;
        }

        // A bow slope occupies the Z/X plane. Rotation 18 is +Z/-X
        // (port) and rotation 16 is its +Z/+X mirror (starboard).
        if (!solid.Contains(cell.X, cell.Y - 1, cell.Z) ||
            !solid.Contains(cell.X, cell.Y + 1, cell.Z))
        {
            return false;
        }

        rotation = isPortSide ? 18 : 16;
        return true;
    }

    private static bool IsOneCellCrossSectionStep(VoxelSolid solid, Cell cell, bool isPortSide)
    {
        if (cell.Y <= solid.FloorYAt(cell.Z))
            return false;

        var current = solid.SpanAt(cell.Y, cell.Z);
        var below = solid.SpanAt(cell.Y - 1, cell.Z);
        // Under a profile cut there is no row below; that is the overhang's underside,
        // not a one-cell step, so it stays a cube.
        if (below.IsEmpty)
            return false;
        return isPortSide
            ? current.MinX == below.MinX - 1
            : current.MaxX == below.MaxX + 1;
    }

    private static bool IsOneCellBowStep(VoxelSolid solid, Cell cell, bool isPortSide)
    {
        if (cell.Z < solid.BowStartZ || cell.Z >= solid.MaxZ)
            return false;

        var current = solid.SpanAt(cell.Y, cell.Z);
        var next = solid.SpanAt(cell.Y, cell.Z + 1);
        // The row ends here because the floor rises at the next station: a vertical
        // face under a raked stem, not a plan-view step.
        if (next.IsEmpty)
            return false;
        return isPortSide
            ? next.MinX == current.MinX + 1
            : next.MaxX == current.MaxX - 1;
    }

    private readonly record struct Cell(int X, int Y, int Z);

    private readonly record struct CornerPlacement(BlockShape Shape, int Rotation);

    /// <summary>
    /// One row of the solid: the X-interval it fills at a (height, station) pair, or
    /// <see cref="Empty" /> below a profile style's floor. An empty row has no cells,
    /// so its bounds must never be used as coordinates.
    /// </summary>
    private readonly record struct RowSpan(int MinX, int MaxX)
    {
        public static RowSpan Empty => new(1, 0);

        public bool IsEmpty => MaxX < MinX;

        public int CellCount => IsEmpty ? 0 : MaxX - MinX + 1;

        public bool Contains(int x) => x >= MinX && x <= MaxX;
    }

    /// <summary>How a profile style lifts the floor toward its end.</summary>
    private enum FloorCut
    {
        None,

        /// <summary>A straight rake: removed rows grow linearly to the end plane.</summary>
        Linear,

        /// <summary>A cut-away forefoot: lifts fast then flattens toward the end plane.</summary>
        Convex,

        /// <summary>A sloped transom that keeps at least half the hull's height at the end plane.</summary>
        HalfHeight,

        /// <summary>A long hollow curve that stays low before rising sharply at the end.</summary>
        Concave,
    }

    /// <summary>
    /// What an end style means to the sampler. <paramref name="EndFraction" /> is the
    /// half-beam, as a fraction of the maximum, at the extreme station; zero closes to
    /// the backbone point. <paramref name="Rounded" /> swaps the smoothstep plan curve
    /// for a quarter ellipse. <paramref name="WidestStationFraction" /> only matters for
    /// a stern style. <paramref name="CutFraction" /> is the share of the end segment's
    /// stations whose floor is lifted by <paramref name="Cut" />.
    /// </summary>
    private readonly record struct EndProfile(
        double EndFraction,
        bool Rounded,
        double WidestStationFraction,
        double CutFraction,
        FloorCut Cut,
        double DeckCutFraction = 0)
    {
        // The original ends: the transom sits at 65% of the maximum half-beam and the
        // widest station at 40% of the length. These two entries must reproduce the
        // pre-style geometry exactly; the smoothing fixtures were captured on it.
        public static EndProfile For(BowStyle style) => style switch
        {
            BowStyle.Raked => new(0, false, 0, 0.45, FloorCut.Linear),
            BowStyle.Spoon => new(0, true, 0, 0.50, FloorCut.Convex),
            BowStyle.Blunt => new(0.45, false, 0, 0, FloorCut.None),
            BowStyle.Axe => new(0, false, 0, 0, FloorCut.None, 0.55),
            BowStyle.Clipper => new(0, false, 0, 0.65, FloorCut.Concave),
            _ => new(0, false, 0, 0, FloorCut.None),
        };

        public static EndProfile For(SternStyle style) => style switch
        {
            SternStyle.Square => new(0.85, false, 0.40, 0, FloorCut.None),
            SternStyle.Counter => new(0.65, false, 0.40, 0.35, FloorCut.HalfHeight),
            SternStyle.Canoe => new(0, true, 0.50, 0.45, FloorCut.Linear),
            SternStyle.Cruiser => new(0.25, true, 0.45, 0.35, FloorCut.Convex),
            SternStyle.Fantail => new(0.80, true, 0.40, 0, FloorCut.None),
            _ => new(0.65, false, 0.40, 0, FloorCut.None),
        };
    }

    /// <summary>
    /// A bulbous bow: a round-sectioned ellipsoid whose centre sits a signed distance
    /// ahead of the stem's last station and whose underside is lifted off the keel. The
    /// part ahead of the stem is the bulb proper; the part behind it merges into the
    /// forefoot, widening the rows the cross-section curve narrowed to the backbone.
    /// Everything is expressed in lattice units.
    /// </summary>
    private sealed record BulbGeometry(int Stations, double CentreOffset, double CentreY, double SemiX, double SemiY, double SemiZ)
    {
        /// <summary>
        /// Sizes the bulb from its percentages, or returns null when there is no room:
        /// the hull needs a stem taper of at least one station and four metres of height,
        /// and the bulb must keep clear of the deck row and the row beneath it. The
        /// vertical semi-axis is squashed rather than the bulb refused when the rise pushes
        /// it toward the deck.
        /// </summary>
        public static BulbGeometry? Create(HullParameters parameters, int widestStationIndex)
        {
            var bulb = parameters.EffectiveBulb;
            var bowStations = parameters.Length - 1 - widestStationIndex;
            if (bowStations < 2 || parameters.Height < 4)
                return null;

            var radius = Math.Max(0.5, parameters.Width * 0.5 * bulb.WidthPercent / 100.0);
            // Row 0 spans -0.5..0.5, so a bulb seated on the keel has its underside at -0.5;
            // seating it at 0 would put the keel row exactly on the ellipsoid's boundary.
            var rise = (parameters.Height - 1) * bulb.RisePercent / 100.0;
            var centreY = rise + radius - 0.5;
            // Top row must stay at or below Height - 3, i.e. the bulb's crown under Height - 2.5.
            var semiY = Math.Min(radius, parameters.Height - 2.5 - centreY);
            if (semiY < 0.5)
                return null;
            var semiZ = Math.Max(0.5, parameters.Length * bulb.LengthPercent / 200.0);
            // Extended placements use a connecting fairing, so the center can move
            // beyond the ellipsoid's own overlap with the stem.
            var centreOffset = parameters.Length * bulb.ForeAftPercent / 100.0;

            // How far the ellipsoid actually reaches ahead of the stem, in whole stations
            // that hold at least one cell. Deriving this from the geometry rather than the
            // settings is what guarantees a bulb cell on the end plane.
            var reach = 0;
            var probe = new BulbGeometry(0, centreOffset, centreY, radius, semiY, semiZ);
            for (var k = 1; k <= (int)Math.Ceiling(centreOffset + semiZ) + 1; k++)
            {
                var any = false;
                for (var y = Math.Min(0, (int)Math.Ceiling(centreY - semiY)); y < parameters.Height && !any; y++)
                    any = probe.HalfBreadth(k, y, stemIndex: 0) > 0;
                if (any)
                    reach = k;
            }

            var stations = Math.Min(reach, parameters.Length - 3);
            return probe with { Stations = stations };
        }

        /// <summary>Gets the bulb's half-breadth at a row, or zero where the ellipsoid does not reach.</summary>
        public double HalfBreadth(int zIndex, int yIndex, int stemIndex)
        {
            var dz = (zIndex - stemIndex - CentreOffset) / SemiZ;
            var dy = (yIndex - CentreY) / SemiY;
            var remainder = 1.0 - dz * dz - dy * dy;
            return remainder <= 0 ? 0 : SemiX * Math.Sqrt(remainder);
        }
    }

    private readonly record struct RegionLayout(int SternIntervals, int BowIntervals, int BowStartIndex)
    {
        public static RegionLayout Create(int stationCount, int sternPercent, int bowPercent)
        {
            var intervals = stationCount - 1;
            var stern = Math.Clamp(
                (int)Math.Round(intervals * Math.Clamp(sternPercent, 1, 99) / 100d, MidpointRounding.AwayFromZero),
                1,
                intervals - 1);
            var bow = Math.Clamp(
                (int)Math.Round(intervals * Math.Clamp(bowPercent, 1, 99) / 100d, MidpointRounding.AwayFromZero),
                1,
                intervals - 1);

            if (stern + bow > intervals)
            {
                var scaledStern = stern * intervals / (double)(stern + bow);
                stern = Math.Clamp(
                    (int)Math.Round(scaledStern, MidpointRounding.AwayFromZero),
                    1,
                    intervals - 1);
                bow = intervals - stern;
            }

            return new RegionLayout(stern, bow, intervals - bow);
        }
    }

    private sealed class VoxelSolid
    {
        private readonly RowSpan[,] _spans;
        private readonly int[] _floorByStation;
        private readonly int[] _deckByStation;
        private readonly bool[,]? _protectedRows;

        private VoxelSolid(
            RowSpan[,] spans,
            int[] floorByStation,
            int[] deckByStation,
            int minX,
            int maxX,
            int minY,
            int maxY,
            int minZ,
            int maxZ,
            int sternEndZ,
            int bowStartZ,
            bool[,]? protectedRows = null)
        {
            _spans = spans;
            _floorByStation = floorByStation;
            _deckByStation = deckByStation;
            _protectedRows = protectedRows;
            MinX = minX;
            MaxX = maxX;
            MinY = minY;
            MaxY = maxY;
            MinZ = minZ;
            MaxZ = maxZ;
            SternEndZ = sternEndZ;
            BowStartZ = bowStartZ;
        }

        public int MinX { get; }

        public int MaxX { get; }

        public int MinY { get; }

        public int MaxY { get; }

        public int MinZ { get; }

        public int MaxZ { get; }

        public int SternEndZ { get; }

        public int BowStartZ { get; }

        public Smoothing.HullSurfaceGrid ToSurfaceGrid() => new(
            MinX, MaxX, MinY, MaxY, MinZ, MaxZ,
            (y, z) => new Smoothing.HullSurfaceSpan(SpanAt(y, z).MinX, SpanAt(y, z).MaxX),
            FloorYAt, DeckYAt,
            (y, z) => _protectedRows?[z - MinZ, y - MinY] == true);

        public VoxelSolid WithSurfaceGrid(Smoothing.HullSurfaceGrid grid)
        {
            var spans = new RowSpan[MaxZ - MinZ + 1, MaxY - MinY + 1];
            for (var z = MinZ; z <= MaxZ; z++)
            for (var y = MinY; y <= MaxY; y++)
            {
                var span = grid.SpanAt(y, z);
                spans[z - MinZ, y - MinY] = new RowSpan(span.MinX, span.MaxX);
            }
            return new VoxelSolid(spans, _floorByStation, _deckByStation,
                MinX, MaxX, MinY, MaxY, MinZ, MaxZ, SternEndZ, BowStartZ, _protectedRows);
        }

        /// <summary>
        /// Expands brief one-to-three-station breadth reversals between occupied
        /// cells on the same longitudinal line. These are voxel-rounding notches,
        /// not requested hull character: they interrupt otherwise continuous beam
        /// and smoothing runs and can leave isolated dents in the exported skin.
        /// Empty profile rows remain empty, preserving keel and deck elevations.
        /// </summary>
        public VoxelSolid FillShortLongitudinalNotches(int maximumLength)
        {
            var spans = (RowSpan[,])_spans.Clone();
            for (var y = MinY; y <= MaxY; y++)
            for (var x = MinX; x <= MaxX; x++)
            {
                var z = MinZ + 1;
                while (z < MaxZ)
                {
                    if (SpanAt(y, z).IsEmpty || Contains(x, y, z))
                    {
                        z++;
                        continue;
                    }

                    var start = z;
                    while (z < MaxZ && !SpanAt(y, z).IsEmpty && !Contains(x, y, z))
                        z++;
                    if (z - start > maximumLength ||
                        !Contains(x, y, start - 1) || !Contains(x, y, z))
                        continue;

                    for (var fillZ = start; fillZ < z; fillZ++)
                    {
                        var span = spans[fillZ - MinZ, y - MinY];
                        spans[fillZ - MinZ, y - MinY] = new RowSpan(
                            Math.Min(span.MinX, x), Math.Max(span.MaxX, x));
                    }
                }
            }

            return new VoxelSolid(spans, _floorByStation, _deckByStation,
                MinX, MaxX, MinY, MaxY, MinZ, MaxZ, SternEndZ, BowStartZ, _protectedRows);
        }

        public long TotalCellCount
        {
            get
            {
                long count = 0;
                for (var z = MinZ; z <= MaxZ; z++)
                for (var y = MinY; y <= MaxY; y++)
                    count += SpanAt(y, z).CellCount;
                return count;
            }
        }

        public long CellCountBelowDeck => TotalCellCount - DeckSurfaceCellCount;

        public long DeckSurfaceCellCount
        {
            get
            {
                long count = 0;
                for (var z = MinZ; z <= MaxZ; z++)
                {
                    var deckY = DeckYAt(z);
                    if (deckY >= MinY)
                        count += SpanAt(deckY, z).CellCount;
                }
                return count;
            }
        }

        public static VoxelSolid Create(HullParameters parameters, CancellationToken cancellationToken)
        {
            var shape = parameters.EffectiveShape;
            var minX = -(parameters.Width / 2);
            var maxX = minX + parameters.Width - 1;
            var minY = 0;
            var minZ = -(parameters.Length / 2);
            var maxZ = minZ + parameters.Length - 1;
            var bow = EndProfile.For(parameters.BowStyle);
            var stern = EndProfile.For(parameters.SternStyle);

            // Reserve bulb-only stations from the preliminary bow region, then
            // redistribute the two transition lengths across the remaining stem.
            var initialRegions = RegionLayout.Create(
                parameters.Length,
                shape.Stern.RunLengthPercent,
                shape.Bow.EntranceLengthPercent);
            var bulb = parameters.HasBulb
                ? BulbGeometry.Create(parameters, initialRegions.BowStartIndex)
                : null;
            if (bulb is not null)
                minY = Math.Min(0, (int)Math.Ceiling(bulb.CentreY - bulb.SemiY));
            var stemLength = parameters.Length - (bulb?.Stations ?? 0);
            var regions = RegionLayout.Create(
                stemLength,
                shape.Stern.RunLengthPercent,
                shape.Bow.EntranceLengthPercent);

            var profile = shape.Profile;
            var maxY = parameters.Height - 1 + Math.Max(profile.BowDeckRise, profile.SternDeckRise);
            var spans = new RowSpan[parameters.Length, maxY - minY + 1];
            var protectedRows = new bool[parameters.Length, maxY - minY + 1];
            var floors = new int[parameters.Length];
            var decks = Enumerable.Repeat(int.MinValue, parameters.Length).ToArray();
            var styleFloors = ComputeRegionalFloors(
                stemLength,
                parameters.Height,
                regions,
                bow,
                stern);
            var styleDeckCuts = ComputeRegionalDeckCuts(
                stemLength,
                parameters.Height,
                regions,
                bow,
                stern);
            var centreX = (minX + maxX) / 2.0;
            var fairingActive = false;
            var fairingStartStation = 0;
            var fairingEndStation = 0;
            var fairingAttachmentRow = 0;
            var extendedFairing = bulb is not null &&
                (parameters.EffectiveBulb.RisePercent < 0 || bulb.CentreOffset > bulb.SemiZ - 0.5);

            for (var zIndex = 0; zIndex < stemLength; zIndex++)
            {
                var sternEndWeight = zIndex < regions.SternIntervals
                    ? SmoothStep(1 - zIndex / (double)regions.SternIntervals)
                    : 0;
                var bowEndWeight = zIndex > regions.BowStartIndex
                    ? SmoothStep((zIndex - regions.BowStartIndex) / (double)regions.BowIntervals)
                    : 0;
                // A reverse axe stem begins lowering the deck part-way through the
                // entrance. Bring requested bow sheer to its full height at that
                // shoulder so the style cut changes the stem without silently
                // reducing the parameter's declared overall height.
                var bowDeckWeight = bow.DeckCutFraction > 0 && zIndex > regions.BowStartIndex
                    ? SmoothStep(Math.Clamp(
                        (zIndex - regions.BowStartIndex) /
                        (regions.BowIntervals * (1 - bow.DeckCutFraction)), 0, 1))
                    : bowEndWeight;

                var requestedFloor = (int)Math.Round(
                    Math.Max(
                        profile.SternKeelRise * sternEndWeight,
                        profile.BowKeelRise * bowEndWeight),
                    MidpointRounding.AwayFromZero);
                var floor = Math.Max(styleFloors[zIndex], requestedFloor);
                var deckRise = (int)Math.Round(
                    Math.Max(
                        profile.SternDeckRise * sternEndWeight,
                        profile.BowDeckRise * bowDeckWeight),
                    MidpointRounding.AwayFromZero);
                var deck = parameters.Height - 1 + deckRise - styleDeckCuts[zIndex];
                floors[zIndex] = Math.Min(floor, deck - 1);
                decks[zIndex] = deck;
            }

            if (bulb is not null)
                ConfigureBulbFairing();

            // Fairing anchors, one per row: the breadth the row already carries at the
            // fairing's first station, and the widest the bulb ever gets on that row.
            // Grading between them lets the forefoot meet the bulb's flank in single
            // cell steps the slope fills can bevel. The ellipsoid's own fore/aft factor
            // is zero at the bulb's aft tangent, which left that join as a vertical
            // step of a dozen stations with nothing for the fills to work with.
            var fairingBaseBreadth = new double[maxY - minY + 1];
            var fairingPeakBreadth = new double[maxY - minY + 1];
            if (bulb is not null && fairingActive)
            {
                for (var y = minY; y <= maxY; y++)
                {
                    for (var sample = 0; sample < parameters.Length; sample++)
                    {
                        fairingPeakBreadth[y - minY] = Math.Max(
                            fairingPeakBreadth[y - minY],
                            bulb.HalfBreadth(sample, y, stemLength - 1));
                    }
                }
            }

            for (var zIndex = 0; zIndex < parameters.Length; zIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var isStemStation = zIndex < stemLength;
                var longitudinalFactor = isStemStation
                    ? GetRegionalLongitudinalFactor(zIndex, regions, shape, stern, bow)
                    : 0;

                var bodyWeight = 1d;
                var sideShape = shape.Body.SideShape;
                if (isStemStation && zIndex < regions.SternIntervals)
                {
                    bodyWeight = SmoothStep(zIndex / (double)regions.SternIntervals);
                    sideShape = Lerp(shape.Stern.SideShape, shape.Body.SideShape, bodyWeight);
                }
                else if (isStemStation && zIndex > regions.BowStartIndex)
                {
                    bodyWeight = 1 - SmoothStep((zIndex - regions.BowStartIndex) / (double)regions.BowIntervals);
                    sideShape = Lerp(shape.Bow.Flare, shape.Body.SideShape, bodyWeight);
                }

                // Chine is a body decision, but easing its strength near either end
                // prevents a hard shoulder from terminating as a transverse seam.
                var chine = -0.9 + (shape.Body.Chine + 0.9) * (0.55 + 0.45 * bodyWeight);
                var localFloor = isStemStation ? floors[zIndex] : minY;
                var localDeck = isStemStation ? decks[zIndex] : -1;
                var localHeight = Math.Max(1, localDeck - localFloor);
                var maximumBeamRow = localFloor + (int)Math.Round(
                    RegionalSectionEvaluator.MaximumBeamHeight(sideShape) * localHeight,
                    MidpointRounding.AwayFromZero);
                var chineRow = localFloor + (int)Math.Round(
                    RegionalSectionEvaluator.ChineHeight(shape.Body.Fullness, sideShape) * localHeight,
                    MidpointRounding.AwayFromZero);

                for (var y = minY; y <= maxY; y++)
                {
                    var bulbHalfBreadth = bulb?.HalfBreadth(zIndex, y, stemLength - 1) ?? 0;
                    var fairingHalfBreadth = GetBulbFairingHalfBreadth(zIndex, y);
                    // Preserve analytic shoulders/chines and the sampled bulb
                    // attachment when the construction planner regularizes rows.
                    protectedRows[zIndex, y - minY] = y == localFloor || y == localDeck ||
                        y == maximumBeamRow || (chine > 0 && y == chineRow) ||
                        bulbHalfBreadth > 0 || fairingHalfBreadth > 0;
                    double rowHalfBreadth;
                    if (!isStemStation || y < localFloor || y > localDeck)
                    {
                        var effectiveBulbHalfBreadth = Math.Max(bulbHalfBreadth, fairingHalfBreadth);
                        rowHalfBreadth = effectiveBulbHalfBreadth > 0
                            ? Math.Max(0.5, effectiveBulbHalfBreadth)
                            : 0;
                    }
                    else
                    {
                        var verticalFraction = (y - localFloor) / (double)localHeight;
                        var sectionFactor = RegionalSectionEvaluator.Evaluate(
                            verticalFraction,
                            shape.Body.Fullness,
                            sideShape,
                            chine,
                            shape.Body.FlatBottom);
                        if (longitudinalFactor >= 1 - 1e-9 && y == maximumBeamRow)
                            sectionFactor = 1;
                        var halfBreadth = Math.Max(
                            0.5,
                            parameters.Width * 0.5 * longitudinalFactor * sectionFactor);
                        rowHalfBreadth = Math.Max(halfBreadth, Math.Max(bulbHalfBreadth, fairingHalfBreadth));
                    }

                    if (zIndex == fairingStartStation)
                        fairingBaseBreadth[y - minY] = rowHalfBreadth;

                    spans[zIndex, y - minY] = rowHalfBreadth > 0 ? SpanFor(rowHalfBreadth) : RowSpan.Empty;
                }
            }

            if (bulb is not null)
            {
                // Keep the taper for the visual continuity.
                // The legacy bridge remains as a fallback to avoid regressions at
                // compact hull sizes where the tapered blend cannot be guaranteed.
                if (!extendedFairing)
                    ConnectBulbAftLegacy();
            }

            // A bulb belongs on the front of the hull, so taper its lower body aftward
            // into the forefoot until the full bulb reaches its centre row.
            double GetBulbFairingHalfBreadth(int zIndex, int yIndex)
            {
                if (!fairingActive || zIndex <= fairingStartStation || zIndex > fairingEndStation)
                    return 0;
                if (!extendedFairing && zIndex >= stemLength)
                    return 0;

                var progress = (zIndex - fairingStartStation) / (double)(fairingEndStation - fairingStartStation);
                var radiusProgress = Math.Sqrt(SmoothStep(progress));
                var centreY = Lerp(fairingAttachmentRow, bulb!.CentreY, radiusProgress);
                var semiX = bulb.SemiX * radiusProgress;
                var semiY = bulb.SemiY * radiusProgress;

                // Rows the bulb reaches are graded from the breadth they already carry
                // where the fairing starts to the widest the bulb gets on them, so the
                // join behind the bulb lands in steps a slope fill can bevel.
                var rowIndex = yIndex - minY;
                var gradedBreadth = fairingPeakBreadth[rowIndex] > 0
                    ? Lerp(fairingBaseBreadth[rowIndex], fairingPeakBreadth[rowIndex], SmoothStep(progress))
                    : 0;

                if (extendedFairing)
                {
                    semiY = Math.Max(1, semiY);
                    semiX = Math.Max(0.5, semiX);
                    var vertical = (yIndex - centreY) / semiY;
                    var chord = Math.Abs(vertical) >= 1 ? 0 : semiX * Math.Sqrt(1 - vertical * vertical);
                    return Math.Max(chord, gradedBreadth);
                }
                if (semiX < 0.5 || semiY < 0.5)
                    return gradedBreadth;

                var dz = (zIndex - (stemLength - 1 + bulb.CentreOffset)) / bulb.SemiZ;
                var dy = (yIndex - centreY) / semiY;
                var remainder = 1.0 - dz * dz - dy * dy;
                return remainder <= 0 ? gradedBreadth : Math.Max(gradedBreadth, semiX * Math.Sqrt(remainder));
            }

            void ConfigureBulbFairing()
            {
                var centreZ = stemLength - 1 + bulb!.CentreOffset;
                if (extendedFairing)
                {
                    fairingStartStation = Math.Clamp((int)Math.Floor(Math.Min(stemLength - 3,
                        centreZ - bulb.SemiZ - Math.Max(4, bulb.SemiZ))), 0, stemLength - 1);
                    fairingEndStation = Math.Clamp((int)Math.Round(centreZ), fairingStartStation + 1, parameters.Length - 1);
                    fairingAttachmentRow = floors[fairingStartStation];
                    fairingActive = true;
                    return;
                }
                var requestedStart = (int)Math.Ceiling(
                    centreZ - bulb!.SemiZ - Math.Max(3, Math.Ceiling(0.75 * bulb!.SemiZ)));
                var requestedEnd = (int)Math.Round(centreZ);

                fairingStartStation = Math.Clamp(requestedStart, regions.BowStartIndex, stemLength - 1);
                fairingEndStation = Math.Clamp(requestedEnd, regions.BowStartIndex, stemLength - 1);
                if (fairingEndStation <= fairingStartStation)
                    return;

                while (fairingStartStation > regions.BowStartIndex &&
                       floors[fairingStartStation] > bulb!.CentreY)
                {
                    fairingStartStation--;
                }

                if (floors[fairingStartStation] > bulb!.CentreY)
                    return;

                fairingActive = true;
                fairingAttachmentRow = floors[fairingStartStation];
            }

            void ConnectBulbAftLegacy()
            {
                var attachmentY = Enumerable.Range(minY, maxY - minY + 1)
                    .Where(y => Enumerable.Range(0, parameters.Length)
                        .Any(z => bulb!.HalfBreadth(z, y, stemLength - 1) > 0))
                    .OrderBy(y => Math.Abs(y - bulb!.CentreY))
                    .FirstOrDefault(minY);

                var aftmostBulbStation = -1;
                for (var zIndex = 0; zIndex < parameters.Length; zIndex++)
                {
                    if (bulb!.HalfBreadth(zIndex, attachmentY, stemLength - 1) > 0)
                    {
                        aftmostBulbStation = zIndex;
                        break;
                    }
                }

                if (aftmostBulbStation < 1 ||
                    aftmostBulbStation < stemLength && floors[aftmostBulbStation] <= attachmentY)
                    return;

                for (var zIndex = aftmostBulbStation - 1; zIndex >= 0; zIndex--)
                {
                    if (zIndex < stemLength && floors[zIndex] <= attachmentY)
                        return;

                    if (spans[zIndex, attachmentY - minY].IsEmpty)
                        spans[zIndex, attachmentY - minY] = SpanFor(0.5);
                }
            }

            RowSpan SpanFor(double halfBreadth)
            {
                var rowMinX = Math.Max(minX, (int)Math.Ceiling(centreX - halfBreadth));
                var rowMaxX = Math.Min(maxX, (int)Math.Floor(centreX + halfBreadth));
                if (rowMinX > rowMaxX)
                {
                    rowMinX = (int)Math.Floor(centreX);
                    rowMaxX = (int)Math.Ceiling(centreX);
                }
                return new RowSpan(rowMinX, rowMaxX);
            }

            return new VoxelSolid(
                spans,
                floors,
                decks,
                minX,
                maxX,
                minY,
                maxY,
                minZ,
                maxZ,
                minZ + regions.SternIntervals,
                minZ + regions.BowStartIndex,
                protectedRows);
        }

        public static VoxelSolid CreateLegacy(HullParameters parameters, CancellationToken cancellationToken)
        {
            var minX = -(parameters.Width / 2);
            var maxX = minX + parameters.Width - 1;
            var minY = 0;
            var maxY = parameters.Height - 1;
            var minZ = -(parameters.Length / 2);
            var maxZ = minZ + parameters.Length - 1;
            var bow = EndProfile.For(parameters.BowStyle);
            var stern = EndProfile.For(parameters.SternStyle);
            var widestStationIndex = Math.Clamp(
                (int)Math.Round((parameters.Length - 1) * stern.WidestStationFraction, MidpointRounding.AwayFromZero),
                1,
                parameters.Length - 2);
            var widestStationZ = minZ + widestStationIndex;
            // A bulbous bow shortens the stem: the pointed taper ends at the stem station
            // and only the bulb's rows continue to the end plane.
            var bulb = parameters.HasBulb ? BulbGeometry.Create(parameters, widestStationIndex) : null;
            var stemLength = parameters.Length - (bulb?.Stations ?? 0);
            var floors = ComputeFloors(stemLength, parameters.Height, widestStationIndex, bow, stern);
            var spans = new RowSpan[parameters.Length, parameters.Height];
            var centreX = (minX + maxX) / 2.0;

            for (var zIndex = 0; zIndex < parameters.Length; zIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var isStemStation = zIndex < stemLength;
                var longitudinalFactor = isStemStation
                    ? GetLongitudinalFactor(
                        zIndex,
                        widestStationIndex,
                        stemLength,
                        parameters.SternFullness,
                        parameters.BowFullness,
                        stern,
                        bow)
                    : 0;

                for (var yIndex = 0; yIndex < parameters.Height; yIndex++)
                {
                    var bulbHalfBreadth = bulb?.HalfBreadth(zIndex, yIndex, stemLength - 1) ?? 0;
                    if (!isStemStation || yIndex < floors[zIndex])
                    {
                        // Beyond the stem, or beneath a profile cut, only the bulb can
                        // fill the row.
                        spans[zIndex, yIndex] = bulbHalfBreadth > 0
                            ? SpanFor(Math.Max(0.5, bulbHalfBreadth))
                            : RowSpan.Empty;
                        continue;
                    }

                    var verticalFraction = yIndex / (double)(parameters.Height - 1);
                    var crossSectionFactor = ShapeProgress(verticalFraction, parameters.CrossSectionCurve);
                    var halfBreadth = Math.Max(
                        0.5,
                        parameters.Width * 0.5 * longitudinalFactor * crossSectionFactor);
                    // The bulb's aft half fairs into the forefoot as the wider of the two.
                    spans[zIndex, yIndex] = SpanFor(Math.Max(halfBreadth, bulbHalfBreadth));
                }

                if (bulb is not null && isStemStation)
                    ConnectBulbToFloor(zIndex, floors[zIndex]);
            }

            // The frozen legacy evaluator retains its historical vertical strut so exact
            // corrected smoothing fixtures remain reproducible. Shape V2 uses the aftward
            // attachment rule in Create instead.
            void ConnectBulbToFloor(int zIndex, int floor)
            {
                var crownRow = -1;
                var widest = 0.0;
                for (var yIndex = 0; yIndex < floor && yIndex < parameters.Height; yIndex++)
                {
                    var half = bulb!.HalfBreadth(zIndex, yIndex, stemLength - 1);
                    if (half <= 0)
                        continue;
                    crownRow = yIndex;
                    widest = Math.Max(widest, half);
                }

                if (crownRow < 0 || crownRow + 1 >= floor)
                    return;
                var strut = Math.Max(0.5, Math.Min(bulb!.SemiX * 0.5, widest));
                for (var yIndex = crownRow + 1; yIndex < floor; yIndex++)
                    spans[zIndex, yIndex] = SpanFor(strut);
            }

            RowSpan SpanFor(double halfBreadth)
            {
                var rowMinX = Math.Max(minX, (int)Math.Ceiling(centreX - halfBreadth));
                var rowMaxX = Math.Min(maxX, (int)Math.Floor(centreX + halfBreadth));

                // The 0.5m minimum is deliberately large enough to keep the
                // central one-cell (odd beam) or two-cell (even beam) backbone.
                if (rowMinX > rowMaxX)
                {
                    rowMinX = (int)Math.Floor(centreX);
                    rowMaxX = (int)Math.Ceiling(centreX);
                }

                return new RowSpan(rowMinX, rowMaxX);
            }

            var stationFloors = new int[parameters.Length];
            Array.Copy(floors, stationFloors, floors.Length);
            var stationDecks = Enumerable.Repeat(-1, parameters.Length).ToArray();
            Array.Fill(stationDecks, maxY, 0, stemLength);
            return new VoxelSolid(
                spans,
                stationFloors,
                stationDecks,
                minX,
                maxX,
                minY,
                maxY,
                minZ,
                maxZ,
                widestStationZ,
                widestStationZ);
        }

        public bool Contains(int x, int y, int z)
        {
            if (y < MinY || y > MaxY || z < MinZ || z > MaxZ)
                return false;

            return SpanAt(y, z).Contains(x);
        }

        public bool IsExterior(Cell cell) =>
            !Contains(cell.X - 1, cell.Y, cell.Z) ||
            !Contains(cell.X + 1, cell.Y, cell.Z) ||
            !Contains(cell.X, cell.Y - 1, cell.Z) ||
            !Contains(cell.X, cell.Y + 1, cell.Z) ||
            !Contains(cell.X, cell.Y, cell.Z - 1) ||
            !Contains(cell.X, cell.Y, cell.Z + 1);

        public HashSet<Cell> ExteriorCells(CancellationToken cancellationToken)
        {
            var exterior = new HashSet<Cell>();
            for (var z = MinZ; z <= MaxZ; z++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (var y = MinY; y <= MaxY; y++)
                {
                    var current = SpanAt(y, z);
                    // A row below a profile floor has no cells, and its bounds are
                    // not coordinates.
                    if (current.IsEmpty)
                        continue;
                    exterior.Add(new Cell(current.MinX, y, z));
                    exterior.Add(new Cell(current.MaxX, y, z));
                    AddDifference(current, NeighbourSpan(y - 1, z), y, z, exterior);
                    AddDifference(current, NeighbourSpan(y + 1, z), y, z, exterior);
                    AddDifference(current, NeighbourSpan(y, z - 1), y, z, exterior);
                    AddDifference(current, NeighbourSpan(y, z + 1), y, z, exterior);
                }
            }
            return exterior;
        }

        /// <summary>
        /// Gets a neighbouring row for the exterior test, or null when it is outside the
        /// lattice or cut away. Both leave the whole current row exposed on that side.
        /// </summary>
        private RowSpan? NeighbourSpan(int y, int z)
        {
            if (y < MinY || y > MaxY || z < MinZ || z > MaxZ)
                return null;
            var span = SpanAt(y, z);
            return span.IsEmpty ? null : span;
        }

        private static void AddDifference(
            RowSpan current,
            RowSpan? neighbour,
            int y,
            int z,
            ISet<Cell> exterior)
        {
            if (neighbour is null)
            {
                AddRange(current.MinX, current.MaxX);
                return;
            }

            AddRange(current.MinX, Math.Min(current.MaxX, neighbour.Value.MinX - 1));
            AddRange(Math.Max(current.MinX, neighbour.Value.MaxX + 1), current.MaxX);
            return;

            void AddRange(int from, int to)
            {
                for (var x = from; x <= to; x++)
                {
                    exterior.Add(new Cell(x, y, z));
                    if (x == to)
                        break;
                }
            }
        }

        public int MirrorX(int x) => MinX + MaxX - x;

        public RowSpan SpanAt(int y, int z) => _spans[z - MinZ, y - MinY];

        public int FloorYAt(int z) => _floorByStation[z - MinZ];

        public int DeckYAt(int z) => _deckByStation[z - MinZ];

        public bool IsDeckSurface(Cell cell) =>
            cell.Z >= MinZ && cell.Z <= MaxZ &&
            cell.Y == DeckYAt(cell.Z) &&
            Contains(cell.X, cell.Y, cell.Z);

        public IEnumerable<Cell> DeckSurfaceCells()
        {
            for (var z = MinZ; z <= MaxZ; z++)
            {
                var deckY = DeckYAt(z);
                if (deckY < MinY)
                    continue;
                foreach (var cell in CellsInRow(deckY, z))
                    yield return cell;
            }
        }

        public IEnumerable<Cell> CellsInRow(int y, int z)
        {
            if (y < MinY || y > MaxY || z < MinZ || z > MaxZ)
                yield break;
            var span = SpanAt(y, z);
            for (var x = span.MinX; x <= span.MaxX; x++)
                yield return new Cell(x, y, z);
        }

        public IEnumerable<Cell> Cells()
        {
            for (var z = MinZ; z <= MaxZ; z++)
            {
                for (var y = MinY; y <= MaxY; y++)
                {
                    var span = SpanAt(y, z);
                    for (var x = span.MinX; x <= span.MaxX; x++)
                        yield return new Cell(x, y, z);
                }
            }
        }

        public IEnumerable<Cell> CellsAtY(int y)
        {
            for (var z = MinZ; z <= MaxZ; z++)
            {
                var span = SpanAt(y, z);
                for (var x = span.MinX; x <= span.MaxX; x++)
                    yield return new Cell(x, y, z);
            }
        }

        /// <summary>
        /// Gets the plan-view half-beam at a station as a fraction of the maximum. Aft
        /// of the widest station the curve rises from the stern's end fraction to one;
        /// forward of it the curve falls from one to the bow's end fraction. The rounded
        /// styles hold their beam and turn in sharply at the end, the way a quarter
        /// ellipse does; the others use smoothstep. Both are pinned to the same
        /// endpoints, so the fullness sliders only warp the rate.
        /// </summary>
        private static double GetRegionalLongitudinalFactor(
            int zIndex,
            RegionLayout regions,
            HullShapeSettings shape,
            EndProfile stern,
            EndProfile bow)
        {
            if (zIndex <= regions.SternIntervals)
            {
                var t = zIndex / (double)regions.SternIntervals;
                var progress = ShapeProgress(t, shape.Stern.Fullness);
                var rise = stern.Rounded
                    ? Math.Sqrt(1 - (1 - progress) * (1 - progress))
                    : SmoothStep(progress);
                return stern.EndFraction + (1 - stern.EndFraction) * rise;
            }

            if (zIndex >= regions.BowStartIndex)
            {
                var t = (zIndex - regions.BowStartIndex) / (double)regions.BowIntervals;
                var progress = ShapeProgress(t, -shape.Bow.Fullness);
                var hold = bow.Rounded
                    ? Math.Sqrt(1 - progress * progress)
                    : 1 - SmoothStep(progress);
                return bow.EndFraction + (1 - bow.EndFraction) * hold;
            }

            return 1;
        }

        private static double GetLongitudinalFactor(
            int zIndex,
            int widestStationIndex,
            int length,
            double sternFullness,
            double bowFullness,
            EndProfile stern,
            EndProfile bow)
        {
            if (zIndex <= widestStationIndex)
            {
                var sternT = zIndex / (double)widestStationIndex;
                var sternProgress = ShapeProgress(sternT, sternFullness);
                var rise = stern.Rounded
                    ? Math.Sqrt(1.0 - (1.0 - sternProgress) * (1.0 - sternProgress))
                    : SmoothStep(sternProgress);
                return stern.EndFraction + (1.0 - stern.EndFraction) * rise;
            }

            var bowLength = length - 1 - widestStationIndex;
            var bowT = (zIndex - widestStationIndex) / (double)bowLength;
            var bowProgress = ShapeProgress(bowT, -bowFullness);
            var hold = bow.Rounded
                ? Math.Sqrt(1.0 - bowProgress * bowProgress)
                : 1.0 - SmoothStep(bowProgress);
            return bow.EndFraction + (1.0 - bow.EndFraction) * hold;
        }

        /// <summary>
        /// Gets, per station, how many rows are removed from the keel upward by the
        /// profile styles. The widest station is never cut, the deck row and one row
        /// beneath it always survive, and the count never decreases toward an end, which
        /// is what the deck-plane invariants, the extents check, and the surface-area
        /// memory bound all rely on. A cut is capped at four stations per removed row
        /// so no overhang step is longer than the slope the vertical fill can bevel.
        /// </summary>
        private static int[] ComputeFloors(
            int length,
            int height,
            int widestStationIndex,
            EndProfile bow,
            EndProfile stern)
        {
            var floors = new int[length];
            var removable = height - 2;
            if (removable <= 0)
                return floors;

            // Stern: cut stations run from zIndex n - 1 down to the end plane at 0.
            var sternStations = widestStationIndex;
            var sternCut = CutStations(stern, sternStations, removable);
            for (var k = 1; k <= sternCut; k++)
                floors[sternCut - k] = RemovedRows(stern.Cut, k, sternCut, removable, height);

            // Bow: cut stations run from zIndex length - n up to the end plane.
            var bowStations = length - 1 - widestStationIndex;
            var bowCut = CutStations(bow, bowStations, removable);
            for (var k = 1; k <= bowCut; k++)
                floors[length - 1 - bowCut + k] = RemovedRows(bow.Cut, k, bowCut, removable, height);

            return floors;

            static int CutStations(EndProfile profile, int stations, int removable)
            {
                if (profile.Cut == FloorCut.None || stations < 1)
                    return 0;
                var count = Math.Clamp((int)Math.Round(profile.CutFraction * stations, MidpointRounding.AwayFromZero), 1, stations);
                return Math.Min(count, 4 * removable);
            }

            static int RemovedRows(FloorCut cut, int k, int n, int removable, int height) => cut switch
            {
                FloorCut.Linear => removable * k / n,
                FloorCut.Convex => (int)Math.Floor(removable * (1.0 - (1.0 - k / (double)n) * (1.0 - k / (double)n))),
                FloorCut.HalfHeight => Math.Min(removable, height / 2) * k / n,
                FloorCut.Concave => (int)Math.Floor(removable * Math.Pow(k / (double)n, 2)),
                _ => 0,
            };
        }

        /// <summary>
        /// Applies the existing style-specific forefoot and run cuts inside their
        /// Shape V2 regions. Unlike the legacy helper, the two region lengths are
        /// independent and may leave a full-height body plateau between them.
        /// </summary>
        private static int[] ComputeRegionalFloors(
            int length,
            int height,
            RegionLayout regions,
            EndProfile bow,
            EndProfile stern)
        {
            var floors = new int[length];
            var removable = height - 2;
            if (removable <= 0)
                return floors;

            var sternCut = CutStations(stern, regions.SternIntervals, removable);
            for (var k = 1; k <= sternCut; k++)
                floors[sternCut - k] = RemovedRows(stern.Cut, k, sternCut, removable, height);

            var bowCut = CutStations(bow, regions.BowIntervals, removable);
            for (var k = 1; k <= bowCut; k++)
                floors[length - 1 - bowCut + k] = RemovedRows(bow.Cut, k, bowCut, removable, height);

            return floors;

            static int CutStations(EndProfile profile, int stations, int removable)
            {
                if (profile.Cut == FloorCut.None || stations < 1)
                    return 0;
                var count = Math.Clamp(
                    (int)Math.Round(profile.CutFraction * stations, MidpointRounding.AwayFromZero),
                    1,
                    stations);
                return Math.Min(count, 4 * removable);
            }

            static int RemovedRows(FloorCut cut, int k, int n, int removable, int height) => cut switch
            {
                FloorCut.Linear => removable * k / n,
                FloorCut.Convex => (int)Math.Floor(
                    removable * (1 - (1 - k / (double)n) * (1 - k / (double)n))),
                FloorCut.HalfHeight => Math.Min(removable, height / 2) * k / n,
                FloorCut.Concave => (int)Math.Floor(removable * Math.Pow(k / (double)n, 2)),
                _ => 0,
            };
        }

        /// <summary>
        /// Lowers the local deck toward an axe bow so its lower prow projects beyond
        /// the upper stem. Two rows always remain, matching the forefoot-cut invariant.
        /// </summary>
        private static int[] ComputeRegionalDeckCuts(
            int length,
            int height,
            RegionLayout regions,
            EndProfile bow,
            EndProfile stern)
        {
            var cuts = new int[length];
            var removable = height - 2;
            if (removable <= 0)
                return cuts;

            Apply(stern, regions.SternIntervals, isBow: false);
            Apply(bow, regions.BowIntervals, isBow: true);
            return cuts;

            void Apply(EndProfile profile, int intervals, bool isBow)
            {
                if (profile.DeckCutFraction <= 0 || intervals < 1)
                    return;
                var count = Math.Clamp(
                    (int)Math.Round(profile.DeckCutFraction * intervals, MidpointRounding.AwayFromZero),
                    1,
                    intervals);
                count = Math.Min(count, 4 * removable);
                for (var k = 1; k <= count; k++)
                {
                    var index = isBow ? length - 1 - count + k : count - k;
                    cuts[index] = removable * k / count;
                }
            }
        }

        private static double ShapeProgress(double t, double control) =>
            Math.Clamp(t + control * t * (1.0 - t), 0.0, 1.0);

        private static double Lerp(double from, double to, double amount) =>
            from + (to - from) * amount;

        private static double SmoothStep(double t) => t * t * (3.0 - 2.0 * t);
    }
}
