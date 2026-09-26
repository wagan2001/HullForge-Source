using System.Collections.Immutable;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Composition;
using FtdHullGenerator.Domain.Design;
using FtdHullGenerator.Domain.Layout;
using FtdHullGenerator.Domain.Superstructures;
using FtdHullGenerator.Geometry;
using FtdHullGenerator.Geometry.Components;
using FtdHullGenerator.Geometry.Composition;
using FtdHullGenerator.Geometry.Superstructures;

/// <summary>
/// Independent, minimized S01 oracles. Expected masks/counts below are enumerated from the small
/// rectangles, not produced by another shell implementation.
/// </summary>
internal static class ModularSuperstructureTests
{
    private const string RootId = "root";
    private const string SpanId = "span";
    private static readonly IReadOnlyDictionary<string, ArrangementNodeKind> KnownSpans =
        new Dictionary<string, ArrangementNodeKind>(StringComparer.Ordinal)
    {
        [SpanId] = ArrangementNodeKind.SuperstructureSpan,
    };

    public static void Run()
    {
        VerifyOverlapUnionsBeforeShelling();
        VerifyAdjacentAndConcaveUnions();
        VerifySurvivingInteriorConnectivity();
        VerifyDisconnectedAndMixedMaterialFailures();
        VerifyActualFacesRatherThanBoundingBoxes();
        VerifyRequiredWellsAndHullCollisions();
        VerifyMeasuredArrangementFootprint();
        VerifyPagodaTemplateAndCompatibilityDiscriminator();
        VerifyOneAndEightLevelsAndLocalDeck();
        VerifyIdentifiersBudgetsAndCancellation();

        Console.WriteLine(
            "Modular superstructure: independent overlap/adjacency/concavity shells, real-face support, " +
            "floating/mixed-material/well/hull conflicts, measured footprint, pagoda 1/8 levels, local deck, " +
            "legacy discrimination, IDs, budgets and cancellation passed.");
    }

    private static void VerifyOverlapUnionsBeforeShelling()
    {
        var layer = new SuperstructureLayer("layer-1", 1,
        [
            Box("left", along: 0, athwart: -1, length: 5, width: 5, clear: 1),
            Box("right", along: 0, athwart: 1, length: 5, width: 5, clear: 1),
        ]);
        var result = Generate(Layout(layer));
        RequireValid(result, "overlapping boxes");

        // Union footprint is the independently enumerated 7 x 5 rectangle. At wall height its
        // one-cell perimeter is 20; its complete 7 x 5 roof is 35. There is no doubled inner wall.
        Require(result.Cells.Length == 55,
            $"The 7x5 overlap union must have 20 wall + 35 roof cells, not {result.Cells.Length}.");
        var y = result.Layers.Single().BaseY;
        Require(!Has(result, 0, y, 0), "The overlap union's central lower cell must remain hollow.");
        Require(Has(result, 0, y + 1, 0), "The overlap union must retain its central roof cell.");
        var sharedRoof = result.Cells.Single(cell => cell.Cell == new HullCell(0, y + 1, 0));
        Require(sharedRoof.ModuleIds.SequenceEqual(new[] { "left", "right" }),
            "A shared union cell must retain both stable owners in deterministic order.");

        var reordered = Generate(Layout(layer with { Modules = layer.Modules.Reverse().ToImmutableArray() }));
        Require(reordered.Cells.Select(CellKey).SequenceEqual(result.Cells.Select(CellKey)) &&
                reordered.Layers.SequenceEqual(result.Layers),
            "Module input order must not change union cells, owners, roles or layer realization.");
    }

    private static void VerifyAdjacentAndConcaveUnions()
    {
        var adjacent = Generate(Layout(new SuperstructureLayer("layer-1", 1,
        [
            Box("a", 0, -2, 3, 3, 1), // x -3..-1
            Box("b", 0, 1, 3, 3, 1),  // x  0.. 2, sharing a full face
        ])));
        RequireValid(adjacent, "adjacent boxes");
        Require(adjacent.Cells.Length == 32,
            $"The adjacent 6x3 union must have 14 wall + 18 roof cells, not {adjacent.Cells.Length}.");
        var adjacentY = adjacent.Layers.Single().BaseY;
        Require(!Has(adjacent, -1, adjacentY, 0) && !Has(adjacent, 0, adjacentY, 0),
            "The shared face between adjacent boxes must not survive as an internal wall.");

        // Two rectangles form a concave T: 5x3 plus 3x5 with a 3x3 overlap.
        var concave = Generate(Layout(new SuperstructureLayer("layer-1", 1,
        [
            Box("cross", 0, 0, 3, 5, 1),
            Box("stem", 0, -1, 5, 3, 1),
        ])));
        RequireValid(concave, "concave union");
        var y = concave.Layers.Single().BaseY;
        Require(!Has(concave, -1, y, 0), "A fully surrounded concave-union cell must remain interior air.");
        Require(Has(concave, 0, y, 2), "The exposed end of the concave stem must be a wall.");
        Require(!Has(concave, 1, y + 1, 2), "A roof cannot exist outside the concave union volume.");
        Require(Has(concave, 0, y + 1, 2), "Every occupied plan cell needs a roof in the first-pass policy.");
    }

    private static void VerifyDisconnectedAndMixedMaterialFailures()
    {
        var disconnected = Generate(Layout(new SuperstructureLayer("layer-1", 1,
        [
            Box("port", 0, -4, 3, 3, 1),
            Box("starboard", 0, 4, 3, 3, 1),
        ])));
        RequireError(disconnected, SuperstructureDiagnosticCodes.DisconnectedUnion);
        Require(disconnected.Cells.IsEmpty, "A disconnected layer must expose no partial cell intent.");

        var mixed = Generate(Layout(new SuperstructureLayer("layer-1", 1,
        [
            Box("metal", 0, -1, 5, 5, 1),
            Box("wood", 0, 1, 5, 5, 1, MaterialKind.Wood),
        ])));
        RequireError(mixed, SuperstructureDiagnosticCodes.MaterialConflict);
        Require(mixed.Cells.IsEmpty, "A mixed-material overlap must expose no partial cell intent.");
        var overlapBounds = mixed.Diagnostics.Single(d => d.Code == SuperstructureDiagnosticCodes.MaterialConflict)
            .AffectedBounds;
        var deckY = HullGenerator.CreateContext(HullParameters.Default).DeckYAt(0);
        Require(overlapBounds is not null &&
                overlapBounds.MinX.Metres == -1 && overlapBounds.MaxX.Metres == 1 &&
                overlapBounds.MinZ.Metres == -2 && overlapBounds.MaxZ.Metres == 2 &&
                overlapBounds.MinY.Metres == deckY + 1 && overlapBounds.MaxY.Metres == deckY + 2,
            $"The complete 3x5x2 pair overlap bounds were not retained: {overlapBounds}.");
    }

    private static void VerifySurvivingInteriorConnectivity()
    {
        // Review regression: each 3x3 box has one intended-air cell. Their volumes meet only in
        // one corner cell, which survives as shell; union connectivity alone therefore hides two
        // sealed one-cell pockets. The post-shell air graph must reject this exact arrangement.
        var cornerBridge = Generate(Layout(new SuperstructureLayer("layer-1", 1,
        [
            Box("first", 0, 0, 3, 3, 1),
            Box("second", 2, 2, 3, 3, 1),
        ])));
        RequireError(cornerBridge, SuperstructureDiagnosticCodes.InteriorDisconnected);
        Require(cornerBridge.Cells.IsEmpty && cornerBridge.Layers.IsEmpty,
            "A corner-connected shell with sealed air pockets must fail atomically with zero intents.");
        var finding = cornerBridge.Diagnostics.Single(d =>
            d.Code == SuperstructureDiagnosticCodes.InteriorDisconnected);
        Require(finding.NodeId == "layer-1" && finding.AffectedBounds is not null,
            "The sealed-interior finding must identify its layer and complete intended-air bounds.");
    }

    private static void VerifyActualFacesRatherThanBoundingBoxes()
    {
        // A connected U-shaped first layer has a 9x7 bounding box but no cells in its 3-cell-wide
        // inner opening. The upper 3x3 box lies inside that bounding box and must still float.
        var lower = new SuperstructureLayer("layer-1", 1,
        [
            Box("left", 0, -3, 7, 3, 1),
            Box("right", 0, 3, 7, 3, 1),
            Box("base", 2, 0, 3, 9, 1),
        ]);
        var upper = new SuperstructureLayer("layer-2", 2,
        [
            Box("floating", -1, 0, 3, 3, 1),
        ]);
        var result = Generate(Layout(lower, upper));
        RequireError(result, SuperstructureDiagnosticCodes.UnsupportedFootprint);
        Require(result.Diagnostics.Single(d => d.Code == SuperstructureDiagnosticCodes.UnsupportedFootprint)
                .Message.Contains("actual supporting top face", StringComparison.Ordinal),
            "The floating diagnosis must identify missing real faces, not a bounding-box fit.");

        // A pole occupies the cell below, but its reduced top face is not established as a complete
        // floor-support face by the pre-materialization context. S01 rejects it conservatively.
        var reducedFaceDeck = ModularSuperstructureGenerator.Generate(
            CollisionContext(ArmorConstruction.Pole, includeCollisions: false),
            Layout(new SuperstructureLayer("layer-1", 1, [Box("box", 0, 0, 3, 3, 1)])),
            Root(), SuperstructureGenerationRoute.ModularLayersV2, KnownSpans);
        RequireError(reducedFaceDeck, SuperstructureDiagnosticCodes.NativeSupportUnverified);

        var ordinaryContext = HullGenerator.CreateContext(HullParameters.Default);
        var openedDeckCell = new HullCell(0, ordinaryContext.DeckYAt(0), 0);
        var openedContext = HullGenerator.CreateContext(HullParameters.Default,
            DeckOpeningMask.FromCells([openedDeckCell]));
        var overOpening = ModularSuperstructureGenerator.Generate(openedContext,
            Layout(new SuperstructureLayer("layer-1", 1, [Box("box", 0, 0, 3, 3, 1)])),
            Root(), SuperstructureGenerationRoute.ModularLayersV2, KnownSpans);
        RequireError(overOpening, SuperstructureDiagnosticCodes.UnsupportedFootprint);
    }

    private static void VerifyRequiredWellsAndHullCollisions()
    {
        var layout = Layout(new SuperstructureLayer("layer-1", 1,
        [
            Box("box", 0, 0, 3, 3, 1),
        ]));
        var baseline = Generate(layout);
        RequireValid(baseline, "well baseline");
        var roof = baseline.Cells.Single(cell => cell.Cell.X == 0 && cell.Cell.Z == 0 &&
            cell.Role == SuperstructureCellRole.Roof);
        var blocked = Generate(layout,
        [
            new RequiredVoidExclusion("turret-well", [roof.Cell]),
        ]);
        RequireError(blocked, SuperstructureDiagnosticCodes.RequiredVoidCollision);
        Require(blocked.Cells.IsEmpty, "A roof sealing a required well must expose no partial intent.");

        var fake = CollisionContext();
        var collision = ModularSuperstructureGenerator.Generate(fake, layout, Root(),
            SuperstructureGenerationRoute.ModularLayersV2, KnownSpans);
        RequireError(collision, SuperstructureDiagnosticCodes.HullCavityCollision);
        RequireError(collision, SuperstructureDiagnosticCodes.HullArmorCollision);
    }

    private static void VerifyMeasuredArrangementFootprint()
    {
        var layout = Layout(new SuperstructureLayer("layer-1", 1,
        [
            Box("main", 0, 0, 5, 3, 1),       // along edges -2.5..+2.5
            Box("aft", 3, 0, 3, 3, 1),        // along edges +1.5..+4.5
        ]));
        var measurement = ModularSuperstructureGenerator.MeasureFootprint(layout);
        Require(measurement.IsValid, $"The asymmetric measurement failed: {measurement.Diagnostics.Summary()}.");
        var footprint = measurement.Footprint!;
        Require(footprint.Along.Start.Metres == -2.5 && footprint.Along.End.Metres == 4.5,
            $"The hand-authored along edges must be [-2.5, 4.5], not {footprint.Along}.");
        Require(footprint.RealizedLength.Metres == 7 && footprint.CenterOffsetAlong.Metres == 1 &&
                footprint.LongitudinalHalfExtent.Metres == 3.5,
            "L01 output must retain the exact asymmetric centre offset and realized half extent.");
        Require(footprint.RealizedWidth.Metres == 3 && footprint.AthwartshipsHalfExtent.Metres == 1.5,
            "The measured width must include half-cell outer faces.");
    }

    private static void VerifyPagodaTemplateAndCompatibilityDiscriminator()
    {
        var template = PagodaTemplate.Create(3, MaterialKind.LightweightAlloy, SpanId, RootId);
        Require(template.Layout.Layers.Length == 3 && template.Layout.ModuleCount == 3,
            "A three-level pagoda template must produce three ordinary layers and boxes.");
        Require(template.Layout.Layers.All(layer => layer.Modules.All(module =>
                module.Material == MaterialKind.LightweightAlloy && module.ClearHeightMetres == 2)),
            "Pagoda template values must remain editable ordinary module data.");
        var edited = template.Layout with
        {
            Layers = template.Layout.Layers.SetItem(0, template.Layout.Layers[0] with
            {
                Modules = template.Layout.Layers[0].Modules.SetItem(0,
                    template.Layout.Layers[0].Modules[0] with { Width = DesignMeasure.FromMetres(13) }),
            }),
        };
        Require(template.OriginFor(template.Layout).DisplayName == "Pagoda" &&
                template.OriginFor(edited) == SuperstructureTemplateOrigin.Custom,
            "Editing an ordinary template box must change its origin label to Custom.");

        // A save/load adapter reconstructs every immutable array. Equal graph values must retain
        // their origin even though no collection shares the original backing storage.
        var reconstructed = new SuperstructureLayout(template.Layout.Enabled,
            template.Layout.Layers.Select(layer => new SuperstructureLayer(
                new string(layer.Id.ToCharArray()), layer.Level,
                layer.Modules.Select(module => module with
                {
                    Id = new string(module.Id.ToCharArray()),
                }).ToImmutableArray())).ToImmutableArray(),
            template.Layout.TowerRoots.Select(root => root with
            {
                Id = new string(root.Id.ToCharArray()),
                SpanNodeId = new string(root.SpanNodeId.ToCharArray()),
            }).ToImmutableArray(),
            template.Layout.Legacy is null
                ? null
                : new LegacySuperstructureSettings(template.Layout.Legacy.UseLegacyGenerator,
                    template.Layout.Legacy.Settings with { }));
        Require(reconstructed != template.Layout &&
                template.OriginFor(reconstructed).DisplayName == "Pagoda",
            "A structurally identical reconstructed graph must retain the Pagoda origin.");

        var legacy = ModularSuperstructureGenerator.Generate(HullGenerator.CreateContext(HullParameters.Default),
            template.Layout, Root(), SuperstructureGenerationRoute.LegacyVersion1, KnownSpans);
        RequireError(legacy, SuperstructureDiagnosticCodes.LegacyRouteRequired);
        Require(legacy.Cells.IsEmpty, "The modular engine must never silently replace legacy output.");

        var conflicting = template.Layout with
        {
            Legacy = LegacySuperstructureSettings.FromParameters(SuperstructureSettings.Default with
            {
                Enabled = true,
                Style = SuperstructureStyle.CenterIsland,
            }),
        };
        var conflict = Generate(conflicting);
        RequireError(conflict, SuperstructureDiagnosticCodes.ModularLegacyConflict);
        Require(conflict.Cells.IsEmpty,
            "A hidden legacy style must not become modular merely because a module graph is also present.");
    }

    private static void VerifyOneAndEightLevelsAndLocalDeck()
    {
        foreach (var levels in new[] { 1, 8 })
        {
            var template = PagodaTemplate.Create(levels, MaterialKind.Metal, SpanId, RootId);
            var pagodaResult = Generate(template.Layout);
            RequireValid(pagodaResult, $"pagoda {levels} levels");
            Require(pagodaResult.Layers.Length == levels,
                $"The {levels}-level pagoda realized {pagodaResult.Layers.Length} layer(s).");
            Require(pagodaResult.Layers.Select(layer => layer.BaseY).SequenceEqual(
                    pagodaResult.Layers.Select(layer => layer.BaseY).Order()),
                "Pagoda layer elevations must be monotonic.");
        }

        var raisedParameters = HullParameters.Default with
        {
            Length = 60,
            Width = 17,
            Height = 12,
            BowStyle = BowStyle.Axe,
            SternStyle = SternStyle.Counter,
        };
        var context = HullGenerator.CreateContext(raisedParameters);
        var rootZ = Enumerable.Range(context.MinZ + 1, context.MaxZ - context.MinZ - 1)
            .First(z => context.DeckYAt(z - 1) == context.DeckYAt(z) &&
                        context.DeckYAt(z) == context.DeckYAt(z + 1));
        var layout = Layout(new SuperstructureLayer("layer-1", 1,
        [
            Box("local", 0, 0, 3, 3, 1),
        ]));
        var result = ModularSuperstructureGenerator.Generate(context, layout,
            Root() with { WorldZ = DesignMeasure.FromCellAnchor(rootZ) },
            SuperstructureGenerationRoute.ModularLayersV2, KnownSpans);
        RequireValid(result, "raised local deck");
        Require(result.Layers.Single().BaseY == context.DeckYAt(rootZ) + 1,
            "The module base must use its local deck elevation, not the hull's global maximum.");
    }

    private static void VerifyIdentifiersBudgetsAndCancellation()
    {
        var duplicate = new SuperstructureLayout(true,
        [
            new SuperstructureLayer("layer-1", 1,
            [
                Box("same", 0, 0, 3, 3, 1),
                Box("same", 0, 0, 3, 3, 1),
            ]),
        ], [new SuperstructureTowerRoot(RootId, SpanId, DesignMeasure.Zero)], LegacySuperstructureSettings.None);
        RequireError(Generate(duplicate), DesignDiagnosticCodes.IdentifierDuplicate);

        var unknownSpan = ModularSuperstructureGenerator.Generate(HullGenerator.CreateContext(HullParameters.Default),
            Layout(new SuperstructureLayer("layer-1", 1, [Box("box", 0, 0, 3, 3, 1)])),
            Root(), SuperstructureGenerationRoute.ModularLayersV2,
            new Dictionary<string, ArrangementNodeKind>(StringComparer.Ordinal)
            {
                ["different"] = ArrangementNodeKind.SuperstructureSpan,
            });
        RequireError(unknownSpan, SuperstructureDiagnosticCodes.ArrangementRootInvalid);

        var wrongKind = ModularSuperstructureGenerator.Generate(HullGenerator.CreateContext(HullParameters.Default),
            Layout(new SuperstructureLayer("layer-1", 1, [Box("box", 0, 0, 3, 3, 1)])),
            Root(), SuperstructureGenerationRoute.ModularLayersV2,
            new Dictionary<string, ArrangementNodeKind>(StringComparer.Ordinal)
            {
                [SpanId] = ArrangementNodeKind.Barbette,
            });
        RequireError(wrongKind, SuperstructureDiagnosticCodes.ArrangementRootInvalid);

        var limited = Generate(Layout(new SuperstructureLayer("layer-1", 1,
            [Box("box", 0, 0, 5, 5, 1)])), options: new SuperstructureGenerationOptions
        {
            MaxVolumeCells = 10,
        });
        RequireError(limited, SuperstructureDiagnosticCodes.CandidateBudgetExceeded);
        Require(limited.Cells.IsEmpty, "A budget failure must expose no partial geometry.");

        var moduleLimited = Generate(duplicate, options: new SuperstructureGenerationOptions
        {
            MaxModules = 1,
        });
        RequireError(moduleLimited, SuperstructureDiagnosticCodes.CandidateBudgetExceeded);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var observed = false;
        try
        {
            _ = ModularSuperstructureGenerator.Generate(HullGenerator.CreateContext(HullParameters.Default),
                Layout(new SuperstructureLayer("layer-1", 1, [Box("box", 0, 0, 3, 3, 1)])),
                Root(), SuperstructureGenerationRoute.ModularLayersV2, KnownSpans,
                cancellationToken: cancelled.Token);
        }
        catch (OperationCanceledException)
        {
            observed = true;
        }
        Require(observed, "A pre-cancelled modular generation must throw without returning a partial result.");
    }

    private static SuperstructureGenerationResult Generate(
        SuperstructureLayout layout,
        IEnumerable<RequiredVoidExclusion>? requiredVoids = null,
        SuperstructureGenerationOptions? options = null) =>
        ModularSuperstructureGenerator.Generate(HullGenerator.CreateContext(HullParameters.Default),
            layout, Root(), SuperstructureGenerationRoute.ModularLayersV2, KnownSpans,
            requiredVoids, options);

    private static SuperstructureLayout Layout(params SuperstructureLayer[] layers) =>
        new(true, layers.ToImmutableArray(),
        [
            new SuperstructureTowerRoot(RootId, SpanId, DesignMeasure.Zero),
        ], LegacySuperstructureSettings.None);

    private static SuperstructureRootPlacement Root() =>
        new(RootId, SpanId, DesignMeasure.Zero, DesignMeasure.Zero);

    private static SuperstructureBoxModule Box(
        string id,
        int along,
        int athwart,
        int length,
        int width,
        int clear,
        MaterialKind material = MaterialKind.Metal) =>
        new(id, DesignMeasure.FromMetres(along), DesignMeasure.FromMetres(athwart),
            DesignMeasure.FromMetres(length), DesignMeasure.FromMetres(width), clear, 1, 1, material);

    private static HullBuildContext CollisionContext(
        ArmorConstruction deckConstruction = ArmorConstruction.Solid,
        bool includeCollisions = true)
    {
        var armor = new Dictionary<HullCell, HullCellIntent>();
        for (var z = -1; z <= 1; z++)
        for (var x = -1; x <= 1; x++)
        {
            var cell = new HullCell(x, 0, z);
            armor[cell] = new HullCellIntent(cell, HullCellRole.DeckArmor, MaterialKind.Metal,
                ArmorRegion.Deck, 0, deckConstruction);
        }
        var rogueArmor = includeCollisions ? new HullCell(-1, 1, 0) : new HullCell(99, 99, 99);
        if (includeCollisions)
            armor[rogueArmor] = new HullCellIntent(rogueArmor, HullCellRole.SideArmor, MaterialKind.Metal,
                ArmorRegion.Side, 0, ArmorConstruction.Solid);
        var cavity = includeCollisions ? new HullCell(1, 1, 0) : new HullCell(99, 99, 99);
        return new HullBuildContext(-2, 2, -1, 3, -2, 2,
            2,
            (x, y, z) => armor.ContainsKey(new HullCell(x, y, z)) || new HullCell(x, y, z) == cavity,
            (_, _, _) => false,
            _ => 0,
            _ => -1,
            armor,
            new HashSet<HullCell>(),
            DeckOpeningMask.None,
            [], [],
            _ => new GeneratedHull(HullParameters.Default, [], -2, 2, -1, 3, -2, 2));
    }

    private static bool Has(SuperstructureGenerationResult result, int x, int y, int z) =>
        result.Cells.Any(intent => intent.Cell == new HullCell(x, y, z));

    private static string CellKey(SuperstructureCellIntent intent) =>
        $"{intent.Cell.X},{intent.Cell.Y},{intent.Cell.Z}:{intent.Material}:{intent.Role}:" +
        string.Join(",", intent.ModuleIds);

    private static void RequireValid(SuperstructureGenerationResult result, string scenario) =>
        Require(result.IsValid,
            $"{scenario} must be valid: {string.Join(" | ", result.Diagnostics.Select(d => d.ToString()))}");

    private static void RequireError(SuperstructureGenerationResult result, string code) =>
        Require(result.Diagnostics.Any(diagnostic => diagnostic.IsError && diagnostic.Code == code),
            $"Expected {code}, found {string.Join(", ", result.Diagnostics.Select(d => d.Code))}.");

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
