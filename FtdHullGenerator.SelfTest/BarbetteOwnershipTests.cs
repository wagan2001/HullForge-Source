using System.Text.Json;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Components;
using FtdHullGenerator.Domain.Composition;
using FtdHullGenerator.Domain.Design;
using FtdHullGenerator.Domain.Layout;
using FtdHullGenerator.Domain.Projects;
using FtdHullGenerator.Geometry;
using FtdHullGenerator.Geometry.Components;
using FtdHullGenerator.Geometry.Composition;
using FtdHullGenerator.Infrastructure;
using FtdHullGenerator.Serialization;
using FtdHullGenerator.UI;

/// <summary>
/// Independent BAR02 oracles for frozen barbette ownership, hull/deck integration and diagnostics.
/// Cases are adversarial by construction: thin hulls, raised decks, sloped keels, floating
/// placement, outermost-first truncation, clear-depth shortfall, clear-volume separation,
/// midpoint/forward armor ownership, invalid roof elevation, deck cuts and no-repositioning.
/// </summary>
internal static class BarbetteOwnershipTests
{
    private static readonly Lazy<HullBuildContext> DefaultContext =
        new(() => HullGenerator.CreateContext(HullParameters.Default));

    private static readonly Lazy<FtdBlockCatalog> Installed = new(LoadInstalledCatalog);

    /// <summary>Moore eight-neighbourhood, declared independently of the production rasterizer.</summary>
    private static readonly (int X, int Z)[] MooreOffsets =
    [
        (-1, -1), (0, -1), (1, -1),
        (-1, 0), (1, 0),
        (-1, 1), (0, 1), (1, 1),
    ];

    public static void Run()
    {
        VerifyMandatoryInnermostSideBoundary();
        VerifyShallowerCavityBeforeMandatoryBoundaryRejection();
        VerifyPartialSideLayerTruncation();
        VerifyMandatorySideBoundaryRejectsAtomically();
        VerifyArmorAirSupersedesInteriorArmor();
        VerifyMandatoryBoundarySnapshotInvariants();
        VerifyFlatZeroOffsetPlacement();
        VerifyFloatingNeckContinuity();
        VerifyRaisedDeckTrunkAndNeckTop();
        VerifyProtectedSkinPreserved();
        VerifyInteriorArmorSuperseded();
        VerifyOutermostFirstTruncation();
        VerifyClearDepthShortfall();
        VerifyComfortableMandatoryBottomFloor();
        VerifyFloatingMandatoryBottomFloor();
        VerifyBottomTruncatesToMandatoryLayer();
        VerifyImpossibleFloorRejectsAtomically();
        VerifyNoValidResultDropsAllBottomLayers();
        VerifyCorrectedRoofAndFloorSurviveComposition();
        VerifySlopedBottomFlatFloor();
        VerifyClearVolumeSeparation();
        VerifyMidpointAndForwardTieOwnership();
        VerifyInvalidRoofElevation();
        VerifyNeckObstruction();
        VerifyDeckCutsAndSurroundingPacking();
        VerifyNoRepositioning();
        VerifyCompositionPreviewExportConsistency();

        Console.WriteLine(
            "Barbette ownership: flat/floating/raised/sloped integration, protected skin, interior-armor " +
            "supersession, outermost-first truncation, clear-depth shortfall without upward fill, one-metre " +
            "clear separation, midpoint/forward armor ownership, invalid roof elevation, neck obstruction, " +
            "deck cuts, no-repositioning and preview/export identity passed.");
    }

    /// <summary>
    /// A mandatory-boundary failure at the deeper cavity rows tries a shallower depth before the
    /// whole barbette is rejected, per 04-barbettes §2 ("generate the deepest valid clear cavity and
    /// warn"). This forward station narrows the hull so ring 0 is not protectable at depth two; the
    /// deepest valid cavity (one metre) is realized and reported as a yellow shortfall, not BAR216.
    /// Before the depth-loop correction the same request would have selected depth two and rejected.
    /// </summary>
    private static void VerifyShallowerCavityBeforeMandatoryBoundaryRejection()
    {
        var parameters = HullParameters.Default with
        {
            Length = 60,
            Width = 9,
            Height = 12,
            Smoothing = SmoothingMethod.None,
            Superstructure = SuperstructureSettings.Default with { Enabled = false },
        };
        var context = HullGenerator.CreateContext(parameters);
        var definition = Definition("shorten", clear: 3, depth: 4, side: 1, bottom: 1, neck: 1,
            neckSize: 1);
        var generation = BarbetteGenerator.Generate(definition, context.CenterPlaneX,
            DesignMeasure.FromCellAnchor(14), context);
        Require(generation.IsValid, Details(generation.Diagnostics));
        AssertMandatoryInnermostBoundary(generation, context, "shorten w=9 clear=3 z=14");
        AssertMandatoryRingRoofed(generation, context, "shorten w=9 clear=3 z=14");
        Require(generation.Layout!.RealizedClearDepthMetres < definition.ClearDepthMetres &&
                generation.Layout.RealizedClearDepthMetres >= 1,
            "A mandatory-boundary failure at the deeper rows must shorten the cavity before rejecting; " +
            $"got realized depth {generation.Layout.RealizedClearDepthMetres}.");
        Require(Has(generation.Diagnostics, BarbetteDiagnosticCodes.ClearDepthShortfall),
            "Shortening the cavity to keep the mandatory side boundary must report the yellow shortfall.");
        Require(!Has(generation.Diagnostics, BarbetteDiagnosticCodes.MandatorySideBoundaryImpossible) &&
                !Has(generation.Diagnostics, BarbetteDiagnosticCodes.BottomArmorFloorImpossible),
            "A realizable shallower cavity must not report a blocking boundary or floor failure.");
        Require(generation.Measurement!.RealizedClearWidth.Metres == 3,
            "Shortening the cavity must never shrink the protected clear diameter.");
        Require(generation.Measurement.CenterZ == DesignMeasure.FromCellAnchor(14),
            "Shortening the cavity must never move the requested longitudinal placement.");
    }

    /// <summary>
    /// HF-02 reproduction: the mandatory innermost side boundary is derived independently from the
    /// measured clear mask (Moore neighbourhood minus the clear cells) at every realized cavity
    /// height, then every position must be innermost barbette side armor or a retained protected
    /// hull-skin cell. Before the correction a 5 m bore on a 9 m hull realized zero side layers and
    /// left 70 of the 72 mandatory positions unprotected (the other two were already protected skin).
    /// </summary>
    private static void VerifyMandatoryInnermostSideBoundary()
    {
        var parameters = HullParameters.Default with
        {
            Length = 60,
            Width = 9,
            Height = 12,
            Smoothing = SmoothingMethod.None,
            Superstructure = SuperstructureSettings.Default with { Enabled = false },
        };
        var context = HullGenerator.CreateContext(parameters);
        var definition = BarbetteDefinition.Create("b", "n", DesignMeasure.FromMetres(5), 3, 0, 3,
            sideArmor: ArmorLayout.Single(MaterialKind.Metal));
        var generation = BarbetteGenerator.Generate(definition, context.CenterPlaneX,
            DesignMeasure.FromCellAnchor(0), context);
        Require(generation.IsValid, Details(generation.Diagnostics));
        AssertMandatoryInnermostBoundary(generation, context, "HF-02 w=9 clear=5");
        AssertMandatoryRingRoofed(generation, context, "HF-02 w=9 clear=5");
        Require(generation.Solids.Any(intent => intent.Role == BarbetteArmorRole.Side),
            "The corrected barbette must actually emit innermost side armor where the hull allows it.");
        // The requested single layer is boundary-complete (armor plus two protected-skin
        // substitutions), so it is not truncated and must not raise the yellow BAR206 warning.
        Require(generation.ArmorRealization!.RealizedSideLayers == 1,
            "A boundary-complete mandatory ring-0 must report one realized side layer, not zero; got " +
            $"{generation.ArmorRealization.RealizedSideLayers}.");
        Require(!Has(generation.Diagnostics, BarbetteDiagnosticCodes.SideArmorTruncated),
            "A single requested layer satisfied by armor plus protected skin is not truncated.");

        // A protected-skin substitution must not expand the mandatory bottom footprint: it still
        // spans only the protected clear plan (no emitted-armor-complete side layer exists here).
        var layout = generation.Layout!;
        var bottomCells = generation.Solids
            .Where(intent => intent.Role == BarbetteArmorRole.Bottom && intent.Cell.Y == layout.FloorTopY)
            .Select(intent => intent.Cell).ToHashSet();
        var clearAtFloor = generation.Measurement!.ClearCells
            .Select(cell => new HullCell(cell.X, layout.FloorTopY, cell.Z)).ToHashSet();
        Require(bottomCells.SetEquals(clearAtFloor),
            "A protected-skin substitution must not expand the mandatory bottom floor footprint.");
    }

    /// <summary>
    /// Independent roof-coverage oracle for the mandatory innermost side ring. Every realized
    /// mandatory ring-0 plan cell must be capped by roof armor at the roof underside, or be a retained
    /// protected-skin cell there; and the scalar summary must agree with the emitted layer-0 mask plus
    /// protected-skin substitutions.
    /// </summary>
    private static void AssertMandatoryRingRoofed(
        BarbetteGenerationResult generation,
        HullBuildContext context,
        string label)
    {
        var measurement = generation.Measurement!;
        var layout = generation.Layout!;
        var clear = measurement.ClearCells
            .Select(cell => new BarbettePlanCell(cell.X, cell.Z)).ToHashSet();
        var ring0 = Dilate(clear);
        ring0.ExceptWith(clear);

        var layer0 = generation.Solids
            .Where(intent => intent.Role == BarbetteArmorRole.Side && intent.LayerIndex == 0)
            .Select(intent => new HullCell(intent.Cell.X, intent.Cell.Y, intent.Cell.Z))
            .ToHashSet();
        var roof = generation.Solids
            .Where(intent => intent.Role == BarbetteArmorRole.Roof &&
                             intent.Cell.Y == layout.RoofBottomY)
            .Select(intent => new HullCell(intent.Cell.X, intent.Cell.Y, intent.Cell.Z))
            .ToHashSet();

        var unsatisfied = new List<(int X, int Z)>();
        var uncovered = new List<(int X, int Z)>();
        foreach (var cell in ring0)
        {
            var boundarySatisfied = true;
            for (var y = layout.ClearVolumeMinY; y <= layout.ClearVolumeMaxY; y++)
            {
                if (layer0.Contains(new HullCell(cell.X, y, cell.Z)))
                    continue;
                if (context.IsProtectedShellCell(cell.X, y, cell.Z))
                    continue;
                boundarySatisfied = false;
                break;
            }

            if (!boundarySatisfied)
                unsatisfied.Add((cell.X, cell.Z));

            if (!roof.Contains(new HullCell(cell.X, layout.RoofBottomY, cell.Z)) &&
                !context.IsProtectedShellCell(cell.X, layout.RoofBottomY, cell.Z))
                uncovered.Add((cell.X, cell.Z));
        }

        if (unsatisfied.Count > 0)
        {
            var first = unsatisfied[0];
            Require(false,
                $"{label}: {unsatisfied.Count} mandatory ring-0 plan cell(s) are not boundary-satisfied " +
                $"by layer-0 side armor or retained protected skin; first ({first.X}, {first.Z}).");
        }

        if (uncovered.Count > 0)
        {
            var first = uncovered[0];
            Require(false,
                $"{label}: {uncovered.Count} mandatory ring-0 plan cell(s) are not capped by the " +
                $"barbette roof at Y={layout.RoofBottomY} nor retained protected skin; first " +
                $"({first.X}, {first.Z}).");
        }

        Require(generation.ArmorRealization!.RealizedSideLayers >= 1,
            $"{label}: a boundary-satisfied mandatory ring must report at least one complete side layer.");
    }

    /// <summary>
    /// The independent mandatory-boundary oracle. The required positions are the Moore
    /// neighbourhood of the measured clear plan minus the clear plan itself, checked at every
    /// realized cavity height. This never reads the generated side list to decide what is required.
    /// </summary>
    private static void AssertMandatoryInnermostBoundary(
        BarbetteGenerationResult generation,
        HullBuildContext context,
        string label)
    {
        var measurement = generation.Measurement!;
        var layout = generation.Layout!;
        var clear = measurement.ClearCells
            .Select(cell => new BarbettePlanCell(cell.X, cell.Z)).ToHashSet();
        var required = new HashSet<BarbettePlanCell>();
        foreach (var cell in clear)
        foreach (var offset in MooreOffsets)
            required.Add(new BarbettePlanCell(cell.X + offset.X, cell.Z + offset.Z));
        required.ExceptWith(clear);

        var innermost = generation.Solids
            .Where(intent => intent.Role == BarbetteArmorRole.Side && intent.LayerIndex == 0)
            .Select(intent => new HullCell(intent.Cell.X, intent.Cell.Y, intent.Cell.Z))
            .ToHashSet();

        var missing = new List<(int X, int Y, int Z)>();
        foreach (var cell in required)
        for (var y = layout.ClearVolumeMinY; y <= layout.ClearVolumeMaxY; y++)
        {
            if (innermost.Contains(new HullCell(cell.X, y, cell.Z)))
                continue;
            if (context.IsProtectedShellCell(cell.X, y, cell.Z))
                continue;
            missing.Add((cell.X, y, cell.Z));
        }

        if (missing.Count == 0)
            return;
        var first = missing[0];
        Require(false,
            $"{label}: {missing.Count} of {required.Count * layout.RealizedClearDepthMetres} " +
            $"mandatory innermost side-boundary position(s) are neither innermost barbette side armor " +
            $"nor retained protected hull skin; first missing ({first.X}, {first.Y}, {first.Z}); " +
            $"realized side layers {generation.ArmorRealization?.RealizedSideLayers}, " +
            $"side solids {generation.Solids.Count(intent => intent.Role == BarbetteArmorRole.Side)}.");
    }

    /// <summary>
    /// An optional outer side layer that cannot fit at every position truncates locally and
    /// outermost-first, while the mandatory innermost boundary stays complete everywhere and the
    /// clear diameter is unchanged.
    /// </summary>
    private static void VerifyPartialSideLayerTruncation()
    {
        var parameters = HullParameters.Default with
        {
            Length = 60,
            Width = 9,
            Height = 10,
            Smoothing = SmoothingMethod.None,
            Superstructure = SuperstructureSettings.Default with { Enabled = false },
        };
        var context = HullGenerator.CreateContext(parameters);
        var definition = Definition("partial", clear: 3, depth: 3, side: 2, bottom: 2, neck: 1, neckSize: 3);
        var generation = BarbetteGenerator.Generate(definition, context.CenterPlaneX,
            DesignMeasure.FromCellAnchor(0), context);
        Require(generation.IsValid, Details(generation.Diagnostics));
        AssertMandatoryInnermostBoundary(generation, context, "partial w=9 clear=3 side=2");
        AssertMandatoryRingRoofed(generation, context, "partial w=9 clear=3 side=2");

        var measurement = generation.Measurement!;
        var layout = generation.Layout!;
        var clear = measurement.ClearCells
            .Select(cell => new BarbettePlanCell(cell.X, cell.Z)).ToHashSet();
        var ring0 = Dilate(clear);
        ring0.ExceptWith(clear);
        var ring1 = Dilate(new HashSet<BarbettePlanCell>(clear.Concat(ring0)));
        ring1.ExceptWith(clear);
        ring1.ExceptWith(ring0);

        var side = generation.Solids.Where(intent => intent.Role == BarbetteArmorRole.Side).ToArray();
        var layer0 = side.Count(intent => intent.LayerIndex == 0);
        var layer1 = side.Count(intent => intent.LayerIndex == 1);
        var fullLayer1 = ring1.Count * layout.RealizedClearDepthMetres;
        Require(layer0 > 0,
            "The mandatory innermost side layer must be present on a valid result.");
        Require(layer1 > 0 && layer1 < fullLayer1,
            $"The optional outer layer must truncate locally rather than all-or-nothing; got {layer1} " +
            $"of {fullLayer1} outer cells.");
        Require(generation.ArmorRealization!.SideTruncated &&
                Has(generation.Diagnostics, BarbetteDiagnosticCodes.SideArmorTruncated),
            "A locally truncated outer layer must still report the outermost-first truncation warning.");
        Require(measurement.RealizedClearWidth.Metres == 3,
            "Local outer-layer truncation must never shrink the protected clear diameter.");
    }

    /// <summary>
    /// A mandatory innermost boundary position that is deliberate hull armor air can be neither
    /// barbette side armor nor retained protected skin, so the whole barbette rejects atomically with
    /// the new red code and exposes no partial output.
    /// </summary>
    private static void VerifyMandatorySideBoundaryRejectsAtomically()
    {
        var parameters = HullParameters.Default with
        {
            Length = 60,
            Width = 9,
            Height = 12,
            HullArmor = new ArmorLayout([new ArmorLayer(MaterialKind.Metal), ArmorLayer.Air]),
            Smoothing = SmoothingMethod.None,
            Superstructure = SuperstructureSettings.Default with { Enabled = false },
        };
        var context = HullGenerator.CreateContext(parameters);
        var definition = Definition("boundary", clear: 5, depth: 3, side: 1, bottom: 1, neck: 1, neckSize: 3);
        var generation = BarbetteGenerator.Generate(definition, context.CenterPlaneX,
            DesignMeasure.FromCellAnchor(0), context);

        Require(!generation.IsValid &&
                Has(generation.Diagnostics, BarbetteDiagnosticCodes.MandatorySideBoundaryImpossible),
            "A mandatory boundary position that is reserved hull air must reject the whole barbette. " +
            Details(generation.Diagnostics));
        Require(generation.Diagnostics.Single(diagnostic =>
                diagnostic.Code == BarbetteDiagnosticCodes.MandatorySideBoundaryImpossible).Severity ==
            DesignSeverity.Error,
            "An impossible mandatory side boundary must be red/blocking.");
        Require(generation.Solids.Count == 0 && generation.RequiredVoids.Count == 0 &&
                generation.AuthorizedDeckCuts.Count == 0,
            "An impossible mandatory side boundary must expose no partial solids, voids or deck cuts.");
        Require(generation.Measurement is { } measurement &&
                measurement.RequestedClearDiameter == DesignMeasure.FromMetres(5) &&
                measurement.CenterX == context.CenterPlaneX,
            "Rejection must not shrink the requested clear diameter or move the requested center.");
    }

    /// <summary>
    /// A requested barbette armor-air layer explicitly supersedes ordinary interior hull armor it
    /// coincides with, never touches protected skin, and never fills deliberate hull armor air.
    /// </summary>
    private static void VerifyArmorAirSupersedesInteriorArmor()
    {
        var parameters = HullParameters.Default with
        {
            Length = 60,
            Width = 9,
            Height = 10,
            HullArmor = new ArmorLayout([
                new ArmorLayer(MaterialKind.Metal),
                new ArmorLayer(MaterialKind.HeavyArmor),
            ]),
            Smoothing = SmoothingMethod.None,
            Superstructure = SuperstructureSettings.Default with { Enabled = false },
        };
        var context = HullGenerator.CreateContext(parameters);
        var definition = Definition("armor-air", clear: 5, depth: 3, side: 1, bottom: 1, neck: 1, neckSize: 3) with
        {
            SideArmor = new ArmorLayout([new ArmorLayer(MaterialKind.Metal), ArmorLayer.Air]),
        };
        var generation = BarbetteGenerator.Generate(definition, context.CenterPlaneX,
            DesignMeasure.FromCellAnchor(0), context);
        Require(generation.IsValid, Details(generation.Diagnostics));
        AssertMandatoryInnermostBoundary(generation, context, "armor-air w=9 clear=5");

        var measurement = generation.Measurement!;
        var layout = generation.Layout!;
        var airVoids = generation.RequiredVoids
            .Where(intent => !IsClearVolumeVoid(intent, measurement, layout) &&
                             !IsNeckShaftVoid(intent, measurement, layout))
            .ToArray();
        Require(airVoids.Length > 0,
            "The [Metal, Air] side stack must reserve its outer layer as protected air voids.");
        var overlapping = airVoids.Where(intent =>
                context.TryGetArmor(intent.Cell.X, intent.Cell.Y, intent.Cell.Z, out var armor) &&
                armor.IsStructuralArmor && armor.Region != ArmorRegion.Deck &&
                !context.IsProtectedShellCell(intent.Cell.X, intent.Cell.Y, intent.Cell.Z))
            .ToArray();
        Require(overlapping.Length > 0,
            "This fixture must place ordinary interior hull armor inside the requested barbette air gap.");
        Require(overlapping.All(intent => generation.SupersededArmorCells.Contains(intent.Cell)),
            "Every ordinary interior armor cell inside a requested barbette air gap must be explicitly " +
            "superseded rather than silently left in the requested air.");
        Require(generation.SupersededArmorCells.All(cell =>
                !context.IsProtectedShellCell(cell.X, cell.Y, cell.Z)),
            "Protected exterior skin must never be superseded by a barbette air gap.");
        Require(!generation.Solids.Any(intent =>
                context.IsReservedArmorAir(intent.Cell.X, intent.Cell.Y, intent.Cell.Z)),
            "A barbette must never fill deliberate hull reserved armor air.");

        var document = BarbetteDocument(parameters, definition, DesignMeasure.FromCellAnchor(0));
        var composed = new ShipGenerationService().Generate(document, 206, InstalledCatalog());
        Require(composed.IsValid, Details(composed.Diagnostics));
        var snapshot = composed.Snapshot!;
        var occupied = CellMaterials(snapshot.Hull);
        var composedGeneration = snapshot.Barbettes.Single();
        Require(composedGeneration.SupersededArmorCells.SequenceEqual(generation.SupersededArmorCells),
            "The composed barbette must supersede exactly the interior armor the ownership pass reported.");
        var solidCells = composedGeneration.Solids.Select(intent => intent.Cell).ToHashSet();
        Require(generation.SupersededArmorCells.Where(cell => !solidCells.Contains(cell))
                .All(cell => !occupied.ContainsKey(cell)),
            "Superseded armor inside a requested air gap must be absent from the composed snapshot.");
        var missingSkin = snapshot.HullContext.EnumerateArmor()
            .Where(intent => snapshot.HullContext.IsProtectedShellCell(intent.Cell.X, intent.Cell.Y,
                intent.Cell.Z))
            .Where(intent => !occupied.TryGetValue(intent.Cell, out var material) || material != intent.Material)
            .ToArray();
        Require(missingSkin.Length == 0,
            "Armor-air supersession must never change the protected exterior skin.");
    }

    /// <summary>
    /// The composed snapshot agrees with a direct generation for the HF-02 repro, the mandatory
    /// boundary survives composition, and preview/export consume the same resolved plan.
    /// </summary>
    private static void VerifyMandatoryBoundarySnapshotInvariants()
    {
        var parameters = HullParameters.Default with
        {
            Length = 60,
            Width = 9,
            Height = 12,
            Smoothing = SmoothingMethod.None,
            Superstructure = SuperstructureSettings.Default with { Enabled = false },
        };
        var definition = BarbetteDefinition.Create("snapshot", "node-snapshot",
            DesignMeasure.FromMetres(5), 3, 0, 3, sideArmor: ArmorLayout.Single(MaterialKind.Metal));
        var document = BarbetteDocument(parameters, definition, DesignMeasure.FromCellAnchor(0));
        var catalog = InstalledCatalog();
        var composed = new ShipGenerationService().Generate(document, 207, catalog);
        Require(composed.IsValid, Details(composed.Diagnostics));
        var snapshot = composed.Snapshot!;
        var composedGeneration = snapshot.Barbettes.Single();
        AssertMandatoryInnermostBoundary(composedGeneration, snapshot.HullContext,
            "snapshot w=9 clear=5");

        var direct = BarbetteGenerator.Generate(definition, snapshot.HullContext.CenterPlaneX,
            DesignMeasure.FromCellAnchor(0), snapshot.HullContext);
        Require(direct.IsValid, Details(direct.Diagnostics));
        Require(direct.Solids.SequenceEqual(composedGeneration.Solids) &&
                direct.RequiredVoids.SequenceEqual(composedGeneration.RequiredVoids) &&
                direct.AuthorizedDeckCuts.SequenceEqual(composedGeneration.AuthorizedDeckCuts),
            "The composed snapshot barbette must agree with a direct generation on the same hull context.");
        Require(composedGeneration.Measurement!.RequestedClearDiameter == DesignMeasure.FromMetres(5) &&
                composedGeneration.Measurement.RealizedClearWidth.Metres == 5,
            "Composition must preserve the requested and realized clear diameter.");
        Require(composedGeneration.Layout!.RealizedClearDepthMetres >= 1,
            "The repro must keep at least one metre of protected clear cavity after composition.");

        var occupied = CellMaterials(snapshot.Hull);
        var missingSkin = snapshot.HullContext.EnumerateArmor()
            .Where(intent => snapshot.HullContext.IsProtectedShellCell(intent.Cell.X, intent.Cell.Y,
                intent.Cell.Z))
            .Where(intent => !occupied.TryGetValue(intent.Cell, out var material) || material != intent.Material)
            .ToArray();
        Require(missingSkin.Length == 0,
            "A valid mandatory boundary must retain the protected exterior skin through composition.");

        var root = Path.Combine(Path.GetTempPath(), $"HullForge-HF02-{Guid.NewGuid():N}");
        try
        {
            var export = new BlueprintExporter().Export(snapshot, document, 207, root, "hf02");
            Require(export.BlockCount == snapshot.Hull.BlockCount &&
                    export.OccupiedCellCount == snapshot.Hull.OccupiedCellCount,
                "Preview and export must consume the same resolved snapshot.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    /// <summary>Moore dilation used only by the independent test oracle.</summary>
    private static HashSet<BarbettePlanCell> Dilate(IReadOnlySet<BarbettePlanCell> cells)
    {
        var result = new HashSet<BarbettePlanCell>(cells);
        foreach (var cell in cells)
        foreach (var offset in MooreOffsets)
            result.Add(new BarbettePlanCell(cell.X + offset.X, cell.Z + offset.Z));
        return result;
    }

    private static bool IsClearVolumeVoid(
        BarbetteVoidIntent intent,
        BarbetteMeasurement measurement,
        BarbetteVerticalLayout layout) =>
        intent.Cell.Y >= layout.ClearVolumeMinY && intent.Cell.Y <= layout.ClearVolumeMaxY &&
        measurement.ClearCells.Any(cell => cell.X == intent.Cell.X && cell.Z == intent.Cell.Z);

    private static bool IsNeckShaftVoid(
        BarbetteVoidIntent intent,
        BarbetteMeasurement measurement,
        BarbetteVerticalLayout layout) =>
        intent.Cell.Y >= layout.RoofBottomY && intent.Cell.Y <= layout.NeckTopY &&
        measurement.NeckClearCells.Any(cell => cell.X == intent.Cell.X && cell.Z == intent.Cell.Z);

    private static void VerifyFlatZeroOffsetPlacement()
    {
        var context = DefaultContext.Value;
        var definition = Definition("flat", clear: 3, depth: 3, side: 1);
        var result = Reconcile(context, (definition, 0));
        Require(result.IsValid, Details(result.Diagnostics));
        var entry = result.Barbettes.Single();
        var generation = entry.Generation;
        Require(entry.CenterZ == DesignMeasure.FromCellAnchor(0),
            "A valid barbette must keep the requested longitudinal center.");
        Require(generation.Layout!.RoofTopY == context.ReferenceDeckY - 1,
            "A zero-offset barbette must anchor its roof immediately beneath the ship-wide reference deck skin.");
        Require(generation.Layout.NeckTopY == context.ReferenceDeckY &&
                generation.Layout.NeckBottomY == context.ReferenceDeckY,
            "A zero-offset flat-deck barbette must place its neck flush with the reference deck.");
        Require(generation.Layout.RealizedClearDepthMetres == 3,
            "A flat-deck barbette must realize its requested clear depth.");
        Require(generation.ArmorRealization is { SideTruncated: false, BottomTruncated: false },
            "A roomy hull must not truncate requested armor.");
        Require(generation.AuthorizedDeckCuts.Count == 25,
            $"A 3 m neck through one deck layer needs 25 cuts; got {generation.AuthorizedDeckCuts.Count}.");
        Require(generation.SupersededArmorCells.Count == 0,
            "A default hull has no ordinary interior armor for the barbette to supersede.");
        Require(generation.ClearVolumeBounds is { } bounds && bounds.MinY < bounds.MaxY &&
                bounds.MinZ < bounds.MaxZ,
            "The realized protected clear-volume envelope must be exposed for the ruler and renderer.");
        Require(generation.RequiredVoids.Any(intent => intent.Cell.Y == context.ReferenceDeckY) &&
                generation.Solids.Any(intent => intent.Role == BarbetteArmorRole.Neck),
            "A flush neck must emit both its protected shaft and its square armor.");

        // Every resolved cell of a flush barbette stays inside the evaluated hull; nothing pushes
        // the exterior contour outward.
        Require(generation.Solids.All(intent => context.IsOccupied(intent.Cell.X, intent.Cell.Y, intent.Cell.Z)),
            "A flush barbette must not place armor outside the evaluated hull solid.");
    }

    private static void VerifyFloatingNeckContinuity()
    {
        var context = DefaultContext.Value;
        var definition = Definition("floating", clear: 3, depth: 3, side: 1) with { TopOffsetMetres = 3 };
        var result = Reconcile(context, (definition, 0));
        Require(result.IsValid, Details(result.Diagnostics));
        var generation = result.Barbettes.Single().Generation;
        var layout = generation.Layout!;
        Require(layout.RoofTopY == context.ReferenceDeckY - 4,
            "A three-metre top offset must lower the main barbette three metres below the reference underside.");
        Require(layout.NeckBottomY == layout.RoofTopY + 1 && layout.NeckTopY == context.ReferenceDeckY,
            "A floating barbette neck must span from its roof to the common deck top.");
        Require(layout.RealizedClearDepthMetres == 3,
            "Lowering the barbette must not change the realized clear depth.");

        var shaftCells = generation.RequiredVoids
            .Where(intent => intent.Cell.X == 0 && intent.Cell.Z == 0 &&
                             intent.Cell.Y >= layout.RoofBottomY)
            .Select(intent => intent.Cell.Y).Distinct().Order().ToArray();
        Require(shaftCells.SequenceEqual(
                Enumerable.Range(layout.RoofBottomY, layout.NeckTopY - layout.RoofBottomY + 1)),
            "The floating neck shaft must be continuous from the roof underside through the deck.");
        var neckArmor = generation.Solids.Where(intent => intent.Role == BarbetteArmorRole.Neck)
            .Select(intent => intent.Cell.Y).Distinct().Order().ToArray();
        Require(neckArmor.SequenceEqual(
                Enumerable.Range(layout.NeckBottomY, layout.NeckTopY - layout.NeckBottomY + 1)),
            "The floating neck armor must be continuous from the roof top to the common deck top.");
        Require(generation.Solids.Where(intent => intent.Role == BarbetteArmorRole.Side)
                .All(intent => intent.Cell.Y <= layout.ClearTopY),
            "A floating barbette's side armor must stay below its own roof.");
    }

    private static void VerifyRaisedDeckTrunkAndNeckTop()
    {
        var parameters = HullParameters.Default with
        {
            Length = 72,
            Shape = HullShapeSettings.Default with { Profile = new HullProfileSettings(4, 2, 3, 1) },
        };
        var context = HullGenerator.CreateContext(parameters);
        int? raisedZ = null;
        for (var z = context.MinZ + 6; z <= context.MaxZ - 6 && raisedZ is null; z++)
            if (context.DeckYAt(z) > context.ReferenceDeckY)
                raisedZ = z;
        Require(raisedZ is not null, "The raised-profile fixture must expose a locally raised deck station.");

        var definition = Definition("raised", clear: 3, depth: 3, side: 1, neckSize: 3);
        var result = Reconcile(context, (definition, raisedZ!.Value));
        Require(result.IsValid, Details(result.Diagnostics));
        var generation = result.Barbettes.Single().Generation;
        var layout = generation.Layout!;
        Require(layout.RoofTopY == context.ReferenceDeckY - 1,
            "A locally raised deck must never lift the main barbette above the ship-wide reference plane.");

        var footprintStations = generation.Measurement!.NeckClearCells
            .Concat(generation.Measurement.NeckArmorCells).Select(cell => cell.Z).Distinct().ToArray();
        var expectedTop = footprintStations.Max(context.DeckYAt);
        Require(layout.NeckTopY == expectedTop,
            $"The neck top must be the highest local deck across the complete footprint ({expectedTop}); " +
            $"got {layout.NeckTopY}.");

        var lowerStation = footprintStations.Where(z => context.DeckYAt(z) < layout.NeckTopY)
            .Order().FirstOrDefault();
        Require(footprintStations.Any(z => context.DeckYAt(z) < layout.NeckTopY),
            "The raised fixture must expose a lower neighboring deck cell under the neck footprint.");
        Require(generation.Solids.Any(intent => intent.Role == BarbetteArmorRole.Neck &&
                intent.Cell.Z == lowerStation && intent.Cell.Y > context.DeckYAt(lowerStation)),
            "The neck must protrude as an armored trunk above a lower neighboring deck cell.");

        var shaftCells = generation.RequiredVoids
            .Where(intent => intent.Cell.Z == raisedZ!.Value && intent.Cell.Y >= layout.RoofBottomY)
            .Select(intent => intent.Cell.Y).Distinct().Order().ToArray();
        Require(shaftCells.SequenceEqual(
                Enumerable.Range(layout.RoofBottomY, layout.NeckTopY - layout.RoofBottomY + 1)),
            "The raised-deck neck shaft must be a complete vertical square prism to the common top.");
    }

    private static void VerifyProtectedSkinPreserved()
    {
        // A narrow hull forces the outermost side layer against the protected skin; the skin must
        // survive byte-for-byte while the clear cavity and inner armor remain valid.
        var parameters = HullParameters.Default with
        {
            Length = 60,
            Width = 9,
            Height = 10,
            Smoothing = SmoothingMethod.None,
            Superstructure = SuperstructureSettings.Default with { Enabled = false },
        };
        var definition = Definition("skin", clear: 3, depth: 3, side: 2, bottom: 2, neck: 1, neckSize: 3);
        var document = BarbetteDocument(parameters, definition, DesignMeasure.FromCellAnchor(0));
        var result = new ShipGenerationService().Generate(document, 201, InstalledCatalog());
        Require(result.IsValid, Details(result.Diagnostics));
        var snapshot = result.Snapshot!;
        var context = snapshot.HullContext;
        var occupied = CellMaterials(snapshot.Hull);

        var missingSkin = context.EnumerateArmor()
            .Where(intent => context.IsProtectedShellCell(intent.Cell.X, intent.Cell.Y, intent.Cell.Z))
            .Where(intent => !occupied.TryGetValue(intent.Cell, out var material) || material != intent.Material)
            .ToArray();
        Require(missingSkin.Length == 0,
            "The protected exterior side/bottom skin changed material or disappeared under a valid barbette: " +
            string.Join(", ", missingSkin.Take(3).Select(intent => intent.Cell)));

        var generation = snapshot.Barbettes.Single();
        Require(generation.ArmorRealization!.SideTruncated,
            "The narrow-hull fixture must exercise outermost-first side truncation.");
        Require(generation.Measurement!.RealizedClearWidth.Metres == 3,
            "Truncating side armor must never shrink the protected clear diameter.");
        Require(generation.Solids.All(intent =>
                context.IsOccupied(intent.Cell.X, intent.Cell.Y, intent.Cell.Z)),
            "A flush barbette must never push the exterior hull contour outward.");
        AssertMandatoryInnermostBoundary(generation, context, "protected-skin fixture");
    }

    private static void VerifyInteriorArmorSuperseded()
    {
        var parameters = HullParameters.Default with
        {
            Length = 60,
            Width = 9,
            Height = 10,
            HullArmor = new ArmorLayout([
                new ArmorLayer(MaterialKind.Metal),
                new ArmorLayer(MaterialKind.HeavyArmor),
            ]),
            Smoothing = SmoothingMethod.None,
            Superstructure = SuperstructureSettings.Default with { Enabled = false },
        };
        var context = HullGenerator.CreateContext(parameters);
        var definition = Definition("supersede", clear: 5, depth: 3, side: 1, bottom: 1, neck: 1, neckSize: 3);
        var result = Reconcile(context, (definition, 0));
        Require(result.IsValid, Details(result.Diagnostics));
        var generation = result.Barbettes.Single().Generation;
        Require(generation.SupersededArmorCells.Count > 0,
            "A wide barbette in a narrow two-layer hull must supersede ordinary interior armor.");
        Require(generation.SupersededArmorCells.All(cell =>
                context.TryGetArmor(cell.X, cell.Y, cell.Z, out var armor) && armor.IsStructuralArmor &&
                !context.IsProtectedShellCell(cell.X, cell.Y, cell.Z) &&
                armor.Region != ArmorRegion.Deck),
            "Only ordinary non-deck interior hull armor may be superseded.");

        var document = BarbetteDocument(parameters, definition, DesignMeasure.FromCellAnchor(0));
        var composed = new ShipGenerationService().Generate(document, 204, InstalledCatalog());
        Require(composed.IsValid, Details(composed.Diagnostics));
        var snapshot = composed.Snapshot!;
        var occupied = CellMaterials(snapshot.Hull);
        var composedGeneration = snapshot.Barbettes.Single();
        Require(composedGeneration.SupersededArmorCells.SequenceEqual(generation.SupersededArmorCells),
            "The composed barbette must supersede exactly the same interior armor the ownership pass reported.");
        var barbetteSolids = composedGeneration.Solids.ToDictionary(intent => intent.Cell,
            intent => intent.Material);
        Require(generation.SupersededArmorCells.Where(cell => !barbetteSolids.ContainsKey(cell))
                .All(cell => !occupied.ContainsKey(cell)),
            "Superseded interior armor that the barbette leaves clear must be removed from the resolved plan.");
        Require(generation.SupersededArmorCells.Where(barbetteSolids.ContainsKey)
                .All(cell => occupied.TryGetValue(cell, out var material) &&
                             material == barbetteSolids[cell]),
            "Superseded interior armor the barbette refills must carry the barbette material, not the hull layer.");
        var missingSkin = snapshot.HullContext.EnumerateArmor()
            .Where(intent => snapshot.HullContext.IsProtectedShellCell(intent.Cell.X, intent.Cell.Y,
                intent.Cell.Z))
            .Where(intent => !occupied.TryGetValue(intent.Cell, out var material) || material != intent.Material)
            .ToArray();
        Require(missingSkin.Length == 0,
            "Superseding ordinary interior armor must never touch the protected exterior skin.");
    }

    private static void VerifyOutermostFirstTruncation()
    {
        var parameters = HullParameters.Default with { Length = 60, Width = 9, Height = 10 };
        var context = HullGenerator.CreateContext(parameters);
        var definition = Definition("truncate", clear: 3, depth: 3, side: 3, bottom: 1, neck: 1, neckSize: 3);
        var result = Reconcile(context, (definition, 0));
        Require(result.IsValid, Details(result.Diagnostics));
        var generation = result.Barbettes.Single().Generation;
        var realization = generation.ArmorRealization!;
        Require(realization.RequestedSideLayers == 3 && realization.RealizedSideLayers < 3,
            $"A 9 m hull must truncate the outermost side layers; realized " +
            $"{realization.RealizedSideLayers} of {realization.RequestedSideLayers}.");
        Require(realization.RealizedSideLayers >= 1,
            "The innermost side layer against the clear cavity must survive a thin hull.");
        Require(generation.Measurement!.RealizedClearWidth.Metres == 3,
            "Truncating side armor must never shrink the protected clear diameter.");
        Require(Has(generation.Diagnostics, BarbetteDiagnosticCodes.SideArmorTruncated),
            "Outermost-first truncation must emit a yellow warning.");
        Require(generation.Diagnostics.Where(diagnostic =>
                diagnostic.Code == BarbetteDiagnosticCodes.SideArmorTruncated)
            .All(diagnostic => diagnostic.Severity == DesignSeverity.Warning),
            "Armor truncation is a compromise with a usable result, not a blocking error.");
        var emittedLayers = generation.Solids.Where(intent => intent.Role == BarbetteArmorRole.Side)
            .Select(intent => intent.LayerIndex).Distinct().Order().ToArray();
        Require(emittedLayers.Length > 0 && emittedLayers[0] == 0,
            "The innermost mandatory side layer must always be present on a valid result.");
        Require(emittedLayers.SequenceEqual(Enumerable.Range(0, emittedLayers.Length)),
            "Emitted side layers must remain a contiguous innermost-first prefix even when an outer " +
            "layer truncates locally.");
        Require(emittedLayers.All(layer => layer < realization.RequestedSideLayers),
            "No side layer beyond the requested stack may be emitted.");
        Require(realization.RealizedSideLayers <= emittedLayers.Length &&
                realization.RealizedSideLayers <= realization.RequestedSideLayers,
            "The scalar realized-layer summary must never exceed the emitted or requested stack.");
    }

    private static void VerifyClearDepthShortfall()
    {
        var parameters = HullParameters.Default with { Length = 60, Width = 21, Height = 8 };
        var context = HullGenerator.CreateContext(parameters);
        var definition = Definition("short", clear: 3, depth: 12, side: 1, bottom: 1, neck: 3);
        var result = Reconcile(context, (definition, 0));
        Require(result.IsValid, Details(result.Diagnostics));
        var generation = result.Barbettes.Single().Generation;
        var layout = generation.Layout!;
        Require(layout.RequestedClearDepthMetres == 12 && layout.RealizedClearDepthMetres < 12,
            "A 12 m clear-depth request on an 8 m hull must report a shortfall.");
        Require(layout.RealizedClearDepthMetres >= 1,
            "The deepest valid clear cavity must still exist when a shortfall is a warning.");
        Require(Has(generation.Diagnostics, BarbetteDiagnosticCodes.ClearDepthShortfall),
            "A clear-depth shortfall must emit a yellow warning.");
        Require(generation.Diagnostics.Single(diagnostic =>
                diagnostic.Code == BarbetteDiagnosticCodes.ClearDepthShortfall).Requested ==
                DesignMeasure.FromMetres(12) &&
            generation.Diagnostics.Single(diagnostic =>
                diagnostic.Code == BarbetteDiagnosticCodes.ClearDepthShortfall).Realized ==
                DesignMeasure.FromMetres(layout.RealizedClearDepthMetres),
            "The shortfall warning must report requested and realized clear depth.");
        Require(layout.ClearTopY == layout.RoofBottomY - 1,
            "A shortfall must preserve the highest valid clear cavity: the top stays at the roof underside.");

        var clear = generation.Measurement!.ClearCells
            .Select(cell => new BarbettePlanCell(cell.X, cell.Z)).ToHashSet();
        Require(!generation.Solids.Any(intent =>
                clear.Contains(new BarbettePlanCell(intent.Cell.X, intent.Cell.Z)) &&
                intent.Cell.Y >= layout.ClearVolumeMinY && intent.Cell.Y <= layout.ClearVolumeMaxY),
            "Nothing may be filled upward into the protected clear depth to level the floor.");
        var cavityVoids = generation.RequiredVoids
            .Where(intent => clear.Contains(new BarbettePlanCell(intent.Cell.X, intent.Cell.Z)) &&
                             intent.Cell.Y >= layout.ClearVolumeMinY &&
                             intent.Cell.Y <= layout.ClearVolumeMaxY)
            .ToArray();
        Require(cavityVoids.Length == clear.Count * layout.RealizedClearDepthMetres,
            "Every clear column must reserve exactly the realized clear depth as protected void.");
        Require(cavityVoids.Select(intent => intent.Cell.Y).Distinct().Count() ==
                layout.RealizedClearDepthMetres,
            "The protected floor must stay perfectly flat across every clear column.");
    }

    /// <summary>
    /// A roomy hull realizes the requested clear depth and still keeps its mandatory bottom floor.
    /// </summary>
    private static void VerifyComfortableMandatoryBottomFloor()
    {
        var context = DefaultContext.Value;
        var definition = Definition("roomy-floor", clear: 3, depth: 3, side: 1, bottom: 1);
        var result = Reconcile(context, (definition, 0));
        Require(result.IsValid, Details(result.Diagnostics));
        var generation = result.Barbettes.Single().Generation;
        Require(generation.Layout!.RealizedClearDepthMetres == 3 &&
                generation.ArmorRealization!.RealizedBottomLayers == 1,
            "A roomy hull must realize the requested clear depth and its mandatory bottom floor.");
    }

    /// <summary>
    /// A floating barbette that reaches the protected inner bottom must shorten its clear depth
    /// before it drops the mandatory floor. Before this correction the same request returned a valid
    /// result with zero realized bottom layers under only a yellow warning.
    /// </summary>
    private static void VerifyFloatingMandatoryBottomFloor()
    {
        var context = DefaultContext.Value;
        var definition = Definition("deep-floor", clear: 3, depth: 6, side: 1, bottom: 1) with
        {
            TopOffsetMetres = 2,
        };
        var result = Reconcile(context, (definition, 0));
        Require(result.IsValid, Details(result.Diagnostics));
        var generation = result.Barbettes.Single().Generation;
        var layout = generation.Layout!;
        Require(generation.ArmorRealization!.RealizedBottomLayers >= 1,
            "Bottom armor was requested, so at least the innermost bottom layer must be realized.");
        Require(layout.RealizedClearDepthMetres < definition.ClearDepthMetres &&
                layout.RealizedClearDepthMetres >= 1,
            "The realized clear depth must shorten before the mandatory floor is sacrificed.");
        Require(Has(generation.Diagnostics, BarbetteDiagnosticCodes.ClearDepthShortfall),
            "Shortening the clear depth to keep the mandatory floor must still report the shortfall.");
        Require(generation.Solids.Where(intent => intent.Role == BarbetteArmorRole.Bottom)
                .Select(intent => intent.Cell.Y).Distinct().Order().SequenceEqual([layout.FloorTopY]),
            "The single requested bottom layer must be the mandatory flat floor at the floor top.");
        Require(generation.Solids.Where(intent => intent.Role == BarbetteArmorRole.Bottom)
                .Select(intent => intent.Cell).ToHashSet()
                .IsSupersetOf(generation.Measurement!.ClearCells.Select(cell =>
                    new HullCell(cell.X, layout.FloorTopY, cell.Z))),
            "The mandatory bottom layer must span the protected floor under every clear column.");
    }

    /// <summary>
    /// Optional outer bottom layers truncate outermost-first down to the single mandatory innermost
    /// layer, reported as the existing yellow BAR207 warning.
    /// </summary>
    private static void VerifyBottomTruncatesToMandatoryLayer()
    {
        var context = DefaultContext.Value;
        var definition = Definition("trunc-floor", clear: 3, depth: 6, side: 1, bottom: 3) with
        {
            TopOffsetMetres = 1,
        };
        var result = Reconcile(context, (definition, 0));
        Require(result.IsValid, Details(result.Diagnostics));
        var generation = result.Barbettes.Single().Generation;
        var realization = generation.ArmorRealization!;
        Require(realization.RequestedBottomLayers == 3 && realization.RealizedBottomLayers == 1,
            "The hull boundary must truncate the optional outer bottom layers to the one mandatory " +
            $"layer; got {realization.RealizedBottomLayers} of {realization.RequestedBottomLayers}.");
        Require(Has(generation.Diagnostics, BarbetteDiagnosticCodes.BottomArmorTruncated),
            "Outermost-first bottom truncation must emit the yellow BAR207 warning.");
        Require(generation.Diagnostics.Single(diagnostic =>
                diagnostic.Code == BarbetteDiagnosticCodes.BottomArmorTruncated).Severity ==
            DesignSeverity.Warning,
            "Bottom truncation is a compromise with a usable result, not a blocking error.");
        Require(generation.Solids.Where(intent => intent.Role == BarbetteArmorRole.Bottom)
                .Select(intent => intent.LayerIndex).Distinct().Order().SequenceEqual([0]),
            "Bottom truncation must keep the innermost mandatory layer only.");
        Require(generation.Solids.Where(intent => intent.Role == BarbetteArmorRole.Bottom)
                .All(intent => intent.Cell.Y == generation.Layout!.FloorTopY),
            "The surviving bottom layer must be the mandatory flat floor at the protected floor top.");
    }

    /// <summary>
    /// When even one metre of clear cavity cannot coexist with the mandatory floor, the whole
    /// barbette is rejected atomically with the blocking BAR215 diagnostic.
    /// </summary>
    private static void VerifyImpossibleFloorRejectsAtomically()
    {
        // A shallow hull cannot realize one metre of clear cavity above the mandatory bottom floor.
        var parameters = HullParameters.Default with { Length = 60, Width = 15, Height = 5 };
        var context = HullGenerator.CreateContext(parameters);
        var definition = Definition("no-floor", clear: 3, depth: 1, side: 1, bottom: 1);
        var result = Reconcile(context, (definition, 0));
        Require(!result.IsValid && Has(result.Diagnostics, BarbetteDiagnosticCodes.BottomArmorFloorImpossible),
            "A barbette that cannot realize one metre of clear depth plus its mandatory floor must " +
            $"block. {Details(result.Diagnostics)}");
        Require(result.Diagnostics.Single(diagnostic =>
                diagnostic.Code == BarbetteDiagnosticCodes.BottomArmorFloorImpossible).Severity ==
            DesignSeverity.Error,
            "The mandatory-floor failure must be red/blocking.");
        var generation = result.Barbettes.Single().Generation;
        Require(result.Solids.Count == 0 && generation.Solids.Count == 0 &&
                generation.RequiredVoids.Count == 0 && generation.AuthorizedDeckCuts.Count == 0,
            "A blocked mandatory floor must expose no partial component output.");
        Require(result.Barbettes.Single().CenterZ == DesignMeasure.FromCellAnchor(0),
            "A blocked mandatory floor must not move the requested barbette.");
    }

    /// <summary>
    /// Sweeps representative hulls and requests and proves no valid barbette with requested bottom
    /// armor ever reports zero realized bottom layers. This fails against the pre-correction
    /// warning-only behavior.
    /// </summary>
    private static void VerifyNoValidResultDropsAllBottomLayers()
    {
        var parameterSets = new[]
        {
            HullParameters.Default,
            HullParameters.Default with { Length = 60, Width = 21, Height = 8 },
            HullParameters.Default with { Length = 60, Width = 15, Height = 6 },
            HullParameters.Default with { Length = 60, Width = 15, Height = 5 },
        };
        foreach (var parameters in parameterSets)
        {
            var context = HullGenerator.CreateContext(parameters);
            for (var bottom = 1; bottom <= 3; bottom++)
            for (var topOffset = 0; topOffset <= 3; topOffset++)
            for (var depth = 1; depth <= 6; depth++)
            {
                var definition = Definition($"matrix-b{bottom}-o{topOffset}-d{depth}", clear: 3,
                    depth: depth, side: 1, bottom: bottom) with { TopOffsetMetres = topOffset };
                var result = Reconcile(context, (definition, 0));
                if (!result.IsValid)
                    continue;
                Require(result.Barbettes.Single().Generation.ArmorRealization!.RealizedBottomLayers >= 1,
                    "A valid barbette with requested bottom armor dropped every bottom layer " +
                    $"(width {parameters.Width}, height {parameters.Height}, bottom {bottom}, " +
                    $"offset {topOffset}, depth {depth}).");
            }
        }
    }

    /// <summary>
    /// The corrected roof coverage and mandatory floor must survive the resolved composition and
    /// snapshot path, not only the local generator.
    /// </summary>
    private static void VerifyCorrectedRoofAndFloorSurviveComposition()
    {
        var parameters = HullParameters.Default with
        {
            Smoothing = SmoothingMethod.None,
            Superstructure = SuperstructureSettings.Default with { Enabled = false },
        };
        var definition = Definition("compose-floor", clear: 5, depth: 6, side: 1, roof: 1, bottom: 2,
            neck: 1, neckSize: 1);
        var document = BarbetteDocument(parameters, definition, DesignMeasure.FromCellAnchor(0));
        var result = new ShipGenerationService().Generate(document, 205, InstalledCatalog());
        Require(result.IsValid, Details(result.Diagnostics));
        var snapshot = result.Snapshot!;
        var generation = snapshot.Barbettes.Single();
        var layout = generation.Layout!;
        var occupied = CellMaterials(snapshot.Hull);
        var clear = generation.Measurement!.ClearCells;
        var neckClear = generation.Measurement.NeckClearCells.ToHashSet();

        var roofCells = generation.Solids.Where(intent => intent.Role == BarbetteArmorRole.Roof)
            .Select(intent => intent.Cell).ToHashSet();
        Require(clear.Where(cell => !neckClear.Contains(cell))
                .All(cell => roofCells.Contains(new HullCell(cell.X, layout.RoofBottomY, cell.Z))),
            "The composed roof must cover the protected cavity below it.");
        Require(roofCells.All(occupied.ContainsKey),
            "Every composed roof cell must appear in the resolved snapshot.");
        Require(generation.AuthorizedDeckCuts.Count == 9,
            "A 1 m neck through one deck layer must still cut its 3x3 aperture; got " +
            $"{generation.AuthorizedDeckCuts.Count}.");

        Require(generation.ArmorRealization!.RealizedBottomLayers >= 1,
            "The composed barbette must retain its mandatory bottom floor.");
        var floorCells = generation.Solids
            .Where(intent => intent.Role == BarbetteArmorRole.Bottom && intent.Cell.Y == layout.FloorTopY)
            .Select(intent => intent.Cell).ToHashSet();
        Require(clear.Where(cell => !neckClear.Contains(cell))
                .All(cell => floorCells.Contains(new HullCell(cell.X, layout.FloorTopY, cell.Z))),
            "The composed mandatory bottom layer must span the protected floor under every clear column.");
    }

    private static void VerifySlopedBottomFlatFloor()
    {
        var parameters = HullParameters.Default with
        {
            Length = 72,
            Height = 12,
            Shape = HullShapeSettings.Default with { Profile = new HullProfileSettings(2, 1, 5, 4) },
        };
        var context = HullGenerator.CreateContext(parameters);
        var definition = Definition("sloped", clear: 3, depth: 4, side: 1, bottom: 2, neck: 3);
        var result = Reconcile(context, (definition, -6));
        Require(result.IsValid, Details(result.Diagnostics));
        var generation = result.Barbettes.Single().Generation;
        var layout = generation.Layout!;
        Require(layout.RealizedClearDepthMetres >= 1,
            "A sloped/raised keel must still realize at least one metre of protected clear cavity.");

        var clear = generation.Measurement!.ClearCells
            .Select(cell => new BarbettePlanCell(cell.X, cell.Z)).ToHashSet();
        var bottomInsideClear = generation.Solids
            .Where(intent => intent.Role == BarbetteArmorRole.Bottom &&
                             clear.Contains(new BarbettePlanCell(intent.Cell.X, intent.Cell.Z)))
            .ToArray();
        Require(bottomInsideClear.Length > 0 &&
                bottomInsideClear.Max(intent => intent.Cell.Y) == layout.FloorTopY &&
                bottomInsideClear.All(intent => intent.Cell.Y <= layout.FloorTopY),
            "The protected floor seen from inside the cavity must be flat and its mandatory bottom " +
            "layer must be realized over the sloped keel.");
        Require(!generation.Solids.Any(intent =>
                clear.Contains(new BarbettePlanCell(intent.Cell.X, intent.Cell.Z)) &&
                intent.Cell.Y >= layout.ClearVolumeMinY && intent.Cell.Y <= layout.ClearVolumeMaxY),
            "A sloped keel must never be levelled by filling upward into the clear cavity.");
    }

    private static void VerifyClearVolumeSeparation()
    {
        var context = DefaultContext.Value;
        var first = Definition("sep-a", clear: 3, depth: 3, side: 1);
        var second = Definition("sep-b", clear: 3, depth: 3, side: 1);

        // Exactly one metre of structure between the two protected clear volumes; exterior armor
        // legitimately overlaps at the shared column.
        var separated = Reconcile(context, (first, 0), (second, 4), midpoint: 2);
        Require(separated.IsValid, Details(separated.Diagnostics));
        Require(Has(separated.Diagnostics, BarbetteDiagnosticCodes.ArmorOwnershipMerged),
            "Overlapping exterior armor must report the merged ownership warning.");
        var shared = separated.Barbettes[0].Generation.Solids.Select(intent => intent.Cell)
            .Intersect(separated.Barbettes[1].Generation.Solids.Select(intent => intent.Cell))
            .ToArray();
        Require(shared.Length > 0,
            "The one-metre clear separator fixture must actually exercise shared exterior armor.");

        var adjacent = Reconcile(context, (first, 0), (second, 3), midpoint: 2);
        Require(!adjacent.IsValid && Has(adjacent.Diagnostics, BarbetteDiagnosticCodes.ClearVolumeSeparation),
            "Protected clear volumes that are face-adjacent without a one-metre separator must block.");

        var overlapping = Reconcile(context, (first, 0), (second, 2), midpoint: 2);
        Require(!overlapping.IsValid && Has(overlapping.Diagnostics, BarbetteDiagnosticCodes.ClearVolumeOverlap),
            "Overlapping protected clear volumes must block.");
        Require(overlapping.Diagnostics.Any(diagnostic =>
                diagnostic.Code is BarbetteDiagnosticCodes.ClearVolumeOverlap
                    or BarbetteDiagnosticCodes.ArmorPenetratesClearVolume) &&
            overlapping.Diagnostics.Any(diagnostic => diagnostic.AffectedBounds is not null),
            "A clear-volume conflict must carry bounded diagnostic evidence.");
    }

    private static void VerifyMidpointAndForwardTieOwnership()
    {
        var context = DefaultContext.Value;
        var aft = Definition("own-aft", clear: 3, depth: 3, side: 1);
        var forward = Definition("own-forward", clear: 3, depth: 3, side: 1);

        // Shared armor column at Z=2. With midpoint 1 the aft barbette is closer and owns it.
        var aftWins = Reconcile(context, (aft, 0), (forward, 4), midpoint: 1);
        Require(aftWins.IsValid, Details(aftWins.Diagnostics));
        var aftShared = SharedCells(aftWins);
        Require(aftShared.Length > 0, "The tie fixture must share armor cells.");
        Require(aftShared.All(cell => OwnerAt(aftWins, cell) == "own-aft"),
            "The barbette closer to the ship longitudinal midpoint must own the shared armor cell.");

        // With midpoint 2 both are equally distant; the forward barbette wins.
        var forwardWins = Reconcile(context, (aft, 0), (forward, 4), midpoint: 2);
        Require(forwardWins.IsValid, Details(forwardWins.Diagnostics));
        var forwardShared = SharedCells(forwardWins);
        Require(forwardShared.Length > 0, "The equal-distance tie fixture must share armor cells.");
        Require(forwardShared.All(cell => OwnerAt(forwardWins, cell) == "own-forward"),
            "An equal-distance tie must be won by the forward barbette.");

        // Exactly one physical owner per merged cell, and neither barbette moved.
        Require(forwardWins.Solids.Select(intent => intent.Cell).Distinct().Count() ==
                forwardWins.Solids.Count,
            "A merged armor cell must have exactly one physical owner.");
        Require(forwardWins.Barbettes.All(entry => entry.CenterZ == DesignMeasure.FromCellAnchor(
                entry.Definition.Id == "own-aft" ? 0 : 4)),
            "Overlap ownership must never move either barbette.");
    }

    private static void VerifyInvalidRoofElevation()
    {
        var context = DefaultContext.Value;
        var definition = Definition("roof", clear: 3, depth: 3, side: 1);

        var midship = Reconcile(context, (definition, 0));
        Require(midship.IsValid && !Has(midship.Diagnostics, BarbetteDiagnosticCodes.RoofAboveLocalDeck),
            "A midship barbette on the reference deck must not report a roof protrusion.");

        // The Axe bow cuts the deck down part-way through its entrance, producing a genuine local
        // exterior surface below the ship-wide reference plane. The innermost cut station is the
        // widest such station, so the clear cavity still fits and only the roof protrudes.
        var loweredContext = HullGenerator.CreateContext(HullParameters.Default with
        {
            BowStyle = BowStyle.Axe,
        });
        int? loweredZ = null;
        for (var z = loweredContext.MinZ + 2; z <= loweredContext.MaxZ - 2 && loweredZ is null; z++)
            if (loweredContext.DeckYAt(z) != int.MinValue &&
                loweredContext.DeckYAt(z) < loweredContext.ReferenceDeckY)
                loweredZ = z;
        Require(loweredZ is not null,
            "The Axe bow fixture must expose a local exterior surface below the reference deck plane.");

        var blocked = Reconcile(loweredContext, (definition, loweredZ!.Value));
        Require(!blocked.IsValid && Has(blocked.Diagnostics, BarbetteDiagnosticCodes.RoofAboveLocalDeck),
            "A roof above a lower local exterior surface must be blocking, not silently lowered. " +
            Details(blocked.Diagnostics));
        Require(blocked.Diagnostics.Single(diagnostic =>
                diagnostic.Code == BarbetteDiagnosticCodes.RoofAboveLocalDeck).Severity == DesignSeverity.Error,
            "Invalid roof elevation is a red/blocking condition.");
        Require(blocked.Barbettes.Single().CenterZ == DesignMeasure.FromCellAnchor(loweredZ.Value) &&
                blocked.Solids.Count == 0,
            "A blocked roof must expose no partial geometry and must not move the requested barbette.");
    }

    private static void VerifyNeckObstruction()
    {
        var parameters = HullParameters.Default with { Length = 60, Width = 9, Height = 10 };
        var context = HullGenerator.CreateContext(parameters);
        var wide = Definition("neck-wide", clear: 5, depth: 2, side: 1, neck: 2, neckSize: 5);
        var blocked = Reconcile(context, (wide, 0));
        Require(!blocked.IsValid && Has(blocked.Diagnostics, BarbetteDiagnosticCodes.NeckObstructed),
            "A neck armor square wider than the hull deck must block as an incomplete prism.");
        Require(blocked.Diagnostics.Single(diagnostic =>
                diagnostic.Code == BarbetteDiagnosticCodes.NeckObstructed).Severity == DesignSeverity.Error,
            "Neck obstruction is a red/blocking condition.");

        var narrow = Definition("neck-narrow", clear: 5, depth: 2, side: 1, neck: 1, neckSize: 3);
        var valid = Reconcile(context, (narrow, 0));
        Require(valid.IsValid, Details(valid.Diagnostics));
    }

    private static void VerifyDeckCutsAndSurroundingPacking()
    {
        var parameters = HullParameters.Default with
        {
            DeckArmor = new ArmorLayout([new ArmorLayer(MaterialKind.Wood), new ArmorLayer(MaterialKind.Lead)]),
            Smoothing = SmoothingMethod.None,
            Superstructure = SuperstructureSettings.Default with { Enabled = false },
        };
        var definition = Definition("deck", clear: 3, depth: 3, side: 1, neck: 1, neckSize: 3);
        var document = BarbetteDocument(parameters, definition, DesignMeasure.FromCellAnchor(0));
        var result = new ShipGenerationService().Generate(document, 202, InstalledCatalog());
        Require(result.IsValid, Details(result.Diagnostics));
        var snapshot = result.Snapshot!;
        var generation = snapshot.Barbettes.Single();
        Require(generation.AuthorizedDeckCuts.Count == 50,
            $"A 5x5 neck through two deck layers needs 50 cuts; got {generation.AuthorizedDeckCuts.Count}.");

        var occupied = CellMaterials(snapshot.Hull);
        var clearOpening = generation.Measurement!.NeckClearCells
            .Select(cell => (cell.X, cell.Z)).ToHashSet();
        var deckLevels = generation.AuthorizedDeckCuts.Select(intent => intent.Cell.Y).Distinct().ToArray();
        Require(deckLevels.Length == 2, "Both deck layers must be cut.");
        foreach (var y in deckLevels)
        foreach (var (x, z) in clearOpening)
            Require(!occupied.ContainsKey(new HullCell(x, y, z)),
                $"Deck material inside the clear neck opening must be removed at ({x}, {y}, {z}).");

        // Surrounding deck cells survive and remain ordinary packed deck structure.
        var surrounding = deckLevels.SelectMany(y => Enumerable.Range(-3, 7)
                .Where(x => Math.Abs(x) > 2)
                .Select(x => new HullCell(x, y, 0)))
            .ToArray();
        Require(surrounding.All(occupied.ContainsKey),
            "Deck material around the neck aperture must survive as ordinary deck structure.");
        Require(snapshot.Hull.Blocks.Any(block => block.CellLength > 1 &&
                block.Origin == BlockOrigin.Shell &&
                block.OccupiedCells.Any(cell => deckLevels.Contains(cell.Y))),
            "The surviving deck around the neck aperture must remain eligible for normal packing.");
    }

    private static void VerifyNoRepositioning()
    {
        var context = DefaultContext.Value;
        var definition = Definition("fixed", clear: 3, depth: 3, side: 1) with { TopOffsetMetres = 2 };
        var requested = DesignMeasure.FromCellAnchor(-7);
        var result = Reconcile(context, (definition, requested.TwiceMetres / 2));
        Require(result.IsValid, Details(result.Diagnostics));
        var entry = result.Barbettes.Single();
        Require(entry.CenterZ == requested, "Generation must not move the requested longitudinal center.");
        Require(entry.Definition.TopOffsetMetres == 2 &&
                entry.Generation.Layout!.TopOffsetMetres == 2,
            "Generation must not move the requested vertical placement.");

        // An impossible centerline request is diagnosed rather than relocated.
        var offCenter = BarbetteOwnership.Reconcile(
            [new BarbettePlacement(definition, context.CenterPlaneX + DesignMeasure.FromMetres(1), requested)],
            DesignMeasure.Zero, context);
        Require(!offCenter.IsValid && Has(offCenter.Diagnostics, BarbetteDiagnosticCodes.CenterlineMismatch),
            "An off-center barbette must be diagnosed, never silently recentered.");
        Require(offCenter.Barbettes.Single().CenterX ==
                context.CenterPlaneX + DesignMeasure.FromMetres(1),
            "A rejected off-center request must retain its requested X.");
    }

    private static void VerifyCompositionPreviewExportConsistency()
    {
        var parameters = HullParameters.Default with
        {
            Smoothing = SmoothingMethod.None,
            Superstructure = SuperstructureSettings.Default with { Enabled = false },
        };
        var definition = Definition("preview", clear: 3, depth: 3, side: 1, neck: 1, neckSize: 3);
        var document = BarbetteDocument(parameters, definition, DesignMeasure.FromCellAnchor(0));
        var catalog = InstalledCatalog();
        var result = new ShipGenerationService().Generate(document, 203, catalog);
        Require(result.IsValid, Details(result.Diagnostics));
        var snapshot = result.Snapshot!;

        var generation = snapshot.Barbettes.Single();
        var barbetteCells = generation.Solids.Select(intent => intent.Cell).ToHashSet();
        var occupied = snapshot.Hull.Blocks.SelectMany(block => block.OccupiedCells
                .Select(cell => (Cell: new HullCell(cell.X, cell.Y, cell.Z), Block: block)))
            .ToDictionary(item => item.Cell, item => item.Block);
        Require(barbetteCells.All(occupied.ContainsKey),
            "Every reconciled barbette armor cell must appear in the resolved snapshot.");
        Require(barbetteCells.All(cell => occupied[cell].Material ==
                generation.Solids.Single(intent => intent.Cell == cell).Material),
            "The resolved snapshot must preserve each reconciled barbette armor material.");

        // The renderer highlight reads the same provenance the export path consumes.
        var selected = HullPreviewControl.SelectBarbettePlacements(snapshot.Hull, snapshot.Hull.Blocks,
            "preview");
        var selectedCells = selected.SelectMany(block => block.OccupiedCells)
            .Select(cell => new HullCell(cell.X, cell.Y, cell.Z)).ToHashSet();
        Require(barbetteCells.IsSubsetOf(selectedCells),
            "The barbette highlight must select the resolved barbette armor.");
        Require(selectedCells.All(cell => snapshot.Hull.CellProvenance.Single(item => item.Cell == cell)
                .Owners.Any(owner => owner.Role == PhysicalCellRole.Barbette &&
                    owner.OwnerId == "preview")),
            "The barbette highlight must select only cells the barbette owns.");

        var root = Path.Combine(Path.GetTempPath(), $"HullForge-BAR02-{Guid.NewGuid():N}");
        try
        {
            var export = new BlueprintExporter().Export(snapshot, document, 203, root, "bar02");
            Require(export.BlockCount == snapshot.Hull.BlockCount &&
                    export.OccupiedCellCount == snapshot.Hull.OccupiedCellCount,
                "Export must consume the same resolved snapshot as preview.");
            using var json = JsonDocument.Parse(File.ReadAllText(export.FilePath));
            var blueprint = json.RootElement.GetProperty("Blueprint");
            var positions = blueprint.GetProperty("BLP").EnumerateArray()
                .Select(item => item.GetString()).ToArray();
            Require(positions.SequenceEqual(snapshot.Hull.Blocks.Select(block =>
                    $"{block.X},{block.Y},{block.Z}")),
                "The native export must reproduce the resolved barbette placement list exactly.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    private static BarbetteOwnershipResult Reconcile(
        HullBuildContext context,
        params (BarbetteDefinition Definition, int CenterZ)[] barbettes) =>
        Reconcile(context, barbettes, DesignMeasure.Zero);

    private static BarbetteOwnershipResult Reconcile(
        HullBuildContext context,
        (BarbetteDefinition Definition, int CenterZ) first,
        (BarbetteDefinition Definition, int CenterZ) second,
        int midpoint) =>
        Reconcile(context, [first, second], DesignMeasure.FromCellAnchor(midpoint));

    private static BarbetteOwnershipResult Reconcile(
        HullBuildContext context,
        (BarbetteDefinition Definition, int CenterZ)[] barbettes,
        DesignMeasure midpoint) =>
        BarbetteOwnership.Reconcile(
            barbettes.Select(item => new BarbettePlacement(item.Definition, context.CenterPlaneX,
                DesignMeasure.FromCellAnchor(item.CenterZ))).ToArray(),
            midpoint, context);

    private static HullCell[] SharedCells(BarbetteOwnershipResult result)
    {
        var first = result.Barbettes[0].Generation.Solids.Select(intent => intent.Cell).ToHashSet();
        var second = result.Barbettes[1].Generation.Solids.Select(intent => intent.Cell).ToHashSet();
        return first.Intersect(second).OrderBy(cell => cell.Z).ThenBy(cell => cell.Y).ThenBy(cell => cell.X)
            .ToArray();
    }

    private static string? OwnerAt(BarbetteOwnershipResult result, HullCell cell) =>
        result.Solids.FirstOrDefault(intent => intent.Cell == cell)?.OwnerId;

    private static BarbetteDefinition Definition(
        string id,
        double clear,
        int depth,
        int side,
        int roof = 1,
        int bottom = 1,
        int neck = 1,
        int neckSize = 3) => BarbetteDefinition.Create(
        id,
        $"node-{id}",
        DesignMeasure.FromMetres(clear),
        depth,
        topOffsetMetres: 0,
        neckClearSizeMetres: neckSize,
        sideArmor: Stack(side),
        roofArmor: Stack(roof),
        bottomArmor: Stack(bottom),
        neckArmor: Stack(neck));

    private static ArmorLayout Stack(int thickness)
    {
        var layers = new List<MaterialKind>();
        for (var index = 0; index < thickness; index++)
            layers.Add(index == thickness - 1 ? MaterialKind.HeavyArmor : MaterialKind.Metal);
        return new ArmorLayout(layers);
    }

    private static ShipDocument BarbetteDocument(
        HullParameters parameters,
        BarbetteDefinition definition,
        DesignMeasure centerZ)
    {
        var context = HullGenerator.CreateContext(parameters);
        var stations = context.EnumerateArmor()
            .Where(intent => intent.IsStructuralArmor && intent.Region == ArmorRegion.Deck)
            .Select(intent => intent.Cell.Z).Distinct().Order().ToArray();
        var bowDatum = DesignMeasure.FromTwiceMetres(checked(stations[^1] * 2 + 1));
        var measured = BarbetteGenerator.Measure(definition, context.CenterPlaneX, centerZ);
        Require(measured.Measurement is not null, Details(measured.Diagnostics));
        var half = measured.Measurement!.LongitudinalHalfExtent;
        var bowMargin = bowDatum - centerZ - half;
        var node = ArrangementNode.Create($"node-{definition.Id}", ArrangementNodeKind.Barbette,
            definition.Id, half);
        var arrangement = new Arrangement([node], [], [], bowMargin, DesignMeasure.Zero,
            ArrangementAnchorKind.BowDatum, ArrangementResizePolicy.PreserveAbsolute);
        return ShipDocument.CreateNew(definition.Id, parameters, $"bar02-{definition.Id}") with
        {
            Arrangement = arrangement,
            Datum = new LayoutDatum(bowDatum, DesignMeasure.Zero),
            Barbettes = [definition],
        };
    }

    private static Dictionary<HullCell, MaterialKind> CellMaterials(GeneratedHull hull) =>
        hull.Blocks.SelectMany(block => block.OccupiedCells.Select(cell =>
                (Cell: new HullCell(cell.X, cell.Y, cell.Z), block.Material)))
            .GroupBy(item => item.Cell)
            .ToDictionary(group => group.Key, group => group.First().Material);

    private static FtdBlockCatalog InstalledCatalog() => Installed.Value;

    private static FtdBlockCatalog LoadInstalledCatalog()
    {
        var gameDirectory = FtdInstallationLocator.FindInstalledGame() ??
                            throw new InvalidOperationException(
                                "From The Depths is required for the BAR02 installed-catalog oracle.");
        return FtdBlockCatalog.Load(gameDirectory);
    }

    private static bool Has(IEnumerable<DesignDiagnostic> diagnostics, string code) =>
        diagnostics.Any(diagnostic => diagnostic.Code == code);

    private static string Details(IEnumerable<DesignDiagnostic> diagnostics) =>
        string.Join(" | ", diagnostics.Select(diagnostic => diagnostic.ToString()));

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
