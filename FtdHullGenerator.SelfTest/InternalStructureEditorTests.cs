using System.Collections.Immutable;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Xml.Linq;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Components;
using FtdHullGenerator.Domain.Design;
using FtdHullGenerator.Domain.Layout;
using FtdHullGenerator.Domain.Projects;
using FtdHullGenerator.Geometry.Components;
using FtdHullGenerator.Geometry.Composition;
using FtdHullGenerator.Infrastructure;
using FtdHullGenerator.Serialization;
using FtdHullGenerator.Serialization.Projects;
using FtdHullGenerator.UI;
using FtdHullGenerator.UI.Components;
using FtdHullGenerator.UI.Editor;
using FtdHullGenerator.UI.Workspace;

/// <summary>U02 editor, presentation, persistence and resolved-export regressions.</summary>
internal static class InternalStructureEditorTests
{
    public static void Run(bool full = false)
    {
        AllFamiliesShareOneDraftAndRoundTripToResolvedExport();
        InvalidParityAndFixedCountRemainVisibleIntent();
        AutomaticLayoutsStayOnGridAndShareOneUndo(full);
        DeveloperFeatureExposureFailsClosedAcrossTiers();
        RealizedCountAndClippingAreProjectedWithoutRecomputation();
        StaticEditorSurfaceKeepsGuidesAndAccessibleControls();
        RealWpfPanelRoutesSelectedCutawayAndUsesSharedWorkspace();
        ReloadingTheComponentsTabKeepsTheEditorAlive();
        Console.WriteLine("Internal structure editor: atomic three-family editing, persistence/export, " +
                          "invalid-intent diagnostics, realization projection, guides and accessibility passed.");
    }

    private static void AllFamiliesShareOneDraftAndRoundTripToResolvedExport()
    {
        var parameters = HullParameters.Default with
        {
            Smoothing = SmoothingMethod.None,
            Superstructure = SuperstructureSettings.Default with { Enabled = false },
        };
        var source = ShipDocument.CreateNew("U02", parameters, "u02-all") with
        {
            Datum = new LayoutDatum(DesignMeasure.Zero, DesignMeasure.Zero),
        };
        var session = new EditorSession(source);
        using var workspace = new WorkspaceViewModel(session);
        using var editor = new InternalStructureEditorViewModel(workspace);

        var longitudinal = editor.Families.Single(item =>
            item.Family == InternalPlaneFamily.LongitudinalBulkhead);
        longitudinal.Enabled = true;
        longitudinal.Thickness = 1;
        longitudinal.Material = MaterialKind.Metal;
        longitudinal.SpacingKind = InternalSpacingKind.ClearCompartmentGap;
        longitudinal.SpacingMetres = 5;
        longitudinal.CountMode = InternalPlaneCountMode.FixedCount;
        longitudinal.Count = 1;
        longitudinal.IncludeCentralPlane = true;

        var deck = editor.Families.Single(item => item.Family == InternalPlaneFamily.InternalDeck);
        deck.Enabled = true;
        deck.Material = MaterialKind.Metal;
        deck.Thickness = 1;
        deck.CountMode = InternalPlaneCountMode.FixedCount;
        deck.Count = 1;
        deck.OffsetMetres = 2;
        deck.Datum = InternalPlaneDatum.HullOrigin;
        deck.Direction = InternalRepeatDirection.Positive;

        var transverse = editor.Families.Single(item =>
            item.Family == InternalPlaneFamily.TransverseBulkhead);
        transverse.Enabled = true;
        transverse.Material = MaterialKind.Metal;
        transverse.Thickness = 1;
        transverse.CountMode = InternalPlaneCountMode.FixedCount;
        transverse.Count = 1;
        transverse.OffsetMetres = 0;
        transverse.Datum = InternalPlaneDatum.HullOrigin;
        transverse.Direction = InternalRepeatDirection.Positive;

        Require(workspace.IsDraftDirty && session.Document.Internals == InternalStructure.Disabled,
            "The U02 controls bypassed the workspace transaction or mutated the committed document.");
        Require(workspace.Apply() == WorkspaceApplyResult.Applied && session.Revision == 2 &&
                session.Document.Internals.Families.All(family => family.Enabled),
            "All three family edits did not apply as one immutable revision.");

        var serialized = ProjectDocumentSerializer.Serialize(session.Document);
        Require(serialized.Succeeded, Describe(serialized.Diagnostics));
        var restored = ProjectDocumentSerializer.Deserialize(serialized.Json!);
        Require(restored.Succeeded &&
                restored.Document!.Internals.Families.SequenceEqual(session.Document.Internals.Families) &&
                restored.Document.Internals.JunctionPriority.SequenceEqual(
                    session.Document.Internals.JunctionPriority),
            "U02 family material/spacing/thickness/offset intent did not survive project persistence.");

        var catalog = EmptyCatalog();
        var generated = new ShipGenerationService().Generate(restored.Document!, 2, catalog);
        Require(generated.IsValid && generated.Snapshot is not null,
            "The saved U02 intent did not reach the real composition/export snapshot: " +
            Describe(generated.Diagnostics));
        var snapshot = generated.Snapshot!;
        Require(snapshot.Internals.Planes.Select(plane => plane.Family).Distinct().Count() == 3 &&
                snapshot.Hull.CellProvenance.Any(item => item.Owners.Any(owner =>
                    owner.Role == PhysicalCellRole.InternalStructure)),
            "The saved U02 intent did not materialize all three families with internal provenance.");

        var output = Path.Combine(Path.GetTempPath(), "HullForge-U02-" + Guid.NewGuid().ToString("n"));
        try
        {
            var export = new BlueprintExporter().Export(snapshot, restored.Document!, 2, output, "u02");
            Require(File.Exists(export.FilePath) && export.BlockCount == snapshot.Hull.BlockCount,
                "The U02 snapshot was not exported through the revision-checked snapshot overload.");
        }
        finally
        {
            if (Directory.Exists(output))
                Directory.Delete(output, recursive: true);
        }
    }

    private static void InvalidParityAndFixedCountRemainVisibleIntent()
    {
        var invalid = new InternalStructureFamily(InternalPlaneFamily.LongitudinalBulkhead,
            true, 1, MaterialKind.Metal, DesignMeasure.FromMetres(6), 2,
            DesignMeasure.Zero, true)
        {
            Datum = InternalPlaneDatum.CenterPlane,
            Direction = InternalRepeatDirection.Both,
            IncludeCentralPlane = true,
            CountMode = InternalPlaneCountMode.FixedCount,
        };
        var document = ShipDocument.CreateNew("Invalid", HullParameters.Default with { Width = 20 }) with
        {
            Datum = new LayoutDatum(DesignMeasure.Zero, DesignMeasure.FromTwiceMetres(-1)),
            Internals = Structure(invalid),
        };
        var session = new EditorSession(document);
        using var workspace = new WorkspaceViewModel(session);
        using var editor = new InternalStructureEditorViewModel(workspace);
        editor.SelectedFamily = editor.Families.Single(item =>
            item.Family == InternalPlaneFamily.LongitudinalBulkhead);

        Require(editor.SelectedDiagnostics.Any(message =>
                    message.Contains(InternalStructureDiagnosticCodes.CenterSlabParity,
                        StringComparison.Ordinal)) &&
                editor.SelectedDiagnostics.Any(message =>
                    message.Contains(InternalStructureDiagnosticCodes.FamilyConfigurationInvalid,
                        StringComparison.Ordinal)) &&
                editor.SelectedFamily.Thickness == 1 && editor.SelectedFamily.Count == 2 &&
                editor.SelectedFamily.IncludeCentralPlane,
            "The editor hid or silently corrected invalid parity/fixed-count intent.");
        Require(!workspace.IsDraftDirty && editor.SelectedFamily.HasCustomLayout,
            "Opening a saved custom layout edited it or concealed its status.");
        editor.SelectedFamily.UseAutomaticLayout();
        Require(editor.SelectedFamily.UsesSimpleLayout &&
                !workspace.Document.Internals.Validate().Any(d => d.IsError),
            "Automatic layout did not repair the loaded invalid configuration.");
        workspace.Cancel();
        Require(workspace.Document.Internals.Find(InternalPlaneFamily.LongitudinalBulkhead) == invalid,
            "Cancelling the repair lost the saved custom settings.");
    }

    private static void AutomaticLayoutsStayOnGridAndShareOneUndo(bool full)
    {
        foreach (var width in full ? new[] { 9, 10, 20, 21 } : new[] { 20, 21 })
        {
            var parameters = HullParameters.Default with { Width = width };
            var session = new EditorSession(ShipDocument.CreateNew("Automatic", parameters));
            using var workspace = new WorkspaceViewModel(session);
            using var editor = new InternalStructureEditorViewModel(workspace);
            var context = FtdHullGenerator.Geometry.HullGenerator.CreateContext(parameters);
            foreach (var family in editor.Families)
            {
                family.Enabled = true;
                Require(ReferenceEquals(editor.SelectedFamily, family) && family.Count > 0 &&
                        family.UsesSimpleLayout && !workspace.Document.Internals.Validate().Any(d => d.IsError),
                    "Checking a family did not select it and create valid bounded intent.");
                var gaps = family.ClearGapChoices;
                foreach (var gap in full ? gaps : new[] { gaps.First(), gaps[gaps.Count / 2], gaps.Last() })
                {
                    family.ClearGap = gap;
                    var generated = BulkheadGenerator.Generate(context, workspace.Document.Internals);
                    Require(generated.IsValid,
                        $"Automatic {family.Family}, width {width}, gap {gap}: {Describe(generated.Diagnostics)}");
                }
                family.ClearGap = family.ClearGapChoices.First();
            }
            var expected = workspace.Document.Internals;
            Require(workspace.Apply() == WorkspaceApplyResult.Applied, "Automatic layout did not apply.");
            var saved = ProjectDocumentSerializer.Serialize(session.Document);
            Require(saved.Succeeded && ProjectDocumentSerializer.Deserialize(saved.Json!).Document!.Internals.Families
                    .SequenceEqual(expected.Families), "Automatic layout changed during save/load.");
            Require(session.Undo() && session.Document.Internals == InternalStructure.Disabled && session.Redo() &&
                    session.Document.Internals == expected, "Automatic layout was not one undo/redo step.");
            editor.Families[0].Enabled = false;
            workspace.Cancel();
            Require(editor.Families[0].Enabled && !workspace.IsDraftDirty, "Cancel lost the original layout.");
        }
    }

    private static void DeveloperFeatureExposureFailsClosedAcrossTiers()
    {
        var enabled = ShipDocument.CreateNew("Loaded", HullParameters.Default) with
        {
            Internals = Structure(new InternalStructureFamily(
                InternalPlaneFamily.TransverseBulkhead, true, 1, MaterialKind.Metal,
                DesignMeasure.FromMetres(8), 1, DesignMeasure.Zero, false)
            {
                Datum = InternalPlaneDatum.HullOrigin,
                Direction = InternalRepeatDirection.Positive,
            }),
        };
        foreach (var experimentalEnabled in new[] { false, true })
        {
            var exposure = new FeatureExposurePolicy(experimentalEnabled);
            Require(InternalStructureFeatureAccess.IsEditorAvailable(exposure) == experimentalEnabled,
                $"C09 editor exposure drifted for experimental={experimentalEnabled}.");
            Require(InternalStructureFeatureAccess.CanPreviewOrExport(enabled, exposure,
                        out var reason) == experimentalEnabled &&
                    (experimentalEnabled ? reason is null : reason?.Contains("Experimental Features", StringComparison.Ordinal) == true),
                $"A loaded internal document did not fail closed for experimental={experimentalEnabled}.");
        }

        Require(InternalStructureFeatureAccess.CanPreviewOrExport(
                ShipDocument.CreateNew("Disabled", HullParameters.Default), new FeatureExposurePolicy(false), out _),
            "C09 blocked an ordinary feature-free document.");
    }

    private static void RealizedCountAndClippingAreProjectedWithoutRecomputation()
    {
        var session = new EditorSession(ShipDocument.CreateNew("Projection", HullParameters.Default));
        using var workspace = new WorkspaceViewModel(session);
        using var editor = new InternalStructureEditorViewModel(workspace);
        editor.SelectedFamily = editor.Families.Single(item => item.Family == InternalPlaneFamily.InternalDeck);
        editor.SelectedFamily.Enabled = true;
        editor.SetGenerationResult(new InternalStructureGenerationResult([], [
            new InternalPlaneRealization("internal:deck:001", InternalPlaneFamily.InternalDeck,
                MaterialKind.Metal, DesignMeasure.FromMetres(2), 100, 70, 11, 19, null),
            new InternalPlaneRealization("internal:deck:002", InternalPlaneFamily.InternalDeck,
                MaterialKind.Metal, DesignMeasure.FromMetres(6), 80, 60, 3, 17, null),
        ], [], 180));

        Require(editor.SelectedFamily.RealizedSummary.Contains("2 plane(s)", StringComparison.Ordinal) &&
                editor.SelectedFamily.RealizedSummary.Contains("36 cavity", StringComparison.Ordinal) &&
                editor.SelectedFamily.RealizedSummary.Contains("14 reserved", StringComparison.Ordinal),
            "Realized count or clipping diagnostics were recomputed or omitted by the UI projection.");
        editor.SetGenerationDiagnostics([
            new DesignDiagnostic(InternalStructureDiagnosticCodes.FixedCountDoesNotFit,
                DesignSeverity.Error, "The requested deck plane has no usable cavity cells.",
                "internal:deck:002"),
        ]);
        Require(editor.SelectedDiagnostics.Any(message =>
                message.Contains(InternalStructureDiagnosticCodes.FixedCountDoesNotFit,
                    StringComparison.Ordinal)),
            "A rejected fixed-count realization was not retained on the selected family panel.");
    }

    private static void StaticEditorSurfaceKeepsGuidesAndAccessibleControls()
    {
        var path = Path.Combine(FindRepositoryRoot(), "FtdHullGenerator", "UI", "Components",
            "InternalStructureEditor.xaml");
        var xaml = XDocument.Load(path);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        Require(xaml.Descendants(presentation + "ComboBox").Count() == 2 &&
                !xaml.Descendants().Any(e => e.Name.LocalName == "DesignMeasureInput"),
            "The simple panel regained advanced placement controls or free-form numeric entry.");
        Require(xaml.Descendants().Count(element => element.Name.LocalName == "InternalStructureGuide") == 2,
            "U02 no longer shows both front and side family guides.");
        Require(xaml.Descendants(presentation + "ComboBox").All(element =>
                    element.Attributes().Any(attribute =>
                        attribute.Name.LocalName.EndsWith("Name", StringComparison.Ordinal))),
            "A U02 family control lost its automation name.");
        Require(xaml.Descendants(presentation + "Button").Any(element =>
                    (string?)element.Attribute("Content") == "Inspect selected family in 3D cutaway"),
            "The selected-family cutaway action is unavailable.");
    }

    private static void RealWpfPanelRoutesSelectedCutawayAndUsesSharedWorkspace()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var session = new EditorSession(ShipDocument.CreateNew("WPF U02", HullParameters.Default));
                using var workspace = new WorkspaceViewModel(session);
                using var control = new InternalStructureEditor();
                control.Attach(workspace);
                var mainWindow = new FtdHullGenerator.MainWindow();
                Require((mainWindow.InternalStructureEditorForTests is not null) ==
                        mainWindow.ExperimentalFeaturesEnabledForTests,
                    "MainWindow editor exposure did not follow the persisted runtime policy.");
                Require(control.ViewModel is { Families.Count: 3 },
                    "The real U02 control did not attach all three families to the supplied workspace.");
                var viewModel = control.ViewModel!;

                viewModel.SelectedFamily = viewModel.Families.Single(item =>
                    item.Family == InternalPlaneFamily.TransverseBulkhead);
                InternalPlaneFamily? requested = null;
                control.CutawayRequested += (_, eventArgs) => requested = eventArgs.Family;
                var button = Descendants(control).OfType<Button>().Single(item =>
                    string.Equals(item.Content as string, "Inspect selected family in 3D cutaway",
                        StringComparison.Ordinal));
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(requested == InternalPlaneFamily.TransverseBulkhead,
                    "The real U02 panel did not route the selected family to the cutaway boundary.");

                viewModel.SelectedFamily.Enabled = true;
                Require(workspace.IsDraftDirty && session.Document.Internals == InternalStructure.Disabled,
                    "The real U02 control bypassed the shared workspace draft.");
                viewModel.SelectedFamily.Count = 1;

                var deck = viewModel.Families.Single(item => item.Family == InternalPlaneFamily.InternalDeck);
                var spacing = new DesignMeasureInput();
                spacing.SetBinding(DesignMeasureInput.ValueProperty,
                    new Binding(nameof(InternalFamilyEditorViewModel.SpacingMetres))
                    {
                        Source = deck,
                        Mode = BindingMode.TwoWay,
                    });
                var offset = new DesignMeasureInput();
                offset.SetBinding(DesignMeasureInput.ValueProperty,
                    new Binding(nameof(InternalFamilyEditorViewModel.OffsetMetres))
                    {
                        Source = deck,
                        Mode = BindingMode.TwoWay,
                    });
                var numericPanel = new StackPanel { Children = { spacing, offset } };
                using var numericSource = new HwndSource(new HwndSourceParameters("HullForge U02 numeric input")
                {
                    Width = 420,
                    Height = 160,
                    WindowStyle = 0,
                });
                numericSource.RootVisual = numericPanel;
                PumpDispatcher();
                spacing.TextBoxForTests.Text = "75000.5";
                RaisePreviewKey(spacing.TextBoxForTests, Key.Enter);
                offset.TextBoxForTests.Text = "-99999.5";
                RaisePreviewKey(offset.TextBoxForTests, Key.Up);
                Require(deck.SpacingMetres == 75000.5 && deck.OffsetMetres == -99999.0,
                    "Full-range half-metre numeric entry or keyboard stepping was capped or rounded.");
                var persisted = ProjectDocumentSerializer.Serialize(workspace.Document);
                Require(persisted.Succeeded, Describe(persisted.Diagnostics));
                var restored = ProjectDocumentSerializer.Deserialize(persisted.Json!);
                Require(restored.Succeeded, Describe(restored.Diagnostics));
                var restoredDeck = restored.Document!.Internals.Find(InternalPlaneFamily.InternalDeck)!;
                Require(restoredDeck.Spacing.Metres == 75000.5 && restoredDeck.Offset.Metres == -99999.0,
                    "Full-range half-metre numeric intent did not round-trip through persistence.");

                var preview = new HullPreviewControl();
                var selectionHull = SelectionHull();
                preview.SetHull(selectionHull, highlightEdits: false);
                foreach (var expectedFamily in Enum.GetValues<InternalPlaneFamily>())
                {
                    preview.Cutaway = expectedFamily switch
                    {
                        InternalPlaneFamily.LongitudinalBulkhead => CutawayPlane.Centreline,
                        InternalPlaneFamily.InternalDeck => CutawayPlane.Waterline,
                        _ => CutawayPlane.Station,
                    };
                    preview.CutawayFraction = 1;
                    preview.SelectInternalFamily(expectedFamily);
                    var prefix = FamilyPrefix(expectedFamily);
                    Require(preview.SelectedInternalOwnerPrefix == prefix &&
                            preview.ShipHighlightModel is { } highlight &&
                            HullPreviewControl.SelectInternalFamilyPlacements(
                                selectionHull,
                                HullPreviewControl.SelectDrawnPlacements(selectionHull, true,
                                    preview.Cutaway, 1), prefix).Count == 1 &&
                            ((System.Windows.Media.Media3D.Model3DGroup)highlight).Children.Count > 0,
                        $"The {expectedFamily} cutaway did not retain a provenance-based family highlight.");
                }

                viewModel.SelectedFamily = deck;
                deck.Enabled = true;
                deck.UseAutomaticLayout();
                using var panelSource = new HwndSource(new HwndSourceParameters("HullForge simple internals probe")
                {
                    Width = 1040, Height = 430, WindowStyle = 0,
                });
                panelSource.RootVisual = control;
                PumpDispatcher();
                var combos = Descendants(control).OfType<ComboBox>().ToArray();
                Require(combos.Length == 2, "The live panel did not expose exactly material and spacing.");
                var gapCombo = combos.Single(combo => combo.ItemsSource == deck.ClearGapChoices ||
                    System.Windows.Automation.AutomationProperties.GetName(combo).StartsWith("Clear space"));
                gapCombo.SelectedItem = 4;
                PumpDispatcher();
                Require(deck.ClearGap == 4 && deck.UsesSimpleLayout && workspace.Document.Internals.Find(
                    InternalPlaneFamily.InternalDeck)!.Spacing.Metres == 4,
                    "The live spacing dropdown did not update one coherent automatic layout.");
                var screenshot = Environment.GetEnvironmentVariable("HULLFORGE_INTERNAL_EDITOR_RENDER");
                if (!string.IsNullOrWhiteSpace(screenshot))
                {
                    control.Measure(new Size(1040, 430));
                    control.Arrange(new Rect(0, 0, 1040, 430));
                    control.UpdateLayout();
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(1040, 430, 96, 96,
                        System.Windows.Media.PixelFormats.Pbgra32);
                    bitmap.Render(control);
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    using var stream = File.Create(screenshot);
                    encoder.Save(stream);
                }
                panelSource.RootVisual = null;
                mainWindow.Close();

            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(10)))
            throw new InvalidOperationException("The real U02 WPF panel probe did not complete.");
        if (failure is not null)
            throw new InvalidOperationException("The real U02 WPF panel probe failed.", failure);
    }

    /// <summary>
    /// A tab switch or a detach/re-dock round trip removes the editor from the visual tree and
    /// raises WPF's <c>Unloaded</c>; the live editor must survive that. Only replacing the tab's
    /// content is a lifetime boundary.
    /// </summary>
    private static void ReloadingTheComponentsTabKeepsTheEditorAlive()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                // Only one Application may exist per AppDomain; an earlier WPF probe may still
                // own it, so reuse it rather than constructing a second.
                var ownsApplication = Application.Current is null;
                if (ownsApplication)
                {
                    var created = new FtdHullGenerator.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    created.InitializeComponent();
                    created.Navigating += (_, eventArgs) => eventArgs.Cancel = true;
                    FtdHullGenerator.UI.ThemeManager.Apply(FtdHullGenerator.UI.AppTheme.Workbench);
                }

                var session = new EditorSession(ShipDocument.CreateNew("WPF reload", HullParameters.Default));
                using var workspace = new WorkspaceViewModel(session);
                var host = new WorkspaceHost { ViewModel = workspace };
                var componentsTab = (TabItem)host.Tabs.Items[1];
                var editor = new InternalStructureEditor();
                editor.Attach(workspace);
                componentsTab.Content = new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    CanContentScroll = false,
                    Content = editor,
                };
                var loaded = 0;
                var unloaded = 0;
                editor.Loaded += (_, _) => loaded++;
                editor.Unloaded += (_, _) => unloaded++;
                using var source = new HwndSource(new HwndSourceParameters("HullForge components reload probe")
                {
                    Width = 900,
                    Height = 500,
                    WindowStyle = 0,
                });
                source.RootVisual = host;
                PumpDispatcher();
                host.Tabs.SelectedIndex = 1;
                PumpDispatcher();
                Require(loaded > 0 && editor.ViewModel is { Families.Count: 3 } &&
                        ReferenceEquals(editor.DataContext, editor.ViewModel),
                    "The editor did not attach when its tab first loaded.");

                host.Tabs.SelectedIndex = 0;
                PumpDispatcher();
                Require(unloaded > 0,
                    "The probe did not reproduce the visual-tree unload that raised the original defect.");
                host.Tabs.SelectedIndex = 1;
                PumpDispatcher();
                Require(editor.ViewModel is { Families.Count: 3 } &&
                        ReferenceEquals(editor.DataContext, editor.ViewModel),
                    "Leaving and returning to the Components tab disposed the live editor view model.");

                var family = editor.ViewModel!.Families.Single(item =>
                    item.Family == InternalPlaneFamily.TransverseBulkhead);
                family.Enabled = true;
                Require(workspace.IsDraftDirty && editor.ViewModel.SelectedDiagnostics.Count > 0,
                    "The reloaded editor no longer shared the workspace draft.");

                editor.Dispose();
                source.RootVisual = null;
                PumpDispatcher();
                if (ownsApplication)
                    Application.Current!.Shutdown();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(15)))
            throw new InvalidOperationException("The internal-structure reload probe did not complete.");
        if (failure is not null)
            throw new InvalidOperationException("The internal-structure reload probe failed.", failure);
    }

    private static GeneratedHull SelectionHull()
    {
        var families = Enum.GetValues<InternalPlaneFamily>();
        var blocks = families.Select((family, index) => new BlockPlacement(
            BlockShape.Cube, MaterialKind.Metal, index, 0, 0, 0)
        {
            Origin = BlockOrigin.Shell,
        }).ToArray();
        var provenance = families.Select((family, index) => new PhysicalCellProvenance(
            new FtdHullGenerator.Domain.Composition.HullCell(index, 0, 0), MaterialKind.Metal,
            [new PhysicalCellOwner(FamilyPrefix(family) + "001", PhysicalCellRole.InternalStructure)]))
            .ToArray();
        return new GeneratedHull(HullParameters.Default, blocks, 0, 2, 0, 0, 0, 0)
        {
            CellProvenance = provenance,
        };
    }

    private static string FamilyPrefix(InternalPlaneFamily family) => family switch
    {
        InternalPlaneFamily.LongitudinalBulkhead => "internal:longitudinal:",
        InternalPlaneFamily.InternalDeck => "internal:deck:",
        InternalPlaneFamily.TransverseBulkhead => "internal:transverse:",
        _ => throw new ArgumentOutOfRangeException(nameof(family)),
    };

    private static void RaisePreviewKey(UIElement source, Key key)
    {
        var presentationSource = PresentationSource.FromVisual(source) ??
                                 throw new InvalidOperationException("The numeric input is not connected to a presentation source.");
        source.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, presentationSource,
            Environment.TickCount, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
            Source = source,
        });
        PumpDispatcher();
    }

    private static void PumpDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var descendant in Descendants(child))
                yield return descendant;
        }
    }

    private static InternalStructure Structure(params InternalStructureFamily[] enabled)
    {
        var byFamily = enabled.ToDictionary(family => family.Family);
        return new InternalStructure(Enum.GetValues<InternalPlaneFamily>().Select(family =>
            byFamily.TryGetValue(family, out var settings)
                ? settings
                : InternalStructureFamily.Disabled(family)).ToImmutableArray());
    }

    private static FtdBlockCatalog EmptyCatalog()
    {
        var root = Path.Combine(Path.GetTempPath(), "HullForge-U02-EmptyCatalog");
        Directory.CreateDirectory(Path.Combine(root, "From_The_Depths_Data", "StreamingAssets"));
        return FtdBlockCatalog.Load(root);
    }

    private static string Describe(IEnumerable<DesignDiagnostic> diagnostics) =>
        string.Join(" | ", diagnostics.Select(diagnostic => diagnostic.ToString()));

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null;
             directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "FtdHullGenerator.sln")))
                return directory.FullName;
        throw new InvalidOperationException("Could not find the repository root.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
