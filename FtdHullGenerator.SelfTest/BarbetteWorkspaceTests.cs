using System.Collections.Immutable;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using FtdHullGenerator;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Components;
using FtdHullGenerator.Domain.Design;
using FtdHullGenerator.Domain.Layout;
using FtdHullGenerator.Domain.Projects;
using FtdHullGenerator.Geometry.Components;
using FtdHullGenerator.Geometry.Layout;
using FtdHullGenerator.Serialization.Projects;
using FtdHullGenerator.UI.Components;
using FtdHullGenerator.UI.Editor;
using FtdHullGenerator.UI.Layout;
using FtdHullGenerator.UI.Workspace;

/// <summary>
/// BAR03 frozen 2.0 barbette editor and bow-to-stern drag ruler: exact one-metre snapping,
/// independent drag, selection consistency, create/edit/remove, authoritative bow/stern and
/// clear-space readouts, armor-insensitive gaps, invalid-placement feedback and transaction/undo.
/// </summary>
internal static class BarbetteWorkspaceTests
{
    public static void Run()
    {
        Step(nameof(OneMetreSnappingIsExactAndIndependent), OneMetreSnappingIsExactAndIndependent);
        Step(nameof(DraggingOneBarbetteNeverMovesANeighbour), DraggingOneBarbetteNeverMovesANeighbour);
        Step(nameof(SelectionIsConsistentBetweenRulerAndEditor), SelectionIsConsistentBetweenRulerAndEditor);
        Step(nameof(CreateEditRemoveUseTheOneSharedDraft), CreateEditRemoveUseTheOneSharedDraft);
        Step(nameof(BowAndSternMarginsAreCentreToTip), BowAndSternMarginsAreCentreToTip);
        Step(nameof(ClearSpaceGapsUseMeasuredClearBounds), ClearSpaceGapsUseMeasuredClearBounds);
        Step(nameof(ArmorThicknessDoesNotChangeGapsButClearDiameterDoes),
            ArmorThicknessDoesNotChangeGapsButClearDiameterDoes);
        Step(nameof(InvalidPlacementsStayRequestedAndAreDiagnosed),
            InvalidPlacementsStayRequestedAndAreDiagnosed);
        Step(nameof(EvenWidthCenterlineIntentIsPreventedNotShifted),
            EvenWidthCenterlineIntentIsPreventedNotShifted);
        Step(nameof(EvenWidthTransitionWithBarbettesIsPreventedByTheWidthControl),
            EvenWidthTransitionWithBarbettesIsPreventedByTheWidthControl);
        Step(nameof(ExplicitPlacementSurvivesPersistenceAndTheSolver),
            ExplicitPlacementSurvivesPersistenceAndTheSolver);
        Step(nameof(StaticWorkspaceKeepsTheFrozenEditorSurface), StaticWorkspaceKeepsTheFrozenEditorSurface);
        Step(nameof(RealWpfRulerSelectsDragsAndNudgesOneBarbette),
            RealWpfRulerSelectsDragsAndNudgesOneBarbette);
        Step(nameof(RealWpfArmorMaterialChangesPreserveBoundLayerControls),
            RealWpfArmorMaterialChangesPreserveBoundLayerControls);
        Console.WriteLine("Barbette workspace: one-metre snapping, independent drag, selection, " +
                          "create/edit/remove, exact margins/gaps, armor-insensitive clear gaps, " +
                          "invalid-placement feedback, persistence, transaction behavior, and re-entrant " +
                          "armor material editing passed.");
    }

    private static void Step(string name, Action action)
    {
        try
        {
            action();
        }
        catch (Exception error)
        {
            // The self-test runner reports GetBaseException().Message, so name the failing step
            // in the outermost exception rather than nesting the original as an inner exception.
            throw new InvalidOperationException($"{name}: {error.GetType().Name}: {error.Message}");
        }
    }

    private static void OneMetreSnappingIsExactAndIndependent()
    {
        // A half-metre bow datum is the real hull case: the snap lattice is whole-metre world Z.
        var model = BarbetteRulerModel.Build([], DesignMeasure.FromMetres(100),
            DesignMeasure.FromTwiceMetres(81), DesignMeasure.Zero);
        Require(model.SnapToCellAnchor(DesignMeasure.FromMetres(12.3)).Metres == 12.5,
            "A half-metre datum did not snap to the nearest whole-metre world-Z cell anchor.");
        Require((model.RulerCenterToWorldZ(model.SnapToCellAnchor(DesignMeasure.FromMetres(12.3)))).IsWholeMetre,
            "The snapped centre was not a whole-metre cell anchor.");

        var session = new EditorSession(Document(DesignMeasure.FromTwiceMetres(81)));
        using var workspace = new WorkspaceViewModel(session);
        using var editor = new BarbetteEditorViewModel(workspace);
        var barbette = editor.AddBarbette()!;
        var nodeId = barbette.NodeId;

        foreach (var metres in new[] { 11.2, 27.8, 63.4, 88.9 })
        {
            editor.MoveBarbette(barbette.Id, DesignMeasure.FromMetres(metres));
            var requested = workspace.Document.Arrangement.FindNode(nodeId)!.RequestedCenter!.Value;
            var worldZ = workspace.Document.EffectiveDatum.LayoutBowZ - requested;
            Require(worldZ.IsWholeMetre,
                $"A drag to {metres} m did not land on a whole-metre cell anchor (world Z {worldZ.Metres}).");
        }

        var before = workspace.Document.Arrangement.FindNode(nodeId)!.RequestedCenter!.Value;
        editor.MoveBarbette(barbette.Id, before + DesignMeasure.FromMetres(1));
        var after = workspace.Document.Arrangement.FindNode(nodeId)!.RequestedCenter!.Value;
        Require(after - before == DesignMeasure.FromMetres(1),
            "A one-metre move was not exactly one metre on the snap lattice.");
        Require(!editor.MoveBarbette(barbette.Id, after),
            "Re-dragging to the same metre was not an idempotent no-op move.");
    }

    private static void DraggingOneBarbetteNeverMovesANeighbour()
    {
        var session = new EditorSession(Document(DesignMeasure.Zero));
        using var workspace = new WorkspaceViewModel(session);
        using var editor = new BarbetteEditorViewModel(workspace);
        editor.AddBarbette();
        editor.AddBarbette();
        editor.AddBarbette();
        var items = editor.Barbettes.ToArray();

        var snapshot = workspace.Document.Arrangement.Nodes
            .ToDictionary(node => node.Id, node => node.RequestedCenter);

        var middle = items[1];
        var otherIds = items.Where(item => item.Id != middle.Id).Select(item => item.NodeId).ToArray();
        var otherBefore = otherIds.ToDictionary(id => id,
            id => workspace.Document.Arrangement.FindNode(id)!.RequestedCenter);

        var requested = middle.RulerCenter + DesignMeasure.FromMetres(7);
        Require(editor.MoveBarbette(middle.Id, requested),
            "The independent drag did not move the selected barbette.");
        var middleAfter = workspace.Document.Arrangement.FindNode(middle.NodeId)!.RequestedCenter;
        Require(middleAfter == requested,
            $"The dragged barbette did not land on the requested metre " +
            $"(requested {requested.Metres}, node {middleAfter?.Metres}, " +
            $"nodes {string.Join(',', workspace.Document.Arrangement.Nodes.Select(n => n.RequestedCenter?.Metres))}).");

        foreach (var id in otherIds)
            Require(workspace.Document.Arrangement.FindNode(id)!.RequestedCenter == otherBefore[id],
                $"Dragging one barbette moved neighbour '{id}'.");
        foreach (var node in workspace.Document.Arrangement.Nodes)
            Require(snapshot[node.Id] == node.RequestedCenter ||
                    node.Id == middle.NodeId,
                $"Node '{node.Id}' changed without being the dragged barbette.");
    }

    private static void SelectionIsConsistentBetweenRulerAndEditor()
    {
        var session = new EditorSession(Document(DesignMeasure.Zero));
        using var workspace = new WorkspaceViewModel(session);
        using var editor = new BarbetteEditorViewModel(workspace);
        editor.AddBarbette();
        var second = editor.AddBarbette()!;

        editor.SelectedBarbetteId = second.Id;
        Require(editor.SelectedBarbette?.Id == second.Id && editor.SelectedBarbetteId == second.Id,
            "Selecting by ruler id did not select the same barbette in the property editor.");
        Require(editor.Ruler.FindEntry(second.Id)?.BarbetteId == second.Id,
            "The ruler model does not describe the selected barbette.");
        Require(ReferenceEquals(editor.SelectedBarbette, editor.Barbettes.First(item => item.Id == second.Id)),
            "The ruler selection and the editor list disagree about the barbette identity.");

        editor.SelectBarbette(second.Id);
        Require(editor.SelectedBarbetteId == second.Id,
            "The explicit select call lost the selected barbette identity.");
    }

    private static void CreateEditRemoveUseTheOneSharedDraft()
    {
        var source = Document(DesignMeasure.Zero);
        var session = new EditorSession(source);
        using var workspace = new WorkspaceViewModel(session);
        using var editor = new BarbetteEditorViewModel(workspace);

        var barbette = editor.AddBarbette()!;
        Require(workspace.IsDraftDirty && session.Document.Barbettes.Length == 0,
            "Adding a barbette bypassed the shared workspace draft.");

        barbette.ClearDiameterMetres = 9;
        barbette.ClearDepthMetres = 6;
        barbette.TopOffsetMetres = 2;
        barbette.NeckClearSizeMetres = 5;
        barbette.AddLayer(BarbetteArmorRole.Side);
        barbette.AddLayer(BarbetteArmorRole.Roof);
        barbette.AddLayer(BarbetteArmorRole.Bottom);
        barbette.AddLayer(BarbetteArmorRole.Neck);
        barbette.RemoveLayer(BarbetteArmorRole.Bottom);

        var draft = workspace.Document.Barbettes.Single();
        Require(draft.ClearDiameter.Metres == 9 && draft.ClearDepthMetres == 6 &&
                draft.TopOffsetMetres == 2 && draft.NeckClearSizeMetres == 5 &&
                draft.SideArmor.Thickness == 2 && draft.RoofArmor.Thickness == 2 &&
                draft.BottomArmor.Thickness == 1 && draft.NeckArmor.Thickness == 2,
            "The frozen BAR01 inputs did not round-trip through the barbette editor.");

        Require(workspace.Apply() == WorkspaceApplyResult.Applied && session.Revision == 2 &&
                session.Document.Barbettes.Single().ClearDiameter.Metres == 9,
            "All barbette edits did not apply as one immutable revision.");
        Require(session.Undo() && session.Document.Barbettes.Length == 0,
            "One undo did not roll back the complete barbette creation and edit transaction.");
        Require(session.Redo() && session.Document.Barbettes.Single().NeckArmor.Thickness == 2,
            "Redo did not restore the barbette definition.");

        workspace.Cancel();
        using var reloaded = new BarbetteEditorViewModel(workspace);
        var restored = reloaded.Barbettes.Single();
        reloaded.SelectedBarbette = restored;
        Require(reloaded.RemoveSelectedBarbette(), "Removing the selected barbette was rejected.");
        Require(workspace.Document.Barbettes.Length == 0 &&
                workspace.Document.Arrangement.Nodes.Length == 0,
            "Removing a barbette left its definition or arrangement node behind.");
        workspace.Cancel();
    }

    private static void BowAndSternMarginsAreCentreToTip()
    {
        var single = ModelFor((10, 3));
        Require(single.BowMargin == DesignMeasure.FromMetres(10) &&
                single.SternMargin == DesignMeasure.FromMetres(90) && single.Segments.Count == 0,
            "A single barbette did not report centre-to-bow and centre-to-stern margins.");

        var pair = ModelFor((12, 3), (40, 3));
        Require(pair.BowMargin == DesignMeasure.FromMetres(12) && pair.SternMargin == DesignMeasure.FromMetres(60),
            "A pair did not report the foremost bow and rearmost stern margins.");

        var triple = ModelFor((8, 3), (14, 3), (22, 3));
        Require(triple.BowMargin == DesignMeasure.FromMetres(8) && triple.SternMargin == DesignMeasure.FromMetres(78),
            "Three barbettes did not keep the end margins centre-to-tip.");
        Require(triple.Segments.Count == 2 && triple.Segments[0].ClearGap == DesignMeasure.FromMetres(3) &&
                triple.Segments[1].ClearGap == DesignMeasure.FromMetres(5),
            "Three barbettes did not report both interior clear-space gaps.");
    }

    private static void ClearSpaceGapsUseMeasuredClearBounds()
    {
        var pair = ModelFor((10, 3), (14, 3));
        var segment = pair.Segments.Single();
        Require(segment.CenterDistance == DesignMeasure.FromMetres(4) &&
                segment.ClearGap == DesignMeasure.FromMetres(1) &&
                segment.State == BarbetteRulerSegmentState.Valid,
            "A one-metre structural separator did not read exactly 1 m.");

        // Exterior armor may overlap and merge; the protected clear gap must not move.
        var merged = ModelForArmored((10, 3, 5, 1, 1, 1), (14, 3, 5, 1, 1, 1));
        Require(merged.Segments.Single().ClearGap == DesignMeasure.FromMetres(1),
            "Merged exterior armor inflated or reduced the displayed clear-space gap.");

        var wide = ModelFor((10, 7), (16, 3));
        Require(wide.Segments.Single().ClearGap == DesignMeasure.FromMetres(1),
            "A wider clear diameter did not subtract its measured raster extent from the gap.");
    }

    private static void ArmorThicknessDoesNotChangeGapsButClearDiameterDoes()
    {
        var thin = ModelFor((10, 3), (14, 3));
        var thick = ModelForArmored((10, 3, 4, 3, 2, 3), (14, 3, 1, 1, 1, 1));
        Require(thin.Segments.Single().ClearGap == thick.Segments.Single().ClearGap,
            "Armor thickness changed the inter-barbette clear-space gap readout.");

        var wider = ModelFor((10, 5), (16, 3));
        Require(wider.Segments.Single().ClearGap != thin.Segments.Single().ClearGap,
            "Changing the clear diameter did not update the clear-space gap readout.");
        Require(wider.FindEntry("barbette-a")!.ClearBowExtent !=
                thin.FindEntry("barbette-a")!.ClearBowExtent,
            "Changing the clear diameter did not change the measured clear bounds.");
    }

    private static void InvalidPlacementsStayRequestedAndAreDiagnosed()
    {
        var overlapping = ModelFor((10, 3), (12, 3));
        var overlap = overlapping.Segments.Single();
        Require(overlap.State == BarbetteRulerSegmentState.Overlapping &&
                overlap.ClearGap < DesignMeasure.Zero,
            "Overlapping protected clear volumes were not reported as overlapping.");
        Require(overlapping.Diagnostics.Any(item => item.Code == BarbetteDiagnosticCodes.PlacementClearVolumeOverlap &&
                                                    item.Severity == DesignSeverity.Error),
            "An overlap did not raise the blocking clear-volume diagnostic.");
        Require(overlapping.FindEntry("barbette-a")!.RulerCenter == DesignMeasure.FromMetres(10) &&
                overlapping.FindEntry("barbette-b")!.RulerCenter == DesignMeasure.FromMetres(12),
            "Invalid placement silently moved a requested barbette position.");

        var tooClose = ModelFor((10, 3), (13, 3));
        Require(tooClose.Segments.Single().State == BarbetteRulerSegmentState.BelowMinimumSeparation,
            "A zero-metre separator was not reported below the frozen one-metre minimum.");
        Require(tooClose.Diagnostics.Any(item => item.Code == BarbetteDiagnosticCodes.MinimumClearSeparation &&
                                                 item.Severity == DesignSeverity.Error),
            "A minimum-separation failure did not raise the blocking diagnostic.");

        var beyond = ModelFor((1, 7));
        Require(beyond.FindEntry("barbette-a")!.IsBlocking &&
                beyond.Diagnostics.Any(item => item.Code == BarbetteDiagnosticCodes.ClearVolumeOutsideRuler),
            "A clear volume beyond the bow end was not diagnosed as blocking.");

        // The workspace surfaces the same blocking tone to the editor.
        var session = new EditorSession(DocumentWith((10, 3), (12, 3)));
        using var workspace = new WorkspaceViewModel(session);
        using var editor = new BarbetteEditorViewModel(workspace);
        Require(editor.Diagnostics.Any(line => line.IsBlocking && line.Text.Contains("BAR016", StringComparison.Ordinal)),
            "The barbette editor did not surface the blocking overlap diagnostic.");
    }

    /// <summary>
    /// The normal editor must not knowingly create a centerline barbette on an unsupported
    /// even-width lattice, must keep snapping to legal whole-metre anchors on a supported hull,
    /// and must never weaken the generator/document defence for direct or imported intent.
    /// </summary>
    private static void EvenWidthCenterlineIntentIsPreventedNotShifted()
    {
        var even = ShipDocument.CreateNew("Even parity", HullParameters.Default with { Width = 20 });
        var session = new EditorSession(even);
        using var workspace = new WorkspaceViewModel(session);
        using var editor = new BarbetteEditorViewModel(workspace);
        Require(!editor.CanAddBarbette, "The editor offered to add a centerline barbette on an even-width hull.");
        Require(editor.AddBarbette() is null && workspace.Document.Barbettes.Length == 0 && !workspace.IsDraftDirty,
            "Adding a barbette on an even-width hull was not prevented.");
        Require(editor.Diagnostics.Any(line => line.IsBlocking &&
                line.Text.Contains(BarbetteDiagnosticCodes.OddHullWidthRequired, StringComparison.Ordinal)),
            "The even-width prevention was not explained to the user.");

        // A direct/imported even-width document still fails closed in both the document validator
        // and the generator; the UI prevention never weakens that defence.
        var invalid = even with
        {
            Arrangement = new Arrangement(
                [new ArrangementNode("barbette-1-node", ArrangementNodeKind.Barbette, "barbette-1",
                    DesignMeasure.FromMetres(2), [], DesignMeasure.FromMetres(40))],
                [], [], DesignMeasure.Zero, DesignMeasure.Zero, ArrangementAnchorKind.BowDatum,
                ArrangementResizePolicy.PreserveAbsolute),
            Barbettes = [BarbetteDefinition.Create("barbette-1", "barbette-1-node",
                DesignMeasure.FromMetres(5), 3)],
        };
        Require(invalid.Validate().Any(diagnostic =>
                diagnostic.Code == DesignDiagnosticCodes.BarbetteOddHullWidthRequired &&
                diagnostic.Severity == DesignSeverity.Error),
            "A direct even-width document with centerline intent no longer fails closed.");

        // On a supported odd hull the ruler still snaps to a legal whole-metre world-Z anchor and
        // an established neighbour keeps its requested position.
        var oddSession = new EditorSession(ShipDocument.CreateNew("Odd parity", HullParameters.Default, "odd-parity"));
        using var oddWorkspace = new WorkspaceViewModel(oddSession);
        using var oddEditor = new BarbetteEditorViewModel(oddWorkspace);
        Require(oddEditor.CanAddBarbette, "The editor refused a barbette on an odd-width hull.");
        var first = oddEditor.AddBarbette()!;
        var second = oddEditor.AddBarbette()!;
        var neighbourBefore = oddWorkspace.Document.Arrangement.FindNode(first.NodeId)!.RequestedCenter;
        oddEditor.MoveBarbette(second.Id, DesignMeasure.FromMetres(37.4));
        var moved = oddWorkspace.Document.Arrangement.FindNode(second.NodeId)!.RequestedCenter!.Value;
        Require((oddWorkspace.Document.EffectiveDatum.LayoutBowZ - moved).IsWholeMetre,
            "A drag did not land on a legal whole-metre cell anchor.");
        Require(oddWorkspace.Document.Arrangement.FindNode(first.NodeId)!.RequestedCenter == neighbourBefore,
            "A legal snap moved an established neighbour.");
    }

    /// <summary>
    /// The normal width control must refuse an odd-to-even change while centerline barbettes
    /// exist, keeping both the established positions and the last legal width.
    /// </summary>
    private static void EvenWidthTransitionWithBarbettesIsPreventedByTheWidthControl()
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
                    var definition = BarbetteDefinition.Create("barbette-1", "barbette-1-node",
                        DesignMeasure.FromMetres(5), 3);
                    var node = new ArrangementNode(definition.NodeId, ArrangementNodeKind.Barbette,
                        definition.Id, DesignMeasure.FromMetres(4), [], DesignMeasure.FromMetres(40));
                    var document = ShipDocument.CreateNew("Parity transition", HullParameters.Default, "parity-transition") with
                    {
                        Arrangement = new Arrangement([node], [], [], DesignMeasure.Zero, DesignMeasure.Zero,
                            ArrangementAnchorKind.BowDatum, ArrangementResizePolicy.PreserveAbsolute),
                        Barbettes = [definition],
                    };
                    window.EditorSessionForTests.New(document);
                    Require(window.EditorSessionForTests.Document.Hull.Width % 2 != 0,
                        "The parity probe did not start from an odd width.");

                    window.WidthInputForTests.Value = 20;
                    window.QueuePreviewForTests();
                    PumpDispatcher();

                    Require(window.EditorSessionForTests.Document.Hull.Width % 2 != 0,
                        "The even width was committed while a centerline barbette existed.");
                    Require(window.EditorSessionForTests.Document.Barbettes.Length == 1 &&
                            window.EditorSessionForTests.Document.Arrangement.FindNode(definition.NodeId)!
                                .RequestedCenter == DesignMeasure.FromMetres(40),
                        "The parity prevention moved or deleted the established barbette.");
                    Require(window.StatusTextForTests.Contains("odd hull width", StringComparison.Ordinal),
                        "The parity prevention did not explain why the even width was refused. Status: " +
                        window.StatusTextForTests);
                }
                finally
                {
                    window.Close();
                }
            }
            catch (Exception error)
            {
                failure = error;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw new InvalidOperationException("The width parity prevention probe failed: " + failure.Message, failure);
    }

    private static void ExplicitPlacementSurvivesPersistenceAndTheSolver()
    {
        var document = DocumentWith((10, 3), (40, 3), (70, 3));
        var serialized = ProjectDocumentSerializer.Serialize(document);
        Require(serialized.Succeeded, Describe(serialized.Diagnostics));
        var restored = ProjectDocumentSerializer.Deserialize(serialized.Json!);
        Require(restored.Succeeded && restored.Document is not null, Describe(restored.Diagnostics));
        foreach (var node in document.Arrangement.Nodes)
        {
            var restoredNode = restored.Document!.Arrangement.FindNode(node.Id);
            Require(restoredNode?.RequestedCenter == node.RequestedCenter,
                $"Explicit placement for node '{node.Id}' did not survive project persistence.");
        }

        var solution = ArrangementSolver.Solve(restored.Document!.Arrangement,
            new ArrangementSolveOptions(restored.Document.EffectiveDatum.LayoutBowZ,
                DesignMeasure.FromMetres(restored.Document.Hull.Length),
                CenterPlaneX: restored.Document.EffectiveDatum.CenterPlaneX));
        Require(solution.IsSolved, Describe(solution.Diagnostics));
        Require(solution.Nodes.Count == 3, "The pinned arrangement did not solve every barbette node.");
        foreach (var node in restored.Document.Arrangement.Nodes)
        {
            var solved = solution.FindNode(node.Id)!;
            Require(solved.RulerCenter == node.RequestedCenter,
                $"The solver moved pinned node '{node.Id}' away from its requested centre.");
        }
    }

    private static void StaticWorkspaceKeepsTheFrozenEditorSurface()
    {
        var path = Path.Combine(FindRepositoryRoot(), "FtdHullGenerator", "UI", "Layout", "ArrangementView.xaml");
        var xaml = XDocument.Load(path);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        Require(xaml.Descendants().Any(element => element.Name.LocalName == "BarbetteRuler"),
            "The frozen arrangement workspace no longer hosts the barbette ruler.");
        Require(!xaml.Descendants(presentation + "TextBox").Any(),
            "The frozen barbette workspace regained a free-form typed field.");
        Require(xaml.Descendants().Count(element => element.Name.LocalName == "BarbetteDiameterInput") == 1,
            "The barbette editor must expose exactly one dedicated clear-diameter diameter input.");
        Require(!xaml.Descendants().Any(element => element.Name.LocalName == "DesignMeasureInput"),
            "The barbette clear-diameter field still uses the generic half-metre DesignMeasureInput.");
        Require(xaml.Descendants(presentation + "Button")
                .Select(element => (string?)element.Attribute("Content"))
                .Where(content => content is not null)
                .Any(content => content == "Add barbette") &&
            xaml.Descendants(presentation + "Button")
                .Any(element => (string?)element.Attribute("Content") == "Remove barbette"),
            "The frozen barbette create/remove actions are unavailable.");
        Require(xaml.Descendants().Any(element =>
                ((string?)element.Attribute("ItemsSource"))?.Contains("NeckClearSizeChoices", StringComparison.Ordinal) == true),
            "The frozen 1/3/5 m neck clear size choice is no longer exposed.");
        Require(xaml.Descendants().Any(element =>
                ((string?)element.Attribute("ItemsSource"))?.Contains("SelectedBarbette.Stacks", StringComparison.Ordinal) == true),
            "The four independent barbette armor stacks are no longer exposed.");
        Require(xaml.Descendants().Any(element =>
                ((string?)element.Attribute("ItemsSource"))?.Contains("Diagnostics", StringComparison.Ordinal) == true),
            "The workspace no longer surfaces barbette diagnostics.");
    }

    private static void RealWpfRulerSelectsDragsAndNudgesOneBarbette()
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

                var session = new EditorSession(Document(DesignMeasure.Zero));
                using var workspace = new WorkspaceViewModel(session);
                var view = new ArrangementView { Workspace = workspace };
                using var source = new HwndSource(new HwndSourceParameters("HullForge BAR03 ruler probe")
                {
                    Width = 1000,
                    Height = 520,
                    WindowStyle = 0,
                });
                source.RootVisual = view;
                PumpDispatcher();

                var editor = view.ViewModel!;
                view.AddBarbetteForTests();
                view.AddBarbetteForTests();
                PumpDispatcher();
                Require(editor.Barbettes.Count == 2 && view.RulerForTests.Model is { HasEntries: true },
                    "The real workspace did not project both barbettes onto the ruler.");

                var items = editor.Barbettes.ToArray();
                var neighbour = items[0];
                var draggedItem = items[1];
                var ruler = view.RulerForTests;
                Require(ruler.HitTestForTests(ruler.XForTests(draggedItem.RulerCenter))?.BarbetteId == draggedItem.Id,
                    "Clicking a ruler marker did not resolve the same barbette as the property editor.");

                var neighbourBefore = workspace.Document.Arrangement.FindNode(neighbour.NodeId)!.RequestedCenter;
                var requested = draggedItem.RulerCenter - DesignMeasure.FromMetres(9);
                ruler.DragToForTests(draggedItem.Id, ruler.XForTests(requested));
                PumpDispatcher();
                var dragged = workspace.Document.Arrangement.FindNode(draggedItem.NodeId)!.RequestedCenter!.Value;
                Require(dragged == requested,
                    $"The real ruler drag landed on {dragged.Metres} m instead of the requested {requested.Metres} m.");
                Require(workspace.Document.Arrangement.FindNode(neighbour.NodeId)!.RequestedCenter == neighbourBefore,
                    "The real ruler drag moved a neighbouring barbette.");

                ruler.SelectedBarbetteId = draggedItem.Id;
                RaisePreviewKey(ruler, Key.Left);
                PumpDispatcher();
                var nudged = workspace.Document.Arrangement.FindNode(draggedItem.NodeId)!.RequestedCenter!.Value;
                Require(dragged - nudged == DesignMeasure.FromMetres(1),
                    "An arrow key did not move the selected barbette exactly one metre toward the bow.");

                view.RemoveBarbetteForTests();
                PumpDispatcher();
                Require(workspace.Document.Barbettes.Length == 1 &&
                        workspace.Document.Arrangement.Nodes.Length == 1 &&
                        workspace.Document.Barbettes[0].Id == neighbour.Id,
                    "The real Remove barbette action did not drop exactly the selected definition and node.");
            }
            catch (Exception error)
            {
                failure = error;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw new InvalidOperationException("The real WPF barbette ruler probe failed.", failure);
    }

    private static void RealWpfArmorMaterialChangesPreserveBoundLayerControls()
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

                var session = new EditorSession(Document(DesignMeasure.Zero));
                using var workspace = new WorkspaceViewModel(session);
                var view = new ArrangementView { Workspace = workspace };
                using var source = new HwndSource(new HwndSourceParameters("HullForge BAR03 armor probe")
                {
                    Width = 1000,
                    Height = 520,
                    WindowStyle = 0,
                });
                source.RootVisual = view;
                PumpDispatcher();

                view.AddBarbetteForTests();
                PumpDispatcher();
                Require(workspace.Apply() == WorkspaceApplyResult.Applied,
                    "The real arrangement workspace could not commit the created barbette before the armor probe.");
                PumpDispatcher();

                var editor = view.ViewModel!;
                var barbette = editor.SelectedBarbette ??
                    throw new InvalidOperationException("Creating a barbette did not select it in the real editor.");
                var combos = ArmorMaterialCombos(view).ToArray();
                Require(combos.Length == 4,
                    $"The real arrangement editor exposed {combos.Length} barbette material ComboBoxes instead of four.");

                foreach (var stack in barbette.Stacks)
                {
                    var layer = stack.Layers.Single();
                    var combo = combos.SingleOrDefault(item => ReferenceEquals(item.DataContext, layer)) ??
                        throw new InvalidOperationException($"Could not locate the {stack.Role} layer ComboBox.");
                    var original = workspace.Document.Barbettes.Single();

                    foreach (var material in new[]
                             { MaterialKind.HeavyArmor, MaterialKind.Wood, MaterialKind.Metal })
                    {
                        combo.SelectedItem = layer.MaterialChoices.Single(choice => choice.Value == material);
                        PumpDispatcher();

                        var current = workspace.Document.Barbettes.Single();
                        var currentStack = BarbetteEditorItem.StackFor(current, stack.Role);
                        Require(currentStack.Layers.Count == 1 &&
                                currentStack.Layers[0].Material == material &&
                                currentStack.Layers[0].Construction == ArmorConstruction.Solid,
                            $"Changing the {stack.Role} material did not update exactly its solid one-metre layer.");
                        foreach (var other in barbette.Stacks.Where(other => other.Role != stack.Role))
                        {
                            var otherStack = BarbetteEditorItem.StackFor(current, other.Role);
                            var expected = BarbetteEditorItem.StackFor(original, other.Role).Layers.Single();
                            Require(otherStack.Layers.Single() == expected,
                                $"Changing the {stack.Role} material changed the {other.Role} armor stack.");
                        }

                        Require(ReferenceEquals(stack.Layers.Single(), layer) &&
                                ArmorMaterialCombos(view).Contains(combo) &&
                                ReferenceEquals(combo.DataContext, layer),
                            $"Changing the {stack.Role} material rebuilt the bound layer control during its setter.");
                        Require(workspace.HasDraft && workspace.IsDraftDirty,
                            $"Changing the {stack.Role} material did not leave a live workspace draft.");
                        Require(workspace.Document.Validate().All(diagnostic => !diagnostic.IsError),
                            $"Changing the {stack.Role} material left the draft with a blocking diagnostic.");
                    }
                }

                // Apply/Undo/Redo must carry the selected material through the same committed
                // revision path after the complete Metal -> HeavyArmor -> Wood -> Metal cycle.
                var sideStack = barbette.Stacks.Single(stack => stack.Role == BarbetteArmorRole.Side);
                var sideLayer = sideStack.Layers.Single();
                var sideCombo = combos.Single(item => ReferenceEquals(item.DataContext, sideLayer));
                sideCombo.SelectedItem = sideLayer.MaterialChoices.Single(choice =>
                    choice.Value == MaterialKind.HeavyArmor);
                PumpDispatcher();
                Require(workspace.Apply() == WorkspaceApplyResult.Applied,
                    "Applying the selected barbette material did not commit the workspace draft.");
                Require(MaterialFor(session.Document.Barbettes.Single(), BarbetteArmorRole.Side) ==
                        MaterialKind.HeavyArmor,
                    "Apply did not preserve the selected HeavyArmor side layer.");

                Require(session.Undo(), "Undo did not accept the committed barbette material edit.");
                PumpDispatcher();
                Require(MaterialFor(session.Document.Barbettes.Single(), BarbetteArmorRole.Side) ==
                        MaterialKind.Metal && sideCombo.SelectedItem is BarbetteEditorChoice<MaterialKind?>
                        {
                            Value: MaterialKind.Metal,
                        },
                    "Undo did not restore the selected Metal side layer in the live editor.");

                Require(session.Redo(), "Redo did not accept the committed barbette material edit.");
                PumpDispatcher();
                Require(MaterialFor(session.Document.Barbettes.Single(), BarbetteArmorRole.Side) ==
                        MaterialKind.HeavyArmor && sideCombo.SelectedItem is BarbetteEditorChoice<MaterialKind?>
                        {
                            Value: MaterialKind.HeavyArmor,
                        },
                    "Redo did not restore the selected HeavyArmor side layer in the live editor.");
            }
            catch (Exception error)
            {
                failure = error;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(60)))
            throw new InvalidOperationException("The real WPF barbette armor probe did not complete.");
        if (failure is not null)
            throw new InvalidOperationException(
                $"The real WPF barbette armor probe failed: {failure.Message}", failure);
    }

    private static IEnumerable<ComboBox> ArmorMaterialCombos(DependencyObject root) =>
        Descendants<ComboBox>(root).Where(combo =>
            AutomationProperties.GetName(combo) == "Barbette armor layer material");

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
                yield return match;
            foreach (var descendant in Descendants<T>(child))
                yield return descendant;
        }
    }

    private static MaterialKind MaterialFor(BarbetteDefinition definition, BarbetteArmorRole role) =>
        BarbetteEditorItem.StackFor(definition, role).Layers.Single().Material ??
        throw new InvalidOperationException($"The {role} barbette armor layer unexpectedly became air.");

    private static BarbetteRulerModel ModelFor(params (int CenterMetres, int DiameterMetres)[] placements) =>
        ModelForArmored(placements.Select(item => (item.CenterMetres, item.DiameterMetres, 1, 1, 1, 1)).ToArray());

    private static BarbetteRulerModel ModelForArmored(
        params (int CenterMetres, int DiameterMetres, int SideArmorThickness, int RoofArmorThickness,
            int BottomArmorThickness, int NeckArmorThickness)[] placements)
    {
        var requests = placements.Select((placement, index) =>
        {
            var id = $"barbette-{(char)('a' + index)}";
            var definition = BarbetteDefinition.Create(id, $"{id}-node",
                DesignMeasure.FromMetres(placement.DiameterMetres), 3,
                sideArmor: Armor(placement.SideArmorThickness),
                roofArmor: Armor(placement.RoofArmorThickness),
                bottomArmor: Armor(placement.BottomArmorThickness),
                neckArmor: Armor(placement.NeckArmorThickness));
            return new BarbetteRulerRequest(id, $"{id}-node", definition,
                DesignMeasure.FromMetres(placement.CenterMetres));
        }).ToArray();
        return BarbetteRulerModel.Build(requests, DesignMeasure.FromMetres(100), DesignMeasure.Zero,
            DesignMeasure.Zero);
    }

    private static ArmorLayout Armor(int thickness) =>
        new(Enumerable.Repeat(MaterialKind.Metal, thickness));

    private static ShipDocument Document(DesignMeasure layoutBowZ) =>
        ShipDocument.CreateNew("BAR03", HullParameters.Default) with
        {
            Datum = new LayoutDatum(layoutBowZ, DesignMeasure.Zero),
        };

    private static ShipDocument DocumentWith(params (int CenterMetres, int DiameterMetres)[] placements)
    {
        var definitions = new List<BarbetteDefinition>();
        var nodes = new List<ArrangementNode>();
        for (var index = 0; index < placements.Length; index++)
        {
            var id = $"barbette-{(char)('a' + index)}";
            definitions.Add(BarbetteDefinition.Create(id, $"{id}-node",
                DesignMeasure.FromMetres(placements[index].DiameterMetres), 3));
            nodes.Add(new ArrangementNode($"{id}-node", ArrangementNodeKind.Barbette, id,
                DesignMeasure.FromMetres(2), [], DesignMeasure.FromMetres(placements[index].CenterMetres)));
        }

        return Document(DesignMeasure.Zero) with
        {
            Arrangement = new Arrangement([.. nodes], [], [], DesignMeasure.Zero, DesignMeasure.Zero,
                ArrangementAnchorKind.BowDatum, ArrangementResizePolicy.PreserveAbsolute),
            Barbettes = [.. definitions],
        };
    }

    private static string Describe(IEnumerable<DesignDiagnostic> diagnostics) =>
        string.Join(" | ", diagnostics.Select(item => item.ToString()));

    private static void RaisePreviewKey(UIElement source, Key key)
    {
        var presentationSource = PresentationSource.FromVisual(source) ??
            throw new InvalidOperationException("The WPF key source is not connected to a presentation source.");
        var eventArgs = new KeyEventArgs(Keyboard.PrimaryDevice, presentationSource,
            Environment.TickCount, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
            Source = source,
        };
        source.RaiseEvent(eventArgs);
    }

    private static void PumpDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null;
             directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "FtdHullGenerator.sln")))
                return directory.FullName;
        throw new InvalidOperationException("Could not find the repository root for BAR03 checks.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
