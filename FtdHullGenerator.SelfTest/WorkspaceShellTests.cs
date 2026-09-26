using System.Xml.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FtdHullGenerator;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Components;
using FtdHullGenerator.Domain.Design;
using FtdHullGenerator.Domain.Projects;
using FtdHullGenerator.Geometry.Layout;
using FtdHullGenerator.UI.Components;
using FtdHullGenerator.UI.Editor;
using FtdHullGenerator.UI.Workspace;
using static SlopeFillTestSupport;

/// <summary>U01's direct draft, host identity, keyboard, ruler-boundary, and static XAML checks.</summary>
internal static class WorkspaceShellTests
{
    public static void Run()
    {
        SharedDraftAppliesAndCancelsAtomically();
        StaleDraftFailsClosed();
        DirtyDraftInvalidatesPreviewAndSchedulesOnlyCommittedRevision();
        InternalDraftEditsScheduleANonAuthoritativeLivePreview();
        CloseAndKeyboardPoliciesAreExplicit();
        ResolvedArrangementIsPassedThrough();
        PresentationPreferenceIsLocalAndBounded();
        WorkspaceXamlKeepsTheViewportUsable();
        RoutedKeysDetachedCloseAndFocusUseRealWpfControls();
        Console.WriteLine("Workspace shell: shared atomic draft, preview invalidation, shortcut forwarding, routed keys/focus, solution pass-through, and viewport minima passed.");
    }

    private static void SharedDraftAppliesAndCancelsAtomically()
    {
        var original = ShipDocument.CreateNew("Shared", HullEditorSettings.Default);
        var session = new EditorSession(original);
        using var workspace = new WorkspaceViewModel(session);

        workspace.UpdateDraft(document => document with { Name = "Draft" });
        workspace.UpdateDraft(document => document with
        {
            Hull = document.Hull with { Length = 124 },
        });
        Require(ReferenceEquals(workspace.Session, session) && session.Document == original &&
                workspace.Document.Name == "Draft" && workspace.Document.Hull.Length == 124,
            "The workspace did not keep one private draft over the supplied editor session.");

        var revision = session.Revision;
        Require(workspace.Apply() == WorkspaceApplyResult.Applied && session.Revision == revision + 1 &&
                session.Document.Name == "Draft" && session.Document.Hull.Length == 124,
            "Apply did not commit all dependent workspace edits as one revision.");
        Require(session.Undo() && session.Document == original,
            "One undo did not restore the complete workspace transaction.");

        workspace.UpdateDraft(document => document with { Name = "Cancelled" });
        workspace.Cancel();
        Require(session.Document == original && !workspace.HasDraft && !workspace.IsDraftDirty,
            "Cancel allowed some of a workspace draft to escape.");

        workspace.IsDetached = true;
        Require(workspace.IsDetached && ReferenceEquals(workspace.Session, session),
            "Changing hosts changed the editor-session identity.");
        workspace.IsDetached = false;
    }

    private static void StaleDraftFailsClosed()
    {
        var session = new EditorSession(ShipDocument.CreateNew("Base", HullEditorSettings.Default));
        using var workspace = new WorkspaceViewModel(session);
        workspace.UpdateDraft(document => document with { Name = "Stale" });
        session.Commit(session.Document with { Name = "External" });

        Require(workspace.HasConflict && !workspace.CanApply &&
                workspace.Apply() == WorkspaceApplyResult.Conflict && session.Document.Name == "External",
            "A stale workspace draft could overwrite the newer editor revision.");
        workspace.Cancel();
        Require(!workspace.HasConflict && workspace.Document.Name == "External",
            "Cancelling a stale draft did not reveal the current immutable revision.");
    }

    private static void CloseAndKeyboardPoliciesAreExplicit()
    {
        var session = new EditorSession(ShipDocument.CreateNew("Close", HullEditorSettings.Default));
        using var workspace = new WorkspaceViewModel(session);
        workspace.UpdateDraft(document => document with { Name = "Keep" });
        Require(!workspace.TryClose(WorkspaceCloseChoice.KeepEditing) && workspace.IsDraftDirty,
            "Keep editing silently discarded the visible draft.");
        Require(workspace.TryClose(WorkspaceCloseChoice.Discard) && session.Document.Name == "Close",
            "Discard did not roll back the complete draft.");

        workspace.UpdateDraft(document => document with { Name = "Apply" });
        Require(workspace.TryClose(WorkspaceCloseChoice.Apply) && session.Document.Name == "Apply",
            "Close-with-Apply did not commit the draft.");

        Require(WorkspaceInputPolicy.ResolveEditorKey(Key.Escape, ModifierKeys.None, false) == WorkspaceKeyAction.Cancel &&
                WorkspaceInputPolicy.ResolveEditorKey(Key.Enter, ModifierKeys.None, false) == WorkspaceKeyAction.None &&
                WorkspaceInputPolicy.ResolveEditorKey(Key.Enter, ModifierKeys.None, true) == WorkspaceKeyAction.Apply &&
                WorkspaceInputPolicy.ResolveEditorKey(Key.Enter, ModifierKeys.Control, true) == WorkspaceKeyAction.None,
            "Workspace keys captured multiline entry or an existing application shortcut.");
        // The frozen 2.0 surface has no project persistence shortcuts; only edit history
        // remains shared between the docked and detached workspace hosts.
        Require(WorkspaceInputPolicy.ResolveApplicationShortcut(Key.S, ModifierKeys.Control) is null &&
                WorkspaceInputPolicy.ResolveApplicationShortcut(Key.S,
                    ModifierKeys.Control | ModifierKeys.Shift) is null &&
                WorkspaceInputPolicy.ResolveApplicationShortcut(Key.N, ModifierKeys.Control) is null &&
                WorkspaceInputPolicy.ResolveApplicationShortcut(Key.O, ModifierKeys.Control) is null &&
                WorkspaceInputPolicy.ResolveApplicationShortcut(Key.Z, ModifierKeys.Control) ==
                    WorkspaceApplicationShortcut.Undo &&
                WorkspaceInputPolicy.ResolveApplicationShortcut(Key.Y, ModifierKeys.Control) ==
                    WorkspaceApplicationShortcut.Redo,
            "The detached host still forwards a project New/Open/Save shortcut, or lost edit history.");
    }

    private static void DirtyDraftInvalidatesPreviewAndSchedulesOnlyCommittedRevision()
    {
        var session = new EditorSession(ShipDocument.CreateNew("Preview", HullEditorSettings.Default));
        using var workspace = new WorkspaceViewModel(session);
        using var boundary = new RevisionPreviewBoundary<object>();
        boundary.MoveToRevision(session.Revision);
        var request = boundary.Begin(session.Revision);
        var visible = new object();
        Require(boundary.TryAccept(request, visible), "Could not establish the valid preview test precondition.");
        var exportEnabled = true;
        var scheduled = new List<long>();
        using var coordinator = new WorkspacePreviewCoordinator<object>(workspace, boundary,
            () => { visible = null!; exportEnabled = false; }, scheduled.Add);
        var documentObserverSawInvalidation = false;
        workspace.DocumentChanged += (_, _) =>
            documentObserverSawInvalidation = visible is null && !exportEnabled;

        var originalRevision = session.Revision;
        workspace.UpdateDraft(document => document with { Name = "Draft" });
        Require(session.Revision == originalRevision && request.CancellationToken.IsCancellationRequested &&
                visible is null && !exportEnabled && !boundary.TryGetCurrent(originalRevision, out _) &&
                scheduled.Count == 0 && documentObserverSawInvalidation,
            "A dirty uncommitted workspace draft left the validated preview/export state reachable.");

        workspace.Cancel();
        Require(scheduled.SequenceEqual(new[] { originalRevision }),
            "Cancel did not schedule exactly the still-current committed revision.");

        var restored = boundary.Begin(originalRevision);
        visible = new object();
        exportEnabled = boundary.TryAccept(restored, visible);
        workspace.UpdateDraft(document => document with { Name = "Applied" });
        Require(!exportEnabled && visible is null, "A second dirty draft did not invalidate restored export state.");
        Require(workspace.Apply() == WorkspaceApplyResult.Applied &&
                scheduled.SequenceEqual(new[] { originalRevision, session.Revision }) &&
                session.Revision == originalRevision + 1,
            "Apply did not schedule exactly the new committed revision.");
    }

    /// <summary>
    /// A real internal-structure setter updates the one shared draft, so the coordinator must ask
    /// its host for a non-authoritative live preview while keeping the committed boundary empty.
    /// </summary>
    private static void InternalDraftEditsScheduleANonAuthoritativeLivePreview()
    {
        var session = new EditorSession(ShipDocument.CreateNew("Live", HullEditorSettings.Default));
        using var workspace = new WorkspaceViewModel(session);
        using var boundary = new RevisionPreviewBoundary<object>();
        boundary.MoveToRevision(session.Revision);
        var committedScheduled = new List<long>();
        var draftScheduled = 0;
        var visible = new object();
        var exportEnabled = true;
        using var coordinator = new WorkspacePreviewCoordinator<object>(workspace, boundary,
            () => { visible = null!; exportEnabled = false; }, committedScheduled.Add, () => draftScheduled++);
        using var editor = new InternalStructureEditorViewModel(workspace);

        var deck = editor.Families.Single(item => item.Family == InternalPlaneFamily.InternalDeck);
        deck.Enabled = true;
        Require(workspace.IsDraftDirty && draftScheduled == 1 && committedScheduled.Count == 0 &&
                visible is null && !exportEnabled,
            "An internal-structure edit did not schedule a non-authoritative live preview or leaked into the committed boundary.");

        deck.Count = 2;
        Require(draftScheduled == 2 && committedScheduled.Count == 0,
            "A second internal edit did not refresh the debounced draft preview.");

        workspace.Cancel();
        Require(committedScheduled.SequenceEqual(new[] { session.Revision }),
            "Cancel did not schedule exactly the still-current committed revision.");

        deck.Enabled = true;
        Require(draftScheduled == 3, "A post-Cancel edit did not schedule a fresh draft preview.");
        var applyRevision = session.Revision;
        Require(workspace.Apply() == WorkspaceApplyResult.Applied &&
                committedScheduled.SequenceEqual(new[] { applyRevision, applyRevision + 1 }) &&
                draftScheduled == 3,
            "Apply did not schedule exactly the new committed revision or scheduled extra draft work.");
    }

    private static void ResolvedArrangementIsPassedThrough()
    {
        var session = new EditorSession(ShipDocument.CreateNew("Ruler", HullEditorSettings.Default));
        using var workspace = new WorkspaceViewModel(session);
        var solution = new ArrangementSolution(ArrangementSolveStatus.Solved, [], [], [],
            DesignMeasure.FromMetres(80), DesignMeasure.FromMetres(100));
        workspace.SetArrangementSolution(solution);
        Require(ReferenceEquals(solution, workspace.ArrangementSolution),
            "The workspace recomputed or copied an already-resolved arrangement solution.");

        var layoutDirectory = Path.Combine(FindRepositoryRoot(), "FtdHullGenerator", "UI", "Layout");
        var layoutSource = string.Join('\n', Directory.EnumerateFiles(layoutDirectory, "*.cs")
            .Select(File.ReadAllText));
        Require(!layoutSource.Contains("ArrangementSolver.Solve", StringComparison.Ordinal),
            "The ruler duplicated the arrangement solver inside the presentation layer.");
    }

    private static void WorkspaceXamlKeepsTheViewportUsable()
    {
        var root = FindRepositoryRoot();
        var main = XDocument.Load(Path.Combine(root, "FtdHullGenerator", "MainWindow.xaml"));
        var host = XDocument.Load(Path.Combine(root, "FtdHullGenerator", "UI", "Workspace", "WorkspaceHost.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        XElement Named(XDocument document, string name) => document.Descendants()
            .Single(element => (string?)element.Attribute(x + "Name") == name);

        Require((string?)Named(main, "ViewportRow").Attribute("MinHeight") == "280" &&
                (string?)Named(main, "WorkspaceRow").Attribute("MinHeight") == "150" &&
                (string?)Named(main, "WorkspaceSplitter").Attribute("ResizeDirection") == "Rows",
            "The bottom splitter no longer preserves explicit viewport/workspace minima.");
        Require(Named(main, "PreviewWorkspaceGrid") is not null,
            "The preview/workspace grid can no longer constrain a restored height to available space.");
        Require(main.Descendants().Any(element => element.Name.LocalName == "WorkspaceHost" &&
                (string?)element.Attribute(x + "Name") == "DockedWorkspaceHost"),
            "MainWindow no longer owns the docked workspace host.");

        var tabs = host.Descendants(presentation + "TabItem").ToArray();
        Require(tabs.Select(tab => (string?)tab.Attribute("Header"))
                    .SequenceEqual(new[] { "Arrangement", "Components", "Diagnostics" }),
            "The task-oriented workspace tab order changed.");
        Require(host.Descendants(presentation + "ScrollViewer").All(scroll =>
                (string?)scroll.Attribute("CanContentScroll") == "False"),
            "A workspace scroller reverted to DPI-fragile logical scrolling.");
        Require(Named(host, "ApplyDraftButton").Attribute("IsEnabled") is not null &&
                Named(host, "CancelDraftButton").Attribute("IsEnabled") is not null,
            "Workspace Apply/Cancel buttons are no longer bound to draft state.");
    }

    private static void PresentationPreferenceIsLocalAndBounded()
    {
        var root = Path.Combine(Path.GetTempPath(), "HullForge-U01-" + Guid.NewGuid().ToString("n"));
        var path = Path.Combine(root, "height.txt");
        try
        {
            var store = new WorkspacePresentationPreferencesStore(path);
            Require(store.Load().DockedHeight == WorkspacePresentationPreferences.DefaultDockedHeight,
                "A missing workspace-height preference did not use the safe default.");
            store.Save(new WorkspacePresentationPreferences(420.5));
            Require(store.Load().DockedHeight == 420.5,
                "The machine-local workspace height did not round-trip invariantly.");
            store.Save(new WorkspacePresentationPreferences(10_000));
            Require(store.Load().DockedHeight == WorkspacePresentationPreferences.MaximumDockedHeight,
                "An oversized stored workspace height could crush the viewport.");
            File.WriteAllText(path, "not a number");
            Require(store.Load().DockedHeight == WorkspacePresentationPreferences.DefaultDockedHeight,
                "A corrupt workspace-height preference did not fail safely.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static void RoutedKeysDetachedCloseAndFocusUseRealWpfControls()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var application = new App();
                application.InitializeComponent();
                // App.xaml seeds OnMainWindowClose, which would let closing this probe's owner
                // window start a shutdown the abandoned dispatcher never finishes. The later WPF
                // probes reuse this application, so it must stay explicitly alive.
                application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                // The self-test executable is the entry assembly; palettes belong to the app.
                FtdHullGenerator.UI.ThemeManager.Apply(FtdHullGenerator.UI.AppTheme.Workbench);
                Require(application.Resources.MergedDictionaries[0].Contains("PrimaryTextBrush"),
                    "Workbench palette did not load from the application assembly.");
                FtdHullGenerator.UI.ThemeManager.Apply(FtdHullGenerator.UI.AppTheme.Forge);
                Require(application.Resources.MergedDictionaries[0].Contains("PrimaryTextBrush"),
                    "Forge palette did not load from the application assembly.");
                // The probe creates and closes its own explicit windows. Cancel App.xaml's
                // StartupUri navigation so the first dispatcher pump cannot open a second
                // MainWindow, inspect real recovery state, or block on a modal recovery prompt.
                application.Navigating += (_, eventArgs) => eventArgs.Cancel = true;
                var session = new EditorSession(ShipDocument.CreateNew("WPF", HullEditorSettings.Default));
                using var workspace = new WorkspaceViewModel(session);
                var docked = new WorkspaceHost { ViewModel = workspace };
                var owner = new Window { Width = 900, Height = 500, Content = docked, ShowInTaskbar = false };
                owner.Show();
                owner.Activate();
                PumpDispatcher();
                Require(docked.FocusWorkspace() && docked.IsKeyboardFocusWithin,
                    "The docked host could not place real keyboard focus on its selected tab.");

                // Raising the tunnelling key event from a button proves Enter is not a host-wide
                // default. Only an eligible single-line TextBox may commit the draft.
                workspace.UpdateDraft(document => document with { Name = "Button must not apply" });
                RaisePreviewKey(docked.ApplyButton, Key.Enter);
                Require(workspace.IsDraftDirty && session.Document.Name == "WPF",
                    "Enter from a Button applied the workspace draft.");

                var textBox = new TextBox { AcceptsReturn = false, Text = "12" };
                var comboBox = new ComboBox { ItemsSource = new[] { "one", "two" }, SelectedIndex = 0 };
                var editorPanel = new StackPanel();
                editorPanel.Children.Add(textBox);
                editorPanel.Children.Add(comboBox);
                var componentTab = (TabItem)docked.Tabs.Items[1];
                componentTab.Content = editorPanel;
                docked.Tabs.SelectedIndex = 1;
                PumpDispatcher();
                RaisePreviewKey(comboBox, Key.Enter);
                RaisePreviewKey(componentTab, Key.Enter);
                Require(workspace.IsDraftDirty && session.Document.Name == "WPF",
                    "Enter from a ComboBox or TabItem applied the workspace draft.");
                Require(textBox.Focus() && ReferenceEquals(Keyboard.Focus(textBox), textBox),
                    "The eligible editor TextBox could not receive keyboard focus.");
                RaisePreviewKey(textBox, Key.Enter);
                Require(!workspace.HasDraft && session.Document.Name == "Button must not apply",
                    "Enter from an eligible single-line TextBox did not apply the draft.");

                workspace.UpdateDraft(document => document with { Name = "Multiline" });
                textBox.AcceptsReturn = true;
                RaisePreviewKey(textBox, Key.Enter);
                Require(workspace.IsDraftDirty,
                    "Enter from a multiline TextBox was captured as Apply.");
                textBox.AcceptsReturn = false;
                RaisePreviewKey(textBox, Key.Escape);
                Require(!workspace.HasDraft && session.Document.Name == "Button must not apply",
                    "Escape did not cancel the complete routed editor draft.");

                var forwarded = new List<WorkspaceApplicationShortcut>();
                docked.Visibility = Visibility.Collapsed;
                workspace.IsDetached = true;
                var detached = new DetachedWorkspaceWindow(workspace, owner, shortcut =>
                {
                    forwarded.Add(shortcut);
                    return true;
                });
                detached.Show();
                detached.Activate();
                PumpDispatcher();
                Require(detached.Host.IsKeyboardFocusWithin,
                    "Detach did not move real keyboard focus into the new host.");
                foreach (var expected in Enum.GetValues<WorkspaceApplicationShortcut>())
                {
                    var (key, modifiers) = ShortcutFor(expected);
                    Require(detached.ForwardShortcut(key, modifiers),
                        $"Detached forwarding rejected {expected}.");
                }
                Require(forwarded.SequenceEqual(Enum.GetValues<WorkspaceApplicationShortcut>()),
                    "Detached commands did not use the supplied MainWindow command path in order.");

                var redockRequested = false;
                detached.RedockRequested += (_, _) => redockRequested = true;
                detached.Close();
                PumpDispatcher();
                Require(redockRequested && detached.IsVisible,
                    "The detached close button did not defer a draft-preserving re-dock request.");
                detached.CloseForRedock();
                workspace.IsDetached = false;
                docked.Visibility = Visibility.Visible;
                owner.Activate();
                PumpDispatcher();
                Require(docked.FocusWorkspace() && docked.IsKeyboardFocusWithin,
                    "Re-dock did not restore real keyboard focus to the docked host.");

                VerifyWorkspacePaletteCoverage(application, owner);

                owner.Close();
                MainWindowHistoryResolvesLiveWorkspaceDrafts();
                // The application is deliberately left alive for the later WPF probes, which
                // show their own windows and close them; the process teardown disposes it.
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(20)))
            throw new InvalidOperationException("The real WPF workspace probe did not complete.");
        if (failure is not null)
            throw new InvalidOperationException("The real WPF workspace probe failed.", failure);
    }

    /// <summary>
    /// The workspace tabs and the internal-structure list must not fall back to WPF's system
    /// chrome, and the two custom-drawn schematics must repaint when the palette swaps.
    /// </summary>
    private static void VerifyWorkspacePaletteCoverage(Application application, Window owner)
    {
        foreach (var controlType in new[] { typeof(TabControl), typeof(TabItem), typeof(ListBox), typeof(ListBoxItem) })
        {
            Require(application.Resources[controlType] is Style,
                $"The {controlType.Name} shell is not defined in the shared application resources.");
        }

        var tabControl = new TabControl();
        tabControl.Items.Add(new TabItem { Header = "Probe" });
        var listBox = new ListBox();
        listBox.Items.Add(new ListBoxItem { Content = "Probe" });
        var ruler = new FtdHullGenerator.UI.Layout.ArrangementRuler();
        var barbetteRuler = new FtdHullGenerator.UI.Layout.BarbetteRuler();
        var guide = new FtdHullGenerator.UI.Components.InternalStructureGuide { Width = 120, Height = 60 };
        var panel = new StackPanel();
        panel.Children.Add(tabControl);
        panel.Children.Add(listBox);
        panel.Children.Add(ruler);
        panel.Children.Add(barbetteRuler);
        panel.Children.Add(guide);
        var probe = new Window
        {
            Width = 420,
            Height = 360,
            Content = panel,
            Owner = owner,
            ShowInTaskbar = false,
            ShowActivated = false,
        };
        probe.Show();
        PumpDispatcher();

        void ExpectCurrentPalette(string state)
        {
            Require(MatchesPalette(listBox.Background, application.TryFindResource("RecessBrush")),
                $"{state}: the family list did not take the palette's recess brush.");
            Require(MatchesPalette(tabControl.Background, application.TryFindResource("RecessBrush")),
                $"{state}: the tab well did not take the palette's recess brush.");
            Require(MatchesPalette(ruler.SurfaceBrush, application.TryFindResource("RecessBrush")),
                $"{state}: the arrangement ruler did not follow the palette.");
            Require(MatchesPalette(barbetteRuler.SurfaceBrush, application.TryFindResource("RecessBrush")) &&
                    MatchesPalette(barbetteRuler.ErrorBrush, application.TryFindResource("ErrorBrush")),
                $"{state}: the frozen barbette ruler did not follow the palette.");
            Require(MatchesPalette(guide.OutlineBrush, application.TryFindResource("SecondaryTextBrush")),
                $"{state}: the internal-structure guide did not follow the palette.");
        }

        FtdHullGenerator.UI.ThemeManager.Apply(FtdHullGenerator.UI.AppTheme.Forge);
        PumpDispatcher();
        ExpectCurrentPalette("Forge");
        FtdHullGenerator.UI.ThemeManager.Apply(FtdHullGenerator.UI.AppTheme.Workbench);
        PumpDispatcher();
        ExpectCurrentPalette("Workbench");
        FtdHullGenerator.UI.ThemeManager.Apply(FtdHullGenerator.UI.AppTheme.Forge);
        PumpDispatcher();
        probe.Close();

        static bool MatchesPalette(Brush? actual, object? expected) =>
            actual is SolidColorBrush resolved && expected is SolidColorBrush palette &&
            resolved.Color == palette.Color;
    }

    private static (Key Key, ModifierKeys Modifiers) ShortcutFor(WorkspaceApplicationShortcut shortcut) => shortcut switch
    {
        WorkspaceApplicationShortcut.Undo => (Key.Z, ModifierKeys.Control),
        WorkspaceApplicationShortcut.Redo => (Key.Y, ModifierKeys.Control),
        _ => throw new ArgumentOutOfRangeException(nameof(shortcut)),
    };

    private static void MainWindowHistoryResolvesLiveWorkspaceDrafts()
    {
        var window = new MainWindow();
        var editor = window.EditorSessionForTests;
        var workspace = window.WorkspaceForTests;
        var original = ShipDocument.CreateNew("History base", HullEditorSettings.Default, documentId: "u01-history");

        void SeedUndo()
        {
            workspace.Cancel();
            editor.New(original);
            editor.Commit(original with { Name = "Committed edit" });
            workspace.UpdateDraft(document => document with { Name = "Visible draft" });
        }

        SeedUndo();
        var keepRevision = editor.Revision;
        Require(window.ExecuteApplicationShortcut(
                    WorkspaceApplicationShortcut.Undo, WorkspaceCloseChoice.KeepEditing) &&
                editor.Revision == keepRevision && editor.Document.Name == "Committed edit" &&
                workspace.Document.Name == "Visible draft" && workspace.IsDraftDirty && !workspace.HasConflict,
            "Undo mutated EditorSession or stranded a conflict when the user kept editing the draft.");

        SeedUndo();
        Require(window.ExecuteApplicationShortcut(
                    WorkspaceApplicationShortcut.Undo, WorkspaceCloseChoice.Discard) &&
                editor.Document.Name == "History base" &&
                !workspace.HasDraft && !workspace.HasConflict,
            "Undo after discarding the visible draft did not operate on the prior committed edit cleanly.");

        SeedUndo();
        var applyRevision = editor.Revision;
        Require(window.ExecuteApplicationShortcut(
                    WorkspaceApplicationShortcut.Undo, WorkspaceCloseChoice.Apply) &&
                editor.Revision == applyRevision + 2 &&
                editor.Document.Name == "Committed edit" && editor.CanRedo &&
                !workspace.HasDraft && !workspace.HasConflict,
            "Apply-then-Undo did not create one draft revision and undo that coherent item predictably.");

        void SeedRedo()
        {
            workspace.Cancel();
            editor.New(original);
            editor.Commit(original with { Name = "Committed edit" });
            Require(editor.Undo(), "Could not establish the live MainWindow redo precondition.");
            workspace.UpdateDraft(document => document with { Name = "Visible draft" });
        }

        SeedRedo();
        var redoKeepRevision = editor.Revision;
        Require(window.ExecuteApplicationShortcut(
                    WorkspaceApplicationShortcut.Redo, WorkspaceCloseChoice.KeepEditing) &&
                editor.Revision == redoKeepRevision && editor.Document.Name == "History base" &&
                editor.CanRedo && workspace.IsDraftDirty && !workspace.HasConflict,
            "Redo mutated EditorSession or stranded a conflict when the user kept editing the draft.");

        SeedRedo();
        Require(window.ExecuteApplicationShortcut(
                    WorkspaceApplicationShortcut.Redo, WorkspaceCloseChoice.Discard) &&
                editor.Document.Name == "Committed edit" &&
                !workspace.HasDraft && !workspace.HasConflict,
            "Redo after discarding the visible draft did not restore the committed edit cleanly.");

        SeedRedo();
        var redoApplyRevision = editor.Revision;
        Require(window.ExecuteApplicationShortcut(
                    WorkspaceApplicationShortcut.Redo, WorkspaceCloseChoice.Apply) &&
                editor.Revision == redoApplyRevision + 1 &&
                editor.Document.Name == "Visible draft" && !editor.CanRedo &&
                !workspace.HasDraft && !workspace.HasConflict,
            "Apply-before-Redo did not commit one coherent revision and retire the obsolete redo branch.");

        workspace.Cancel();
        editor.New(original);
        VerifyTesterUx(window, editor, workspace);
        window.Close();
    }

    private static void VerifyTesterUx(MainWindow window, EditorSession editor,
        WorkspaceViewModel workspace)
    {
        Require(window.SidebarEditingEnabledForTests && window.PresetsEnabledForTests,
            "The sidebar or presets browser began locked without a workspace draft.");
        var revision = editor.Revision;
        workspace.UpdateDraft(document => document with { Name = "Locked draft" });
        Require(!window.SidebarEditingEnabledForTests && !window.PresetsEnabledForTests,
            "A dirty workspace draft did not lock both sidebar and modeless presets browser.");
        window.SelectSketchbookEntryForTests("ans-05-high-forecastle",
            FtdHullGenerator.Domain.Sketchbook.SketchbookSizePolicy.KeepCurrentDimensions);
        Require(editor.Revision == revision && workspace.IsDraftDirty,
            "The modeless presets browser committed over a workspace draft.");
        workspace.Cancel();
        Require(window.SidebarEditingEnabledForTests && window.PresetsEnabledForTests,
            "Cancel did not restore sidebar and preset editing.");
        workspace.UpdateDraft(document => document with { Name = "Applied draft" });
        Require(workspace.Apply() == WorkspaceApplyResult.Applied &&
                window.SidebarEditingEnabledForTests && window.PresetsEnabledForTests,
            "Apply did not restore sidebar and preset editing.");

        var panel = window.PreviewPointerSurfaceForTests;
        Require(panel.Background is not null, "The empty preview area is not hit-testable.");
        var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount,
            MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent };
        panel.RaiseEvent(down);
        Require(down.Handled && window.PreviewForTests.IsDraggingForTests,
            "A drag beginning on empty preview space did not start the orbit gesture.");
        window.PreviewForTests.EndHold();

        var width = window.WidthInputForTests;
        width.SetDisplayedValue(21);
        Require(RaiseWheel(width.TextBoxForTests, 120) && width.Value == 23,
            "A wheel notch over an odd-width number did not use its two-metre step.");
        width.SetDisplayedValue((int)width.SliderMaximum);
        Require(RaiseWheel(width.TextBoxForTests, 120) && width.Value == width.SliderMaximum,
            "A numeric wheel at its bound escaped the field or exceeded its range.");
        var fullness = window.BowFullnessSliderForTests;
        fullness.SetDisplayedValue(0);
        Require(RaiseWheel(fullness.TextBoxForTests, 120) &&
                Math.Abs(fullness.Value - 0.05) < 0.000001,
            "A wheel notch over a shape number did not use the slider's small step.");
        var measure = new DesignMeasureInput { Value = 1 };
        Require(RaiseWheel(measure.TextBoxForTests, 120) && measure.Value == 1.5,
            "A workspace measure did not advance by half a metre.");
        var diameter = new FtdHullGenerator.UI.Layout.BarbetteDiameterInput { Value = 3 };
        Require(RaiseWheel(diameter.TextBoxForTests, 120) && diameter.Value == 5,
            "A barbette diameter did not advance to the next supported odd metre.");
    }

    private static bool RaiseWheel(UIElement source, int delta)
    {
        var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
        {
            RoutedEvent = UIElement.PreviewMouseWheelEvent,
        };
        source.RaiseEvent(args);
        return args.Handled;
    }

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
        {
            if (File.Exists(Path.Combine(directory.FullName, "FtdHullGenerator.sln")))
                return directory.FullName;
        }

        throw new InvalidOperationException("Could not find the repository root for workspace XAML checks.");
    }
}
