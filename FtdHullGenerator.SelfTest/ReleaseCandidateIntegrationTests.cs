using System.Collections.Immutable;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using FtdHullGenerator;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Components;
using FtdHullGenerator.Domain.Composition;
using FtdHullGenerator.Domain.Design;
using FtdHullGenerator.Domain.Historical;
using FtdHullGenerator.Domain.Layout;
using FtdHullGenerator.Domain.Projects;
using FtdHullGenerator.Geometry;
using FtdHullGenerator.Geometry.Components;
using FtdHullGenerator.Geometry.Composition;
using FtdHullGenerator.Infrastructure;
using FtdHullGenerator.Serialization;
using FtdHullGenerator.UI.Components;
using FtdHullGenerator.UI.Workspace;

/// <summary>
/// REL01 integrated release-candidate checks: the frozen 2.0 workflow as one coherent product
/// (historical start, edited Shape V2, layered armor, several independently armored centerline
/// barbettes, the ruler readouts, Deco V/H and the resolved native export) plus the two
/// cross-feature seams the individual packets left open — the arrangement bow datum the editor
/// must derive from the evaluated hull, and the live preview dispatch that must route a barbette
/// document through the resolved composition service. INT01 internals are exercised against the
/// barbette reservations in the same document.
/// </summary>
internal static class ReleaseCandidateIntegrationTests
{
    public static void Run(HullGenerator generator, FtdBlockCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(catalog);

        VerifyDeckRulerAuthority();
        VerifyIntegratedFrozenScenario(generator, catalog);
        VerifyExperimentalInternalsRouteAroundBarbettes(catalog);
        VerifyLiveEditorRoutesBarbettesThroughResolvedComposition(catalog);
        VerifyInternalDraftLivePreviewLifecycle(catalog);

        Console.WriteLine(
            "Release candidate: historical start, edited Shape V2, layered armor, three independently " +
            "armored centerline barbettes with a floating case, ruler margins/clear gaps/overlap warning, " +
            "Deco V/H native parity, INT01 barbette routing and resolved native export passed.");
    }

    /// <summary>
    /// The evaluated structural-deck interval is the single ruler authority: a default hull's
    /// forward deck face is 40.5 m and the continuous supported interval is 91 m.
    /// </summary>
    private static void VerifyDeckRulerAuthority()
    {
        var ruler = ShipGenerationService.ResolveDeckRuler(HullParameters.Default);
        Require(ruler is not null, "The default hull has no measurable structural-deck ruler interval.");
        Require(ruler!.BowDatum == DesignMeasure.FromMetres(40.5),
            $"The default hull's forward deck face measured {ruler.BowDatum.Metres:0.##} m, not 40.5 m.");
        Require(ruler.SupportedEnd == DesignMeasure.FromMetres(91),
            $"The default hull's supported deck interval measured {ruler.SupportedEnd.Metres:0.##} m, not 91 m.");

        // A deckless hull has no structural-deck interval, so the ruler must report no measurement
        // instead of inventing a bow datum from the hull origin.
        Require(ShipGenerationService.ResolveDeckRuler(HullParameters.Default with { DeckArmor = null }) is null,
            "A deckless hull reported a structural-deck ruler interval.");
    }

    private static void VerifyIntegratedFrozenScenario(HullGenerator generator, FtdBlockCatalog catalog)
    {
        // 1. Start from a historical hull, then substantially change every Shape V2 group so the
        //    result is an ordinary edited hull rather than the untouched preset.
        var preset = HistoricalPresetCatalog.Find("de-bismarck-bb")
            ?? throw new InvalidOperationException("The Bismarck historical preset is missing.");
        var baseline = preset.ToParameters(HullParameters.Default);
        var shape = baseline.EffectiveShape;
        var parameters = baseline with
        {
            HullArmor = new ArmorLayout([MaterialKind.Metal, MaterialKind.HeavyArmor, MaterialKind.Rubber]),
            BottomArmor = new ArmorLayout([MaterialKind.Metal, MaterialKind.HeavyArmor]),
            DeckArmor = new ArmorLayout([MaterialKind.Metal, MaterialKind.Wood, MaterialKind.Lead]),
            Smoothing = SmoothingMethod.DecoVertical,
            Beamify = true,
            Shape = shape with
            {
                Bow = shape.Bow with { Fullness = 0.62, Flare = 0.48, EntranceLengthPercent = 58 },
                Body = shape.Body with
                {
                    Style = BodyStyle.Custom,
                    Fullness = 0.20,
                    SideShape = 0.42,
                    Chine = 0.15,
                    FlatBottom = 0,
                },
                Stern = shape.Stern with { Fullness = -0.35, SideShape = 0.22, RunLengthPercent = 38 },
                Profile = new HullProfileSettings(2, 1, 1, 0),
            },
        };
        Require(parameters.Validate().Count == 0,
            "The edited historical candidate failed parameter validation: " +
            string.Join("; ", parameters.Validate()));
        Require(parameters.EffectiveShape != preset.Shape && parameters.Length == preset.Length,
            "The candidate did not keep the historical envelope while substantially changing Shape V2.");
        Require(HullGeometryValidator.Validate(generator.Generate(parameters)).Count == 0,
            "The edited historical candidate failed ordinary hull geometry validation.");

        // 2. The evaluated ruler defines the coordinate frame; ruler zero is the forward deck face.
        var ruler = ShipGenerationService.ResolveDeckRuler(parameters)
            ?? throw new InvalidOperationException("The edited historical candidate has no deck ruler interval.");
        Require(ruler.SupportedEnd > DesignMeasure.FromMetres(100),
            $"The {parameters.Length} m candidate reported an implausible {ruler.SupportedEnd.Metres:0.#} m ruler.");

        // 3. Three centerline barbettes with independent armor stacks; the middle one is lowered
        //    below the ship-wide reference deck plane (a floating case).
        var fore = BarbetteDefinition.Create("barbette-fore", "barbette-fore-node",
            DesignMeasure.FromMetres(7), 4, neckClearSizeMetres: 3,
            sideArmor: Armor(MaterialKind.Metal, MaterialKind.Metal),
            roofArmor: Armor(MaterialKind.Metal),
            bottomArmor: Armor(MaterialKind.Metal, MaterialKind.HeavyArmor),
            neckArmor: Armor(MaterialKind.Metal));
        var mid = BarbetteDefinition.Create("barbette-mid", "barbette-mid-node",
            DesignMeasure.FromMetres(9), 5, topOffsetMetres: 2, neckClearSizeMetres: 5,
            sideArmor: Armor(MaterialKind.HeavyArmor, MaterialKind.Metal),
            roofArmor: Armor(MaterialKind.HeavyArmor, MaterialKind.Metal),
            bottomArmor: Armor(MaterialKind.Metal),
            neckArmor: Armor(MaterialKind.Metal, MaterialKind.Metal));
        var aft = BarbetteDefinition.Create("barbette-aft", "barbette-aft-node",
            DesignMeasure.FromMetres(7), 4, neckClearSizeMetres: 1,
            sideArmor: Armor(MaterialKind.Metal),
            roofArmor: Armor(MaterialKind.Metal),
            bottomArmor: Armor(MaterialKind.Metal),
            neckArmor: Armor(MaterialKind.Metal));
        var placements = new[]
        {
            (Definition: fore, Center: DesignMeasure.FromMetres(60.5)),
            (Definition: mid, Center: DesignMeasure.FromMetres(90.5)),
            (Definition: aft, Center: DesignMeasure.FromMetres(120.5)),
        };
        Require(placements.Select(item => item.Definition.SideArmor.Thickness).Distinct().Count() > 1 &&
                placements.Select(item => item.Definition.NeckClearSizeMetres).Distinct().Count() == 3,
            "The three barbettes do not carry independent armor/neck settings.");

        // 4. The ruler readouts: exact end margins, protected-clear-volume gaps, and an explicit
        //    close/overlap warning when one barbette is dragged onto another.
        var requests = placements.Select(item =>
            new BarbetteRulerRequest(item.Definition.Id, item.Definition.NodeId, item.Definition, item.Center)).ToArray();
        var rulerModel = BarbetteRulerModel.Build(requests, ruler.SupportedEnd, ruler.BowDatum, DesignMeasure.Zero);
        Require(rulerModel.BowMargin == DesignMeasure.FromMetres(60.5),
            "The foremost barbette did not report its centre-to-bow margin.");
        Require(rulerModel.SternMargin == ruler.SupportedEnd - DesignMeasure.FromMetres(120.5),
            "The rearmost barbette did not report its centre-to-stern margin.");
        Require(rulerModel.Segments.Count == 2 &&
                rulerModel.Segments.All(segment => segment.State == BarbetteRulerSegmentState.Valid),
            "The spaced candidate did not report two valid protected-clear-volume gaps.");

        var overlapping = requests.Select(request => request.BarbetteId == mid.Id
                ? request with { RulerCenter = DesignMeasure.FromMetres(64.5) }
                : request).ToArray();
        var overlapModel = BarbetteRulerModel.Build(overlapping, ruler.SupportedEnd, ruler.BowDatum, DesignMeasure.Zero);
        Require(overlapModel.Segments[0].State == BarbetteRulerSegmentState.Overlapping &&
                overlapModel.Diagnostics.Any(diagnostic =>
                    diagnostic.Code == BarbetteDiagnosticCodes.PlacementClearVolumeOverlap &&
                    diagnostic.Severity == DesignSeverity.Error),
            "A dragged-on-top barbette did not raise the blocking overlap warning.");
        Require(overlapModel.FindEntry(mid.Id)!.RulerCenter == DesignMeasure.FromMetres(64.5),
            "The invalid overlap silently moved the requested barbette position.");

        // 5. One resolved document, then native export from that same resolved result.
        var document = BuildDocument("REL01 candidate", parameters, ruler, placements);
        var resolved = new ShipGenerationService().Generate(document, 1, catalog);
        Require(resolved.IsValid, "The integrated candidate did not resolve: " + Describe(resolved.Diagnostics));
        var snapshot = resolved.Snapshot!;
        Require(snapshot.Barbettes.Length == 3 && snapshot.Arrangement.Nodes.Count == 3,
            "The resolved candidate lost one of its three centerline barbettes.");

        var occupied = snapshot.Hull.Blocks.SelectMany(block => block.OccupiedCells)
            .Select(cell => new HullCell(cell.X, cell.Y, cell.Z)).ToHashSet();
        var barbetteSolids = snapshot.Barbettes
            .SelectMany(item => item.Solids.Select(solid => solid.Cell)).ToHashSet();
        var barbetteVoids = snapshot.Barbettes
            .SelectMany(item => item.RequiredVoids.Select(voidIntent => voidIntent.Cell)).ToHashSet();
        Require(barbetteSolids.Count > 0 && barbetteSolids.IsSubsetOf(occupied),
            "The resolved candidate did not materialize every barbette armor cell.");
        Require(!barbetteVoids.Overlaps(occupied),
            "The resolved candidate materialized a protected barbette clear-volume cell.");
        Require(snapshot.Hull.CellProvenance.Where(item => barbetteSolids.Contains(item.Cell))
                .All(item => item.Owners.Any(owner => owner.Role == PhysicalCellRole.Barbette)),
            "A barbette cell lost its barbette provenance in the integrated candidate.");

        var exportDirectory = Path.Combine(Path.GetTempPath(), $"HullForge-REL01-{Guid.NewGuid():N}");
        try
        {
            var export = new BlueprintExporter().Export(snapshot, document, 1, exportDirectory,
                "REL01 candidate", exposure: new FeatureExposurePolicy(experimentalFeaturesEnabled: false));
            Require(export.BlockCount == snapshot.Hull.BlockCount &&
                    export.OccupiedCellCount == snapshot.Hull.OccupiedCellCount,
                "The integrated native export did not consume the resolved snapshot placement list.");
            using var json = JsonDocument.Parse(File.ReadAllText(export.FilePath));
            var blueprint = json.RootElement.GetProperty("Blueprint");
            var positions = blueprint.GetProperty("BLP").EnumerateArray().Select(item => item.GetString()).ToArray();
            var rotations = blueprint.GetProperty("BLR").EnumerateArray().Select(item => item.GetInt32()).ToArray();
            Require(positions.SequenceEqual(snapshot.Hull.Blocks.Select(block => $"{block.X},{block.Y},{block.Z}")) &&
                    rotations.SequenceEqual(snapshot.Hull.Blocks.Select(block => block.Rotation)) &&
                    blueprint.GetProperty("BlockIds").GetArrayLength() == snapshot.Hull.BlockCount,
                "The integrated native export changed a resolved anchor, rotation or block count.");
        }
        finally
        {
            if (Directory.Exists(exportDirectory))
                Directory.Delete(exportDirectory, recursive: true);
        }

        // 6. Deco V/H are visual only: the same document under the matching native method keeps
        //    byte-identical physical placements, so preview and export agree with the native pass.
        foreach (var (decorative, native) in new[]
                 {
                     (SmoothingMethod.DecoVertical, SmoothingMethod.VerticalSlopeFill),
                     (SmoothingMethod.DecoHorizontal, SmoothingMethod.HorizontalSlopeFill),
                 })
        {
            var decorativeDocument = document with { Hull = document.Hull with { Smoothing = decorative } };
            decorativeDocument = decorativeDocument with
            {
                Smoothing = document.Smoothing with { NativeMethod = decorative },
            };
            var nativeDocument = document with { Hull = document.Hull with { Smoothing = native } };
            nativeDocument = nativeDocument with
            {
                Smoothing = document.Smoothing with { NativeMethod = native },
            };
            var decorativeResult = new ShipGenerationService().Generate(decorativeDocument, 2, catalog);
            var nativeResult = new ShipGenerationService().Generate(nativeDocument, 2, catalog);
            Require(decorativeResult.IsValid && nativeResult.IsValid,
                $"{decorative} did not resolve in the integrated candidate: " +
                Describe(decorativeResult.Diagnostics) + " | " + Describe(nativeResult.Diagnostics));
            Require(decorativeResult.Snapshot!.Hull.Blocks.SequenceEqual(nativeResult.Snapshot!.Hull.Blocks),
                $"{decorative} changed native physical placements instead of only adding decorations.");
        }
    }

    private static void VerifyExperimentalInternalsRouteAroundBarbettes(FtdBlockCatalog catalog)
    {
        var parameters = HullParameters.Default with
        {
            Smoothing = SmoothingMethod.None,
            DeckArmor = new ArmorLayout([MaterialKind.Wood, MaterialKind.Metal]),
        };
        var ruler = ShipGenerationService.ResolveDeckRuler(parameters)
            ?? throw new InvalidOperationException("The internals fixture has no deck ruler interval.");
        var definition = BarbetteDefinition.Create("barbette-1", "barbette-1-node",
            DesignMeasure.FromMetres(7), 4, neckClearSizeMetres: 3);
        var placements = new[] { (Definition: definition, Center: DesignMeasure.FromMetres(44.5)) };
        var document = BuildDocument("REL01 internals", parameters, ruler, placements) with
        {
            Internals = Structure(
                SimpleInternalLayout.Create(
                    InternalStructureFamily.Disabled(InternalPlaneFamily.LongitudinalBulkhead) with { Enabled = true }, 5),
                SimpleInternalLayout.Create(
                    InternalStructureFamily.Disabled(InternalPlaneFamily.InternalDeck) with { Enabled = true }, 6),
                SimpleInternalLayout.Create(
                    InternalStructureFamily.Disabled(InternalPlaneFamily.TransverseBulkhead) with { Enabled = true }, 7)),
        };

        var result = new ShipGenerationService().Generate(document, 3, catalog);
        Require(result.IsValid, "The experimental internals run did not resolve: " + Describe(result.Diagnostics));
        var snapshot = result.Snapshot!;
        var barbetteFootprint = snapshot.Barbettes.SelectMany(item =>
                item.Solids.Select(solid => solid.Cell)
                    .Concat(item.RequiredVoids.Select(voidIntent => voidIntent.Cell))
                    .Concat(item.AuthorizedDeckCuts.Select(cut => cut.Cell)))
            .ToHashSet();
        Require(snapshot.Internals.Cells.Length > 0,
            "The experimental internals run realized no internal cells to check.");
        Require(snapshot.Internals.Cells.All(intent => !barbetteFootprint.Contains(intent.Cell)),
            "An experimental internal cell invaded the resolved barbette footprint.");
        Require(document.Internals.Families.Count(family => family.Enabled) == 3,
            "The experimental run did not enable all three internal families.");
        var familyCount = snapshot.Internals.Cells.Select(intent => intent.WinningFamily).Distinct().Count();
        Require(familyCount == 3,
            $"Only {familyCount} of the three internal families realized any cell around the barbette.");
    }

    /// <summary>
    /// The live editor must route a barbette document through the resolved composition service and
    /// publish the measured bow datum as a derived layout frame, instead of silently dropping the
    /// barbettes on the plain hull path or committing a background datum revision. This is the
    /// REL01 cross-feature seam.
    /// </summary>
    private static void VerifyLiveEditorRoutesBarbettesThroughResolvedComposition(FtdBlockCatalog catalog)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null)
                {
                    var created = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    created.InitializeComponent();
                    created.Navigating += (_, eventArgs) => eventArgs.Cancel = true;
                    FtdHullGenerator.UI.ThemeManager.Apply(FtdHullGenerator.UI.AppTheme.Workbench);
                }

                var window = new MainWindow();
                try
                {
                    // A headless window has not run its Loaded pass, so initialize the shape
                    // controls through the normal preset path before marking it loaded.
                    window.SelectHistoricalPresetForTests("us-fletcher-dd");
                    window.MarkLoadedForTests();
                    window.SetCatalogForTests(catalog);
                    // The real preview awaits Task.Run work; without a dispatcher synchronization
                    // context the continuation would resume on the pool and the WPF preview update
                    // would throw. Install one so PumpUntil drives the normal dispatcher path.
                    SynchronizationContext.SetSynchronizationContext(
                        new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

                    var parameters = HullParameters.Default;
                    var ruler = ShipGenerationService.ResolveDeckRuler(parameters)
                        ?? throw new InvalidOperationException("The live fixture has no deck ruler interval.");
                    var definition = BarbetteDefinition.Create("barbette-1", "barbette-1-node",
                        DesignMeasure.FromMetres(5), 3);
                    var node = new ArrangementNode(definition.NodeId, ArrangementNodeKind.Barbette,
                        definition.Id, DesignMeasure.FromMetres(4), [], DesignMeasure.FromMetres(44.5));
                    var document = ShipDocument.CreateNew("REL01 live", parameters, "rel01-live") with
                    {
                        Arrangement = new Arrangement([node], [], [], DesignMeasure.Zero, DesignMeasure.Zero,
                            ArrangementAnchorKind.BowDatum, ArrangementResizePolicy.PreserveAbsolute),
                        Barbettes = [definition],
                    };
                    Require(document.EffectiveDatum.LayoutBowZ == DesignMeasure.Zero,
                        "The live fixture must start from the stale origin datum to prove the refresh.");

                    window.EditorSessionForTests.New(document);
                    var revisionBefore = window.EditorSessionForTests.Revision;
                    var canUndoBefore = window.EditorSessionForTests.CanUndo;
                    var preview = window.GeneratePreviewForTestsAsync();
                    PumpUntil(() => preview.IsCompleted, TimeSpan.FromSeconds(60));
                    preview.GetAwaiter().GetResult();

                    Require(window.CurrentShipSnapshotForTests is not null,
                        "The live preview did not route a barbette-only document through resolved composition. Status: " +
                        window.StatusTextForTests);
                    Require(window.CurrentShipSnapshotForTests!.Barbettes.Length == 1,
                        "The live resolved snapshot lost the barbette.");
                    // The preview prepares a derived frame; it must not commit a datum revision or
                    // create a background undo item.
                    Require(window.EditorSessionForTests.Revision == revisionBefore &&
                            window.EditorSessionForTests.CanUndo == canUndoBefore,
                        "The live preview committed a background datum revision or created an undo item.");
                    Require(window.EditorSessionForTests.Document.EffectiveDatum.LayoutBowZ == DesignMeasure.Zero,
                        "The live preview persisted the measured bow datum instead of leaving the document untouched.");
                    var frame = window.WorkspaceForTests.LayoutFrame;
                    Require(frame is not null &&
                            frame.IsCurrent(window.EditorSessionForTests.Document.DocumentId,
                                window.EditorSessionForTests.Revision) &&
                            frame.BowDatum == ruler.BowDatum,
                        "The live preview did not publish a current derived frame with the evaluated bow datum.");
                    Require(window.MeasuredSupportedRulerEndForTests == ruler.SupportedEnd,
                        "The live preview did not publish the measured supported ruler end.");
                }
                finally
                {
                    window.EditorSessionForTests.New(
                        ShipDocument.CreateNew("REL01 probe clean", HullEditorSettings.Default));
                    window.Close();
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(90)))
            throw new InvalidOperationException("The live REL01 preview probe did not complete.");
        if (failure is not null)
            throw new InvalidOperationException("The live REL01 preview probe failed.", failure);
    }

    private static ShipDocument BuildDocument(
        string name,
        HullParameters parameters,
        SupportedDeckRuler ruler,
        IReadOnlyList<(BarbetteDefinition Definition, DesignMeasure Center)> placements)
    {
        var nodes = placements.Select(placement => new ArrangementNode(placement.Definition.NodeId,
            ArrangementNodeKind.Barbette, placement.Definition.Id, DesignMeasure.FromMetres(4),
            [], placement.Center)).ToImmutableArray();
        return ShipDocument.CreateNew(name, parameters, name.Replace(' ', '-').ToLowerInvariant()) with
        {
            Datum = new LayoutDatum(ruler.BowDatum, DesignMeasure.Zero),
            Arrangement = new Arrangement(nodes, [], [], DesignMeasure.Zero, DesignMeasure.Zero,
                ArrangementAnchorKind.BowDatum, ArrangementResizePolicy.PreserveAbsolute),
            Barbettes = placements.Select(placement => placement.Definition).ToImmutableArray(),
        };
    }

    private static InternalStructure Structure(params InternalStructureFamily[] enabled)
    {
        var byFamily = enabled.ToDictionary(family => family.Family);
        return new InternalStructure(Enum.GetValues<InternalPlaneFamily>()
            .Select(family => byFamily.TryGetValue(family, out var settings)
                ? settings
                : InternalStructureFamily.Disabled(family)).ToImmutableArray());
    }

    /// <summary>
    /// P1-B: a dirty internal-structure draft gets a debounced, cancellable live preview that is
    /// visible but never export-authoritative. Apply is the only transaction boundary (one
    /// revision/undo item), Cancel restores the committed geometry, rapid edits cannot let a
    /// stale generation win, and an invalid draft cannot re-enable export.
    /// </summary>
    private static void VerifyInternalDraftLivePreviewLifecycle(FtdBlockCatalog catalog)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new MainWindow();
                try
                {
                    window.SelectHistoricalPresetForTests("us-fletcher-dd");
                    window.MarkLoadedForTests();
                    window.SetCatalogForTests(catalog);
                    window.SetExperimentalFeaturesForTests(true);
                    SynchronizationContext.SetSynchronizationContext(
                        new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                    // Use the default hull so the internal deck has a usable cavity, exactly as
                    // the committed internal-structure editor flow does.
                    window.EditorSessionForTests.New(
                        ShipDocument.CreateNew("P1-B live", HullParameters.Default, "p1b-live"));

                    var editor = window.InternalStructureEditorForTests?.ViewModel
                        ?? throw new InvalidOperationException("The internal-structure editor was not installed.");
                    var deck = editor.Families.Single(item => item.Family == InternalPlaneFamily.InternalDeck);
                    // A committed internal family makes the base preview a resolved-composition
                    // result, so the export-authoritative snapshot is observable.
                    deck.Enabled = true;
                    Require(window.WorkspaceForTests.Apply() == WorkspaceApplyResult.Applied &&
                            deck.Enabled && window.WorkspaceForTests.Document.Internals
                                .Families.Any(family => family.Family == InternalPlaneFamily.InternalDeck && family.Enabled),
                        "The committed base internal deck was not applied.");
                    var committedRevision = window.EditorSessionForTests.Revision;
                    var basePreview = window.GeneratePreviewForTestsAsync();
                    PumpUntil(() => basePreview.IsCompleted, TimeSpan.FromSeconds(60));
                    basePreview.GetAwaiter().GetResult();
                    Require(window.CurrentShipSnapshotForTests is not null && window.ExportEnabledForTests &&
                            !window.VisiblePreviewIsDraftForTests,
                        "The committed base preview did not become export-authoritative. Status: " +
                        window.StatusTextForTests + " | snapshot=" + (window.CurrentShipSnapshotForTests is not null) +
                        " | export=" + window.ExportEnabledForTests + " | draft=" + window.VisiblePreviewIsDraftForTests);
                    Require(window.CurrentShipSnapshotForTests!.Internals.Planes.Any(plane => plane.RealizedCellCount > 0),
                        "The committed base internal deck realized no planes, so the live-preview probe has nothing to show.");
                    var baseHull = window.PreviewForTests.CurrentHull!;
                    var gaps = deck.ClearGapChoices;

                    Require(deck.Enabled, "The committed base document did not enable the internal deck.");
                    deck.ClearGap = gaps[0];
                    Require(window.WorkspaceForTests.IsDraftDirty,
                        "The internal-structure edit did not update the shared workspace draft.");
                    Require(!window.ExportEnabledForTests,
                        "Export stayed enabled while an internal-structure draft was dirty.");
                    Require(window.CurrentShipSnapshotForTests is null,
                        "A dirty draft left the committed snapshot export-authoritative.");

                    var draft = window.GenerateDraftPreviewForTestsAsync();
                    PumpUntil(() => draft.IsCompleted, TimeSpan.FromSeconds(60));
                    draft.GetAwaiter().GetResult();
                    Require(window.VisiblePreviewIsDraftForTests && window.CurrentShipSnapshotForTests is null &&
                            !window.ExportEnabledForTests &&
                            window.EditorSessionForTests.Revision == committedRevision,
                        "The live draft preview became export-authoritative or committed a revision.");
                    var draftHull = window.PreviewForTests.CurrentHull
                        ?? throw new InvalidOperationException("The live draft preview showed no geometry. Status: " +
                            window.StatusTextForTests + " | draft=" + window.WorkspaceForTests.IsDraftDirty +
                            " | conflict=" + window.WorkspaceForTests.HasConflict);
                    var expectedDraft = new ShipGenerationService().Generate(
                        window.WorkspaceForTests.Document, committedRevision, catalog).Snapshot!;
                    Require(draftHull.BlockCount == expectedDraft.Hull.BlockCount &&
                            draftHull.BlockCount != baseHull.BlockCount,
                        "The live draft preview did not visibly change to the current draft's geometry.");

                    // A rapid second edit must beat the first in-flight preview.
                    var first = window.GenerateDraftPreviewForTestsAsync();
                    deck.ClearGap = gaps[gaps.Count / 2];
                    var second = window.GenerateDraftPreviewForTestsAsync();
                    PumpUntil(() => first.IsCompleted && second.IsCompleted, TimeSpan.FromSeconds(60));
                    first.GetAwaiter().GetResult();
                    second.GetAwaiter().GetResult();
                    var expected = new ShipGenerationService().Generate(
                        window.WorkspaceForTests.Document, committedRevision, catalog).Snapshot!;
                    Require(window.PreviewForTests.CurrentHull!.BlockCount == expected.Hull.BlockCount,
                        "A stale draft preview overwrote the newest draft geometry.");

                    // Apply is the only transaction boundary: one revision, one undo item.
                    var beforeApplyDocument = window.EditorSessionForTests.Document;
                    var applyRevision = window.EditorSessionForTests.Revision;
                    Require(window.WorkspaceForTests.Apply() == WorkspaceApplyResult.Applied,
                        "Applying the internal-structure draft was rejected.");
                    Require(window.EditorSessionForTests.Revision == applyRevision + 1,
                        "Apply did not create exactly one revision.");
                    var appliedPreview = window.GeneratePreviewForTestsAsync();
                    PumpUntil(() => appliedPreview.IsCompleted, TimeSpan.FromSeconds(60));
                    appliedPreview.GetAwaiter().GetResult();
                    Require(window.CurrentShipSnapshotForTests is not null && window.ExportEnabledForTests &&
                            !window.VisiblePreviewIsDraftForTests,
                        "Apply did not make the visible geometry export-authoritative.");
                    Require(window.EditorSessionForTests.Undo() &&
                            ReferenceEquals(window.EditorSessionForTests.Document, beforeApplyDocument),
                        "One undo did not roll back the whole internal-structure draft.");
                    Require(window.EditorSessionForTests.Redo(),
                        "Redo did not restore the applied internal-structure draft.");

                    // Cancel discards the draft and restores the committed authoritative geometry.
                    var redoPreview = window.GeneratePreviewForTestsAsync();
                    PumpUntil(() => redoPreview.IsCompleted, TimeSpan.FromSeconds(60));
                    redoPreview.GetAwaiter().GetResult();
                    deck.ClearGap = gaps[^1];
                    var cancelDraft = window.GenerateDraftPreviewForTestsAsync();
                    PumpUntil(() => cancelDraft.IsCompleted, TimeSpan.FromSeconds(60));
                    cancelDraft.GetAwaiter().GetResult();
                    var cancelRevision = window.EditorSessionForTests.Revision;
                    window.WorkspaceForTests.Cancel();
                    var restored = window.GeneratePreviewForTestsAsync();
                    PumpUntil(() => restored.IsCompleted, TimeSpan.FromSeconds(60));
                    restored.GetAwaiter().GetResult();
                    Require(window.EditorSessionForTests.Revision == cancelRevision &&
                            !window.WorkspaceForTests.IsDraftDirty &&
                            window.CurrentShipSnapshotForTests is not null && window.ExportEnabledForTests,
                        "Cancel did not restore the committed authoritative preview.");

                    // An invalid draft cannot re-enable export.
                    window.WorkspaceForTests.UpdateDraft(document => document with
                    {
                        Hull = document.Hull with { Length = 0 },
                    });
                    Require(!window.ExportEnabledForTests,
                        "An invalid draft re-enabled export before any preview ran.");
                    var invalid = window.GenerateDraftPreviewForTestsAsync();
                    PumpUntil(() => invalid.IsCompleted, TimeSpan.FromSeconds(60));
                    invalid.GetAwaiter().GetResult();
                    Require(!window.ExportEnabledForTests && window.CurrentShipSnapshotForTests is null,
                        "An invalid draft preview re-enabled export.");
                    window.WorkspaceForTests.Cancel();
                }
                finally
                {
                    window.Close();
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(150)))
            throw new InvalidOperationException("The internal live-preview probe did not complete.");
        if (failure is not null)
            throw new InvalidOperationException("The internal live-preview probe failed.", failure);
    }

    private static ArmorLayout Armor(params MaterialKind[] materials) => new(materials);

    private static void PumpUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }

        Require(condition(), "The live preview did not complete within its timeout.");
    }

    private static string Describe(IEnumerable<DesignDiagnostic> diagnostics) =>
        string.Join(" | ", diagnostics.Select(diagnostic => diagnostic.ToString()));

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
