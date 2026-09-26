using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using System.Xml.Linq;
using FtdHullGenerator;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Components;
using FtdHullGenerator.Domain.Design;
using FtdHullGenerator.Domain.Layout;
using FtdHullGenerator.Domain.Projects;
using FtdHullGenerator.Geometry;
using FtdHullGenerator.Geometry.Components;
using FtdHullGenerator.Geometry.Composition;
using FtdHullGenerator.Geometry.Layout;
using FtdHullGenerator.Infrastructure;
using FtdHullGenerator.Serialization;
using FtdHullGenerator.UI;
using FtdHullGenerator.UI.Components;
using FtdHullGenerator.UI.Editor;
using FtdHullGenerator.UI.Layout;
using FtdHullGenerator.UI.Scenes;
using FtdHullGenerator.UI.Workspace;

/// <summary>
/// Hull Forge 2.0 stabilization pass 2, Team C (HF-07, HF-08, HF-09). These probes fail against
/// the pre-change behavior where practical: they pin the absence of the barbette Construction
/// surface, the BAR019 rejection of persisted non-Solid barbette armor, revision-matched
/// publication of resolved generation findings, the accepted-compromise status, the top-offset
/// wording convention, and the single catalog-resolved export-authoritative snapshot for plain
/// hulls (including catalog replacement and presentation invariance).
/// </summary>
internal static class Stabilization2TeamCTests
{
    public static void Run(HullGenerator generator, FtdBlockCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(catalog);

        Step(nameof(VerifyBarbetteConstructionSurfaceRemoved), VerifyBarbetteConstructionSurfaceRemoved);
        Step(nameof(VerifyNonSolidBarbetteConstructionRejected), VerifyNonSolidBarbetteConstructionRejected);
        Step(nameof(VerifyTopOffsetWordingConvention), VerifyTopOffsetWordingConvention);
        Step(nameof(VerifyResolvedGenerationPublicationIsRevisionMatchedAndCleared),
            VerifyResolvedGenerationPublicationIsRevisionMatchedAndCleared);
        Step(nameof(VerifyBarbetteEditorSeparatesRulerAndGenerationDiagnostics),
            VerifyBarbetteEditorSeparatesRulerAndGenerationDiagnostics);
        Step(nameof(VerifyFirstAddUsesNonOriginMeasuredBowDatum),
            VerifyFirstAddUsesNonOriginMeasuredBowDatum);
        Step(nameof(VerifyFirstAddFailsClosedWithoutFrame),
            VerifyFirstAddFailsClosedWithoutFrame);
        Step(nameof(VerifyStaleFramePublicationIsSuppressed),
            VerifyStaleFramePublicationIsSuppressed);

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

                // The live preview awaits Task.Run work; the continuation must resume on the
                // dispatcher so the WPF preview update stays on its owning thread.
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

                Step(nameof(VerifyPlainHullWithCatalogUsesResolvedSnapshotAndExport),
                    () => VerifyPlainHullWithCatalogUsesResolvedSnapshotAndExport(catalog));
                Step(nameof(VerifyPlainHullWithoutCatalogIsProvisional),
                    VerifyPlainHullWithoutCatalogIsProvisional);
                Step(nameof(VerifyCatalogReplacementInvalidatesAndRegenerates),
                    VerifyCatalogReplacementInvalidatesAndRegenerates);
                Step(nameof(VerifyPreviewStatusDistinguishesWarningsFromErrors),
                    () => VerifyPreviewStatusDistinguishesWarningsFromErrors(catalog));
                Step(nameof(VerifyPresentationDoesNotChangeResolvedBlocks),
                    () => VerifyPresentationDoesNotChangeResolvedBlocks(generator, catalog));
                Step(nameof(VerifyPreviewDoesNotCommitBackgroundDatumOrUndo),
                    () => VerifyPreviewDoesNotCommitBackgroundDatumOrUndo(catalog));
                Step(nameof(VerifyClearDiameterProgressionAndExplicitRejection),
                    VerifyClearDiameterProgressionAndExplicitRejection);
                Step(nameof(VerifyBarbetteDiameterControlProgressionAndRejection),
                    VerifyBarbetteDiameterControlProgressionAndRejection);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(240)))
            throw new InvalidOperationException("The HF-07/08/09 WPF probe did not complete.");
        if (failure is not null)
            throw new InvalidOperationException("The HF-07/08/09 WPF probe failed.", failure);

        Console.WriteLine(
            "Stabilization pass 2 Team C: barbette Construction surface removed while hull armor keeps it, " +
            "BAR019 rejects persisted non-Solid barbette armor, resolved generation findings publish only for the " +
            "current revision and stay separate from ruler diagnostics, plain-hull catalog preview is one " +
            "export-authoritative snapshot, no-catalog preview is provisional, catalog replacement invalidates and " +
            "regenerates, warning/error status is distinct, and presentation leaves the resolved block list unchanged. " +
            "HF-03/HF-05 product wiring: first Add places from the measured frame and persists its datum, a deckless " +
            "hull fails closed with LAY010, preview creates no background datum revision or undo item, stale frame " +
            "publication is suppressed, and the dedicated diameter UI offers only odd whole metres while explicitly " +
            "rejecting even/fractional typed values with BAR009 passed.");
    }

    // ------------------------------------------------------------------ HF-07

    private static void VerifyBarbetteConstructionSurfaceRemoved()
    {
        var arrangementPath = Path.Combine(FindRepositoryRoot(), "FtdHullGenerator", "UI", "Layout",
            "ArrangementView.xaml");
        var arrangement = XDocument.Load(arrangementPath);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        Require(!arrangement.Descendants().Any(element =>
                ((string?)element.Attribute("ItemsSource"))?.Contains("ConstructionChoices",
                    StringComparison.Ordinal) == true),
            "The normal barbette editor still binds a ConstructionChoices surface.");
        Require(!arrangement.Descendants().Any(element =>
                (string?)element.Attribute("AutomationProperties.Name") == "Barbette armor layer construction"),
            "The normal barbette editor still exposes a barbette armor construction control.");
        Require(arrangement.Descendants().Any(element =>
                ((string?)element.Attribute("ItemsSource"))?.Contains("MaterialChoices",
                    StringComparison.Ordinal) == true),
            "The barbette armor material control disappeared with the construction control.");
        Require(arrangement.Descendants().Any(element =>
                ((string?)element.Attribute("ItemsSource"))?.Contains("GenerationDiagnostics",
                    StringComparison.Ordinal) == true),
            "The barbette editor no longer surfaces resolved generation findings.");

        // The now-unused construction projection must be gone from the layer view model too.
        Require(!HasPublicProperty(typeof(BarbetteArmorLayerViewModel), "ConstructionChoice") &&
                !HasPublicProperty(typeof(BarbetteArmorLayerViewModel), "ConstructionChoices"),
            "BarbetteArmorLayerViewModel still exposes a construction surface.");

        // Ordinary hull armor keeps every construction alternative.
        var mainWindowSource = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "FtdHullGenerator",
            "MainWindow.xaml.cs"));
        foreach (var construction in new[]
                 {
                     "ArmorConstruction.Solid", "ArmorConstruction.Pole", "ArmorConstruction.BeamSlopeUp",
                     "ArmorConstruction.BeamSlopeDown", "ArmorConstruction.BeamSlopeSpike",
                 })
        {
            Require(mainWindowSource.Contains(construction, StringComparison.Ordinal),
                $"The ordinary hull armor construction surface lost {construction}.");
        }

        Require(mainWindowSource.Contains("ConstructionBox", StringComparison.Ordinal) &&
                mainWindowSource.Contains("ArmorConstructionChoice.All", StringComparison.Ordinal),
            "The ordinary hull/deck/bottom armor Construction control was removed with the barbette one.");
    }

    private static void VerifyNonSolidBarbetteConstructionRejected()
    {
        var definition = BarbetteDefinition.Create("barbette-1", "barbette-1-node",
            DesignMeasure.FromMetres(5), 3,
            sideArmor: new ArmorLayout([new ArmorLayer(MaterialKind.Metal, ArmorConstruction.Pole)]));
        var document = BarbetteDocument("HF-07 reject", definition);

        var definitionErrors = definition.Validate()
            .Where(diagnostic => diagnostic.Code == DesignDiagnosticCodes.BarbetteConstructionUnsupported)
            .ToArray();
        Require(definitionErrors.Length == 1 && definitionErrors[0].Severity == DesignSeverity.Error,
            "A direct non-Solid barbette layer was not rejected with the blocking BAR019 diagnostic.");
        Require(definition.SideArmor.Layers[0].Construction == ArmorConstruction.Pole,
            "Validation silently coerced the persisted non-Solid barbette construction.");

        var documentErrors = document.Validate()
            .Where(diagnostic => diagnostic.Code == DesignDiagnosticCodes.BarbetteConstructionUnsupported)
            .ToArray();
        Require(documentErrors.Length == 1 && documentErrors[0].IsError,
            "ShipDocument.Validate did not reject a persisted non-Solid barbette layer with BAR019.");

        var catalog = EmptyCatalog();
        var composition = new ShipGenerationService().Generate(document, 5, catalog);
        Require(!composition.IsValid && composition.Snapshot is null &&
                composition.Diagnostics.Any(diagnostic =>
                    diagnostic.Code == DesignDiagnosticCodes.BarbetteConstructionUnsupported &&
                    diagnostic.IsError),
            "Resolved generation did not fail closed on a non-Solid barbette layer; it may have coerced it.");

        var context = HullGenerator.CreateContext(HullParameters.Default);
        var direct = BarbetteGenerator.Generate(definition, context.CenterPlaneX, DesignMeasure.Zero, context);
        Require(direct.Solids.Count == 0 &&
                direct.Diagnostics.Any(diagnostic =>
                    diagnostic.Code == DesignDiagnosticCodes.BarbetteConstructionUnsupported),
            "BarbetteGenerator.Generate did not reject a non-Solid barbette layer atomically.");
    }

    // ------------------------------------------------------------------ HF-08

    private static void VerifyTopOffsetWordingConvention()
    {
        var negativeOffset = BarbetteDefinition.Create("wording", "wording-node",
            DesignMeasure.FromMetres(5), 3, topOffsetMetres: -1);
        var offsetDiagnostic = negativeOffset.Validate()
            .Single(diagnostic => diagnostic.Code == DesignDiagnosticCodes.BarbetteTopOffsetInvalid);
        Require(offsetDiagnostic.SuggestedCorrection is not null &&
                offsetDiagnostic.SuggestedCorrection.Contains("increase the offset to lower",
                    StringComparison.OrdinalIgnoreCase),
            "BAR011 no longer states that increasing the top offset lowers the barbette.");

        var parameters = HullParameters.Default with { Length = 60, Width = 21, Height = 8 };
        var context = HullGenerator.CreateContext(parameters);
        var shortDefinition = BarbetteDefinition.Create("short", "short-node",
            DesignMeasure.FromMetres(3), 12, neckClearSizeMetres: 3);
        var generation = BarbetteGenerator.Generate(shortDefinition, context.CenterPlaneX,
            DesignMeasure.Zero, context);
        var shortfall = generation.Diagnostics.SingleOrDefault(diagnostic =>
            diagnostic.Code == BarbetteDiagnosticCodes.ClearDepthShortfall);
        Require(shortfall is not null && shortfall.Severity == DesignSeverity.Warning,
            "The clear-depth shortfall warning BAR205 was not produced by the short hull.");
        var suggestion = shortfall!.SuggestedCorrection ?? string.Empty;
        Require(suggestion.Contains("increasing it lowers", StringComparison.OrdinalIgnoreCase) &&
                suggestion.Contains("top offset", StringComparison.OrdinalIgnoreCase),
            $"The BAR205 suggestion does not state the canonical convention: '{suggestion}'.");
        Require(!Regex.IsMatch(suggestion, @"increase[^.]*\braise\b", RegexOptions.IgnoreCase),
            $"The BAR205 suggestion still instructs increasing the offset to raise the barbette: '{suggestion}'.");

        // No barbette suggestion may tell the user that increasing TopOffsetMetres raises the
        // main barbette; the canonical direction is the opposite.
        foreach (var diagnostic in generation.Diagnostics
                     .Concat(negativeOffset.Validate())
                     .Where(diagnostic => diagnostic.SuggestedCorrection is not null))
        {
            Require(!Regex.IsMatch(diagnostic.SuggestedCorrection!, @"increase[^.]*\braise\b", RegexOptions.IgnoreCase),
                $"{diagnostic.Code} suggests increasing the offset raises the barbette: " +
                $"'{diagnostic.SuggestedCorrection}'.");
        }
    }

    private static void VerifyResolvedGenerationPublicationIsRevisionMatchedAndCleared()
    {
        var session = new EditorSession(ShipDocument.CreateNew("HF-08 publish", HullParameters.Default,
            "hf08-publish"));
        using var workspace = new WorkspaceViewModel(session);
        var diagnostic = new DesignDiagnostic(BarbetteDiagnosticCodes.ClearDepthShortfall,
            DesignSeverity.Warning, "Barbette 'barbette-1' realized less clear depth.",
            "barbette-1-node", nameof(BarbetteDefinition.ClearDepthMetres),
            DesignMeasure.FromMetres(12), DesignMeasure.FromMetres(6),
            "Reduce the top offset to raise the barbette (increasing it lowers the barbette).");

        Require(workspace.PublishResolvedGeneration(session.Revision, session.Document.DocumentId,
                [diagnostic], "1.0.0.0", "FP-A"),
            "A publication for the current revision was rejected.");
        Require(workspace.ResolvedGenerationDiagnostics.Count == 1 &&
                workspace.ResolvedGenerationDiagnostics[0] == diagnostic &&
                workspace.ResolvedGenerationRevision == session.Revision &&
                workspace.HasResolvedGeneration &&
                workspace.ResolvedCatalogVersion == "1.0.0.0" &&
                workspace.ResolvedCatalogFingerprint == "FP-A",
            "The current-revision findings and catalog identity were not published read-only.");

        var stale = new DesignDiagnostic(ShipCompositionDiagnosticCodes.CatalogFallback,
            DesignSeverity.Warning, "stale catalog fallback");
        Require(!workspace.PublishResolvedGeneration(session.Revision + 1, session.Document.DocumentId,
                [stale], "9.9.9.9", "FP-Z"),
            "A publication for a mismatched revision was accepted.");
        Require(workspace.ResolvedGenerationDiagnostics.Single() == diagnostic &&
                workspace.ResolvedCatalogFingerprint == "FP-A",
            "A stale publication replaced the current findings.");

        // A dirty draft is not the committed revision: the findings must be cleared.
        workspace.UpdateDraft(document => document with { Name = document.Name + " edited" });
        Require(workspace.IsDraftDirty && workspace.ResolvedGenerationDiagnostics.Count == 0 &&
                workspace.ResolvedGenerationRevision is null && !workspace.HasResolvedGeneration,
            "A dirty draft left the committed resolved generation findings published.");
        workspace.Cancel();

        // A new committed revision clears the previous findings too.
        Require(workspace.PublishResolvedGeneration(session.Revision, session.Document.DocumentId,
                [diagnostic], "1.0.0.0", "FP-A"),
            "The findings could not be republished after cancelling the draft.");
        session.Commit(session.Document with { Name = "HF-08 next revision" });
        Require(workspace.ResolvedGenerationDiagnostics.Count == 0 &&
                workspace.ResolvedGenerationRevision is null && !workspace.HasResolvedGeneration,
            "A new committed revision left the previous resolved generation findings published.");
    }

    private static void VerifyBarbetteEditorSeparatesRulerAndGenerationDiagnostics()
    {
        var session = new EditorSession(ShipDocument.CreateNew("HF-08 editor", HullParameters.Default,
            "hf08-editor"));
        using var workspace = new WorkspaceViewModel(session);
        using var editor = new BarbetteEditorViewModel(workspace);
        var first = editor.AddBarbette()!;
        var second = editor.AddBarbette()!;
        var firstId = first.Id;
        var firstNodeId = first.NodeId;
        // Deliberately overlap the protected clear volumes so the live ruler raises BAR016.
        Require(editor.MoveBarbette(second.Id, first.RulerCenter),
            "The editor could not move the second barbette onto the first.");
        Require(workspace.Apply() == WorkspaceApplyResult.Applied,
            "The overlapping barbette draft did not apply as one revision.");
        editor.SelectBarbette(firstId);

        var generationDiagnostic = new DesignDiagnostic(BarbetteDiagnosticCodes.ClearDepthShortfall,
            DesignSeverity.Warning, $"Barbette '{firstId}' realized less clear depth than requested.",
            firstNodeId, nameof(BarbetteDefinition.ClearDepthMetres),
            DesignMeasure.FromMetres(12), DesignMeasure.FromMetres(6),
            "Reduce the top offset to raise the barbette (increasing it lowers the barbette).");
        Require(workspace.PublishResolvedGeneration(session.Revision, session.Document.DocumentId,
                [generationDiagnostic], "1.0.0.0", "FP-EDITOR"),
            "The editor probe could not publish its resolved generation finding.");

        Require(editor.Diagnostics.Any(line => line.Text.Contains("BAR016", StringComparison.Ordinal)),
            "The live ruler overlap diagnostic disappeared from the ruler diagnostics collection.");
        var line = editor.GenerationDiagnostics.SingleOrDefault(item =>
            item.Text.Contains("BAR205", StringComparison.Ordinal));
        Require(line is not null,
            "The resolved generation warning for the selected barbette node was not shown in its own collection.");
        Require(line!.Text.Contains("requested", StringComparison.OrdinalIgnoreCase) &&
                line.Text.Contains("realized", StringComparison.OrdinalIgnoreCase) &&
                line.Text.Contains("12", StringComparison.Ordinal) &&
                line.Text.Contains("6", StringComparison.Ordinal),
            $"The resolved generation line omitted the requested/realized values: '{line.Text}'.");
        Require(!line.IsBlocking && editor.HasGenerationDiagnostics,
            "A resolved warning was styled as blocking or the generation collection stayed empty.");
        Require(editor.Diagnostics.All(item => !item.Text.Contains("BAR205", StringComparison.Ordinal)),
            "The resolved generation warning leaked into the ruler diagnostics collection.");
        Require(editor.GenerationDiagnostics.All(item => !item.Text.Contains("BAR016", StringComparison.Ordinal)),
            "A live ruler diagnostic leaked into the resolved generation collection.");

        // The selected barbette's finding is shown; another barbette's finding is not.
        var otherDiagnostic = generationDiagnostic with { NodeId = second.NodeId, Message = "other barbette" };
        workspace.PublishResolvedGeneration(session.Revision, session.Document.DocumentId,
            [generationDiagnostic, otherDiagnostic], "1.0.0.0", "FP-EDITOR");
        Require(editor.GenerationDiagnostics.Count == 1 &&
                editor.GenerationDiagnostics.All(item =>
                    !item.Text.Contains("other barbette", StringComparison.Ordinal)) &&
                editor.GenerationDiagnostics[0].Text.Contains("BAR205", StringComparison.Ordinal),
            "The editor did not scope resolved findings to the selected barbette.");
    }

    // ------------------------------------------------------------------ HF-03/HF-04/HF-05 wiring

    /// <summary>
    /// First Add must place against the derived frame's measured bow datum and supported ruler end,
    /// never the persisted <see cref="LayoutDatum.Origin" /> or the nominal hull length. The old
    /// wiring placed at ruler 50 m and persisted datum 0 m.
    /// </summary>
    private static void VerifyFirstAddUsesNonOriginMeasuredBowDatum()
    {
        var document = ShipDocument.CreateNew("HF-03 first add", HullParameters.Default, "hf03-first-add");
        Require(document.EffectiveDatum.LayoutBowZ == LayoutDatum.Origin.LayoutBowZ,
            "The probe document must start from the default origin datum.");
        var session = new EditorSession(document);
        using var workspace = new WorkspaceViewModel(session);
        using var editor = new BarbetteEditorViewModel(workspace);

        var item = editor.AddBarbette();
        Require(item is not null, "The first Add did not create a barbette: " +
            string.Join(" | ", editor.Diagnostics.Select(line => line.Text)));
        Require(editor.Barbettes.Count == 1 && workspace.IsDraftDirty,
            "The first Add did not add exactly one barbette to the draft.");

        var node = workspace.Document.Arrangement.FindNode(item!.NodeId);
        Require(node?.RequestedCenter == DesignMeasure.FromTwiceMetres(91),
            $"The first Add did not place at the frame midpoint 45.5 m; got " +
            $"{node?.RequestedCenter?.Metres:0.##} m.");
        Require(workspace.Document.Datum!.LayoutBowZ == DesignMeasure.FromTwiceMetres(81) &&
                workspace.Document.Datum.CenterPlaneX == DesignMeasure.Zero,
            "The arrangement transaction did not persist the measured 40.5 m frame datum.");
        Require(workspace.Document.Datum.LayoutBowZ != LayoutDatum.Origin.LayoutBowZ,
            "The first Add reused the origin datum.");

        var frame = workspace.ResolveLayoutFrameNow().Frame;
        Require(frame is not null && frame.BowDatum == DesignMeasure.FromTwiceMetres(81),
            "The derived frame did not report the measured 40.5 m bow datum.");
        Require(frame!.BowDatum - node!.RequestedCenter!.Value == DesignMeasure.FromMetres(-5) &&
                (frame.BowDatum - node.RequestedCenter!.Value).IsWholeMetre,
            "The first Add placement is not a whole metre from the measured bow datum.");
    }

    /// <summary>
    /// A hull with no contiguous supported deck interval yields no frame, so the first Add must
    /// create nothing and surface the blocking LAY010 reason instead of falling back to the nominal
    /// ruler.
    /// </summary>
    private static void VerifyFirstAddFailsClosedWithoutFrame()
    {
        var parameters = HullParameters.Default with { DeckArmor = null };
        var document = ShipDocument.CreateNew("HF-03 deckless", parameters, "hf03-deckless");
        var session = new EditorSession(document);
        using var workspace = new WorkspaceViewModel(session);
        using var editor = new BarbetteEditorViewModel(workspace);

        var item = editor.AddBarbette();
        Require(item is null, "The first Add created a barbette without a current frame.");
        Require(!workspace.IsDraftDirty, "A failed first Add left a dirty draft.");
        Require(workspace.Document.Barbettes.Length == 0 && workspace.Document.Arrangement.Nodes.Length == 0,
            "A failed first Add created a barbette or arrangement node.");
        Require(editor.Diagnostics.Any(line =>
                line.Text.Contains(LayoutFrameDiagnosticCodes.FrameUnavailable, StringComparison.Ordinal) &&
                line.IsBlocking),
            "A failed first Add did not surface a visible blocking LAY010 line: " +
            string.Join(" | ", editor.Diagnostics.Select(line => line.Text)));

        // The hull regains a supported deck interval, so the published frame supersedes the
        // fail-closed reason: the stale blocking LAY010 line must not survive a valid frame.
        session.Commit(session.Document with { Hull = HullParameters.Default });
        Require(workspace.ResolveLayoutFrameNow().IsResolved,
            "The decked revision did not resolve a current frame.");
        Require(editor.Diagnostics.All(line =>
                !line.Text.Contains(LayoutFrameDiagnosticCodes.FrameUnavailable, StringComparison.Ordinal)),
            "A resolved frame left the stale LAY010 first-Add failure visible: " +
            string.Join(" | ", editor.Diagnostics.Select(line => line.Text)));
    }

    /// <summary>
    /// A frame is published only for the current document/revision/catalog identity, and a
    /// superseded background job must never become or replace the published frame.
    /// </summary>
    private static void VerifyStaleFramePublicationIsSuppressed()
    {
        var session = new EditorSession(ShipDocument.CreateNew("HF-03 stale", HullParameters.Default,
            "hf03-stale"));
        using var workspace = new WorkspaceViewModel(session);
        workspace.LayoutCatalogIdentity = "cat-a";
        var good = new ResolvedLayoutFrame(DesignMeasure.FromTwiceMetres(81), DesignMeasure.FromMetres(91),
            DesignMeasure.Zero, session.Document.DocumentId, session.Revision, 1, "cat-a");
        Require(workspace.TryPublishLayoutFrame(good) && workspace.LayoutFrame == good,
            "A frame current for the document/revision/catalog was not published.");

        Require(!workspace.TryPublishLayoutFrame(good with { SourceRevision = session.Revision + 1 }) &&
                workspace.LayoutFrame == good,
            "A stale-revision frame was accepted or overwrote the current frame.");
        Require(!workspace.TryPublishLayoutFrame(good with { SourceDocumentId = "other-document" }) &&
                workspace.LayoutFrame == good,
            "A stale-document frame was accepted or overwrote the current frame.");
        Require(!workspace.TryPublishLayoutFrame(good with { CatalogIdentity = "cat-b" }) &&
                workspace.LayoutFrame == good,
            "A wrong-catalog frame was accepted or overwrote the current frame.");

        Require(!workspace.TryPublishLayoutFrame(null) && workspace.LayoutFrame is null,
            "A null frame did not clear the published frame.");
        Require(workspace.TryPublishLayoutFrame(good) && workspace.LayoutFrame == good,
            "The current frame could not be republished.");
        session.Commit(session.Document with { Name = "HF-03 stale next" });
        Require(workspace.LayoutFrame is null,
            "Committing a new revision left the previous frame published.");

        // An arrangement-only draft keeps the committed hull, so the current frame stays published
        // and the ruler/arrangement/generation keep consuming the same frame. A draft that rebuilds
        // the hull parameters invalidates it.
        var draftSession = new EditorSession(ShipDocument.CreateNew("HF-03 draft frame",
            HullParameters.Default, "hf03-draft-frame"));
        using var draftWorkspace = new WorkspaceViewModel(draftSession);
        var draftFrame = draftWorkspace.ResolveLayoutFrameNow().Frame;
        Require(draftFrame is not null && draftWorkspace.LayoutFrame == draftFrame,
            "The draft-frame probe could not resolve and publish a current frame.");
        draftWorkspace.UpdateDraft(current => current with { Name = current.Name + " draft" });
        Require(draftWorkspace.IsDraftDirty && ReferenceEquals(draftWorkspace.LayoutFrame, draftFrame),
            "An arrangement-only draft dropped the current derived frame.");
        draftWorkspace.UpdateDraft(current => current with { Hull = current.Hull with { Length = 90 } });
        Require(draftWorkspace.LayoutFrame is null,
            "A hull-changing draft left a stale derived frame published.");

        // A background job superseded by a newer job must never become the published frame, even
        // though it completes last.
        var gates = new ConcurrentDictionary<long, ManualResetEventSlim>();
        using var preparation = new LayoutFramePreparation(job =>
        {
            gates.GetOrAdd(job.JobId, _ => new ManualResetEventSlim(false)).Wait(job.CancellationToken);
            return new LayoutFrameResolution(new ResolvedLayoutFrame(
                DesignMeasure.FromTwiceMetres(81), DesignMeasure.FromMetres(91), DesignMeasure.Zero,
                job.DocumentId, job.Revision, job.JobId, job.CatalogIdentity), []);
        });
        using var gated = new WorkspaceViewModel(session, preparation);
        gated.LayoutCatalogIdentity = "cat-a";

        var first = gated.ResolveLayoutFrameAsync();
        var firstJob = preparation.CurrentJobId;
        var second = gated.ResolveLayoutFrameAsync();
        var secondJob = preparation.CurrentJobId;
        Require(secondJob > firstJob, "The second background job did not supersede the first.");

        gates.GetOrAdd(secondJob, _ => new ManualResetEventSlim(false)).Set();
        var secondResolution = second.GetAwaiter().GetResult();
        Require(secondResolution.Frame?.JobId == secondJob && gated.LayoutFrame?.JobId == secondJob,
            "The newest background job did not publish its frame.");

        gates.GetOrAdd(firstJob, _ => new ManualResetEventSlim(false)).Set();
        var firstResolution = first.GetAwaiter().GetResult();
        Require(firstResolution.Frame is null, "A superseded background job returned a frame.");
        Require(gated.LayoutFrame?.JobId == secondJob,
            "A superseded background job became or replaced the published frame.");

        // A background resolve whose revision moved on must never become the published frame.
        var moved = gated.ResolveLayoutFrameAsync();
        var movedJob = preparation.CurrentJobId;
        session.Commit(session.Document with { Name = "HF-03 stale moved" });
        gates.GetOrAdd(movedJob, _ => new ManualResetEventSlim(false)).Set();
        var movedResolution = moved.GetAwaiter().GetResult();
        Require(movedResolution.Frame is null ||
                !movedResolution.Frame.IsCurrent(session.Document.DocumentId, session.Revision),
            "A resolve for a superseded revision returned a current frame.");
        Require(gated.LayoutFrame is null,
            "A background resolve for a superseded revision became the published frame.");
    }

    /// <summary>
    /// Normal diameter editing offers only the domain odd whole-metre progression; an even or
    /// fractional typed value is rejected with the domain BAR009 line, never rounded or clamped.
    /// </summary>
    private static void VerifyClearDiameterProgressionAndExplicitRejection()
    {
        var session = new EditorSession(ShipDocument.CreateNew("HF-04 diameter", HullParameters.Default,
            "hf04-diameter"));
        using var workspace = new WorkspaceViewModel(session);
        using var editor = new BarbetteEditorViewModel(workspace);
        var item = editor.AddBarbette();
        Require(item is not null, "The diameter probe could not add a barbette: " +
            string.Join(" | ", editor.Diagnostics.Select(line => line.Text)));

        item!.ClearDiameterMetres = 3;
        Require(item.ClearDiameterMetres == 3 && item.Definition.ClearDiameter == DesignMeasure.FromMetres(3),
            "A legal 3 m diameter was not committed.");
        item.ClearDiameterMetres = 5;
        Require(item.ClearDiameterMetres == 5 && item.Definition.ClearDiameter == DesignMeasure.FromMetres(5),
            "A legal 5 m diameter was not committed.");

        foreach (var invalid in new[] { 4d, 4.5, 0d, -1d, 3.0000001, double.NaN, double.PositiveInfinity })
        {
            item.ClearDiameterMetres = invalid;
            Require(item.ClearDiameterMetres == 5 && item.Definition.ClearDiameter == DesignMeasure.FromMetres(5),
                $"The rejected diameter {invalid} mutated or was rounded/clamped by the definition.");
            Require(editor.Diagnostics.Any(line => line.IsBlocking &&
                    line.Text.Contains(DesignDiagnosticCodes.BarbetteClearDiameterInvalid,
                        StringComparison.Ordinal)),
                $"The rejected diameter {invalid} did not surface a visible blocking BAR009 line: " +
                string.Join(" | ", editor.Diagnostics.Select(line => line.Text)));
        }

        item.ClearDiameterMetres = 7;
        Require(item.ClearDiameterMetres == 7 && item.Definition.ClearDiameter == DesignMeasure.FromMetres(7),
            "A legal 7 m diameter was not committed after a rejection.");
        Require(editor.Diagnostics.All(line =>
                !line.Text.Contains(DesignDiagnosticCodes.BarbetteClearDiameterInvalid, StringComparison.Ordinal)),
            "A legal value did not clear the pending BAR009 rejection.");
    }

    /// <summary>
    /// The dedicated control nudges only along the domain odd whole-metre progression and restores
    /// the last legal value when the view model rejects a raw typed even/fractional value.
    /// </summary>
    private static void VerifyBarbetteDiameterControlProgressionAndRejection()
    {
        var control = new BarbetteDiameterInput { Value = 3 };
        var up = new List<double> { control.Value };
        for (var step = 0; step < 6; step++)
        {
            control.NudgeForTests(1);
            up.Add(control.Value);
        }

        Require(up.SequenceEqual(new[] { 3d, 5d, 7d, 9d, 11d, 13d, 15d }),
            "Nudging up did not follow 3, 5, 7, 9, 11, … : " + string.Join(", ", up));

        var down = new List<double> { control.Value };
        for (var step = 0; step < 3; step++)
        {
            control.NudgeForTests(-1);
            down.Add(control.Value);
        }

        Require(down.SequenceEqual(new[] { 15d, 13d, 11d, 9d }),
            "Nudging down did not follow the odd whole-metre progression: " + string.Join(", ", down));

        control.Value = 1;
        Require(!control.DecreaseButtonForTests.IsEnabled,
            "The decrease stepper stayed enabled at the 1 m minimum.");
        control.Value = BarbetteDefinition.MaximumSupportedClearDiameter.Metres;
        Require(!control.IncreaseButtonForTests.IsEnabled,
            "The increase stepper stayed enabled at the maximum supported diameter.");

        // The typed commit sends the raw parsed value; the view model rejects it and the binding
        // restores the last legal value while BAR009 stays visible.
        var session = new EditorSession(ShipDocument.CreateNew("HF-04 control", HullParameters.Default,
            "hf04-control"));
        using var workspace = new WorkspaceViewModel(session);
        using var editor = new BarbetteEditorViewModel(workspace);
        var item = editor.AddBarbette();
        Require(item is not null, "The diameter control probe could not add a barbette.");
        var bound = new BarbetteDiameterInput();
        bound.SetBinding(BarbetteDiameterInput.ValueProperty, new Binding(
            nameof(BarbetteEditorItem.ClearDiameterMetres))
        {
            Source = item,
            Mode = BindingMode.TwoWay,
        });
        Require(bound.Value == 7, "The control did not bind the selected barbette's legal diameter.");

        bound.TextBoxForTests.Text = "4.5";
        bound.CommitTextForTests();
        Require(item!.Definition.ClearDiameter == DesignMeasure.FromMetres(7) && bound.Value == 7,
            "A fractional typed value mutated the definition or was not restored to the last legal value.");
        Require(editor.Diagnostics.Any(line => line.IsBlocking &&
                line.Text.Contains(DesignDiagnosticCodes.BarbetteClearDiameterInvalid, StringComparison.Ordinal)),
            "A fractional typed value did not surface a visible BAR009 line.");

        bound.TextBoxForTests.Text = "4";
        bound.CommitTextForTests();
        Require(item.Definition.ClearDiameter == DesignMeasure.FromMetres(7) && bound.Value == 7,
            "An even typed value mutated the definition or was not restored to the last legal value.");
    }

    // ------------------------------------------------------------------ HF-09

    /// <summary>
    /// The live preview publishes a derived frame for the committed revision; it must not commit a
    /// datum revision or create a background undo item, and the persisted document datum stays
    /// untouched while generation composes a frame-consistent in-memory document.
    /// </summary>
    private static void VerifyPreviewDoesNotCommitBackgroundDatumOrUndo(FtdBlockCatalog catalog)
    {
        var window = new MainWindow();
        try
        {
            InitializeControls(window);
            window.MarkLoadedForTests();
            window.SetCatalogForTests(catalog);

            var parameters = HullParameters.Default;
            var definition = BarbetteDefinition.Create("barbette-1", "barbette-1-node",
                DesignMeasure.FromMetres(5), 3);
            var node = new ArrangementNode(definition.NodeId, ArrangementNodeKind.Barbette,
                definition.Id, DesignMeasure.FromMetres(4), [], DesignMeasure.FromTwiceMetres(91));
            var document = ShipDocument.CreateNew("HF-03 no commit", parameters, "hf03-no-commit") with
            {
                Arrangement = new Arrangement([node], [], [], DesignMeasure.Zero, DesignMeasure.Zero,
                    ArrangementAnchorKind.BowDatum, ArrangementResizePolicy.PreserveAbsolute),
                Barbettes = [definition],
            };
            Require(document.EffectiveDatum.LayoutBowZ == DesignMeasure.Zero,
                "The live fixture must start from the stale origin datum to prove no background commit.");

            window.EditorSessionForTests.New(document);
            var revisionBefore = window.EditorSessionForTests.Revision;
            var canUndoBefore = window.EditorSessionForTests.CanUndo;
            Require(!canUndoBefore, "The live fixture must start with no undo history.");

            var preview = window.GeneratePreviewForTestsAsync();
            PumpUntil(() => preview.IsCompleted, TimeSpan.FromSeconds(60));
            preview.GetAwaiter().GetResult();

            var snapshot = window.CurrentShipSnapshotForTests;
            Require(snapshot is not null && snapshot.Barbettes.Length == 1,
                "The preview did not resolve the barbette snapshot. Status: " + window.StatusTextForTests);
            Require(window.EditorSessionForTests.Revision == revisionBefore &&
                    window.EditorSessionForTests.CanUndo == canUndoBefore,
                "The preview committed a background datum revision or created a background undo item.");
            Require(window.EditorSessionForTests.Document.Datum!.LayoutBowZ == DesignMeasure.Zero,
                "The preview persisted the measured datum instead of leaving the document untouched.");
            var frame = window.WorkspaceForTests.LayoutFrame;
            Require(frame is not null && frame.BowDatum == DesignMeasure.FromTwiceMetres(81) &&
                    window.MeasuredSupportedRulerEndForTests == DesignMeasure.FromMetres(91),
                "The preview did not publish the current 40.5 m / 91 m frame. Status: " +
                window.StatusTextForTests);
        }
        finally
        {
            window.EditorSessionForTests.New(
                ShipDocument.CreateNew("HF-03 probe clean", HullEditorSettings.Default));
            window.Close();
        }
    }

    private static void VerifyPlainHullWithCatalogUsesResolvedSnapshotAndExport(FtdBlockCatalog catalog)
    {
        var window = new MainWindow();
        var exportDirectory = Path.Combine(Path.GetTempPath(), $"HullForge-HF09-plain-{Guid.NewGuid():N}");
        var referenceDirectory = Path.Combine(Path.GetTempPath(), $"HullForge-HF09-ref-{Guid.NewGuid():N}");
        try
        {
            InitializeControls(window);
            window.MarkLoadedForTests();
            window.SetCatalogForTests(catalog);
            var parameters = window.ReadParametersForTests(out var message) ??
                throw new InvalidOperationException("The default controls did not read: " + message);
            window.EditorSessionForTests.New(ShipDocument.CreateNew("HF-09 zero components", parameters,
                "hf09-zero"));
            window.QueuePreviewForTests();
            var preview = window.GeneratePreviewForTestsAsync();
            PumpUntil(() => preview.IsCompleted, TimeSpan.FromSeconds(60));
            preview.GetAwaiter().GetResult();

            var snapshot = window.CurrentShipSnapshotForTests;
            Require(snapshot is not null,
                "A plain hull with a catalog did not use the resolved composition snapshot. Status: " +
                window.StatusTextForTests);
            Require(snapshot!.Barbettes.Length == 0 && snapshot.Internals.Planes.IsEmpty &&
                    (snapshot.Refinement is null || snapshot.Refinement.Extensions.IsEmpty),
                "The plain-hull resolved snapshot carried unexpected barbette/internal/Deco components.");
            Require(snapshot.Hull.ResolvedCatalogVersion == catalog.GameVersion &&
                    snapshot.Hull.ResolvedCatalogFingerprint == ShipCatalogFingerprint.Compute(catalog),
                "The plain-hull snapshot did not bind the installed catalog identity.");
            Require(window.ExportEnabledForTests,
                "The plain-hull catalog preview did not enable export. Status: " + window.StatusTextForTests);

            // Physical invariance: composition is feature-free-identical to the legacy generator.
            var raw = new HullGenerator().Generate(parameters);
            Require(CellIdentity(snapshot.Hull).SetEquals(CellIdentity(raw)),
                "Catalog-resolved composition changed the plain hull's occupied cells or materials.");

            // Export consumes exactly the snapshot: ExportClicked's output must equal a direct
            // snapshot export for the same revision/document/name.
            window.DestinationForTests = exportDirectory;
            window.ExportForTests();
            var exported = Directory.Exists(exportDirectory)
                ? Directory.GetFiles(exportDirectory, "*.blueprint")
                : [];
            Require(exported.Length == 1,
                "ExportClicked did not write exactly one blueprint for the plain-hull catalog preview. Status: " +
                window.StatusTextForTests);
            var reference = new BlueprintExporter().Export(snapshot, window.EditorSessionForTests.Document,
                window.EditorSessionForTests.Revision, referenceDirectory, "reference");
            Require(BlueprintBlockList(exported[0]) == BlueprintBlockList(reference.FilePath),
                "ExportClicked did not write exactly the resolved snapshot's block list.");
        }
        finally
        {
            window.Close();
            if (Directory.Exists(exportDirectory))
                Directory.Delete(exportDirectory, recursive: true);
            if (Directory.Exists(referenceDirectory))
                Directory.Delete(referenceDirectory, recursive: true);
        }
    }

    private static void VerifyPlainHullWithoutCatalogIsProvisional()
    {
        var window = new MainWindow();
        var exportDirectory = Path.Combine(Path.GetTempPath(), $"HullForge-HF09-prov-{Guid.NewGuid():N}");
        try
        {
            InitializeControls(window);
            window.MarkLoadedForTests();
            var parameters = window.ReadParametersForTests(out var message) ??
                throw new InvalidOperationException("The default controls did not read: " + message);
            window.EditorSessionForTests.New(ShipDocument.CreateNew("HF-09 provisional", parameters,
                "hf09-provisional"));
            var preview = window.GeneratePreviewForTestsAsync();
            PumpUntil(() => preview.IsCompleted, TimeSpan.FromSeconds(60));
            preview.GetAwaiter().GetResult();

            Require(window.CurrentShipSnapshotForTests is null,
                "A no-catalog preview claimed a catalog-resolved snapshot.");
            Require(!window.ExportEnabledForTests,
                "A provisional no-catalog preview enabled export.");
            Require(window.StatusTextForTests.Contains("provisional", StringComparison.OrdinalIgnoreCase) &&
                    window.StatusTextForTests.Contains("catalog", StringComparison.OrdinalIgnoreCase),
                "The no-catalog status did not say the display is provisional and not catalog-resolved. Status: " +
                window.StatusTextForTests);

            window.DestinationForTests = exportDirectory;
            window.ExportForTests();
            Require(!Directory.Exists(exportDirectory) ||
                    Directory.GetFiles(exportDirectory, "*.blueprint").Length == 0,
                "Export wrote a file for a provisional no-catalog preview.");
            Require(!window.ExportEnabledForTests && window.CurrentShipSnapshotForTests is null,
                "Exporting a provisional preview left export enabled or installed a snapshot.");
        }
        finally
        {
            window.Close();
            if (Directory.Exists(exportDirectory))
                Directory.Delete(exportDirectory, recursive: true);
        }
    }

    private static void VerifyCatalogReplacementInvalidatesAndRegenerates()
    {
        var window = new MainWindow();
        try
        {
            InitializeControls(window);
            window.MarkLoadedForTests();
            var catalogA = FallbackCatalog("1.0.0.0");
            window.SetCatalogForTests(catalogA);
            var parameters = window.ReadParametersForTests(out var message) ??
                throw new InvalidOperationException("The default controls did not read: " + message);
            window.EditorSessionForTests.New(ShipDocument.CreateNew("HF-09 replacement", parameters,
                "hf09-replacement"));
            var first = window.GeneratePreviewForTestsAsync();
            PumpUntil(() => first.IsCompleted, TimeSpan.FromSeconds(60));
            first.GetAwaiter().GetResult();
            Require(window.CurrentShipSnapshotForTests is not null &&
                    window.ExportEnabledForTests &&
                    window.CurrentShipSnapshotForTests.Hull.ResolvedCatalogFingerprint ==
                    ShipCatalogFingerprint.Compute(catalogA),
                "The first catalog did not produce an export-authoritative snapshot. Status: " +
                window.StatusTextForTests);

            var catalogB = FallbackCatalog("2.0.0.0");
            window.ReplaceCatalogForTests(catalogB);
            Require(window.CurrentShipSnapshotForTests is null && !window.ExportEnabledForTests,
                "Replacing the catalog left the previous catalog's snapshot exportable.");

            // A successful replacement queues a fresh preview against the new catalog.
            PumpUntil(() => window.CurrentShipSnapshotForTests is not null, TimeSpan.FromSeconds(60));
            Require(window.CurrentShipSnapshotForTests!.Hull.ResolvedCatalogVersion == catalogB.GameVersion &&
                    window.CurrentShipSnapshotForTests.Hull.ResolvedCatalogFingerprint ==
                    ShipCatalogFingerprint.Compute(catalogB) &&
                    window.ExportEnabledForTests,
                "The replacement catalog did not regenerate an export-authoritative snapshot. Status: " +
                window.StatusTextForTests);
        }
        finally
        {
            window.Close();
        }
    }

    private static void VerifyPreviewStatusDistinguishesWarningsFromErrors(FtdBlockCatalog catalog)
    {
        var warningWindow = new MainWindow();
        try
        {
            InitializeControls(warningWindow);
            warningWindow.MarkLoadedForTests();
            // A fallback-only catalog forces the CMP109 accepted compromise on a plain hull.
            warningWindow.SetCatalogForTests(FallbackCatalog("3.0.0.0"));
            var parameters = warningWindow.ReadParametersForTests(out var message) ??
                throw new InvalidOperationException("The default controls did not read: " + message);
            warningWindow.EditorSessionForTests.New(ShipDocument.CreateNew("HF-08 warnings", parameters,
                "hf08-warnings"));
            var preview = warningWindow.GeneratePreviewForTestsAsync();
            PumpUntil(() => preview.IsCompleted, TimeSpan.FromSeconds(60));
            preview.GetAwaiter().GetResult();
            var warnings = warningWindow.CurrentShipSnapshotForTests?.Diagnostics
                .Count(diagnostic => diagnostic.Severity == DesignSeverity.Warning) ?? 0;
            Require(warnings > 0, "The fallback catalog did not produce a geometry warning to summarise.");
            Require(warningWindow.StatusTextForTests.Contains("geometry warning", StringComparison.OrdinalIgnoreCase),
                "The resolved warning status did not use the 'geometry warning' summary. Status: " +
                warningWindow.StatusTextForTests);
            Require(warningWindow.StatusIsWarningForTests && !warningWindow.StatusIsErrorForTests,
                "The resolved warning status did not use the warning tone.");
        }
        finally
        {
            warningWindow.Close();
        }

        var errorWindow = new MainWindow();
        try
        {
            InitializeControls(errorWindow);
            errorWindow.MarkLoadedForTests();
            errorWindow.SetCatalogForTests(catalog);
            var parameters = errorWindow.ReadParametersForTests(out var message) ??
                throw new InvalidOperationException("The default controls did not read: " + message);
            var definition = BarbetteDefinition.Create("barbette-1", "barbette-1-node",
                DesignMeasure.FromMetres(5), 3,
                sideArmor: new ArmorLayout([new ArmorLayer(MaterialKind.Metal, ArmorConstruction.Pole)]));
            errorWindow.EditorSessionForTests.New(BarbetteDocument("HF-08 blocking", definition, parameters));
            var preview = errorWindow.GeneratePreviewForTestsAsync();
            PumpUntil(() => preview.IsCompleted, TimeSpan.FromSeconds(60));
            preview.GetAwaiter().GetResult();
            Require(errorWindow.StatusIsErrorForTests && !errorWindow.StatusIsWarningForTests,
                "A blocking non-Solid barbette did not use the distinct error status. Status: " +
                errorWindow.StatusTextForTests);
            Require(errorWindow.StatusTextForTests.Contains("Could not generate", StringComparison.Ordinal) &&
                    !errorWindow.StatusTextForTests.Contains("geometry warning", StringComparison.OrdinalIgnoreCase),
                "The blocking outcome was conflated with the accepted-compromise warning summary. Status: " +
                errorWindow.StatusTextForTests);
            Require(errorWindow.CurrentShipSnapshotForTests is null && !errorWindow.ExportEnabledForTests,
                "A rejected generation installed a snapshot or enabled export.");
        }
        finally
        {
            errorWindow.Close();
        }
    }

    private static void VerifyPresentationDoesNotChangeResolvedBlocks(HullGenerator generator,
        FtdBlockCatalog catalog)
    {
        var parameters = HullParameters.Default;
        var document = ShipDocument.CreateNew("HF-09 invariance", parameters, "hf09-invariance");
        var result = new ShipGenerationService().Generate(document, 1, catalog);
        Require(result.IsValid, Describe(result.Diagnostics));
        var snapshot = result.Snapshot!;
        var blocks = snapshot.Hull.Blocks.ToArray();

        var control = new HullPreviewControl { Width = 640, Height = 480 };
        var before = control.NativeBuildCountForTests;
        control.SetResolvedShip(snapshot, highlightEdits: false);
        Require(control.NativeBuildCountForTests == before + 1,
            "SetResolvedShip did not build the native model exactly once for one assignment.");
        var buildCount = control.NativeBuildCountForTests;
        var shipModel = control.ShipModel;

        control.SceneSettings = new PreviewSceneSettings
        {
            Kind = PreviewSceneKind.Ocean,
            Deterministic = true,
        };
        ArrangeAndRender(control, 640, 480);
        Require(ReferenceEquals(control.ShipModel, shipModel) && control.NativeBuildCountForTests == buildCount,
            "Changing the presentation scene rebuilt or replaced the physical ship model.");
        Require(snapshot.Hull.Blocks.SequenceEqual(blocks),
            "Changing the presentation scene mutated the resolved snapshot's block list.");

        var reference = new HullGenerator().Generate(parameters);
        Require(CellIdentity(snapshot.Hull).SetEquals(CellIdentity(reference)),
            "Composition changed the default hull's occupied cells or materials.");
    }

    // ------------------------------------------------------------------ helpers

    private static ShipDocument BarbetteDocument(string name, BarbetteDefinition definition,
        HullParameters? parameters = null)
    {
        var hull = parameters ?? HullParameters.Default;
        var ruler = ShipGenerationService.ResolveDeckRuler(hull) ??
            throw new InvalidOperationException("The probe hull has no supported deck ruler interval.");
        var node = new ArrangementNode(definition.NodeId, ArrangementNodeKind.Barbette, definition.Id,
            DesignMeasure.FromMetres(4), [], DesignMeasure.FromMetres(40));
        return ShipDocument.CreateNew(name, hull, name.Replace(' ', '-').ToLowerInvariant()) with
        {
            Arrangement = new Arrangement([node], [], [], DesignMeasure.Zero, DesignMeasure.Zero,
                ArrangementAnchorKind.BowDatum, ArrangementResizePolicy.PreserveAbsolute),
            Datum = new LayoutDatum(ruler.BowDatum, DesignMeasure.Zero),
            Barbettes = [definition],
        };
    }

    private static void Step(string name, Action action)
    {
        try
        {
            action();
        }
        catch (Exception error)
        {
            throw new InvalidOperationException(
                $"{name}: {error.GetType().Name}: {error.Message}\n{error.StackTrace}");
        }
    }

    private static bool HasPublicProperty(Type type, string name) =>
        type.GetProperty(name) is not null;

    private static HashSet<string> CellIdentity(GeneratedHull hull) => hull.Blocks
        .SelectMany(block => block.OccupiedCells.Select(cell =>
            $"{cell.X},{cell.Y},{cell.Z}|{(int)block.Material}"))
        .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The serialized block list of a blueprint, independent of the requested file name and the
    /// randomized ForceId. Two exports with this identity contain the same placements, rotations
    /// and catalog item mapping.
    /// </summary>
    private static string BlueprintBlockList(string path)
    {
        var root = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ??
                   throw new InvalidOperationException("The exported blueprint was not a JSON object.");
        var blueprint = root["Blueprint"]!.AsObject();
        return string.Join("|",
            root["SavedTotalBlockCount"]!.ToJsonString(),
            blueprint["TotalBlockCount"]!.ToJsonString(),
            blueprint["BLP"]!.ToJsonString(),
            blueprint["BLR"]!.ToJsonString(),
            blueprint["BlockIds"]!.ToJsonString());
    }

    private static FtdBlockCatalog FallbackCatalog(string version)
    {
        var root = Path.Combine(Path.GetTempPath(), $"HullForge-HF09-catalog-{Guid.NewGuid():N}");
        var streamingAssets = Path.Combine(root, "From_The_Depths_Data", "StreamingAssets");
        Directory.CreateDirectory(streamingAssets);
        File.WriteAllText(Path.Combine(root, "From_The_Depths.exe"), string.Empty);
        File.WriteAllText(Path.Combine(streamingAssets, "BuildLog.json"),
            $"[{{\"Version\":\"{version}\"}}]");
        return FtdBlockCatalog.Load(root);
    }

    private static FtdBlockCatalog EmptyCatalog()
    {
        var root = Path.Combine(Path.GetTempPath(), "HullForge-HF07-EmptyCatalog");
        Directory.CreateDirectory(Path.Combine(root, "From_The_Depths_Data", "StreamingAssets"));
        return FtdBlockCatalog.Load(root);
    }

    private static void ArrangeAndRender(HullPreviewControl control, int width, int height)
    {
        control.Measure(new Size(width, height));
        control.Arrange(new Rect(0, 0, width, height));
        control.UpdateLayout();
        new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96,
            System.Windows.Media.PixelFormats.Pbgra32).Render(control);
    }

    /// <summary>
    /// A headless window has not run its Loaded pass, so initialize every Shape V2 control through
    /// the normal historical-preset path before reading parameters.
    /// </summary>
    private static void InitializeControls(MainWindow window) =>
        window.SelectHistoricalPresetForTests("us-fletcher-dd");

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

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "FtdHullGenerator.sln")))
                return directory.FullName;
        }
        throw new InvalidOperationException("Could not find the repository root for the Team C checks.");
    }

    private static string Describe(IEnumerable<DesignDiagnostic> diagnostics) =>
        string.Join(" | ", diagnostics.Select(diagnostic => diagnostic.ToString()));

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
