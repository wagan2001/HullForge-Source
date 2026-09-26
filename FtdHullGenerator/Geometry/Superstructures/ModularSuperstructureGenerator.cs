using System.Collections.Immutable;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Composition;
using FtdHullGenerator.Domain.Design;
using FtdHullGenerator.Domain.Layout;
using FtdHullGenerator.Domain.Superstructures;
using FtdHullGenerator.Geometry.Components;
using FtdHullGenerator.Geometry.Composition;

namespace FtdHullGenerator.Geometry.Superstructures;

/// <summary>
/// Pure layered-box planner. It unions each complete layer volume before extracting walls and
/// roofs, validates exact one-cell faces for support, and emits intent only. Packing, openings,
/// ownership reconciliation and catalog resolution belong to I01.
/// </summary>
public static class ModularSuperstructureGenerator
{
    private readonly record struct Rect2(int MinX, int MaxX, int MinZ, int MaxZ)
    {
        public IEnumerable<(int X, int Z)> Cells()
        {
            for (var z = MinZ; z <= MaxZ; z++)
            for (var x = MinX; x <= MaxX; x++)
                yield return (x, z);
        }
    }

    private sealed record ResolvedModule(
        SuperstructureBoxModule Module,
        Rect2 Local,
        Rect2 World,
        int BaseY,
        int TopY);

    private sealed record LayerBuild(
        SuperstructureLayer Layer,
        ImmutableArray<ResolvedModule> Modules,
        HashSet<HullCell> Volume,
        ImmutableArray<SuperstructureCellIntent> Shell,
        int BaseY,
        int TopY);

    private static readonly (int X, int Z)[] PlanDirections =
    [
        (-1, 0), (1, 0), (0, -1), (0, 1),
    ];

    private static readonly (int X, int Y, int Z)[] FaceDirections =
    [
        (-1, 0, 0), (1, 0, 0), (0, -1, 0),
        (0, 1, 0), (0, 0, -1), (0, 0, 1),
    ];

    public static SuperstructureFootprintMeasurement MeasureFootprint(
        SuperstructureLayout layout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        cancellationToken.ThrowIfCancellationRequested();

        var diagnostics = new List<DesignDiagnostic>();
        diagnostics.AddRange(layout.Validate());
        ValidateLevelSequence(layout, diagnostics);
        var rects = new List<Rect2>();
        foreach (var layer in StableLayers(layout))
        foreach (var module in StableModules(layer))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryResolveRect(module, DesignMeasure.Zero, DesignMeasure.Zero, false, diagnostics, out var rect))
                rects.Add(rect);
        }

        if (rects.Count == 0)
        {
            if (!diagnostics.Any(diagnostic => diagnostic.Code == SuperstructureDiagnosticCodes.LevelSequenceInvalid))
                diagnostics.Add(DesignDiagnostic.Error(SuperstructureDiagnosticCodes.LevelSequenceInvalid,
                    "An enabled modular superstructure needs at least one box module.", field: nameof(layout.Layers)));
            return new SuperstructureFootprintMeasurement(null, diagnostics.ToImmutableArray());
        }

        var footprint = FootprintOf(rects);
        return new SuperstructureFootprintMeasurement(
            diagnostics.HasErrors() ? null : footprint,
            diagnostics.ToImmutableArray());
    }

    public static SuperstructureGenerationResult Generate(
        HullBuildContext context,
        SuperstructureLayout layout,
        SuperstructureRootPlacement root,
        SuperstructureGenerationRoute route,
        IReadOnlyDictionary<string, ArrangementNodeKind> arrangementNodes,
        IEnumerable<RequiredVoidExclusion>? requiredVoids = null,
        SuperstructureGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(arrangementNodes);
        options ??= SuperstructureGenerationOptions.Default;
        cancellationToken.ThrowIfCancellationRequested();

        var diagnostics = new List<DesignDiagnostic>();
        diagnostics.AddRange(context.Diagnostics);
        diagnostics.AddRange(layout.Validate());
        ValidateOptions(options, diagnostics);
        if (layout.ModuleCount > options.MaxModules)
            diagnostics.Add(new DesignDiagnostic(SuperstructureDiagnosticCodes.CandidateBudgetExceeded,
                DesignSeverity.Error,
                $"The layout declares {layout.ModuleCount} modules; the configured cap is {options.MaxModules}.",
                Field: nameof(SuperstructureGenerationOptions.MaxModules),
                SuggestedCorrection: "Reduce module count or use the supported document cap."));

        if (!Enum.IsDefined(route))
            diagnostics.Add(DesignDiagnostic.Error(SuperstructureDiagnosticCodes.UnsupportedRoute,
                $"The superstructure generation route '{route}' is unsupported.", field: nameof(route)));
        else if (route == SuperstructureGenerationRoute.LegacyVersion1)
            diagnostics.Add(new DesignDiagnostic(SuperstructureDiagnosticCodes.LegacyRouteRequired,
                DesignSeverity.Error,
                "Legacy superstructure intent must use the unchanged version-1 generator; the modular engine will not reinterpret it.",
                Field: nameof(route),
                SuggestedCorrection: "Dispatch this request to SuperstructureGenerator, or explicitly convert a copy to modules."));
        else if (layout.Legacy?.UseLegacyGenerator == true)
            diagnostics.Add(new DesignDiagnostic(SuperstructureDiagnosticCodes.ModularLegacyConflict,
                DesignSeverity.Error,
                "The document still selects legacy generation and cannot also run its module graph.",
                Field: nameof(layout.Legacy),
                SuggestedCorrection: "Keep the legacy route, or explicitly convert a copy and clear legacy generation."));

        if (!layout.Enabled)
            return Empty(diagnostics);

        ValidateRoot(layout, root, arrangementNodes, diagnostics);
        ValidateLevelSequence(layout, diagnostics);

        var voidMask = BuildRequiredVoidMask(requiredVoids, options, diagnostics, cancellationToken);
        var measurement = MeasureFootprint(layout, cancellationToken);
        foreach (var diagnostic in measurement.Diagnostics)
            if (!diagnostics.Contains(diagnostic))
                diagnostics.Add(diagnostic);

        if (diagnostics.HasErrors())
            return Empty(diagnostics, measurement.Footprint);

        var layers = new List<LayerBuild>();
        var estimated = EstimateVolume(layout, diagnostics);
        if (estimated > options.MaxVolumeCells)
        {
            diagnostics.Add(new DesignDiagnostic(SuperstructureDiagnosticCodes.CandidateBudgetExceeded,
                DesignSeverity.Error,
                $"The module volumes contain {estimated:N0} candidate cells; the configured cap is " +
                $"{options.MaxVolumeCells:N0}.",
                Field: nameof(SuperstructureGenerationOptions.MaxVolumeCells),
                SuggestedCorrection: "Reduce module dimensions, height or layer count."));
            return Empty(diagnostics, measurement.Footprint, estimated);
        }

        var previousShell = new HashSet<HullCell>();
        int? previousTop = null;
        foreach (var layer in StableLayers(layout))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var worldRects = new List<(SuperstructureBoxModule Module, Rect2 Local, Rect2 World)>();
            foreach (var module in StableModules(layer))
            {
                if (!TryResolveRect(module, root.WorldX, root.WorldZ, true, diagnostics, out var world))
                    continue;
                TryResolveRect(module, DesignMeasure.Zero, DesignMeasure.Zero, false, diagnostics, out var local);
                worldRects.Add((module, local, world));
            }

            if (diagnostics.HasErrors())
                return Empty(diagnostics, measurement.Footprint, estimated);

            var footprintCells = worldRects.SelectMany(item => item.World.Cells()).ToHashSet();
            if (!IsPlanConnected(footprintCells, cancellationToken))
            {
                diagnostics.Add(new DesignDiagnostic(SuperstructureDiagnosticCodes.DisconnectedUnion,
                    DesignSeverity.Error,
                    $"Layer '{layer.Id}' contains disconnected box volumes.", layer.Id, nameof(layer.Modules),
                    SuggestedCorrection: "Move or resize modules until their volumes overlap or share a full face.",
                    AffectedBounds: BoundsOf(footprintCells.Select(cell => new HullCell(cell.X, 0, cell.Z)))));
                return Empty(diagnostics, measurement.Footprint, estimated);
            }

            int baseY;
            if (previousTop is null)
            {
                var deckRows = footprintCells.Select(cell => context.DeckYAt(cell.Z)).Distinct().ToArray();
                if (deckRows.Length != 1 || deckRows[0] == int.MinValue)
                {
                    diagnostics.Add(new DesignDiagnostic(SuperstructureDiagnosticCodes.UnsupportedFootprint,
                        DesignSeverity.Error,
                        $"Layer '{layer.Id}' does not sit on one flat local deck elevation.", layer.Id,
                        nameof(root.WorldZ),
                        SuggestedCorrection: "Move the root onto a flat supported deck span or reduce the footprint."));
                    return Empty(diagnostics, measurement.Footprint, estimated);
                }
                baseY = checked(deckRows[0] + 1);
            }
            else
            {
                baseY = checked(previousTop.Value + 1);
            }

            var resolved = worldRects.Select(item => new ResolvedModule(item.Module, item.Local, item.World,
                    baseY, checked(baseY + item.Module.ClearHeightMetres + item.Module.RoofThicknessMetres - 1)))
                .ToImmutableArray();
            var built = BuildLayer(layer, resolved, diagnostics, cancellationToken);
            if (diagnostics.HasErrors())
                return Empty(diagnostics, measurement.Footprint, estimated);

            AddInteriorDiagnostics(built, diagnostics, cancellationToken);
            AddSupportDiagnostics(context, built, previousShell, previousTop is null, diagnostics, cancellationToken);
            AddCollisionDiagnostics(context, built, voidMask, diagnostics, cancellationToken);
            if (diagnostics.HasErrors())
                return Empty(diagnostics, measurement.Footprint, estimated);

            layers.Add(built);
            previousShell = built.Shell.Select(intent => intent.Cell).ToHashSet();
            previousTop = built.TopY;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var cells = layers.SelectMany(layer => layer.Shell)
            .OrderBy(intent => intent.Cell.Z)
            .ThenBy(intent => intent.Cell.Y)
            .ThenBy(intent => intent.Cell.X)
            .ToImmutableArray();
        var layerResults = layers.Select(layer => new SuperstructureLayerRealization(
                layer.Layer.Id, layer.Layer.Level, layer.BaseY, layer.TopY,
                layer.Volume.Count, layer.Shell.Length, BoundsOf(layer.Shell.Select(intent => intent.Cell))))
            .ToImmutableArray();
        return new SuperstructureGenerationResult(cells, layerResults, measurement.Footprint,
            diagnostics.ToImmutableArray(), estimated);
    }

    private static LayerBuild BuildLayer(
        SuperstructureLayer layer,
        ImmutableArray<ResolvedModule> modules,
        List<DesignDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var contributors = new Dictionary<HullCell, List<ResolvedModule>>();
        var conflictOverlaps = new Dictionary<(string Left, string Right), List<HullCell>>();
        foreach (var module in modules)
        {
            for (var y = module.BaseY; y <= module.TopY; y++)
            for (var z = module.World.MinZ; z <= module.World.MaxZ; z++)
            for (var x = module.World.MinX; x <= module.World.MaxX; x++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var cell = new HullCell(x, y, z);
                if (!contributors.TryGetValue(cell, out var owners))
                    contributors[cell] = owners = [];
                foreach (var owner in owners)
                {
                    if (owner.Module.Material == module.Module.Material)
                        continue;
                    var ids = new[] { owner.Module.Id, module.Module.Id }.Order(StringComparer.Ordinal).ToArray();
                    var key = (ids[0], ids[1]);
                    if (!conflictOverlaps.TryGetValue(key, out var overlap))
                        conflictOverlaps[key] = overlap = [];
                    overlap.Add(cell);
                }
                owners.Add(module);
            }
        }

        foreach (var pair in conflictOverlaps.OrderBy(pair => pair.Key.Left, StringComparer.Ordinal)
                     .ThenBy(pair => pair.Key.Right, StringComparer.Ordinal))
            diagnostics.Add(new DesignDiagnostic(SuperstructureDiagnosticCodes.MaterialConflict,
                DesignSeverity.Error,
                $"Modules '{pair.Key.Left}' and '{pair.Key.Right}' overlap with different materials.",
                pair.Key.Left, nameof(SuperstructureBoxModule.Material),
                SuggestedCorrection: "Use one material or define an explicit junction material before composition.",
                AffectedBounds: BoundsOf(pair.Value)));

        var volume = contributors.Keys.ToHashSet();
        var shell = new List<SuperstructureCellIntent>();
        foreach (var pair in contributors.OrderBy(pair => pair.Key.Z).ThenBy(pair => pair.Key.Y).ThenBy(pair => pair.Key.X))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var wallThickness = pair.Value.Max(module => module.Module.WallThicknessMetres);
            var roofThickness = pair.Value.Max(module => module.Module.RoofThicknessMetres);
            var wall = IsHorizontalBoundary(pair.Key, volume, wallThickness);
            var roof = IsRoofBoundary(pair.Key, volume, roofThickness);
            if (!wall && !roof)
                continue;

            var owners = pair.Value.Select(module => module.Module.Id).Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToImmutableArray();
            shell.Add(new SuperstructureCellIntent(pair.Key, pair.Value[0].Module.Material, owners,
                wall && roof ? SuperstructureCellRole.WallAndRoof : roof
                    ? SuperstructureCellRole.Roof : SuperstructureCellRole.Wall));
        }

        return new LayerBuild(layer, modules, volume, shell.ToImmutableArray(),
            modules.Min(module => module.BaseY), modules.Max(module => module.TopY));
    }

    /// <summary>
    /// First-pass interior policy: after union and shell extraction, each layer must retain one
    /// non-empty, face-connected intended-air component. A corner-overlap volume can be connected
    /// while leaving sealed rooms; validating the surviving air rather than the input boxes catches
    /// that case without inventing doorways or deleting walls.
    /// </summary>
    private static void AddInteriorDiagnostics(
        LayerBuild layer,
        List<DesignDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var shell = layer.Shell.Select(intent => intent.Cell).ToHashSet();
        var interior = layer.Volume.Where(cell => !shell.Contains(cell)).ToHashSet();
        if (interior.Count == 0)
        {
            diagnostics.Add(new DesignDiagnostic(SuperstructureDiagnosticCodes.HollowInteriorUnavailable,
                DesignSeverity.Error,
                $"Layer '{layer.Layer.Id}' retains no intended interior air after union shelling.",
                layer.Layer.Id, nameof(layer.Layer.Modules),
                SuggestedCorrection: "Increase the union footprint or reduce wall/roof thickness.",
                AffectedBounds: BoundsOf(layer.Volume)));
            return;
        }

        var seen = new HashSet<HullCell>();
        var queue = new Queue<HullCell>();
        queue.Enqueue(interior.OrderBy(cell => cell.Z).ThenBy(cell => cell.Y).ThenBy(cell => cell.X).First());
        while (queue.TryDequeue(out var cell))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!seen.Add(cell))
                continue;
            foreach (var (dx, dy, dz) in FaceDirections)
            {
                var neighbor = new HullCell(cell.X + dx, cell.Y + dy, cell.Z + dz);
                if (interior.Contains(neighbor) && !seen.Contains(neighbor))
                    queue.Enqueue(neighbor);
            }
        }

        if (seen.Count != interior.Count)
            diagnostics.Add(new DesignDiagnostic(SuperstructureDiagnosticCodes.InteriorDisconnected,
                DesignSeverity.Error,
                $"Layer '{layer.Layer.Id}' leaves {interior.Count - seen.Count} intended interior-air " +
                "cell(s) sealed away from the primary face-connected interior.",
                layer.Layer.Id, nameof(layer.Layer.Modules),
                SuggestedCorrection: "Increase the shared opening between modules or model an explicit partition later.",
                AffectedBounds: BoundsOf(interior)));
    }

    private static bool IsHorizontalBoundary(HullCell cell, HashSet<HullCell> volume, int thickness)
    {
        foreach (var (dx, dz) in PlanDirections)
        for (var distance = 1; distance <= thickness; distance++)
            if (!volume.Contains(new HullCell(cell.X + dx * distance, cell.Y, cell.Z + dz * distance)))
                return true;
        return false;
    }

    private static bool IsRoofBoundary(HullCell cell, HashSet<HullCell> volume, int thickness)
    {
        for (var distance = 1; distance <= thickness; distance++)
            if (!volume.Contains(new HullCell(cell.X, cell.Y + distance, cell.Z)))
                return true;
        return false;
    }

    private static void AddSupportDiagnostics(
        HullBuildContext context,
        LayerBuild layer,
        HashSet<HullCell> previousShell,
        bool hullDeck,
        List<DesignDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var unsupported = new List<HullCell>();
        var unverifiedNative = new List<HullCell>();
        foreach (var (x, z) in layer.Modules.SelectMany(module => module.World.Cells()).Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var below = new HullCell(x, layer.BaseY - 1, z);
            var supported = !hullDeck && previousShell.Contains(below);
            var isUnverifiedNative = false;
            if (hullDeck && context.IsOccupied(below.X, below.Y, below.Z) &&
                context.TryGetArmor(below.X, below.Y, below.Z, out var armor) &&
                armor.Role == HullCellRole.DeckArmor && !context.IsAuthorizedDeckOpening(below))
            {
                supported = armor.Construction == ArmorConstruction.Solid;
                if (!supported)
                {
                    isUnverifiedNative = true;
                    unverifiedNative.Add(below);
                }
            }
            if (!supported && !isUnverifiedNative)
                unsupported.Add(below);
        }

        if (unsupported.Count > 0)
            diagnostics.Add(new DesignDiagnostic(SuperstructureDiagnosticCodes.UnsupportedFootprint,
                DesignSeverity.Error,
                $"Layer '{layer.Layer.Id}' has {unsupported.Count} floor cell(s) without an actual supporting top face.",
                layer.Layer.Id, nameof(layer.Layer.Modules),
                SuggestedCorrection: "Move or resize the layer onto real deck/roof cells; bounding-box overlap is not support.",
                AffectedBounds: BoundsOf(unsupported)));
        if (unverifiedNative.Count > 0)
            diagnostics.Add(new DesignDiagnostic(SuperstructureDiagnosticCodes.NativeSupportUnverified,
                DesignSeverity.Error,
                $"Layer '{layer.Layer.Id}' rests on {unverifiedNative.Count} reduced-face native armor member(s) " +
                "whose positive-area top contact is not available before materialization.",
                layer.Layer.Id, nameof(layer.Layer.Modules),
                SuggestedCorrection: "Use full-volume deck construction or provide resolved native face evidence during I01 composition.",
                AffectedBounds: BoundsOf(unverifiedNative)));
    }

    private static void AddCollisionDiagnostics(
        HullBuildContext context,
        LayerBuild layer,
        HashSet<HullCell> requiredVoids,
        List<DesignDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var cavity = new List<HullCell>();
        var armor = new List<HullCell>();
        var voids = new List<HullCell>();
        foreach (var intent in layer.Shell)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (requiredVoids.Contains(intent.Cell))
                voids.Add(intent.Cell);
            var role = context.RoleAt(intent.Cell.X, intent.Cell.Y, intent.Cell.Z);
            if (role == HullCellRole.Cavity)
                cavity.Add(intent.Cell);
            else if (role != HullCellRole.Outside)
                armor.Add(intent.Cell);
        }

        if (cavity.Count > 0)
            diagnostics.Add(new DesignDiagnostic(SuperstructureDiagnosticCodes.HullCavityCollision,
                DesignSeverity.Error,
                $"Layer '{layer.Layer.Id}' would place {cavity.Count} structure cell(s) inside the hull cavity.",
                layer.Layer.Id, nameof(layer.Layer.Modules),
                SuggestedCorrection: "Raise or move the root to the local supporting deck.",
                AffectedBounds: BoundsOf(cavity)));
        if (armor.Count > 0)
            diagnostics.Add(new DesignDiagnostic(SuperstructureDiagnosticCodes.HullArmorCollision,
                DesignSeverity.Error,
                $"Layer '{layer.Layer.Id}' would overwrite {armor.Count} hull armor or reserved-air cell(s).",
                layer.Layer.Id, nameof(layer.Layer.Modules),
                SuggestedCorrection: "Move or resize the module; superstructure cells cannot own hull armor.",
                AffectedBounds: BoundsOf(armor)));
        if (voids.Count > 0)
            diagnostics.Add(new DesignDiagnostic(SuperstructureDiagnosticCodes.RequiredVoidCollision,
                DesignSeverity.Error,
                $"Layer '{layer.Layer.Id}' would seal or enter {voids.Count} required well/void cell(s).",
                layer.Layer.Id, nameof(requiredVoids),
                SuggestedCorrection: "Move the module or reshape it so every required well remains clear.",
                AffectedBounds: BoundsOf(voids)));
    }

    private static HashSet<HullCell> BuildRequiredVoidMask(
        IEnumerable<RequiredVoidExclusion>? requiredVoids,
        SuperstructureGenerationOptions options,
        List<DesignDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var cells = new HashSet<HullCell>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        long visited = 0;
        foreach (var exclusion in requiredVoids ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (exclusion is null)
            {
                diagnostics.Add(DesignDiagnostic.Error(SuperstructureDiagnosticCodes.RequiredVoidInvalid,
                    "A required well/void entry is null.", field: nameof(requiredVoids)));
                continue;
            }
            foreach (var diagnostic in Arrangement.RequireIdentifier(exclusion.Id, "required well/void", exclusion.Id))
                diagnostics.Add(diagnostic with { Code = SuperstructureDiagnosticCodes.RequiredVoidInvalid });
            if (!ids.Add(exclusion.Id))
                diagnostics.Add(DesignDiagnostic.Error(SuperstructureDiagnosticCodes.RequiredVoidInvalid,
                    $"Two required wells/voids share the id '{exclusion.Id}'.", exclusion.Id));
            if (exclusion.Cells.IsDefault)
            {
                diagnostics.Add(DesignDiagnostic.Error(SuperstructureDiagnosticCodes.RequiredVoidInvalid,
                    $"Required well/void '{exclusion.Id}' has an uninitialized cell collection.",
                    exclusion.Id, nameof(exclusion.Cells)));
                continue;
            }
            foreach (var cell in exclusion.Cells)
            {
                cancellationToken.ThrowIfCancellationRequested();
                visited++;
                cells.Add(cell);
                if (visited > options.MaxRequiredVoidCells || cells.Count > options.MaxRequiredVoidCells)
                {
                    diagnostics.Add(new DesignDiagnostic(SuperstructureDiagnosticCodes.RequiredVoidInvalid,
                        DesignSeverity.Error,
                        $"Required wells/voids exceed the {options.MaxRequiredVoidCells:N0}-cell cap.",
                        Field: nameof(SuperstructureGenerationOptions.MaxRequiredVoidCells)));
                    return cells;
                }
            }
        }
        return cells;
    }

    private static void ValidateRoot(
        SuperstructureLayout layout,
        SuperstructureRootPlacement root,
        IReadOnlyDictionary<string, ArrangementNodeKind> arrangementNodes,
        List<DesignDiagnostic> diagnostics)
    {
        foreach (var diagnostic in Arrangement.RequireIdentifier(root.Id, "superstructure root", root.Id))
            diagnostics.Add(diagnostic with { Code = SuperstructureDiagnosticCodes.ArrangementRootInvalid });
        foreach (var diagnostic in Arrangement.RequireIdentifier(root.SpanNodeId, "superstructure span reference", root.Id))
            diagnostics.Add(diagnostic with { Code = SuperstructureDiagnosticCodes.ArrangementRootInvalid });
        if (!root.WorldX.IsWithinDesignBounds || !root.WorldZ.IsWithinDesignBounds)
            diagnostics.Add(new DesignDiagnostic(SuperstructureDiagnosticCodes.ArrangementRootInvalid,
                DesignSeverity.Error, "The superstructure root lies outside supported design coordinates.",
                root.Id, nameof(root.WorldZ)));

        var definition = layout.TowerRoots.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, root.Id, StringComparison.Ordinal));
        if (definition is null || !string.Equals(definition.SpanNodeId, root.SpanNodeId, StringComparison.Ordinal))
            diagnostics.Add(new DesignDiagnostic(SuperstructureDiagnosticCodes.ArrangementRootInvalid,
                DesignSeverity.Error,
                $"Root '{root.Id}' is not declared as a child of arrangement span '{root.SpanNodeId}'.",
                root.Id, nameof(root.SpanNodeId),
                SuggestedCorrection: "Use the same stable root and span IDs in the arrangement and superstructure graph."));
        if (!arrangementNodes.TryGetValue(root.SpanNodeId, out var rootNodeKind) ||
            rootNodeKind != ArrangementNodeKind.SuperstructureSpan)
            diagnostics.Add(new DesignDiagnostic(SuperstructureDiagnosticCodes.ArrangementRootInvalid,
                DesignSeverity.Error,
                $"Root '{root.Id}' does not reference a known superstructure-span arrangement node " +
                $"('{root.SpanNodeId}').",
                root.Id, nameof(root.SpanNodeId),
                SuggestedCorrection: "Reference an ArrangementNodeKind.SuperstructureSpan node."));
        foreach (var declaredRoot in layout.TowerRoots)
            if (!arrangementNodes.TryGetValue(declaredRoot.SpanNodeId, out var declaredNodeKind) ||
                declaredNodeKind != ArrangementNodeKind.SuperstructureSpan)
                diagnostics.Add(new DesignDiagnostic(SuperstructureDiagnosticCodes.ArrangementRootInvalid,
                    DesignSeverity.Error,
                    $"Declared root '{declaredRoot.Id}' references no superstructure-span arrangement node " +
                    $"'{declaredRoot.SpanNodeId}'.",
                    declaredRoot.Id, nameof(declaredRoot.SpanNodeId)));
    }

    private static void ValidateLevelSequence(SuperstructureLayout layout, List<DesignDiagnostic> diagnostics)
    {
        if (!layout.Enabled)
            return;
        var levels = StableLayers(layout).Select(layer => layer.Level).ToArray();
        if (levels.Length == 0 || !levels.SequenceEqual(Enumerable.Range(1, levels.Length)))
            diagnostics.Add(new DesignDiagnostic(SuperstructureDiagnosticCodes.LevelSequenceInvalid,
                DesignSeverity.Error,
                "Modular layers must form one contiguous 1-based stack with no missing level.",
                Field: nameof(layout.Layers),
                SuggestedCorrection: "Renumber layers consecutively from 1 through 8."));
    }

    private static void ValidateOptions(
        SuperstructureGenerationOptions options,
        List<DesignDiagnostic> diagnostics)
    {
        if (options.MaxModules is < 1 or > DesignLimits.MaxSuperstructureBoxesPerDocument)
            diagnostics.Add(DesignDiagnostic.Error(SuperstructureDiagnosticCodes.CandidateBudgetExceeded,
                $"The module cap must be between 1 and {DesignLimits.MaxSuperstructureBoxesPerDocument}.",
                field: nameof(options.MaxModules)));
        if (options.MaxVolumeCells < 1)
            diagnostics.Add(DesignDiagnostic.Error(SuperstructureDiagnosticCodes.CandidateBudgetExceeded,
                "The volume-cell cap must be positive.", field: nameof(options.MaxVolumeCells)));
        if (options.MaxRequiredVoidCells < 0)
            diagnostics.Add(DesignDiagnostic.Error(SuperstructureDiagnosticCodes.RequiredVoidInvalid,
                "The required-void cap cannot be negative.", field: nameof(options.MaxRequiredVoidCells)));
    }

    private static long EstimateVolume(SuperstructureLayout layout, List<DesignDiagnostic> diagnostics)
    {
        long total = 0;
        try
        {
            foreach (var module in StableLayers(layout).SelectMany(StableModules))
            {
                if (!module.Length.IsWholeMetre || !module.Width.IsWholeMetre)
                    continue;
                total = checked(total + checked((long)module.Length.FloorMetres * module.Width.FloorMetres *
                    checked(module.ClearHeightMetres + module.RoofThicknessMetres)));
            }
        }
        catch (OverflowException)
        {
            diagnostics.Add(DesignDiagnostic.Error(SuperstructureDiagnosticCodes.CandidateBudgetExceeded,
                "The requested module volume overflows the supported generation budget."));
            return long.MaxValue;
        }
        return total;
    }

    private static bool TryResolveRect(
        SuperstructureBoxModule module,
        DesignMeasure rootX,
        DesignMeasure rootZ,
        bool world,
        List<DesignDiagnostic> diagnostics,
        out Rect2 rect)
    {
        rect = default;
        if (!module.Length.IsWholeMetre || !module.Width.IsWholeMetre)
        {
            diagnostics.Add(new DesignDiagnostic(SuperstructureDiagnosticCodes.LatticeUnrepresentable,
                DesignSeverity.Error,
                $"Module '{module.Id}' dimensions must be whole realized metres.", module.Id,
                nameof(module.Length), Requested: module.Length,
                SuggestedCorrection: "Choose whole-metre length and width values."));
            return false;
        }

        var length = module.Length.FloorMetres;
        var width = module.Width.FloorMetres;
        var minimumHollowDimension = (long)module.WallThicknessMetres * 2 + 1;
        if (length < minimumHollowDimension || width < minimumHollowDimension)
        {
            diagnostics.Add(new DesignDiagnostic(SuperstructureDiagnosticCodes.HollowInteriorUnavailable,
                DesignSeverity.Error,
                $"Module '{module.Id}' leaves no one-cell hollow interior at its requested wall thickness.",
                module.Id, nameof(module.WallThicknessMetres),
                Requested: DesignMeasure.FromMetres(module.WallThicknessMetres),
                SuggestedCorrection: "Increase length/width or reduce wall thickness while keeping at least one interior cell."));
            return false;
        }

        var centerX = checked(rootX.TwiceMetres + module.OffsetAthwartships.TwiceMetres);
        var centerZ = world
            ? checked(rootZ.TwiceMetres - module.OffsetAlong.TwiceMetres)
            : module.OffsetAlong.TwiceMetres;
        if (!TryFirstCell(centerX, width, out var minX) || !TryFirstCell(centerZ, length, out var minZ))
        {
            diagnostics.Add(new DesignDiagnostic(SuperstructureDiagnosticCodes.LatticeUnrepresentable,
                DesignSeverity.Error,
                $"Module '{module.Id}' cannot be centred on the requested lattice position.", module.Id,
                world ? nameof(module.OffsetAthwartships) : nameof(module.OffsetAlong),
                SuggestedCorrection: "Move the centre by half a metre or choose the opposite dimension parity."));
            return false;
        }

        rect = new Rect2(minX, checked(minX + width - 1), minZ, checked(minZ + length - 1));
        return true;
    }

    private static bool TryFirstCell(int centerTwice, int cells, out int first)
    {
        var numerator = checked(centerTwice - (cells - 1));
        if ((numerator & 1) != 0)
        {
            first = 0;
            return false;
        }
        first = numerator / 2;
        return true;
    }

    private static SuperstructureMeasuredFootprint FootprintOf(IEnumerable<Rect2> rects)
    {
        var all = rects.ToArray();
        var minX = all.Min(rect => rect.MinX);
        var maxX = all.Max(rect => rect.MaxX);
        var minAlong = all.Min(rect => rect.MinZ);
        var maxAlong = all.Max(rect => rect.MaxZ);
        var along = new DesignSpan(DesignMeasure.FromTwiceMetres(checked(minAlong * 2 - 1)),
            DesignMeasure.FromTwiceMetres(checked(maxAlong * 2 + 1)));
        var athwart = new DesignSpan(DesignMeasure.FromTwiceMetres(checked(minX * 2 - 1)),
            DesignMeasure.FromTwiceMetres(checked(maxX * 2 + 1)));
        return new SuperstructureMeasuredFootprint(along, athwart,
            DesignMeasure.FromTwiceMetres((along.Start.TwiceMetres + along.End.TwiceMetres) / 2),
            DesignMeasure.FromTwiceMetres((athwart.Start.TwiceMetres + athwart.End.TwiceMetres) / 2),
            along.Length.TryHalve()!.Value,
            athwart.Length.TryHalve()!.Value);
    }

    private static bool IsPlanConnected(HashSet<(int X, int Z)> cells, CancellationToken cancellationToken)
    {
        if (cells.Count == 0)
            return false;
        var seen = new HashSet<(int X, int Z)>();
        var queue = new Queue<(int X, int Z)>();
        queue.Enqueue(cells.OrderBy(cell => cell.Z).ThenBy(cell => cell.X).First());
        while (queue.TryDequeue(out var cell))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!seen.Add(cell))
                continue;
            foreach (var (dx, dz) in PlanDirections)
            {
                var neighbor = (cell.X + dx, cell.Z + dz);
                if (cells.Contains(neighbor) && !seen.Contains(neighbor))
                    queue.Enqueue(neighbor);
            }
        }
        return seen.Count == cells.Count;
    }

    private static DesignBounds? BoundsOf(IEnumerable<HullCell> cells)
    {
        var all = cells.ToArray();
        if (all.Length == 0)
            return null;
        return new DesignBounds(
            DesignMeasure.FromCellAnchor(all.Min(cell => cell.X)),
            DesignMeasure.FromCellAnchor(all.Max(cell => cell.X)),
            DesignMeasure.FromCellAnchor(all.Min(cell => cell.Y)),
            DesignMeasure.FromCellAnchor(all.Max(cell => cell.Y)),
            DesignMeasure.FromCellAnchor(all.Min(cell => cell.Z)),
            DesignMeasure.FromCellAnchor(all.Max(cell => cell.Z)));
    }

    private static IOrderedEnumerable<SuperstructureLayer> StableLayers(SuperstructureLayout layout) =>
        layout.Layers.OrderBy(layer => layer.Level).ThenBy(layer => layer.Id, StringComparer.Ordinal);

    private static IOrderedEnumerable<SuperstructureBoxModule> StableModules(SuperstructureLayer layer) =>
        layer.Modules.OrderBy(module => module.Id, StringComparer.Ordinal);

    private static SuperstructureGenerationResult Empty(
        IEnumerable<DesignDiagnostic> diagnostics,
        SuperstructureMeasuredFootprint? footprint = null,
        long estimate = 0) =>
        new([], [], footprint, diagnostics.ToImmutableArray(), estimate);
}
