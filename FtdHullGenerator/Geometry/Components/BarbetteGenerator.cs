using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Components;
using FtdHullGenerator.Domain.Composition;
using FtdHullGenerator.Domain.Design;
using FtdHullGenerator.Geometry.Composition;

namespace FtdHullGenerator.Geometry.Components;

/// <summary>
/// Plans the frozen 2.0 centerline barbette: a protected circular clear volume with a flat internal
/// floor, independently layered side, roof, bottom and neck armor, and a concentric square 1/3/5 m
/// neck opening. This component emits cube-cell intent only. It never creates a bearing, weapon,
/// ammunition path, AI component or rotating subconstruct.
/// </summary>
/// <remarks>
/// The local geometry is authoritative for the clear volume: armor always grows away from it and is
/// never pushed inward. Hull-boundary truncation, deck elevation reconciliation and multi-barbette
/// ownership are deliberately not decided here; the measured clear/exterior bounds and per-role
/// intents are exposed for the ownership stage.
/// </remarks>
public static class BarbetteGenerator
{
    private const int HardMaxPlanCells = 1_000_000;
    private const int HardMaxOutputIntents = 4_000_000;

    private static readonly (int X, int Z)[] MooreOffsets =
    [
        (-1, -1), (0, -1), (1, -1),
        (-1, 0), (1, 0),
        (-1, 1), (0, 1), (1, 1),
    ];

    private static readonly (int X, int Z)[] FaceOffsets =
    [
        (0, -1), (-1, 0), (1, 0), (0, 1),
    ];

    /// <summary>
    /// Rasterizes the open clear-bore disk by testing whether each unit cell square's interior
    /// intersects it, then grows the side armor as deterministic eight-neighbor layers and forms the
    /// concentric square neck masks. Every boundary decision uses integer arithmetic.
    /// </summary>
    public static BarbetteMeasurementResult Measure(
        BarbetteDefinition definition,
        DesignMeasure centerX,
        DesignMeasure centerZ,
        BarbetteGenerationLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        limits ??= BarbetteGenerationLimits.Default;
        cancellationToken.ThrowIfCancellationRequested();

        var diagnostics = definition.Validate().ToList();
        ValidateMeasurementInputs(definition, centerX, centerZ, limits, diagnostics);
        // An even-width centerline cannot carry a concentric square neck, but its circular clear mask
        // is still representable and is retained so the invalid intent can be shown rather than lost.
        if (diagnostics.Any(diagnostic => diagnostic.IsError &&
                diagnostic.Code is not (BarbetteDiagnosticCodes.EvenWidthCenterline or
                    BarbetteDiagnosticCodes.CenterParityMismatch)))
            return MeasurementResult(null, diagnostics);

        var plan = BuildPlan(definition, centerX, centerZ, limits, diagnostics, cancellationToken);
        if (plan is null)
            return MeasurementResult(null, diagnostics);

        var measurement = MeasurementOf(definition, centerX, centerZ, plan);
        return MeasurementResult(measurement, diagnostics);
    }

    /// <summary>
    /// Produces the deterministic local 3D intent for one barbette. <paramref name="referenceDeckY"/>
    /// is the ship-wide reference deck plane expressed as the lowest reference deck-skin cell, so a
    /// zero top offset puts the roof top cell immediately beneath that deck skin. Local deck rise must
    /// not move the main barbette; callers pass the ship-wide plane, not a local deck elevation.
    /// </summary>
    public static BarbetteGenerationResult Generate(
        BarbetteDefinition definition,
        DesignMeasure centerX,
        DesignMeasure centerZ,
        int referenceDeckY,
        int? neckTopY = null,
        BarbetteGenerationLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        limits ??= BarbetteGenerationLimits.Default;
        cancellationToken.ThrowIfCancellationRequested();

        var diagnostics = definition.Validate().ToList();
        ValidateMeasurementInputs(definition, centerX, centerZ, limits, diagnostics);
        if (diagnostics.HasErrors())
            return Rejected(null, null, diagnostics);

        var plan = BuildPlan(definition, centerX, centerZ, limits, diagnostics, cancellationToken);
        if (plan is null)
            return Rejected(null, null, diagnostics);

        var measurement = MeasurementOf(definition, centerX, centerZ, plan);
        if (diagnostics.HasErrors())
            return Rejected(measurement, null, diagnostics);

        var layout = BuildLayout(definition, referenceDeckY, neckTopY, definition.ClearDepthMetres);
        if (neckTopY is { } requestedTop && requestedTop < layout.NeckBottomY)
        {
            diagnostics.Add(new DesignDiagnostic(
                BarbetteDiagnosticCodes.NeckTopBelowRoof,
                DesignSeverity.Error,
                $"Barbette '{definition.Id}' neck top {requestedTop} is not above its roof top " +
                $"{layout.RoofTopY}.",
                definition.NodeId,
                nameof(neckTopY),
                DesignMeasure.FromCellAnchor(requestedTop),
                DesignMeasure.FromCellAnchor(layout.NeckBottomY),
                "Raise the neck top above the roof or lower the top offset."));
            return Rejected(measurement, layout, diagnostics);
        }

        var estimate = EstimateIntentCount(plan, layout);
        if (estimate > limits.MaxOutputIntents)
        {
            diagnostics.Add(new DesignDiagnostic(
                BarbetteDiagnosticCodes.OutputBudgetExceeded,
                DesignSeverity.Error,
                $"Barbette '{definition.Id}' needs up to {estimate:N0} component intents, above the " +
                $"configured {limits.MaxOutputIntents:N0}-intent budget.",
                definition.NodeId,
                nameof(BarbetteGenerationLimits.MaxOutputIntents),
                null,
                null,
                "Reduce the clear volume, armor thickness or neck height, or raise the explicit component budget."));
            return Rejected(measurement, layout, diagnostics);
        }

        var solids = new List<BarbetteSolidIntent>();
        var voids = new List<BarbetteVoidIntent>();
        BuildRoof(definition, plan, plan.ExteriorCellSet, layout, context: null, solids, voids,
            cancellationToken);
        BuildSide(definition, plan, layout, FullSideRealization(plan, layout), solids, voids,
            cancellationToken);
        BuildBottom(definition, plan.ExteriorCellSet, layout, layout.BottomThicknessMetres, solids, voids,
            cancellationToken);
        BuildNeck(definition, plan, layout, solids, voids, cancellationToken);
        BuildClearVolume(definition, plan, layout, voids, cancellationToken);

        return new BarbetteGenerationResult(
            measurement,
            layout,
            ReadOnly(solids.OrderBy(intent => intent.Cell.Z).ThenBy(intent => intent.Cell.Y)
                .ThenBy(intent => intent.Cell.X)),
            ReadOnly(voids.OrderBy(intent => intent.Cell.Z).ThenBy(intent => intent.Cell.Y)
                .ThenBy(intent => intent.Cell.X)),
            [],
            ReadOnly(diagnostics));
    }

    /// <summary>
    /// Local generation fully reconciled against the evaluated hull: the ship-wide reference deck
    /// plane, the highest local deck under the complete neck footprint, the deepest clear cavity the
    /// protected hull boundary allows, outermost-first side/bottom armor truncation, the mandatory
    /// roof/neck checks, the concentric square neck-opening deck cuts and the ordinary interior
    /// armor this barbette supersedes. It rejects an even-width or invalid hull context atomically
    /// and never moves the requested longitudinal or vertical placement.
    /// </summary>
    public static BarbetteGenerationResult Generate(
        BarbetteDefinition definition,
        DesignMeasure centerX,
        DesignMeasure centerZ,
        HullBuildContext context,
        BarbetteGenerationLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(context);
        limits ??= BarbetteGenerationLimits.Default;
        cancellationToken.ThrowIfCancellationRequested();

        // Product boundary: a centerline barbette may only enter physical generation when the
        // evaluated hull has a real centre voxel column. This catches direct API use even when
        // document/UI validation was bypassed or became stale.
        if (!context.Lattice.HasCenterColumn)
        {
            return Rejected(null, null,
            [
                new DesignDiagnostic(
                    BarbetteDiagnosticCodes.OddHullWidthRequired,
                    DesignSeverity.Error,
                    "Centerline barbettes require an odd hull width.",
                    definition.Id,
                    nameof(HullParameters.Width),
                    DesignMeasure.FromMetres(context.Lattice.Width),
                    null,
                    "Return the hull to an odd width or explicitly remove this centerline barbette.")
            ]);
        }

        var diagnostics = definition.Validate().ToList();
        ValidateMeasurementInputs(definition, centerX, centerZ, limits, diagnostics);
        if (centerX != context.CenterPlaneX)
        {
            diagnostics.Add(new DesignDiagnostic(
                BarbetteDiagnosticCodes.CenterlineMismatch,
                DesignSeverity.Error,
                $"Barbette '{definition.Id}' is centered at X={centerX.Metres:0.##} m, but the evaluated " +
                $"hull center plane is X={context.CenterPlaneX.Metres:0.##} m.",
                definition.NodeId,
                nameof(centerX),
                centerX,
                context.CenterPlaneX,
                "Place the barbette on the evaluated hull center plane."));
        }

        if (context.Diagnostics.HasErrors())
        {
            diagnostics.Add(new DesignDiagnostic(
                BarbetteDiagnosticCodes.HullContextInvalid,
                DesignSeverity.Error,
                $"Barbette '{definition.Id}' cannot be planned against a hull context with " +
                $"{context.Diagnostics.Count(diagnostic => diagnostic.IsError)} error(s).",
                definition.NodeId,
                SuggestedCorrection: "Resolve the hull armor/topology diagnostics before placing components."));
        }

        if (diagnostics.HasErrors())
            return Rejected(null, null, diagnostics);

        var plan = BuildPlan(definition, centerX, centerZ, limits, diagnostics, cancellationToken);
        if (plan is null)
            return Rejected(null, null, diagnostics);
        var measurement = MeasurementOf(definition, centerX, centerZ, plan);
        if (diagnostics.HasErrors())
            return Rejected(measurement, null, diagnostics);

        var deckStations = measurement.ClearCells.Concat(measurement.SideArmorCells)
            .Select(cell => cell.Z).Distinct().Order().ToArray();
        var referenceDeckY = ResolveReferenceDeckUnderside(context, measurement, deckStations, diagnostics);
        var neckTopY = ResolveNeckTop(context, measurement, diagnostics);
        if (referenceDeckY is null || neckTopY is null)
            return Rejected(measurement, null, diagnostics);

        var requestedLayout = BuildLayout(definition, referenceDeckY.Value, neckTopY.Value,
            definition.ClearDepthMetres);
        if (neckTopY.Value < requestedLayout.NeckBottomY)
        {
            diagnostics.Add(new DesignDiagnostic(
                BarbetteDiagnosticCodes.NeckTopBelowRoof,
                DesignSeverity.Error,
                $"Barbette '{definition.Id}' neck top {neckTopY.Value} is not above its roof top " +
                $"{requestedLayout.RoofTopY}.",
                definition.NodeId,
                nameof(BarbetteDefinition.TopOffsetMetres),
                DesignMeasure.FromCellAnchor(neckTopY.Value),
                DesignMeasure.FromCellAnchor(requestedLayout.NeckBottomY),
                "Raise the deck above the barbette or lower the top offset."));
            return Rejected(measurement, requestedLayout, diagnostics);
        }

        // Realized clear depth: the protected cavity keeps its authoritative top plane and can only
        // lose depth from the bottom. Nothing is ever added upward to level the floor.
        var clearDepthLimit = RealizeClearDepth(definition, plan, requestedLayout, context, diagnostics);
        if (clearDepthLimit < 1)
        {
            if (!diagnostics.HasErrors())
                diagnostics.Add(new DesignDiagnostic(
                    BarbetteDiagnosticCodes.ClearCavityImpossible,
                    DesignSeverity.Error,
                    $"Barbette '{definition.Id}' cannot realize even one metre of protected clear cavity " +
                    "inside the evaluated hull.",
                    definition.NodeId,
                    nameof(BarbetteDefinition.ClearDepthMetres),
                    DesignMeasure.FromMetres(definition.ClearDepthMetres),
                    DesignMeasure.FromMetres(clearDepthLimit),
                    "Move the barbette onto a hull section wide and deep enough for its clear volume."));
            return Rejected(measurement, requestedLayout, diagnostics);
        }

        // Both mandatory constraints are evaluated per candidate: the innermost side boundary must be
        // complete (side armor or retained protected skin), and the innermost bottom-armor floor must
        // fit. The deepest candidate that satisfies both wins; a boundary failure at a deeper
        // candidate tries a shallower cavity before the whole barbette is rejected, per 04-barbettes
        // §2 "generate the deepest valid clear cavity and warn". The bottom footprint spans only
        // layers complete in emitted armor, so a protected-skin substitution can never expand it and
        // can never force a shallower cavity or a spurious BAR215.
        var realizedDepth = 0;
        SideRealization? realizedSide = null;
        var realizedBottomLayers = 0;
        BarbetteVerticalLayout? layout = null;
        HashSet<BarbettePlanCell>? realizedBottomExterior = null;
        var boundarySatisfiable = false;
        (BarbettePlanCell Cell, int Y)? boundaryFailure = null;
        for (var depth = clearDepthLimit; depth >= 1; depth--)
        {
            var candidate = BuildLayout(definition, referenceDeckY.Value, neckTopY.Value, depth);
            var candidateSide = RealizeSideMask(plan, candidate, context);
            if (candidateSide.MissingMandatory is { } missing)
            {
                boundaryFailure ??= missing;
                continue;
            }

            boundarySatisfiable = true;
            var candidateBottomExterior = RealizedExterior(plan, candidateSide.ArmorCompleteLayers);
            var candidateBottomLayers = RealizeBottomLayers(candidateBottomExterior, candidate, context);
            if (definition.BottomArmorThicknessMetres > 0 && candidateBottomLayers < 1)
                continue;
            realizedDepth = depth;
            realizedSide = candidateSide;
            realizedBottomLayers = candidateBottomLayers;
            layout = candidate;
            realizedBottomExterior = candidateBottomExterior;
            break;
        }

        if (layout is null || realizedBottomExterior is null || realizedSide is null)
        {
            if (!boundarySatisfiable && boundaryFailure is { } failure)
            {
                diagnostics.Add(new DesignDiagnostic(
                    BarbetteDiagnosticCodes.MandatorySideBoundaryImpossible,
                    DesignSeverity.Error,
                    $"Barbette '{definition.Id}' cannot protect its mandatory innermost side boundary " +
                    $"at ({failure.Cell.X}, {failure.Y}, {failure.Cell.Z}): the position is neither " +
                    "usable hull interior for innermost barbette side armor nor a retained protected " +
                    "side/bottom hull-skin cell, and no shallower protected clear cavity avoids it.",
                    definition.NodeId,
                    nameof(BarbetteDefinition.SideArmor),
                    SuggestedCorrection: "Move the barbette onto a hull section wide enough for its clear " +
                        "volume plus its innermost side armor, or widen the hull; the clear cavity is never " +
                        "shrunk and the placement is never moved.",
                    AffectedBounds: CellBounds(failure.Cell.X, failure.Y, failure.Cell.Z)));
                return Rejected(measurement, requestedLayout, diagnostics);
            }

            diagnostics.Add(new DesignDiagnostic(
                BarbetteDiagnosticCodes.BottomArmorFloorImpossible,
                DesignSeverity.Error,
                $"Barbette '{definition.Id}' cannot realize at least one metre of protected clear depth " +
                "together with its mandatory innermost bottom-armor floor inside the evaluated hull.",
                definition.NodeId,
                nameof(BarbetteDefinition.BottomArmor),
                DesignMeasure.FromMetres(definition.ClearDepthMetres),
                DesignMeasure.FromMetres(realizedDepth),
                "Move the barbette onto a hull section with more room below it, or reduce the bottom armor stack."));
            return Rejected(measurement, requestedLayout, diagnostics);
        }

        // The realized cavity is authoritative and the mandatory boundary is complete. The roof must
        // cover the protected clear plan plus every boundary-complete side layer (including the
        // innermost ring cells a protected-skin substitution satisfies); the mandatory bottom floor
        // spans the clear plan plus only the emitted-armor-complete side layers.
        var realizedSideLayers = realizedSide.RealizedLayers;
        var realizedExterior = RealizedExterior(plan, realizedSide.RealizedLayers);

        if (realizedDepth < definition.ClearDepthMetres)
        {
            diagnostics.Add(new DesignDiagnostic(
                BarbetteDiagnosticCodes.ClearDepthShortfall,
                DesignSeverity.Warning,
                $"Barbette '{definition.Id}' requested {definition.ClearDepthMetres} m of protected clear " +
                $"depth but the protected hull boundary allows {realizedDepth} m.",
                definition.NodeId,
                nameof(BarbetteDefinition.ClearDepthMetres),
                DesignMeasure.FromMetres(definition.ClearDepthMetres),
                DesignMeasure.FromMetres(realizedDepth),
                "Reduce the top offset to raise the barbette (increasing it lowers the barbette), " +
                "or reduce the requested clear depth."));
        }

        if (realizedSideLayers < plan.SideRings.Count)
        {
            diagnostics.Add(new DesignDiagnostic(
                BarbetteDiagnosticCodes.SideArmorTruncated,
                DesignSeverity.Warning,
                $"Barbette '{definition.Id}' side armor was truncated outermost-first from " +
                $"{plan.SideRings.Count} to {realizedSideLayers} complete layer(s) before the protected " +
                "exterior hull skin; the innermost mandatory boundary stays complete.",
                definition.NodeId,
                nameof(BarbetteDefinition.SideArmor),
                DesignMeasure.FromMetres(plan.SideRings.Count),
                DesignMeasure.FromMetres(realizedSideLayers),
                "Reduce the side armor stack or the clear diameter; the clear volume is preserved."));
        }

        if (realizedBottomLayers < layout.BottomThicknessMetres)
        {
            diagnostics.Add(new DesignDiagnostic(
                BarbetteDiagnosticCodes.BottomArmorTruncated,
                DesignSeverity.Warning,
                $"Barbette '{definition.Id}' bottom armor was truncated outermost-first from " +
                $"{layout.BottomThicknessMetres} to {realizedBottomLayers} layer(s) before the protected exterior hull skin.",
                definition.NodeId,
                nameof(BarbetteDefinition.BottomArmor),
                DesignMeasure.FromMetres(layout.BottomThicknessMetres),
                DesignMeasure.FromMetres(realizedBottomLayers),
                "Reduce the bottom armor stack or raise the barbette; the flat protected floor is preserved."));
        }

        ValidateRoof(definition, plan, realizedExterior, layout, context, diagnostics, cancellationToken);
        ValidateNeck(definition, plan, layout, context, diagnostics, cancellationToken);
        if (diagnostics.HasErrors())
            return Rejected(measurement, layout, diagnostics);

        var estimate = EstimateIntentCount(plan, layout);
        if (estimate > limits.MaxOutputIntents)
        {
            diagnostics.Add(new DesignDiagnostic(
                BarbetteDiagnosticCodes.OutputBudgetExceeded,
                DesignSeverity.Error,
                $"Barbette '{definition.Id}' needs up to {estimate:N0} component intents, above the " +
                $"configured {limits.MaxOutputIntents:N0}-intent budget.",
                definition.NodeId,
                nameof(BarbetteGenerationLimits.MaxOutputIntents),
                null,
                null,
                "Reduce the clear volume, armor thickness or neck height, or raise the explicit component budget."));
            return Rejected(measurement, layout, diagnostics);
        }

        var solids = new List<BarbetteSolidIntent>();
        var voids = new List<BarbetteVoidIntent>();
        BuildRoof(definition, plan, realizedExterior, layout, context, solids, voids, cancellationToken);
        BuildSide(definition, plan, layout, realizedSide, solids, voids, cancellationToken);
        BuildBottom(definition, realizedBottomExterior, layout, realizedBottomLayers, solids, voids,
            cancellationToken);
        BuildNeck(definition, plan, layout, solids, voids, cancellationToken);
        BuildClearVolume(definition, plan, layout, voids, cancellationToken);

        var cuts = BuildNeckDeckCuts(definition, plan, layout, context, cancellationToken);
        var superseded = CollectSupersededArmor(plan, layout, solids, voids, context);
        return new BarbetteGenerationResult(
            measurement,
            layout,
            ReadOnly(solids.OrderBy(intent => intent.Cell.Z).ThenBy(intent => intent.Cell.Y)
                .ThenBy(intent => intent.Cell.X)),
            ReadOnly(voids.OrderBy(intent => intent.Cell.Z).ThenBy(intent => intent.Cell.Y)
                .ThenBy(intent => intent.Cell.X)),
            ReadOnly(cuts.OrderBy(intent => intent.Cell.Z).ThenBy(intent => intent.Cell.Y)
                .ThenBy(intent => intent.Cell.X)),
            ReadOnly(diagnostics))
        {
            ArmorRealization = new BarbetteArmorRealization(
                plan.SideRings.Count, realizedSideLayers,
                layout.BottomThicknessMetres, realizedBottomLayers),
            SupersededArmorCells = superseded,
        };
    }

    /// <summary>
    /// True when the cell can hold protected clear volume: usable hull cavity, or ordinary interior
    /// armor the locally dominant barbette is allowed to supersede. Protected exterior skin, armor
    /// air and anything outside the hull are refused.
    /// </summary>
    private static bool IsClearVolumeCell(HullBuildContext context, int x, int y, int z)
    {
        var role = context.RoleAt(x, y, z);
        return role switch
        {
            HullCellRole.Cavity => true,
            HullCellRole.SideArmor or HullCellRole.BottomArmor or HullCellRole.DeckArmor
                or HullCellRole.InternalArmor => !context.IsProtectedShellCell(x, y, z),
            _ => false,
        };
    }

    /// <summary>
    /// True when a mandatory barbette armor cell fits inside the evaluated hull without consuming
    /// protected exterior skin or a deliberate armor-air reservation.
    /// </summary>
    private static bool IsArmorPlaceableCell(HullBuildContext context, int x, int y, int z) =>
        context.RoleAt(x, y, z) is not HullCellRole.Outside &&
        !context.IsProtectedShellCell(x, y, z) &&
        !context.IsReservedArmorAir(x, y, z);

    /// <summary>
    /// Realizes the deepest protected clear cavity whose top stays at the authoritative roof
    /// underside. The floor rises; the cavity is never levelled by filling upward.
    /// </summary>
    private static int RealizeClearDepth(
        BarbetteDefinition definition,
        BarbettePlan plan,
        BarbetteVerticalLayout layout,
        HullBuildContext context,
        List<DesignDiagnostic> diagnostics)
    {
        var realized = 0;
        for (var depth = 1; depth <= layout.RequestedClearDepthMetres; depth++)
        {
            var y = layout.ClearTopY - depth + 1;
            var blocking = (BarbettePlanCell?)null;
            foreach (var cell in plan.ClearCells)
            {
                if (IsClearVolumeCell(context, cell.X, y, cell.Z))
                    continue;
                blocking = cell;
                break;
            }

            if (blocking is { } blocked)
            {
                if (depth == 1)
                    AddClearCavityBoundaryDiagnostic(definition, context, blocked, y, diagnostics);
                break;
            }

            realized = depth;
        }

        return realized;
    }

    private static void AddClearCavityBoundaryDiagnostic(
        BarbetteDefinition definition,
        HullBuildContext context,
        BarbettePlanCell cell,
        int y,
        List<DesignDiagnostic> diagnostics)
    {
        var cellBounds = CellBounds(cell.X, y, cell.Z);
        var (code, message) = context.IsProtectedShellCell(cell.X, y, cell.Z)
            ? (BarbetteDiagnosticCodes.ClearCavityExteriorBoundary,
                $"Barbette '{definition.Id}' protected clear cavity would consume the protected " +
                $"exterior hull skin at ({cell.X}, {y}, {cell.Z}).")
            : context.IsReservedArmorAir(cell.X, y, cell.Z)
                ? (BarbetteDiagnosticCodes.ClearCavityReservedAir,
                    $"Barbette '{definition.Id}' protected clear cavity would consume deliberate armor " +
                    $"air at ({cell.X}, {y}, {cell.Z}).")
                : (BarbetteDiagnosticCodes.ClearCavityOutsideHull,
                    $"Barbette '{definition.Id}' protected clear cavity would leave the evaluated hull " +
                    $"solid at ({cell.X}, {y}, {cell.Z}).");
        diagnostics.Add(new DesignDiagnostic(code, DesignSeverity.Error, message,
            Field: nameof(BarbetteDefinition.ClearDiameter),
            SuggestedCorrection: "Move the barbette onto a wider/deeper hull section; the cavity is never shrunk laterally or moved.",
            AffectedBounds: cellBounds));
    }

    /// <summary>
    /// The pure local realization: with no evaluated hull there is no boundary to truncate
    /// against, so every requested side layer is realized over the complete cavity height.
    /// </summary>
    private static SideRealization FullSideRealization(BarbettePlan plan, BarbetteVerticalLayout layout)
    {
        var cells = new HashSet<ResolvedSideCell>();
        for (var layer = 0; layer < plan.SideRings.Count; layer++)
        foreach (var cell in plan.SideRings[layer])
        for (var y = layout.FloorTopY + 1; y <= layout.ClearTopY; y++)
            cells.Add(new ResolvedSideCell(cell, y, layer));

        return new SideRealization(cells, null, plan.SideRings.Count, plan.SideRings.Count);
    }

    /// <summary>
    /// Resolves the side armor per cell and per layer instead of per whole ring.
    /// <para>
    /// The innermost ring is mandatory: every position at every realized cavity height must become
    /// innermost barbette side armor, or be a retained protected side/bottom hull-skin cell. Any
    /// other outcome (outside the hull, deliberate armor air, a non-protected interior cell that
    /// cannot be occupied) is an unavoidable failure and returns the first offending position.
    /// </para>
    /// <para>
    /// Optional outer layers truncate locally and outermost-first: a cell that cannot be placed is
    /// dropped without dropping any inner cell, and an outer cell never survives where its inward
    /// support was dropped. The clear diameter and the requested placement are never changed.
    /// </para>
    /// </summary>
    private static SideRealization RealizeSideMask(
        BarbettePlan plan,
        BarbetteVerticalLayout layout,
        HullBuildContext context)
    {
        var cells = new HashSet<ResolvedSideCell>();
        if (plan.SideRings.Count == 0)
            return new SideRealization(cells, null, 0, 0);

        var ringSets = plan.SideRings.Select(ring => ring.ToHashSet()).ToArray();

        foreach (var cell in plan.SideRings[0])
        {
            for (var y = layout.FloorTopY + 1; y <= layout.ClearTopY; y++)
            {
                if (IsArmorPlaceableCell(context, cell.X, y, cell.Z))
                {
                    cells.Add(new ResolvedSideCell(cell, y, 0));
                }
                else if (context.IsProtectedShellCell(cell.X, y, cell.Z))
                {
                    // The one permitted substitution: the protected exterior side/bottom skin cell
                    // is retained unchanged and is the structural boundary at this position.
                }
                else
                {
                    return new SideRealization(cells, (cell, y), 0, 0);
                }
            }
        }

        for (var layer = 1; layer < plan.SideRings.Count; layer++)
        {
            foreach (var cell in plan.SideRings[layer])
            {
                for (var y = layout.FloorTopY + 1; y <= layout.ClearTopY; y++)
                {
                    if (!IsArmorPlaceableCell(context, cell.X, y, cell.Z))
                        continue;
                    if (!IsRadiallySupported(context, cells, ringSets[layer - 1], layer, cell, y))
                        continue;
                    cells.Add(new ResolvedSideCell(cell, y, layer));
                }
            }
        }

        return new SideRealization(cells, null,
            CountCompleteSideLayers(plan, layout, cells, context, allowProtectedSkinOnInnermost: true),
            CountCompleteSideLayers(plan, layout, cells, context, allowProtectedSkinOnInnermost: false));
    }

    /// <summary>
    /// Counts the innermost-first layers that are complete over every requested cell and cavity
    /// height. With <paramref name="allowProtectedSkinOnInnermost"/> the mandatory innermost layer
    /// counts a position satisfied by a retained protected-skin cell as well as by an emitted armor
    /// cell (the boundary-complete scalar); optional outer layers always require an emitted armor
    /// cell, so a locally or wholly skin-substituted outer layer is still a truncation. With it false
    /// every layer requires an emitted armor cell (the armor-only count the mandatory bottom floor
    /// footprint consumes).
    /// </summary>
    private static int CountCompleteSideLayers(
        BarbettePlan plan,
        BarbetteVerticalLayout layout,
        IReadOnlySet<ResolvedSideCell> cells,
        HullBuildContext context,
        bool allowProtectedSkinOnInnermost)
    {
        var complete = 0;
        for (var layer = 0; layer < plan.SideRings.Count; layer++)
        {
            var allowProtectedSkin = allowProtectedSkinOnInnermost && layer == 0;
            var full = true;
            foreach (var cell in plan.SideRings[layer])
            {
                for (var y = layout.FloorTopY + 1; y <= layout.ClearTopY; y++)
                {
                    if (cells.Contains(new ResolvedSideCell(cell, y, layer)))
                        continue;
                    if (allowProtectedSkin && context.IsProtectedShellCell(cell.X, y, cell.Z))
                        continue;
                    full = false;
                    break;
                }

                if (!full)
                    break;
            }

            if (!full)
                break;
            complete++;
        }

        return complete;
    }

    /// <summary>
    /// True when an optional outer-layer cell is attached inward at this height: at least one Moore
    /// neighbour in the immediately inner ring is either realized inner-layer barbette armor or a
    /// retained protected-skin cell. This is the per-cell "an outer layer never survives where its
    /// inner support was dropped" rule.
    /// </summary>
    private static bool IsRadiallySupported(
        HullBuildContext context,
        IReadOnlySet<ResolvedSideCell> cells,
        IReadOnlySet<BarbettePlanCell> innerRing,
        int layer,
        BarbettePlanCell cell,
        int y)
    {
        foreach (var offset in MooreOffsets)
        {
            var neighbor = new BarbettePlanCell(cell.X + offset.X, cell.Z + offset.Z);
            if (!innerRing.Contains(neighbor))
                continue;
            if (cells.Contains(new ResolvedSideCell(neighbor, y, layer - 1)))
                return true;
            if (context.IsProtectedShellCell(neighbor.X, y, neighbor.Z))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The plan footprint a horizontal armor layer must span: the protected clear plan plus the
    /// complete innermost-first side layers. The roof passes the boundary-complete count (so a
    /// protected-skin-substituted mandatory ring is still covered); the mandatory bottom floor passes
    /// the armor-only count (so a skin substitution can never expand it or force a shallower cavity).
    /// A locally truncated outer layer is deliberately excluded from both.
    /// </summary>
    private static HashSet<BarbettePlanCell> RealizedExterior(BarbettePlan plan, int completeSideLayers)
    {
        var exterior = new HashSet<BarbettePlanCell>(plan.ClearCells);
        for (var layer = 0; layer < Math.Min(completeSideLayers, plan.SideRings.Count); layer++)
            exterior.UnionWith(plan.SideRings[layer]);
        return exterior;
    }

    private static int RealizeBottomLayers(
        IReadOnlySet<BarbettePlanCell> exterior,
        BarbetteVerticalLayout layout,
        HullBuildContext context)
    {
        var realized = 0;
        for (var layer = 0; layer < layout.BottomThicknessMetres; layer++)
        {
            var y = layout.FloorTopY - layer;
            var fits = exterior.All(cell => IsArmorPlaceableCell(context, cell.X, y, cell.Z));
            if (!fits)
                break;
            realized = layer + 1;
        }

        return realized;
    }

    /// <summary>
    /// The mandatory roof must sit strictly below the local exterior surface everywhere under its
    /// footprint. A lower local deck is a blocking protrusion; the barbette is never moved upward.
    /// </summary>
    private static void ValidateRoof(
        BarbetteDefinition definition,
        BarbettePlan plan,
        IReadOnlySet<BarbettePlanCell> exterior,
        BarbetteVerticalLayout layout,
        HullBuildContext context,
        List<DesignDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        for (var layer = 0; layer < layout.RoofThicknessMetres; layer++)
        {
            var y = layout.RoofBottomY + layer;
            foreach (var cell in exterior.OrderBy(cell => cell.Z).ThenBy(cell => cell.X))
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Validate exactly the cells BuildRoof covers: the whole realized exterior footprint
                // minus the intended square neck-clear shaft.
                if (!IsRoofCell(plan, cell))
                    continue;
                var localDeck = context.DeckYAt(cell.Z);
                var bounds = CellBounds(cell.X, y, cell.Z);
                if (localDeck == int.MinValue || y >= localDeck)
                {
                    diagnostics.Add(new DesignDiagnostic(
                        BarbetteDiagnosticCodes.RoofAboveLocalDeck,
                        DesignSeverity.Error,
                        $"Barbette '{definition.Id}' roof at Y={y} protrudes through the local exterior " +
                        $"surface Y={(localDeck == int.MinValue ? "none" : localDeck)} at " +
                        $"({cell.X}, {cell.Z}).",
                        definition.NodeId,
                        nameof(BarbetteDefinition.TopOffsetMetres),
                        DesignMeasure.FromCellAnchor(y),
                        localDeck == int.MinValue ? null : DesignMeasure.FromCellAnchor(localDeck),
                        "Increase the top offset to lower the main barbette below the local deck.",
                        bounds));
                    return;
                }

                if (IsArmorPlaceableCell(context, cell.X, y, cell.Z))
                    continue;
                // A retained protected-skin cell at a roof position satisfies roof coverage: the
                // skin is the permitted structural boundary, exactly as for the mandatory side
                // boundary. It is never consumed. Anything else is still a blocking obstruction.
                if (context.IsProtectedShellCell(cell.X, y, cell.Z))
                    continue;
                diagnostics.Add(new DesignDiagnostic(
                    BarbetteDiagnosticCodes.RoofObstructed,
                    DesignSeverity.Error,
                    $"Barbette '{definition.Id}' roof armor cannot occupy ({cell.X}, {y}, {cell.Z}) " +
                    "without consuming protected skin or armor air.",
                    definition.NodeId,
                    nameof(BarbetteDefinition.RoofArmor),
                    SuggestedCorrection: "Move the barbette onto a hull section that leaves room for its roof.",
                    AffectedBounds: bounds));
                return;
            }
        }
    }

    /// <summary>
    /// The complete square neck prism must pass through the hull and deck. Below the local surface
    /// every cell must fit inside the hull; above it the trunk deliberately protrudes.
    /// </summary>
    private static void ValidateNeck(
        BarbetteDefinition definition,
        BarbettePlan plan,
        BarbetteVerticalLayout layout,
        HullBuildContext context,
        List<DesignDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        foreach (var cell in plan.NeckClearCells)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var y = layout.RoofBottomY; y <= layout.NeckTopY; y++)
            {
                var localDeck = context.DeckYAt(cell.Z);
                if (localDeck != int.MinValue && y > localDeck)
                    continue;
                if (IsClearVolumeCell(context, cell.X, y, cell.Z))
                    continue;
                diagnostics.Add(NeckObstructed(definition, cell.X, y, cell.Z, "clear shaft"));
                return;
            }
        }

        foreach (var ring in plan.NeckArmorRings)
        {
            foreach (var cell in ring)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (var y = layout.NeckBottomY; y <= layout.NeckTopY; y++)
                {
                    var localDeck = context.DeckYAt(cell.Z);
                    if (localDeck != int.MinValue && y > localDeck)
                        continue;
                    if (IsArmorPlaceableCell(context, cell.X, y, cell.Z))
                        continue;
                    diagnostics.Add(NeckObstructed(definition, cell.X, y, cell.Z, "armor"));
                    return;
                }
            }
        }
    }

    private static DesignDiagnostic NeckObstructed(
        BarbetteDefinition definition, int x, int y, int z, string part) =>
        new(BarbetteDiagnosticCodes.NeckObstructed,
            DesignSeverity.Error,
            $"Barbette '{definition.Id}' complete square neck {part} cannot occupy ({x}, {y}, {z}) " +
            "inside the evaluated hull/deck.",
            definition.NodeId,
            nameof(BarbetteDefinition.NeckClearSizeMetres),
            SuggestedCorrection: "Move the barbette to a hull station whose deck can carry the complete square trunk.",
            AffectedBounds: CellBounds(x, y, z));

    /// <summary>
    /// Ordinary interior hull armor this barbette legitimately supersedes: its protected clear volume,
    /// square neck shaft, own armor cells and its requested armor-air layers that coincide with
    /// non-protected, non-deck structural armor. Deck material inside the aperture is removed through
    /// <see cref="BarbetteDeckCutIntent"/>. Protected skin and hull reserved air are never superseded.
    /// </summary>
    private static IReadOnlyList<HullCell> CollectSupersededArmor(
        BarbettePlan plan,
        BarbetteVerticalLayout layout,
        IReadOnlyList<BarbetteSolidIntent> solids,
        IReadOnlyList<BarbetteVoidIntent> voids,
        HullBuildContext context)
    {
        var cells = new HashSet<HullCell>();
        for (var y = layout.ClearVolumeMinY; y <= layout.ClearVolumeMaxY; y++)
            foreach (var cell in plan.ClearCells)
                Consider(cell.X, y, cell.Z);
        for (var y = layout.RoofBottomY; y <= layout.NeckTopY; y++)
            foreach (var cell in plan.NeckClearCells)
                Consider(cell.X, y, cell.Z);
        foreach (var intent in solids)
            Consider(intent.Cell.X, intent.Cell.Y, intent.Cell.Z);
        // A requested barbette armor-air layer must not silently leave ordinary interior hull armor
        // inside the requested gap: every air void that is not the protected clear cavity or the neck
        // shaft explicitly authorizes removal of the structural armor it coincides with.
        foreach (var intent in voids)
        {
            var cell = intent.Cell;
            if (IsClearVolumePosition(plan, layout, cell.X, cell.Y, cell.Z) ||
                IsNeckShaftPosition(plan, layout, cell.X, cell.Y, cell.Z))
                continue;
            Consider(cell.X, cell.Y, cell.Z);
        }

        return cells.OrderBy(cell => cell.Z).ThenBy(cell => cell.Y).ThenBy(cell => cell.X).ToArray();

        void Consider(int x, int y, int z)
        {
            if (context.IsProtectedShellCell(x, y, z) || context.IsReservedArmorAir(x, y, z))
                return;
            if (context.TryGetArmor(x, y, z, out var armor) && armor.IsStructuralArmor &&
                armor.Region != ArmorRegion.Deck)
                cells.Add(new HullCell(x, y, z));
        }
    }

    /// <summary>True for a cell of the protected circular clear-cavity prism.</summary>
    private static bool IsClearVolumePosition(
        BarbettePlan plan,
        BarbetteVerticalLayout layout,
        int x,
        int y,
        int z) =>
        y >= layout.ClearVolumeMinY && y <= layout.ClearVolumeMaxY &&
        plan.ClearCellSet.Contains(new BarbettePlanCell(x, z));

    /// <summary>True for a cell of the protected square neck shaft prism.</summary>
    private static bool IsNeckShaftPosition(
        BarbettePlan plan,
        BarbetteVerticalLayout layout,
        int x,
        int y,
        int z) =>
        y >= layout.RoofBottomY && y <= layout.NeckTopY &&
        plan.NeckClearCellSet.Contains(new BarbettePlanCell(x, z));

    private static int? ResolveReferenceDeckUnderside(
        HullBuildContext context,
        BarbetteMeasurement measurement,
        IReadOnlyList<int> stations,
        List<DesignDiagnostic> diagnostics)
    {
        var centerCellX = (int)Math.Round(measurement.CenterX.Metres, MidpointRounding.AwayFromZero);
        var measuredStation = stations[stations.Count / 2];
        var localSurface = context.DeckYAt(measuredStation);
        if (localSurface == int.MinValue)
        {
            diagnostics.Add(new DesignDiagnostic(
                BarbetteDiagnosticCodes.DeckUnavailable,
                DesignSeverity.Error,
                $"Barbette clear volume reaches station Z={measuredStation} with no reference deck surface.",
                Field: nameof(BarbetteDefinition.ClearDiameter),
                SuggestedCorrection: "Move the barbette wholly onto the deck-supported interval."));
            return null;
        }

        var deckBottom = localSurface;
        while (context.TryGetArmor(centerCellX, deckBottom - 1, measuredStation, out var armor) &&
               armor.IsStructuralArmor && armor.Region == ArmorRegion.Deck &&
               !context.IsProtectedShellCell(centerCellX, deckBottom - 1, measuredStation))
            deckBottom--;

        // The deck skin thickness is uniform; the roof anchors beneath the ship-wide nominal deck
        // surface, never beneath a locally raised deck. Local rise only lengthens the neck.
        var deckThickness = localSurface - deckBottom + 1;
        var referenceUnderside = context.ReferenceDeckY - deckThickness + 1;

        foreach (var z in stations)
        {
            if (context.DeckYAt(z) == int.MinValue)
            {
                diagnostics.Add(new DesignDiagnostic(
                    BarbetteDiagnosticCodes.DeckUnavailable,
                    DesignSeverity.Error,
                    $"Barbette clear volume reaches station Z={z} with no supporting deck surface.",
                    Field: nameof(BarbetteDefinition.ClearDiameter),
                    SuggestedCorrection: "Move the barbette wholly onto the deck-supported interval."));
                return null;
            }
        }

        return referenceUnderside;
    }

    private static int? ResolveNeckTop(
        HullBuildContext context,
        BarbetteMeasurement measurement,
        List<DesignDiagnostic> diagnostics)
    {
        // The complete neck footprint, clear opening plus every square armor layer, decides the
        // common top plane. Lower neighboring deck cells deliberately stay lower.
        var stations = measurement.NeckClearCells.Concat(measurement.NeckArmorCells)
            .Select(cell => cell.Z).Distinct().Order().ToArray();
        if (stations.Length == 0)
            stations = measurement.ClearCells.Select(cell => cell.Z).Distinct().Order().ToArray();

        var highest = int.MinValue;
        foreach (var z in stations)
        {
            var surface = context.DeckYAt(z);
            if (surface == int.MinValue)
            {
                diagnostics.Add(new DesignDiagnostic(
                    BarbetteDiagnosticCodes.DeckUnavailable,
                    DesignSeverity.Error,
                    $"Barbette neck footprint reaches station Z={z} with no deck surface.",
                    Field: nameof(BarbetteDefinition.NeckClearSizeMetres),
                    SuggestedCorrection: "Move the barbette wholly onto the deck-supported interval."));
                return null;
            }

            highest = Math.Max(highest, surface);
        }

        return highest == int.MinValue ? null : highest;
    }

    private static IReadOnlyList<BarbetteDeckCutIntent> BuildNeckDeckCuts(
        BarbetteDefinition definition,
        BarbettePlan plan,
        BarbetteVerticalLayout layout,
        HullBuildContext context,
        CancellationToken cancellationToken)
    {
        var cuts = new List<BarbetteDeckCutIntent>();
        for (var y = layout.NeckBottomY; y <= layout.NeckTopY; y++)
        {
            foreach (var planCell in plan.NeckClearCells.Concat(plan.NeckArmorRings.SelectMany(ring => ring))
                         .Distinct().OrderBy(cell => cell.Z).ThenBy(cell => cell.X))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (context.TryGetArmor(planCell.X, y, planCell.Z, out var armor) &&
                    armor.IsStructuralArmor && armor.Region == ArmorRegion.Deck &&
                    armor.Material is { } material &&
                    !context.IsProtectedShellCell(planCell.X, y, planCell.Z))
                {
                    cuts.Add(new BarbetteDeckCutIntent(definition.Id,
                        new HullCell(planCell.X, y, planCell.Z), material, armor.Depth));
                }
            }
        }

        return cuts;
    }

    private static DesignBounds CellBounds(int x, int y, int z) => new(
        DesignMeasure.FromCellAnchor(x), DesignMeasure.FromCellAnchor(x),
        DesignMeasure.FromCellAnchor(y), DesignMeasure.FromCellAnchor(y),
        DesignMeasure.FromCellAnchor(z), DesignMeasure.FromCellAnchor(z));

    private static BarbetteVerticalLayout BuildLayout(
        BarbetteDefinition definition,
        int referenceDeckY,
        int? neckTopY,
        int realizedClearDepthMetres)
    {
        var roofTopY = referenceDeckY - 1 - definition.TopOffsetMetres;
        var roofBottomY = roofTopY - definition.RoofArmorThicknessMetres + 1;
        var clearTopY = roofBottomY - 1;
        var floorTopY = clearTopY - realizedClearDepthMetres;
        var bottomArmorBottomY = floorTopY - definition.BottomArmorThicknessMetres + 1;
        var neckBottomY = roofTopY + 1;
        var resolvedNeckTop = neckTopY ?? referenceDeckY;
        return new BarbetteVerticalLayout(
            referenceDeckY,
            definition.TopOffsetMetres,
            roofTopY,
            roofBottomY,
            clearTopY,
            floorTopY,
            bottomArmorBottomY,
            neckBottomY,
            resolvedNeckTop,
            definition.RoofArmorThicknessMetres,
            definition.SideArmorThicknessMetres,
            definition.BottomArmorThicknessMetres,
            definition.NeckArmorThicknessMetres,
            definition.ClearDepthMetres);
    }

    /// <summary>
    /// True for every realized horizontal exterior cell the roof must cover. The concentric square
    /// neck-clear shaft is the only intended opening; the circular protected clear cavity is below
    /// the roof and is covered like any other exterior cell. <see cref="BuildRoof"/> and
    /// <see cref="ValidateRoof"/> share this predicate so their covered sets cannot diverge.
    /// </summary>
    private static bool IsRoofCell(BarbettePlan plan, BarbettePlanCell cell) =>
        !plan.NeckClearCellSet.Contains(cell);

    private static void BuildRoof(
        BarbetteDefinition definition,
        BarbettePlan plan,
        IReadOnlySet<BarbettePlanCell> exterior,
        BarbetteVerticalLayout layout,
        HullBuildContext? context,
        List<BarbetteSolidIntent> solids,
        List<BarbetteVoidIntent> voids,
        CancellationToken cancellationToken)
    {
        for (var layer = 0; layer < layout.RoofThicknessMetres; layer++)
        {
            var y = layout.RoofBottomY + layer;
            var armorLayer = definition.RoofArmor.Layers[layer];
            foreach (var cell in exterior)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // The realized roof covers the complete horizontal exterior footprint. Only the
                // concentric square neck shaft passes through every roof layer; the circular
                // protected clear cavity lies below the roof and must never become a roof opening.
                if (!IsRoofCell(plan, cell))
                    continue;
                // A retained protected-skin cell at a roof position is the permitted structural
                // boundary: it is never consumed and no roof armor is emitted there. ValidateRoof has
                // already rejected any position that is neither roof-placeable nor retained skin.
                if (context is not null && !IsArmorPlaceableCell(context, cell.X, y, cell.Z))
                    continue;
                AddArmorCell(definition.Id, cell, y, armorLayer, BarbetteArmorRole.Roof, layer, solids, voids);
            }
        }
    }

    private static void BuildSide(
        BarbetteDefinition definition,
        BarbettePlan plan,
        BarbetteVerticalLayout layout,
        SideRealization realization,
        List<BarbetteSolidIntent> solids,
        List<BarbetteVoidIntent> voids,
        CancellationToken cancellationToken)
    {
        // The resolved per-cell/per-layer mask is the placement truth; the scalar summary is only
        // metadata. Emitting exactly the resolved cells keeps a locally truncated outer layer from
        // dropping any inner mandatory cell.
        foreach (var entry in realization.Cells
                     .OrderBy(item => item.Cell.Z).ThenBy(item => item.Y)
                     .ThenBy(item => item.Cell.X).ThenBy(item => item.Layer))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var armorLayer = definition.SideArmor.Layers[entry.Layer];
            AddArmorCell(definition.Id, entry.Cell, entry.Y, armorLayer, BarbetteArmorRole.Side,
                entry.Layer, solids, voids);
        }
    }

    private static void BuildBottom(
        BarbetteDefinition definition,
        IReadOnlySet<BarbettePlanCell> exterior,
        BarbetteVerticalLayout layout,
        int realizedLayers,
        List<BarbetteSolidIntent> solids,
        List<BarbetteVoidIntent> voids,
        CancellationToken cancellationToken)
    {
        var layers = Math.Min(realizedLayers, layout.BottomThicknessMetres);
        for (var layer = 0; layer < layers; layer++)
        {
            var y = layout.FloorTopY - layer;
            var armorLayer = definition.BottomArmor.Layers[layer];
            foreach (var cell in exterior)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddArmorCell(definition.Id, cell, y, armorLayer, BarbetteArmorRole.Bottom, layer, solids, voids);
            }
        }
    }

    private static void BuildNeck(
        BarbetteDefinition definition,
        BarbettePlan plan,
        BarbetteVerticalLayout layout,
        List<BarbetteSolidIntent> solids,
        List<BarbetteVoidIntent> voids,
        CancellationToken cancellationToken)
    {
        for (var layer = 0; layer < layout.NeckThicknessMetres; layer++)
        {
            var armorLayer = definition.NeckArmor.Layers[layer];
            foreach (var planCell in plan.NeckArmorRings[layer])
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (var y = layout.NeckBottomY; y <= layout.NeckTopY; y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AddArmorCell(definition.Id, planCell, y, armorLayer, BarbetteArmorRole.Neck, layer,
                        solids, voids);
                }
            }
        }
    }

    private static void BuildClearVolume(
        BarbetteDefinition definition,
        BarbettePlan plan,
        BarbetteVerticalLayout layout,
        List<BarbetteVoidIntent> voids,
        CancellationToken cancellationToken)
    {
        foreach (var planCell in plan.ClearCells)
        {
            for (var y = layout.ClearVolumeMinY; y <= layout.ClearVolumeMaxY; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                voids.Add(new BarbetteVoidIntent(definition.Id, new HullCell(planCell.X, y, planCell.Z)));
            }
        }

        // The square neck shaft is clear from the roof underside through the deck and up to the neck
        // top. It is a separate protected column from the circular cavity.
        foreach (var planCell in plan.NeckClearCells)
        {
            for (var y = layout.RoofBottomY; y <= layout.NeckTopY; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                voids.Add(new BarbetteVoidIntent(definition.Id, new HullCell(planCell.X, y, planCell.Z)));
            }
        }
    }

    private static void AddArmorCell(
        string ownerId,
        BarbettePlanCell planCell,
        int y,
        ArmorLayer armorLayer,
        BarbetteArmorRole role,
        int layerIndex,
        List<BarbetteSolidIntent> solids,
        List<BarbetteVoidIntent> voids)
    {
        var cell = new HullCell(planCell.X, y, planCell.Z);
        if (armorLayer.IsAir)
            voids.Add(new BarbetteVoidIntent(ownerId, cell));
        else
            solids.Add(new BarbetteSolidIntent(ownerId, cell, armorLayer.Material!.Value, role, layerIndex));
    }

    private static long EstimateIntentCount(BarbettePlan plan, BarbetteVerticalLayout layout)
    {
        var exterior = (long)plan.ExteriorCells.Count;
        var side = (long)plan.SideRings.Sum(ring => ring.Count);
        var neck = (long)plan.NeckArmorRings.Sum(ring => ring.Count);
        var clearDepth = (long)layout.RealizedClearDepthMetres;
        var neckHeight = (long)Math.Max(0, layout.NeckTopY - layout.NeckBottomY + 1);
        return exterior * (layout.RoofThicknessMetres + layout.BottomThicknessMetres) +
               side * clearDepth +
               neck * neckHeight +
               plan.ClearCells.Count * clearDepth +
               plan.NeckClearCells.Count * (neckHeight + layout.RoofThicknessMetres);
    }

    private static BarbettePlan? BuildPlan(
        BarbetteDefinition definition,
        DesignMeasure centerX,
        DesignMeasure centerZ,
        BarbetteGenerationLimits limits,
        List<DesignDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var diameterTwice = definition.ClearDiameter.TwiceMetres;

        // HF-06: budget every planar structure (bore scan, side expansion, neck clear square and
        // neck armor exterior square) with checked arithmetic before allocating any mask. The
        // preflight is the primary early gate; the per-layer MaxPlanCells guard below stays as a
        // redundant defense and the final 3D EstimateIntentCount guard is untouched.
        var preflight = BarbettePlanPreflight.Preflight(definition, limits, cancellationToken);
        if (!preflight.IsValid)
        {
            diagnostics.AddRange(preflight.Diagnostics);
            return null;
        }

        var scanRadius = preflight.ScanRadius;
        var bore = new HashSet<BarbettePlanCell>();
        var centerCellX = FloorHalf(centerX.TwiceMetres);
        var centerCellZ = FloorHalf(centerZ.TwiceMetres);
        for (var z = centerCellZ - scanRadius; z <= centerCellZ + scanRadius; z++)
            for (var x = centerCellX - scanRadius; x <= centerCellX + scanRadius; x++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (CellInteriorIntersectsOpenDisk(x, z, centerX.TwiceMetres, centerZ.TwiceMetres, diameterTwice))
                    bore.Add(new BarbettePlanCell(x, z));
            }

        if (bore.Count == 0)
        {
            diagnostics.Add(DesignDiagnostic.Error(
                BarbetteDiagnosticCodes.DegenerateMask,
                $"Barbette '{definition.Id}' produced no clear-volume cells.",
                definition.NodeId,
                nameof(definition.ClearDiameter)));
            return null;
        }

        var rings = new List<List<BarbettePlanCell>>();
        var dilated = new HashSet<BarbettePlanCell>(bore);
        for (var layer = 0; layer < definition.SideArmorThicknessMetres; layer++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var before = Order(dilated);
            var next = new HashSet<BarbettePlanCell>(dilated);
            foreach (var cell in before)
                foreach (var offset in MooreOffsets)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    next.Add(new BarbettePlanCell(
                        checked(cell.X + offset.X),
                        checked(cell.Z + offset.Z)));
                }

            if (next.Count > limits.MaxPlanCells)
            {
                diagnostics.Add(new DesignDiagnostic(
                    BarbetteDiagnosticCodes.PlanBudgetExceeded,
                    DesignSeverity.Error,
                    $"Barbette '{definition.Id}' exceeded the {limits.MaxPlanCells:N0}-cell mask budget " +
                    $"while adding side armor layer {layer + 1}.",
                    definition.Id,
                    nameof(definition.SideArmor),
                    DesignMeasure.FromMetres(definition.SideArmorThicknessMetres),
                    DesignMeasure.FromMetres(layer),
                    "Reduce the clear diameter or side armor thickness, or raise the explicit component budget."));
                return null;
            }

            rings.Add(Order(next.Where(cell => !dilated.Contains(cell))).ToList());
            dilated = next;
        }

        var clearOrdered = Order(bore);
        var exteriorOrdered = Order(dilated);
        if (!IsFaceConnected(dilated, cancellationToken))
        {
            diagnostics.Add(DesignDiagnostic.Error(
                BarbetteDiagnosticCodes.RingDisconnected,
                $"Barbette '{definition.Id}' does not form one face-connected armored plan.",
                definition.NodeId,
                nameof(definition.SideArmor)));
        }

        var neck = BuildNeckPlan(definition, centerX, centerZ, cancellationToken);
        return new BarbettePlan(
            clearOrdered.ToList(),
            rings,
            exteriorOrdered.ToList(),
            neck.ClearCells,
            neck.ArmorRings,
            BoundsOf(clearOrdered),
            BoundsOf(exteriorOrdered),
            neck.ClearBounds,
            neck.ExteriorBounds);
    }

    private static NeckPlan BuildNeckPlan(
        BarbetteDefinition definition,
        DesignMeasure centerX,
        DesignMeasure centerZ,
        CancellationToken cancellationToken)
    {
        var size = definition.NeckClearSizeMetres;
        var thickness = definition.NeckArmorThicknessMetres;
        var clearHalfTwice = definition.NeckClearSize.TwiceMetres / 2;
        var exteriorHalfTwice = clearHalfTwice + thickness * 2;
        var clearBounds = new BarbettePlanBounds(
            centerX - DesignMeasure.FromTwiceMetres(clearHalfTwice),
            centerX + DesignMeasure.FromTwiceMetres(clearHalfTwice),
            centerZ - DesignMeasure.FromTwiceMetres(clearHalfTwice),
            centerZ + DesignMeasure.FromTwiceMetres(clearHalfTwice));
        var exteriorBounds = new BarbettePlanBounds(
            centerX - DesignMeasure.FromTwiceMetres(exteriorHalfTwice),
            centerX + DesignMeasure.FromTwiceMetres(exteriorHalfTwice),
            centerZ - DesignMeasure.FromTwiceMetres(exteriorHalfTwice),
            centerZ + DesignMeasure.FromTwiceMetres(exteriorHalfTwice));

        if (!centerX.IsWholeMetre || !centerZ.IsWholeMetre)
            return new NeckPlan([], [], clearBounds, exteriorBounds);

        var cx = centerX.TwiceMetres / 2;
        var cz = centerZ.TwiceMetres / 2;
        var clearHalf = (size - 1) / 2;
        var clearCells = new List<BarbettePlanCell>(checked(size * size));
        for (var z = cz - clearHalf; z <= cz + clearHalf; z++)
            for (var x = cx - clearHalf; x <= cx + clearHalf; x++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                clearCells.Add(new BarbettePlanCell(x, z));
            }

        var armorRings = new List<List<BarbettePlanCell>>();
        for (var layer = 0; layer < thickness; layer++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var radius = checked(clearHalf + 1 + layer);
            // HF-06: walk the Chebyshev ring directly instead of scanning the square interior; the
            // helper emits exactly the same z-then-x order the old scan produced.
            var ring = new List<BarbettePlanCell>(checked(radius * 8));
            BarbettePlanPreflight.EnumerateSquarePerimeter(cx, cz, radius, ring.Add, cancellationToken);
            armorRings.Add(ring);
        }

        return new NeckPlan(clearCells, armorRings, clearBounds, exteriorBounds);
    }

    private static BarbetteMeasurement MeasurementOf(
        BarbetteDefinition definition,
        DesignMeasure centerX,
        DesignMeasure centerZ,
        BarbettePlan plan) => new(
        centerX,
        centerZ,
        definition.ClearDiameter,
        definition.ClearDepthMetres,
        definition.TopOffsetMetres,
        definition.NeckClearSizeMetres,
        plan.ClearBounds,
        plan.ExteriorBounds,
        plan.NeckClearBounds,
        plan.NeckExteriorBounds,
        ReadOnly(plan.ClearCells),
        ReadOnly(plan.SideRings.SelectMany(ring => ring)),
        ReadOnly(plan.NeckClearCells),
        ReadOnly(plan.NeckArmorRings.SelectMany(ring => ring)));

    private static void ValidateMeasurementInputs(
        BarbetteDefinition definition,
        DesignMeasure centerX,
        DesignMeasure centerZ,
        BarbetteGenerationLimits limits,
        ICollection<DesignDiagnostic> diagnostics)
    {
        if (limits.MaxPlanCells is < 1 or > HardMaxPlanCells ||
            limits.MaxOutputIntents is < 1 or > HardMaxOutputIntents)
        {
            diagnostics.Add(DesignDiagnostic.Error(
                BarbetteDiagnosticCodes.InvalidBudget,
                $"Barbette generation budgets must be positive and no greater than " +
                $"{HardMaxPlanCells:N0} plan cells / {HardMaxOutputIntents:N0} output intents.",
                definition.Id,
                nameof(BarbetteGenerationLimits)));
        }

        if (!centerX.IsWithinDesignBounds || !centerZ.IsWithinDesignBounds)
        {
            diagnostics.Add(new DesignDiagnostic(
                DesignDiagnosticCodes.MeasureOutOfRange,
                DesignSeverity.Error,
                $"Barbette '{definition.Id}' contains a center outside the supported design range.",
                definition.Id,
                SuggestedCorrection: "Reduce the coordinate magnitude before rasterization."));
        }

        var halfX = !centerX.IsWholeMetre;
        var halfZ = !centerZ.IsWholeMetre;
        if (halfX && halfZ)
        {
            diagnostics.Add(new DesignDiagnostic(
                BarbetteDiagnosticCodes.EvenWidthCenterline,
                DesignSeverity.Error,
                $"Barbette '{definition.Id}' has no real centre voxel column, so its concentric square " +
                "neck cannot be placed without shifting it.",
                definition.Id,
                nameof(centerX),
                centerX,
                null,
                "Return the hull to an odd width; even-width centerline intent is retained but invalid " +
                "rather than moved."));
        }
        else if (halfX || halfZ)
        {
            diagnostics.Add(new DesignDiagnostic(
                BarbetteDiagnosticCodes.CenterParityMismatch,
                DesignSeverity.Error,
                $"Barbette '{definition.Id}' mixes a {(halfX ? "half" : "whole")}-metre X center " +
                $"with a {(halfZ ? "half" : "whole")}-metre Z center.",
                definition.Id,
                nameof(centerZ),
                centerZ,
                centerX,
                "Move the longitudinal center by half a metre so X and Z center parity match."));
        }
    }

    private static bool CellInteriorIntersectsOpenDisk(
        int x,
        int z,
        int centerXTwice,
        int centerZTwice,
        int diameterTwice)
    {
        var dxToSquareTwice = Math.Max(Math.Abs(checked(2L * x - centerXTwice)) - 1L, 0L);
        var dzToSquareTwice = Math.Max(Math.Abs(checked(2L * z - centerZTwice)) - 1L, 0L);

        // Radius in doubled world coordinates is diameterTwice / 2. Multiplying the
        // squared-distance side by four keeps half-metre diameters exact and avoids division.
        var distanceSquared = checked(dxToSquareTwice * dxToSquareTwice + dzToSquareTwice * dzToSquareTwice);
        var diameterSquared = checked((long)diameterTwice * diameterTwice);
        return checked(4L * distanceSquared) < diameterSquared;
    }

    private static bool IsFaceConnected(HashSet<BarbettePlanCell> cells, CancellationToken cancellationToken)
    {
        var first = cells.OrderBy(cell => cell.Z).ThenBy(cell => cell.X).First();
        var reached = new HashSet<BarbettePlanCell> { first };
        var queue = new Queue<BarbettePlanCell>();
        queue.Enqueue(first);
        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cell = queue.Dequeue();
            foreach (var offset in FaceOffsets)
            {
                var neighbor = new BarbettePlanCell(cell.X + offset.X, cell.Z + offset.Z);
                if (cells.Contains(neighbor) && reached.Add(neighbor))
                    queue.Enqueue(neighbor);
            }
        }

        return reached.Count == cells.Count;
    }

    private static BarbettePlanCell[] Order(IEnumerable<BarbettePlanCell> cells) =>
        cells.OrderBy(cell => cell.Z).ThenBy(cell => cell.X).ToArray();

    private static BarbettePlanBounds BoundsOf(IEnumerable<BarbettePlanCell> cells)
    {
        var array = cells as BarbettePlanCell[] ?? cells.ToArray();
        var minX = array.Min(cell => cell.X);
        var maxX = array.Max(cell => cell.X);
        var minZ = array.Min(cell => cell.Z);
        var maxZ = array.Max(cell => cell.Z);
        return new BarbettePlanBounds(
            DesignMeasure.FromTwiceMetres(checked(2 * minX - 1)),
            DesignMeasure.FromTwiceMetres(checked(2 * maxX + 1)),
            DesignMeasure.FromTwiceMetres(checked(2 * minZ - 1)),
            DesignMeasure.FromTwiceMetres(checked(2 * maxZ + 1)));
    }

    private static int FloorHalf(int value)
    {
        var quotient = value / 2;
        return value < 0 && (value & 1) != 0 ? quotient - 1 : quotient;
    }

    private static BarbetteMeasurementResult MeasurementResult(
        BarbetteMeasurement? measurement,
        IEnumerable<DesignDiagnostic> diagnostics) =>
        new(measurement, ReadOnly(diagnostics));

    private static BarbetteGenerationResult Rejected(
        BarbetteMeasurement? measurement,
        BarbetteVerticalLayout? layout,
        IEnumerable<DesignDiagnostic> diagnostics) =>
        new(measurement, layout, [], [], [], ReadOnly(diagnostics));

    private static IReadOnlyList<T> ReadOnly<T>(IEnumerable<T> values) =>
        Array.AsReadOnly(values.ToArray());

    /// <summary>One resolved side-armor (or side-air) cell: its plan cell, height and layer index.</summary>
    private readonly record struct ResolvedSideCell(BarbettePlanCell Cell, int Y, int Layer);

    /// <summary>
    /// The per-cell/per-layer resolved side mask. <see cref="Cells"/> is the placement truth.
    /// <see cref="RealizedLayers"/> is the honest boundary-complete scalar summary: the mandatory
    /// innermost layer counts when every one of its positions is an emitted armor cell or a retained
    /// protected-skin substitution (so the repro reports one complete mandatory layer, not zero),
    /// while each optional outer layer counts only when every one of its positions is an emitted armor
    /// cell (so a locally truncated outer layer still reports a shortfall). <see cref="ArmorCompleteLayers"/>
    /// counts emitted-armor-complete layers and drives the mandatory bottom floor footprint.
    /// <see cref="MissingMandatory"/> is the first innermost-boundary position that could be neither
    /// barbette armor nor retained protected skin.
    /// </summary>
    private sealed record SideRealization(
        HashSet<ResolvedSideCell> Cells,
        (BarbettePlanCell Cell, int Y)? MissingMandatory,
        int RealizedLayers,
        int ArmorCompleteLayers);

    private sealed record NeckPlan(
        List<BarbettePlanCell> ClearCells,
        List<List<BarbettePlanCell>> ArmorRings,
        BarbettePlanBounds ClearBounds,
        BarbettePlanBounds ExteriorBounds);

    private sealed record BarbettePlan(
        List<BarbettePlanCell> ClearCells,
        List<List<BarbettePlanCell>> SideRings,
        List<BarbettePlanCell> ExteriorCells,
        List<BarbettePlanCell> NeckClearCells,
        List<List<BarbettePlanCell>> NeckArmorRings,
        BarbettePlanBounds ClearBounds,
        BarbettePlanBounds ExteriorBounds,
        BarbettePlanBounds NeckClearBounds,
        BarbettePlanBounds NeckExteriorBounds)
    {
        public HashSet<BarbettePlanCell> ClearCellSet { get; } = [.. ClearCells];
        public HashSet<BarbettePlanCell> NeckClearCellSet { get; } = [.. NeckClearCells];
        public HashSet<BarbettePlanCell> ExteriorCellSet { get; } = [.. ExteriorCells];
    }
}
