using System.Diagnostics;
using FtdHullGenerator.UI.Refinement;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Design;
using FtdHullGenerator.Domain.Historical;
using FtdHullGenerator.Domain.Layout;
using FtdHullGenerator.Domain.Projects;
using FtdHullGenerator.Domain.Sketchbook;
using FtdHullGenerator.Domain.Superstructures;
using FtdHullGenerator.Geometry;
using FtdHullGenerator.Geometry.Composition;
using FtdHullGenerator.Geometry.Layout;
using FtdHullGenerator.Infrastructure;
using FtdHullGenerator.Serialization;
using FtdHullGenerator.UI;
using FtdHullGenerator.UI.Components;
using FtdHullGenerator.UI.Editor;
using FtdHullGenerator.UI.Sketchbook;
using FtdHullGenerator.UI.Workspace;

namespace FtdHullGenerator;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly DispatcherTimer _draftPreviewTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private CancellationTokenSource? _draftPreviewCancellation;
    private int _draftPreviewSequence;
    private bool _visiblePreviewIsDraft;
    private readonly HullGenerator _generator = new();
    private readonly ShipGenerationService _shipGenerator = new();
    private readonly BlueprintExporter _exporter = new();
    private readonly ExperimentalFeaturesPreferenceStore _experimentalPreferenceStore = new();
    private readonly EditorSession _editor = new(ShipDocument.CreateNew("Untitled ship", HullEditorSettings.Default));
    private readonly RevisionPreviewBoundary<GeneratedHull> _previewBoundary = new();
    private readonly WorkspaceViewModel _workspace;
    private readonly WorkspacePreviewCoordinator<GeneratedHull> _workspacePreviewCoordinator;
    private readonly WorkspacePresentationPreferencesStore _workspacePreferences =
        WorkspacePresentationPreferencesStore.CreateDefault();
    private readonly FtdPaths _paths;
    private FeatureExposurePolicy _featureExposure;
    private IReadOnlyList<SmoothingChoice> _smoothingChoices = [];
    private IReadOnlyList<BowStyleChoice> _bowStyleChoices = [];
    private IReadOnlyList<SternStyleChoice> _sternStyleChoices = [];
    private FtdBlockCatalog? _catalog;
    private string? _gameDirectory;
    private GeneratedHull? _currentHull;
    private ShipGenerationSnapshot? _currentShipSnapshot;
    private InternalStructureEditor? _activeInternalStructureEditor;
    private CancellationTokenSource? _generationCancellation;
    private int _generationSequence;
    private int _editorActionSequence;
    private bool _uiConstructed;
    private bool _loaded;
    private bool _applyingShapeState;
    private bool _applyingBodyStyle;
    private bool _syncingInternalArmorOption;
    private bool _forceInternalArmor;
    private bool _isDarkTheme;
    private StatusTone _statusTone;
    private bool _syncingName;
    private string? _customName;
    private string _generatedName = "HF_Dolphin_100x21x12";
    private bool _suppressEditHighlight;
    private bool _bottomArmorInheritsSide;
    private DetachedWorkspaceWindow? _detachedWorkspace;
    private HullPresetsWindow? _hullPresetsWindow;
    private GridLength _dockedWorkspaceHeight = new(240);
    private readonly List<ArmorLayerRow> _hullArmorRows = [];
    private readonly List<ArmorLayerRow> _deckArmorRows = [];
    private readonly List<ArmorLayerRow> _bottomArmorRows = [];

    public MainWindow()
    {
        _featureExposure = new FeatureExposurePolicy(_experimentalPreferenceStore.Load());
        InitializeComponent();
        ExperimentalFeaturesToggle.IsChecked = _featureExposure.ExperimentalFeaturesEnabled;
        _workspace = new WorkspaceViewModel(_editor);
        DockedWorkspaceHost.ViewModel = _workspace;
        _activeInternalStructureEditor = InstallInternalStructureEditor(DockedWorkspaceHost);
        _workspace.DocumentChanged += (_, _) => RefreshArrangementWorkspace();
        _workspacePreviewCoordinator = new WorkspacePreviewCoordinator<GeneratedHull>(
            _workspace, _previewBoundary, InvalidateVisibleWorkspacePreview, ScheduleCommittedWorkspacePreview,
            ScheduleDraftWorkspacePreview);
        _workspace.DraftChanged += (_, _) => UpdateSidebarEditLock();
        _dockedWorkspaceHeight = new GridLength(_workspacePreferences.Load().DockedHeight);
        WorkspaceRow.Height = _dockedWorkspaceHeight;
        PreviewWorkspaceGrid.SizeChanged += (_, _) => ApplyWorkspaceHeightForAvailableSpace();
        _uiConstructed = true;
        UpdateSidebarEditLock();
        _paths = FtdPaths.Discover();
        _isDarkTheme = LoadDarkThemePreference();
        ApplyWorkbenchTheme(_isDarkTheme, persist: false);
        DestinationBox.Text = Path.Combine(_paths.ConstructsDirectory, "Hull Generator");
        _gameDirectory = FtdInstallationLocator.FindInstalledGame();
        GameDirectoryBox.Text = _gameDirectory ?? "Not detected — choose the From The Depths game folder.";
        AddArmorLayer(_hullArmorRows, HullArmorLayersPanel, new ArmorLayer(MaterialKind.Metal), "OUTER");
        AddArmorLayer(_deckArmorRows, DeckArmorLayersPanel, new ArmorLayer(MaterialKind.Metal), "TOP");
        AddArmorLayer(_bottomArmorRows, BottomArmorLayersPanel, new ArmorLayer(MaterialKind.Metal), "BOTTOM");
        BodyStyleBox.ItemsSource = BodyStyleChoice.All;
        RefreshAccessChoices();
        UpdateHybridOffsetControls();
        UpdateProfileRiseControls();
        UpdateOverallHeightReadout();
        CutawayBox.ItemsSource = CutawayChoice.All;
        CutawayBox.SelectedIndex = 0;
        UpdateCutaway();
        // Applied here rather than trusting a Checked event that fires mid-parse, before
        // the preview field exists.
        Preview.AutoRotate = AutoRotateToggle.IsChecked == true;
        _previewTimer.Tick += async (_, _) => await GeneratePreviewAsync();
        _draftPreviewTimer.Tick += async (_, _) => await GenerateDraftPreviewAsync();
        _editor.Changed += EditorStateChanged;
        RefreshArrangementWorkspace();
        _previewBoundary.MoveToRevision(_editor.Revision);
        UpdateEditorChrome();
        Loaded += async (_, _) =>
        {
            OnMainWindowLoaded();
            await LoadCatalogAsync();
            QueuePreview();
        };
        SourceInitialized += (_, _) => ApplyTitleBarTheme();
        Closed += (_, _) =>
        {
            _hullPresetsWindow?.CloseForShutdown();
            _previewTimer.Stop();
            _draftPreviewTimer.Stop();
            _draftPreviewCancellation?.Cancel();
            _previewBoundary.Dispose();
            _generationCancellation?.Cancel();
            if (_detachedWorkspace is not null)
            {
                DisposeInstalledEditor((TabItem)_detachedWorkspace.Host.Tabs.Items[1]);
                _detachedWorkspace.CloseForRedock();
            }
            DisposeInstalledEditor((TabItem)DockedWorkspaceHost.Tabs.Items[1]);
            SaveWorkspaceHeight();
            _workspacePreviewCoordinator.Dispose();
            _workspace.Dispose();
        };
    }

    private void RefreshAccessChoices(
        BowStyle bow = BowStyle.Pointed,
        SternStyle stern = SternStyle.Transom,
        SmoothingMethod smoothing = SmoothingMethod.VerticalSlopeFill)
    {
        _smoothingChoices = SmoothingChoice.All;
        SmoothingBox.ItemsSource = GroupedBy(_smoothingChoices, nameof(SmoothingChoice.Group));
        SmoothingBox.SelectedItem = _smoothingChoices.FirstOrDefault(choice => choice.Value == smoothing)
            ?? _smoothingChoices.First(choice => choice.Value == SmoothingMethod.VerticalSlopeFill);

        _bowStyleChoices = BowStyleChoice.All;
        BowStyleBox.ItemsSource = _bowStyleChoices;
        BowStyleBox.SelectedItem = _bowStyleChoices.First(choice => choice.Value == bow);
        _sternStyleChoices = SternStyleChoice.All;
        SternStyleBox.ItemsSource = _sternStyleChoices;
        SternStyleBox.SelectedItem = _sternStyleChoices.First(choice => choice.Value == stern);
        UpdateThemeButton();
    }

    private static ListCollectionView GroupedBy<T>(IReadOnlyList<T> choices, string groupProperty)
    {
        var view = new ListCollectionView(choices.ToList());
        view.GroupDescriptions.Add(new PropertyGroupDescription(groupProperty));
        return view;
    }

    private async Task LoadCatalogAsync(string? selectedGameDirectory = null)
    {
        var gameDirectory = selectedGameDirectory ?? _gameDirectory ?? FtdInstallationLocator.FindInstalledGame();
        if (!FtdInstallationLocator.IsGameDirectory(gameDirectory))
        {
            SetCatalogStatus("NO GAME INSTALLATION", "WarningBrush");
            SetStatus("Choose the folder that contains From_The_Depths.exe to load its armor catalog.", StatusTone.Warning);
            // With no catalog Hull Presets still draws honest provisional native previews rather
            // than leaving sixteen cards pending forever.
            EnsureHullPresetsWindow().Browser.InvalidateThumbnails();
            _ = PopulateSketchbookThumbnailsAsync(null);
            return;
        }

        try
        {
            AdoptCatalog(null, queuePreview: false);
            SetCatalogStatus("READING CATALOG…", "AccentBrush");
            var loaded = await Task.Run(() => FtdBlockCatalog.Load(gameDirectory));
            _gameDirectory = gameDirectory;
            GameDirectoryBox.Text = gameDirectory;
            AdoptCatalog(loaded, queuePreview: _loaded);
            SetCatalogStatus($"CATALOG READY · {loaded.GameVersion}", "OkBrush");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or DirectoryNotFoundException or JsonException)
        {
            SetCatalogStatus("CATALOG UNREADABLE", "ErrorBrush");
            SetStatus(error.Message, StatusTone.Error);
            EnsureHullPresetsWindow().Browser.InvalidateThumbnails();
            _ = PopulateSketchbookThumbnailsAsync(null);
        }
    }

    /// <summary>
    /// Installs one catalog as the export authority. Setting or replacing it cancels and drops any
    /// in-flight or current resolved generation — including the non-authoritative draft preview,
    /// which would otherwise finish against the previous catalog — so a snapshot captured against a
    /// previous catalog fingerprint can never remain exportable. A successful replacement queues a
    /// fresh preview.
    /// </summary>
    private void AdoptCatalog(FtdBlockCatalog? catalog, bool queuePreview)
    {
        _generationCancellation?.Cancel();
        _generationCancellation?.Dispose();
        _generationCancellation = null;
        _generationSequence++;
        CancelDraftPreviewWork();
        _previewBoundary.InvalidateCurrent();
        _currentHull = null;
        _currentShipSnapshot = null;
        // The derived frame binds to the installed catalog; changing the catalog invalidates it.
        _workspace.LayoutCatalogIdentity = catalog is null ? null : ShipCatalogFingerprint.Compute(catalog);
        _workspace.ClearResolvedGeneration();
        _activeInternalStructureEditor?.SetGenerationResult(null);
        ExportButton.IsEnabled = false;
        _catalog = catalog;
        // The Hull Presets thumbnails are bound to the catalog identity; a replacement must drop
        // every stale image before a new one can be drawn.
        EnsureHullPresetsWindow().Browser.InvalidateThumbnails();
        if (queuePreview && _loaded)
        {
            QueuePreview();
            _ = PopulateSketchbookThumbnailsAsync(catalog);
        }
    }

    /// <summary>
    /// Draws every sketchbook thumbnail for the adopted catalog without blocking the caller. A
    /// thumbnail failure is reported on its card, so the observed continuation never throws.
    /// </summary>
    private async Task PopulateSketchbookThumbnailsAsync(FtdBlockCatalog? catalog)
    {
        try
        {
            await EnsureHullPresetsWindow().Browser.PopulateThumbnailsAsync(catalog, CancellationToken.None);
        }
        catch (Exception)
        {
            // Presentation only. Per-card failures are already reported honestly on the card.
        }
    }

    /// <summary>What the status lamp says about the message beside it.</summary>
    private enum StatusTone
    {
        Neutral,
        Working,
        Ready,
        Warning,
        Error,
    }

    /// <summary>Writes the status line and colours its lamp from the shared theme brushes.</summary>
    private void SetStatus(string message, StatusTone tone)
    {
        _statusTone = tone;
        StatusText.Text = message;
        StatusLamp.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, tone switch
        {
            StatusTone.Working => "AccentBrush",
            StatusTone.Ready => "OkBrush",
            StatusTone.Warning => "WarningBrush",
            StatusTone.Error => "ErrorBrush",
            _ => "MutedTextBrush",
        });
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    /// <summary>
    /// Asks the desktop compositor for caption colors matching the active workbench theme.
    /// The attribute is advisory: older Windows builds use a different id,
    /// and any failure simply leaves whatever caption the system chose in place. It is set
    /// explicitly rather than left alone because the user's system theme may be light.
    /// </summary>
    private void ApplyTitleBarTheme()
    {
        const int UseImmersiveDarkMode = 20;
        const int UseImmersiveDarkModeBefore20H1 = 19;
        var window = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (window == IntPtr.Zero)
            return;

        var darkMode = ThemeManager.Current == AppTheme.Forge ? 1 : 0;
        try
        {
            if (DwmSetWindowAttribute(window, UseImmersiveDarkMode, ref darkMode, sizeof(int)) != 0)
                DwmSetWindowAttribute(window, UseImmersiveDarkModeBefore20H1, ref darkMode, sizeof(int));
        }
        catch (DllNotFoundException)
        {
            // The window simply keeps the system default caption.
        }
    }

    private void ThemeClicked(object sender, RoutedEventArgs eventArgs)
    {
        ApplyWorkbenchTheme(!_isDarkTheme, persist: true);
        SetStatus(_isDarkTheme
            ? "Dark workbench enabled."
            : "Light workbench enabled.", StatusTone.Ready);
    }

    private void ApplyWorkbenchTheme(bool dark, bool persist)
    {
        _isDarkTheme = dark;
        ThemeManager.Apply(_isDarkTheme ? AppTheme.Forge : AppTheme.Workbench);

        UpdateThemeButton();
        ApplyTitleBarTheme();
        if (persist)
            SaveDarkThemePreference(_isDarkTheme);
    }

    private void UpdateThemeButton()
    {
        if (ThemeButton is null)
            return;

        ThemeButton.Content = _isDarkTheme ? "LIGHT MODE" : "DARK MODE";
        ThemeButton.ToolTip = _isDarkTheme
            ? "Switch to the light workbench. Either palette is a standard Hull Forge choice."
            : "Switch back to the standard dark workbench.";
    }

    private static string ThemePreferencePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hull Forge", "theme.txt");

    private static bool LoadDarkThemePreference()
    {
        try
        {
            return DecodeThemePreference(
                File.Exists(ThemePreferencePath) ? File.ReadAllText(ThemePreferencePath) : null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// Reads a stored theme preference. The dark workbench is the standard one, so a missing
    /// file, an unreadable one, or anything that is not an explicit request for the light
    /// bench leaves it in place.
    /// </summary>
    internal static bool DecodeThemePreference(string? stored) =>
        !string.Equals(stored?.Trim(), "light", StringComparison.OrdinalIgnoreCase);

    private static void SaveDarkThemePreference(bool dark)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ThemePreferencePath)!);
            File.WriteAllText(ThemePreferencePath, dark ? "dark" : "light");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Drives the nameplate lamp and its caption from the shared theme brushes.</summary>
    private void SetCatalogStatus(string caption, string brushKey)
    {
        CatalogStatusText.Text = caption;
        CatalogStatusText.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        CatalogStatusLamp.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, brushKey);
    }

    // Standing views. A preset reframes the whole hull, so it doubles as the way back
    // from an orbited and zoomed camera.
    private void IsometricViewClicked(object sender, RoutedEventArgs eventArgs) => Preview.ShowView(PreviewView.Isometric);

    private void SideViewClicked(object sender, RoutedEventArgs eventArgs) => Preview.ShowView(PreviewView.Side);

    private void TopViewClicked(object sender, RoutedEventArgs eventArgs) => Preview.ShowView(PreviewView.Top);

    private void BowViewClicked(object sender, RoutedEventArgs eventArgs) => Preview.ShowView(PreviewView.Bow);

    // The gentle turn is a view behaviour, not part of the placement list, so the switch
    // acts on the preview directly. Reset deliberately leaves it where the user set it:
    // it is a motion preference, like the palette, not hull state.
    private void AutoRotateChanged(object sender, RoutedEventArgs eventArgs)
    {
        if (Preview is null || !_uiConstructed)
            return;
        Preview.AutoRotate = AutoRotateToggle.IsChecked == true;
    }

    private void SettingsChanged(object sender, RoutedEventArgs eventArgs) => QueuePreview();

    // Display-only settings act on the preview directly. They never change the
    // placement list, so there is nothing to regenerate or re-validate.
    private void ShowInternalArmorChanged(object sender, RoutedEventArgs eventArgs)
    {
        if (Preview is null || _syncingInternalArmorOption)
            return;
        _forceInternalArmor = ShowInternalArmorCheckBox.IsChecked == true;
        Preview.ShowInternalArmor = _forceInternalArmor;
    }

    /// <summary>
    /// Keeps the debug option in step with the hull's size. A hull within the automatic
    /// limit draws its interior regardless, so the box shows ticked and greyed; a larger
    /// hull enables the box and restores whatever the operator last chose for large hulls.
    /// </summary>
    private void UpdateInternalArmorOption(GeneratedHull? hull)
    {
        _syncingInternalArmorOption = true;
        try
        {
            var limit = HullPreviewControl.AutomaticInternalArmorCellLimit;
            if (hull is null)
            {
                ShowInternalArmorCheckBox.IsEnabled = false;
                ShowInternalArmorCheckBox.IsChecked = _forceInternalArmor;
                ShowInternalArmorCheckBox.ToolTip = $"Display only. Internal armor is drawn automatically up to {limit:N0} cells; this draws it above that.";
                return;
            }

            var cells = hull.InternalArmorCellCount;
            if (HullPreviewControl.DrawsInternalArmorAutomatically(hull))
            {
                ShowInternalArmorCheckBox.IsEnabled = false;
                ShowInternalArmorCheckBox.IsChecked = true;
                ShowInternalArmorCheckBox.ToolTip = cells == 0
                    ? "Display only. This hull has no internal armor."
                    : $"Display only. Drawn automatically: {cells:N0} internal armor cells, within the {limit:N0} limit.";
            }
            else
            {
                ShowInternalArmorCheckBox.IsEnabled = true;
                ShowInternalArmorCheckBox.IsChecked = _forceInternalArmor;
                ShowInternalArmorCheckBox.ToolTip = $"Display only. {cells:N0} internal armor cells, above the {limit:N0} limit; drawing them slows the rebuild.";
            }
        }
        finally
        {
            _syncingInternalArmorOption = false;
        }
    }

    /// <summary>
    /// A new plane also moves the camera to the standing view that faces its cut, since
    /// the default isometric camera sits on the stern side and would otherwise show
    /// only exterior. Moving the slider afterwards leaves the camera alone.
    /// </summary>
    private void CutawayPlaneChanged(object sender, SelectionChangedEventArgs eventArgs)
    {
        UpdateCutaway();
        if (Preview is null || (CutawayBox.SelectedItem as CutawayChoice)?.Value is not { } plane || plane == CutawayPlane.None)
            return;
        Preview.ShowView(plane switch
        {
            CutawayPlane.Station => PreviewView.Bow,
            CutawayPlane.Centreline => PreviewView.Side,
            _ => PreviewView.Top,
        });
    }

    private void CutawayPositionChanged(object sender, RoutedPropertyChangedEventArgs<double> eventArgs) => UpdateCutaway();

    /// <summary>
    /// Pushes the cutaway strip's state into the preview. The slider reads as a
    /// percentage of the hull's extent along the chosen plane's axis, and the readout
    /// says which side is kept so the number has a direction.
    /// </summary>
    private void UpdateCutaway()
    {
        if (Preview is null || CutawayBox is null || CutawaySlider is null || CutawayReadout is null)
            return;

        var plane = (CutawayBox.SelectedItem as CutawayChoice)?.Value ?? CutawayPlane.None;
        var percent = (int)Math.Round(CutawaySlider.Value);
        CutawaySlider.IsEnabled = plane != CutawayPlane.None;
        Preview.CutawayFraction = percent / 100d;
        Preview.Cutaway = plane;
        CutawayReadout.Text = plane switch
        {
            CutawayPlane.Station => $"STERN {percent}%",
            CutawayPlane.Centreline => $"PORT {percent}%",
            CutawayPlane.Waterline => $"KEEL {percent}%",
            _ => "WHOLE HULL",
        };
    }

    private void UpdateBlueprintName(HullParameters parameters)
    {
        _generatedName = CreateBlueprintName(parameters);
        if (CustomNameCheckBox.IsChecked == true)
            return;
        _syncingName = true;
        try { BlueprintNameBox.Text = _generatedName; }
        finally { _syncingName = false; }
    }

    private void CustomNameToggled(object sender, RoutedEventArgs eventArgs)
    {
        if (!_uiConstructed)
            return;
        var custom = CustomNameCheckBox.IsChecked == true;
        BlueprintNameBox.IsReadOnly = !custom;
        _syncingName = true;
        try
        {
            if (custom)
            {
                BlueprintNameBox.Text = _customName ?? _generatedName;
                BlueprintNameBox.Focus();
                BlueprintNameBox.SelectAll();
            }
            else
            {
                _customName = null;
                BlueprintNameBox.Text = _generatedName;
            }
        }
        finally { _syncingName = false; }
        ShowNameNotice(custom ? BlueprintNameBox.Text : null);
    }

    private void CustomNameEdited(object sender, TextChangedEventArgs eventArgs)
    {
        if (!_uiConstructed || _syncingName || CustomNameCheckBox.IsChecked != true)
            return;
        _customName = BlueprintNameBox.Text;
        ShowNameNotice(_customName);
    }

    private void ShowNameNotice(string? candidate)
    {
        if (candidate is null)
        {
            NameNoticeText.Visibility = Visibility.Collapsed;
            return;
        }

        var sanitized = BlueprintNaming.Sanitize(candidate);
        if (sanitized.Length == 0)
        {
            NameNoticeText.Text = $"That name has no usable characters. Export will use {_generatedName}.";
            NameNoticeText.Visibility = Visibility.Visible;
            return;
        }

        if (sanitized == candidate.Trim())
        {
            NameNoticeText.Visibility = Visibility.Collapsed;
            return;
        }

        NameNoticeText.Text = $"Will be saved as {sanitized}.";
        NameNoticeText.Visibility = Visibility.Visible;
    }

    private string EffectiveBlueprintName()
    {
        if (CustomNameCheckBox.IsChecked != true)
            return _generatedName;
        var sanitized = BlueprintNaming.Sanitize(BlueprintNameBox.Text);
        return sanitized.Length == 0 ? _generatedName : sanitized;
    }

    private void AllowEvenWidthChanged(object sender, RoutedEventArgs eventArgs)
    {
        if (!_uiConstructed)
            return;
        var allowEven = AllowEvenWidthCheckBox.IsChecked == true;
        WidthInput.SliderMaximum = allowEven ? 100 : 99;
        WidthInput.IsStepped = !allowEven;
        QueuePreview();
    }

    private void DimensionChanged(object sender, RoutedEventArgs eventArgs)
    {
        if (!_uiConstructed)
            return;
        UpdateHybridOffsetControls();
        UpdateProfileRiseControls();
        UpdateOverallHeightReadout();
        QueuePreview();
    }

    private void ExperimentalFeaturesChanged(object sender, RoutedEventArgs eventArgs)
    {
        if (!_uiConstructed)
            return;

        var enabled = ExperimentalFeaturesToggle.IsChecked == true;
        if (_featureExposure.ExperimentalFeaturesEnabled == enabled)
            return;

        _experimentalPreferenceStore.Save(enabled, out var persistenceError);
        ApplyExperimentalFeatures(enabled);
        SetStatus(persistenceError ?? $"Experimental Features {(enabled ? "enabled" : "disabled")}.",
            persistenceError is null ? StatusTone.Ready : StatusTone.Warning);
    }

    /// <summary>
    /// Rebuilds the exposure-dependent surface for a new experimental-features state without
    /// touching the persisted preference. The test seam below uses this so a probe can exercise
    /// the real internal-structure editor without writing machine-local state.
    /// </summary>
    private void ApplyExperimentalFeatures(bool enabled)
    {
        _featureExposure = new FeatureExposurePolicy(enabled);
        _editorActionSequence++;
        _generationCancellation?.Cancel();
        _generationSequence++;
        _previewBoundary.InvalidateCurrent();
        _currentHull = null;
        _currentShipSnapshot = null;
        _workspace.ClearResolvedGeneration();
        _activeInternalStructureEditor?.SetGenerationResult(null);
        Preview.SetHull(null);
        ExportButton.IsEnabled = false;
        _suppressEditHighlight = true;

        var host = _workspace.IsDetached && _detachedWorkspace is not null
            ? _detachedWorkspace.Host
            : DockedWorkspaceHost;
        _activeInternalStructureEditor = InstallInternalStructureEditor(host);
        RefreshArrangementWorkspace();
        QueuePreview(captureEditorState: false);
    }

    /// <summary>Test seam: applies an exposure state without persisting a machine-local preference.</summary>
    internal void SetExperimentalFeaturesForTests(bool enabled) => ApplyExperimentalFeatures(enabled);

    private void SmoothingChanged(object sender, SelectionChangedEventArgs eventArgs)
    {
        if (!_uiConstructed)
            return;
        SmoothingBox.ToolTip = (SmoothingBox.SelectedItem as SmoothingChoice)?.Hint;
        UpdateHybridOffsetControls();
        QueuePreview();
    }

    private void ShapeSettingChanged(object sender, RoutedEventArgs eventArgs)
    {
        if (!_uiConstructed || _applyingShapeState)
            return;
        QueuePreview();
    }

    private void BodyShapeSliderChanged(object sender, RoutedEventArgs eventArgs)
    {
        if (!_uiConstructed || _applyingShapeState || _applyingBodyStyle)
            return;

        // A slider edit is the user taking over the body form: the preset becomes
        // Custom and nothing else is rewritten. Every other body, bow, stern, profile
        // and bulb value stays exactly where the user left it.
        _applyingBodyStyle = true;
        BodyStyleBox.SelectedItem = BodyStyleChoice.For(BodyStyle.Custom);
        _applyingBodyStyle = false;
        QueuePreview();
    }

    private void BodyStyleChanged(object sender, SelectionChangedEventArgs eventArgs)
    {
        if (!_uiConstructed || _applyingShapeState || _applyingBodyStyle ||
            BodyStyleBox.SelectedItem is not BodyStyleChoice choice)
            return;

        // A body form is an initializer, not a mode. Choosing one loads its body
        // values and leaves every other normal Shape V2 control alone; the user can
        // immediately edit any of them.
        if (choice.Value != BodyStyle.Custom)
        {
            _applyingBodyStyle = true;
            var body = BodyShapeSettings.ForStyle(choice.Value);
            BodyFullnessSlider.SetDisplayedValue(body.Fullness);
            BodySideShapeSlider.SetDisplayedValue(body.SideShape);
            BodyChineSlider.SetDisplayedValue(body.Chine);
            BodyFlatBottomSlider.SetDisplayedValue(body.FlatBottom);
            _applyingBodyStyle = false;
        }

        UpdateStyleHint();
        QueuePreview();
    }

    /// <summary>
    /// Applies a historical catalog entry through the same control and editor-state path a
    /// manually shaped hull uses. Only dimensions, bow/stern style and Shape V2 values change;
    /// armor, construction, smoothing and superstructure stay where the user left them, and
    /// nothing records a continuing nation or class mode. The historical library is no longer a
    /// UI surface; this compatibility path is retained for the catalog probes.
    /// </summary>
    private void ApplyHistoricalPreset(HistoricalHullPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (!CanEditSidebar()) return;
        var basis = ReadParameters(out _) ?? _editor.Document.Hull;
        _suppressEditHighlight = true;
        ApplyEditorParameters(preset.ToParameters(basis), resetArmor: false);
        SetStatus(
            $"Loaded the {preset.Nation.DisplayName} {preset.Name} starting hull. Every Shape V2 control stays editable.",
            StatusTone.Ready);
    }

    /// <summary>
    /// The launch handshake: mark the session live and open the Hull Presets window on top. The
    /// caller then finishes the catalog read and queues the first preview. Extracted so a headless
    /// probe can drive exactly the launch surface without showing the editor window.
    /// </summary>
    private void OnMainWindowLoaded()
    {
        _loaded = true;
        OpenHullPresetsWindow();
    }

    private void OpenHullPresetsClicked(object sender, RoutedEventArgs eventArgs) =>
        OpenHullPresetsWindow();

    /// <summary>
    /// Shows or re-activates the one Hull Presets window. The owner is only adopted once the
    /// editor window has loaded; the window itself hides instead of closing, so this always brings
    /// back the same instance.
    /// </summary>
    private void OpenHullPresetsWindow()
    {
        var window = EnsureHullPresetsWindow();
        if (window.Owner is null && IsLoaded)
            window.Owner = this;
        if (!window.IsVisible)
            window.Show();
        window.Activate();
    }

    /// <summary>
    /// The session's single Hull Presets window, created on first use so a headless probe never
    /// pays for a window it does not inspect and the catalog wiring stays owned by the editor.
    /// </summary>
    private HullPresetsWindow EnsureHullPresetsWindow() =>
        _hullPresetsWindow ??= CreateHullPresetsWindow();

    private HullPresetsWindow CreateHullPresetsWindow()
    {
        var window = new HullPresetsWindow();
        window.Browser.EntrySelected += SketchbookEntrySelected;
        window.Browser.IsEnabled = !_workspace.IsDraftDirty;
        return window;
    }

    private void SketchbookEntrySelected(object? sender, SketchbookEntryEventArgs eventArgs)
    {
        if (!_uiConstructed)
            return;
        ApplySketchbookEntry(eventArgs.Entry, eventArgs.Policy);
    }

    /// <summary>
    /// Applies one Hull Presets catalog entry as exactly one editor transaction, then shows the
    /// committed parameters without committing them a second time. The catalog's comparison
    /// smoothing recommendation is never applied.
    /// </summary>
    private void ApplySketchbookEntry(AlternateNavalSketchbookEntry entry, SketchbookSizePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!CanEditSidebar()) return;
        var application = SketchbookEditorContract.Apply(_editor, entry, policy);
        _suppressEditHighlight = true;
        ShowParametersInControls(application.Document.Hull, resetArmor: false);
        UpdateStyleHint();
        // The entry was already committed by the contract; the preview must not capture the
        // controls again, which would risk a second revision.
        QueuePreview(captureEditorState: false);

        var policyLabel = policy == SketchbookSizePolicy.SuggestedDimensions
            ? "suggested size"
            : "kept current size";

        var errors = application.Diagnostics.Where(diagnostic => diagnostic.IsError).ToArray();
        if (errors.Length > 0)
        {
            var rest = errors.Length - 1;
            var more = rest == 0 ? string.Empty : $" (+{rest} more)";
            SetStatus(
                $"Applied {entry.Name} at its {policyLabel}, but the design is not valid: {errors[0].Message}{more}",
                StatusTone.Error);
            return;
        }

        var warnings = application.Diagnostics.Count(diagnostic => diagnostic.Severity == DesignSeverity.Warning);
        if (warnings > 0)
        {
            SetStatus(
                $"Applied {entry.Name} at its {policyLabel} with {warnings} warning{(warnings == 1 ? string.Empty : "s")}.",
                StatusTone.Warning);
            return;
        }

        SetStatus($"Applied {entry.Name} at its {policyLabel}.", StatusTone.Ready);
    }

    private void ProfileSettingChanged(object sender, RoutedEventArgs eventArgs)
    {
        if (!_uiConstructed || _applyingShapeState)
            return;
        UpdateOverallHeightReadout();
        QueuePreview();
    }

    private void ShapeStyleChanged(object sender, SelectionChangedEventArgs eventArgs)
    {
        if (!_uiConstructed || _applyingShapeState)
            return;
        UpdateStyleHint();
        QueuePreview();
    }

    private void BulbSettingChanged(object sender, RoutedEventArgs eventArgs)
    {
        if (!_uiConstructed || _applyingShapeState)
            return;
        QueuePreview();
    }

    private void BulbToggled(object sender, RoutedEventArgs eventArgs)
    {
        if (!_uiConstructed)
            return;
        BulbPanel.Visibility = HasBulb ? Visibility.Visible : Visibility.Collapsed;
        if (_applyingShapeState)
            return;
        QueuePreview();
    }

    private bool HasBulb => BulbCheckBox.IsChecked == true;

    /// <summary>
    /// Reads the bulb panel. Out-of-range or non-numeric entries fall back to the
    /// default so the preview keeps working; the parameters' own validation reports
    /// the range when the entry is a number.
    /// </summary>
    private BulbSettings ReadBulb()
    {
        var length = BulbLengthInput.TryGetValue(out var lengthPercent) ? lengthPercent : BulbSettings.Default.LengthPercent;
        var width = BulbWidthInput.TryGetValue(out var widthPercent) ? widthPercent : BulbSettings.Default.WidthPercent;
        var foreAft = BulbForeAftInput.TryGetValue(out var foreAftPercent) ? foreAftPercent : BulbSettings.Default.ForeAftPercent;
        var rise = BulbRiseInput.TryGetValue(out var risePercent) ? risePercent : BulbSettings.Default.RisePercent;
        return new BulbSettings(length, width, foreAft, rise);
    }

    private BowStyle SelectedBowStyle => (BowStyleBox.SelectedItem as BowStyleChoice)?.Value ?? BowStyle.Pointed;

    private SternStyle SelectedSternStyle => (SternStyleBox.SelectedItem as SternStyleChoice)?.Value ?? SternStyle.Transom;

    private BodyStyle SelectedBodyStyle =>
        (BodyStyleBox.SelectedItem as BodyStyleChoice)?.Value ?? HullParameters.Default.EffectiveShape.Body.Style;

    /// <summary>Each style box carries the description of its current choice as its tooltip.</summary>
    private void UpdateStyleHint()
    {
        if (Preview is null)
            return;
        BowStyleBox.ToolTip = (BowStyleBox.SelectedItem as BowStyleChoice)?.Hint;
        BodyStyleBox.ToolTip = (BodyStyleBox.SelectedItem as BodyStyleChoice)?.Hint;
        SternStyleBox.ToolTip = (SternStyleBox.SelectedItem as SternStyleChoice)?.Hint;
    }

    private void ApplyShapeToControls(HullShapeSettings shape)
    {
        BodyStyleBox.SelectedItem = BodyStyleChoice.For(shape.Body.Style);
        BowFullnessSlider.SetDisplayedValue(shape.Bow.Fullness);
        BowFlareSlider.SetDisplayedValue(shape.Bow.Flare);
        EntranceLengthInput.SetDisplayedValue(shape.Bow.EntranceLengthPercent);
        BodyFullnessSlider.SetDisplayedValue(shape.Body.Fullness);
        BodySideShapeSlider.SetDisplayedValue(shape.Body.SideShape);
        BodyChineSlider.SetDisplayedValue(shape.Body.Chine);
        BodyFlatBottomSlider.SetDisplayedValue(shape.Body.FlatBottom);
        SternFullnessSlider.SetDisplayedValue(shape.Stern.Fullness);
        SternSideShapeSlider.SetDisplayedValue(shape.Stern.SideShape);
        RunLengthInput.SetDisplayedValue(shape.Stern.RunLengthPercent);
        BowDeckRiseInput.SetDisplayedValue(shape.Profile.BowDeckRise);
        SternDeckRiseInput.SetDisplayedValue(shape.Profile.SternDeckRise);
        BowKeelRiseInput.SetDisplayedValue(shape.Profile.BowKeelRise);
        SternKeelRiseInput.SetDisplayedValue(shape.Profile.SternKeelRise);
    }

    private ShipDocument? ReadEditorDocument(out string? message)
    {
        var parameters = ReadParameters(out message);
        if (parameters is null)
            return null;

        var current = _editor.Document;
        return current with
        {
            Hull = parameters,
            Datum = current.EffectiveDatum with
            {
                // The generator's odd lattice mirrors around X=0; an even lattice spans
                // -width/2 through width/2-1 and therefore mirrors around X=-0.5.
                CenterPlaneX = parameters.HasSingleBlockCenterline
                    ? DesignMeasure.Zero
                    : DesignMeasure.FromTwiceMetres(-1),
            },
            Smoothing = current.Smoothing with { NativeMethod = parameters.Smoothing },
            Superstructure = current.Superstructure with
            {
                Legacy = current.Superstructure.Legacy is null
                    ? null
                    : LegacySuperstructureSettings.FromParameters(parameters.EffectiveSuperstructure),
            },
        };
    }

    private void CaptureEditorState()
    {
        if (_workspace.IsDraftDirty) return;
        var document = ReadEditorDocument(out _);
        if (document is null)
        {
            _editor.MarkInvalidDraft();
            return;
        }

        // Automatic parity prevention: an even-width hull cannot carry a centerline barbette, so
        // never commit that combination from the normal width controls. The requested even width
        // is not applied, and every established barbette position stays exactly where it was.
        if (!document.Hull.HasSingleBlockCenterline &&
            (document.Barbettes.Length > 0 || _workspace.Document.Barbettes.Length > 0))
        {
            _applyingShapeState = true;
            try
            {
                WidthInput.SetDisplayedValue(_editor.Document.Hull.Width);
            }
            finally
            {
                _applyingShapeState = false;
            }
            SetStatus("Centerline barbettes require an odd hull width. " +
                      "The even width was not applied; remove the barbettes or choose an odd width.", StatusTone.Warning);
            return;
        }

        _editor.Commit(document);
    }

    private void QueuePreview(bool captureEditorState = true)
    {
        if (!_loaded || _applyingShapeState)
            return;
        if (captureEditorState)
            CaptureEditorState();
        if (_workspace.IsDraftDirty)
        {
            InvalidateVisibleWorkspacePreview();
            return;
        }
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private async Task GeneratePreviewAsync()
    {
        _previewTimer.Stop();
        if (_workspace.IsDraftDirty)
        {
            InvalidateVisibleWorkspacePreview();
            return;
        }
        // Invalidate work before reading the controls. An invalid edit must cancel
        // an older valid generation too, or that older task could repopulate the
        // preview and re-enable export after the invalid state is shown.
        _generationCancellation?.Cancel();
        _generationCancellation?.Dispose();
        _generationCancellation = null;
        var sequence = ++_generationSequence;
        string? generationUnavailable = null;
        if (!InternalStructureFeatureAccess.CanPreviewOrExport(_editor.Document,
                _featureExposure, out generationUnavailable) ||
            !SlopeRefinementFeatureAccess.CanPreviewOrExport(_editor.Document, _featureExposure, out generationUnavailable) ||
            !EditorControlProjection.CanGenerate(_editor.Document, _featureExposure, out generationUnavailable))
        {
            _previewBoundary.InvalidateCurrent();
            _currentHull = null;
            _currentShipSnapshot = null;
            _workspace.ClearResolvedGeneration();
            _activeInternalStructureEditor?.SetGenerationResult(null);
            Preview.SetHull(null);
            UpdateInternalArmorOption(null);
            BlockCountText.Text = DimensionsText.Text = MaterialCostText.Text = "——";
            SetStatus(generationUnavailable!, StatusTone.Warning);
            ExportButton.IsEnabled = false;
            return;
        }
        var parameters = ReadParameters(out var message);
        if (parameters is null)
        {
            _currentHull = null;
            _currentShipSnapshot = null;
            _workspace.ClearResolvedGeneration();
            _activeInternalStructureEditor?.SetGenerationResult(null);
            Preview.SetHull(null);
            UpdateInternalArmorOption(null);
            BlockCountText.Text = DimensionsText.Text = MaterialCostText.Text = "——";
            SetStatus(message!, StatusTone.Warning);
            ExportButton.IsEnabled = false;
            return;
        }

        UpdateBlueprintName(parameters);

        // The frozen barbette ruler, the arrangement workspace and the preview all consume the one
        // derived layout frame. Preparation is derived work: it never commits a revision or writes
        // the persisted datum, so a preview cannot create a background undo item.
        var document = _editor.Document;
        await _workspace.ResolveLayoutFrameAsync(CancellationToken.None);
        if (sequence != _generationSequence)
            return;
        RefreshArrangementWorkspace();

        // Generation consumes the same frame. When the persisted datum is stale, an in-memory,
        // frame-consistent document is composed; it is never persisted by the preview. With no
        // current frame the document is passed unchanged so ShipGenerationService fails closed.
        var generationDocument = document;
        if (_workspace.LayoutFrame is { } frame &&
            (document.EffectiveDatum.LayoutBowZ != frame.BowDatum ||
             document.EffectiveDatum.CenterPlaneX != frame.CenterPlaneX))
        {
            generationDocument = document with
            {
                Datum = new LayoutDatum(frame.BowDatum, frame.CenterPlaneX),
            };
        }

        var revisionRequest = _previewBoundary.Begin(_editor.Revision);
        _generationCancellation = CancellationTokenSource.CreateLinkedTokenSource(revisionRequest.CancellationToken);
        var cancellation = _generationCancellation;
        var cancellationToken = cancellation.Token;
        SetStatus("Updating preview…", StatusTone.Working);
        ExportButton.IsEnabled = false;
        IReadOnlyList<DesignDiagnostic>? internalGenerationDiagnostics = null;

        try
        {
            ShipGenerationSnapshot? shipSnapshot = null;
            GeneratedHull hull;
            var requiresComposition = generationDocument.Barbettes.Length > 0 ||
                generationDocument.Internals.Families.Any(family => family.Enabled) ||
                generationDocument.Smoothing.ExplicitRefinement is not null ||
                SmoothingMethodMapping.IsDecorative(generationDocument.Smoothing.NativeMethod);
            if (_catalog is null && requiresComposition)
                throw new InvalidOperationException(
                    "Barbette, decorative and internal-structure generation need the installed game catalog before preview or export.");
            // One catalog-resolved physical snapshot is the export authority for every preview,
            // including a plain hull with no components. The raw generator remains only for the
            // explicitly provisional no-catalog display and the deferred inverted construction
            // that cannot reach an authoritative preview.
            var catalogResolved = _catalog is not null &&
                generationDocument.Smoothing.NativeMethod != SmoothingMethod.InvertedTriangleFill;
            if (catalogResolved)
            {
                var result = await Task.Run(() => _shipGenerator.Generate(
                    generationDocument, revisionRequest.Revision, _catalog!, cancellationToken: cancellationToken));
                if (!result.IsValid || result.Snapshot is null)
                {
                    internalGenerationDiagnostics = result.Diagnostics;
                    throw new HullGenerationException(result.Diagnostics.Select(diagnostic => diagnostic.ToString()));
                }
                shipSnapshot = result.Snapshot;
                hull = shipSnapshot.Hull;
            }
            else
            {
                hull = await Task.Run(() => _generator.Generate(parameters, cancellationToken));
            }
            if (sequence != _generationSequence || cancellationToken.IsCancellationRequested)
                return;

            if (!_previewBoundary.TryAccept(revisionRequest, hull))
                return;
            _currentHull = hull;
            _currentShipSnapshot = shipSnapshot;
            if (shipSnapshot is null || !_workspace.PublishResolvedGeneration(shipSnapshot))
                _workspace.ClearResolvedGeneration();
            _activeInternalStructureEditor?.SetGenerationResult(shipSnapshot?.Internals);
            _applyingShapeState = true;
            try
            {
                UpdateHybridOffsetControls(hull.OccupiedHeight);
                HybridOffsetInput.SetDisplayedValue(hull.Parameters.HybridFillOffset);
                OverallHeightText.Text = $"{hull.OccupiedHeight} m";
            }
            finally { _applyingShapeState = false; }
            if (shipSnapshot is not null) Preview.SetResolvedShip(shipSnapshot, highlightEdits: !_suppressEditHighlight);
            else Preview.SetHull(hull, highlightEdits: !_suppressEditHighlight);
            _visiblePreviewIsDraft = false;
            _suppressEditHighlight = false;
            UpdateInternalArmorOption(hull);
            BlockCountText.Text = parameters.Beamify
                ? $"{hull.BlockCount:N0} ({hull.OccupiedCellCount:N0} m)"
                : hull.BlockCount.ToString("N0", CultureInfo.CurrentCulture);
            DimensionsText.Text = $"{hull.OccupiedLength} × {hull.OccupiedWidth} × {hull.OccupiedHeight} m";
            var cost = _catalog is null ? 0 : hull.Blocks.Sum(block => EstimateCost(_catalog, block));
            MaterialCostText.Text = _catalog is null ? "N/A" : cost.ToString("N0", CultureInfo.CurrentCulture);
            var geometryWarnings = shipSnapshot?.Diagnostics
                .Where(diagnostic => diagnostic.Severity == DesignSeverity.Warning).ToArray() ?? [];
            if (_catalog is null)
                SetStatus("Preview ready (provisional, not catalog-resolved). Export needs the installed game catalog.",
                    StatusTone.Warning);
            else if (geometryWarnings.Length > 0)
                SetStatus($"Preview ready with {geometryWarnings.Length} geometry " +
                          $"warning{(geometryWarnings.Length == 1 ? string.Empty : "s")}.", StatusTone.Warning);
            else if (shipSnapshot?.Refinement is { } skin)
                SetStatus($"Preview ready: {skin.Extensions.Length} decorative extensions; structural dimensions unchanged. Generated-output game review pending.", StatusTone.Warning);
            else if (hull.ConstructionNotes.Count > 0)
                SetStatus("Preview ready. " + hull.ConstructionNotes[0], StatusTone.Warning);
            else if (parameters.HasSingleBlockCenterline)
                SetStatus("Preview ready.", StatusTone.Ready);
            else
                SetStatus("Preview ready. Even width: the centreline falls between two blocks, so export will ask for confirmation.", StatusTone.Warning);
            ExportButton.IsEnabled = _catalog is not null && !_editor.HasInvalidDraft;
        }
        catch (OperationCanceledException)
        {
            // A newer edit has already queued or started its own preview.
        }
        catch (Exception error)
        {
            if (sequence != _generationSequence || cancellationToken.IsCancellationRequested)
                return;
            _currentHull = null;
            _currentShipSnapshot = null;
            _workspace.ClearResolvedGeneration();
            if (internalGenerationDiagnostics is null)
                _activeInternalStructureEditor?.SetGenerationResult(null);
            else
                _activeInternalStructureEditor?.SetGenerationDiagnostics(internalGenerationDiagnostics);
            Preview.SetHull(null);
            UpdateInternalArmorOption(null);
            SetStatus($"Could not generate this hull: {error.Message}", StatusTone.Error);
        }
    }

    private HullParameters? ReadParameters(out string? message)
    {
        message = null;
        if (!LengthInput.TryGetValue(out var length) ||
            !WidthInput.TryGetValue(out var width) ||
            !HeightInput.TryGetValue(out var height))
        {
            message = "Length, width, and height must be whole numbers.";
            return null;
        }
        if (!HybridOffsetInput.TryGetValue(out var hybridOffset))
        {
            message = "Horizontal top offset must be a whole number.";
            return null;
        }

        if (_hullArmorRows.Concat(_bottomArmorRows).Any(row => row.MaterialBox.SelectedItem is not MaterialChoice))
        {
            message = "Choose a material for every hull armor layer.";
            return null;
        }

        if (!TryReadShape(out var shape))
        {
            message = "Every shape control must be a valid number.";
            return null;
        }
        if (DeckCheckBox.IsChecked == true &&
            _deckArmorRows.Any(row => row.MaterialBox.SelectedItem is not MaterialChoice))
        {
            message = "Choose a material for every deck armor layer.";
            return null;
        }

        var smoothing = (SmoothingBox.SelectedItem as SmoothingChoice)?.Value ?? SmoothingMethod.None;
        var hullArmor = new ArmorLayout(_hullArmorRows.Select(ReadArmorLayer));
        var bottomArmor = EditorControlProjection.ResolveBottomArmor(_bottomArmorInheritsSide,
            new ArmorLayout(_bottomArmorRows.Select(ReadArmorLayer)));
        var parameters = new HullParameters(
            length,
            width,
            height,
            shape.Bow.Fullness,
            shape.Stern.Fullness,
            shape.Body.Fullness,
            hullArmor,
            DeckCheckBox.IsChecked == true
                ? new ArmorLayout(_deckArmorRows.Select(ReadArmorLayer))
                : null,
            SingleBlocksCheckBox.IsChecked != true,
            smoothing,
            hybridOffset,
            SelectedBowStyle,
            SelectedSternStyle,
            HasBulb,
            ReadBulb(),
            shape,
            bottomArmor,
            // Superstructures are post-2.0 and have no normal-surface control; the frozen
            // document default is a disabled layout. The generator, persistence and naming
            // implementation remain intact for the preserved legacy path and its tests.
            SuperstructureSettings.Default);
        if (HullEditorSettings.ValidateFeatureAvailability(parameters, _featureExposure) is { } accessError)
        {
            message = accessError;
            return null;
        }
        var errors = parameters.Validate();
        if (errors.Count > 0)
        {
            message = errors[0];
            return null;
        }
        return parameters;
    }

    internal static string CreateBlueprintName(HullParameters parameters)
    {
        var shape = HullShapePreset.Match(parameters)?.Name ?? "Custom";
        var name = $"HF_{shape}_{parameters.Length}x{parameters.Width}x{parameters.Height}";
        var superstructure = parameters.EffectiveSuperstructure;
        if (!superstructure.Enabled)
            return name;
        var style = superstructure.Style switch
        {
            SuperstructureStyle.CenterIsland => "CenterIsland",
            SuperstructureStyle.FrenchHotel => "FrenchHotel",
            SuperstructureStyle.JapanesePagoda => "Pagoda",
            _ => superstructure.Style.ToString(),
        };
        var smoothing = superstructure.Smoothing == SuperstructureSmoothingMethod.HorizontalSlopeFill ? "_HFill" : "";
        return $"{name}_SS_{style}_L{superstructure.Levels}{smoothing}_FA{superstructure.ForeAftPercent}";
    }

    private bool TryReadShape(out HullShapeSettings shape)
    {
        var valid =
            BowFullnessSlider.TryGetValue(out var bowFullness) &
            BowFlareSlider.TryGetValue(out var bowFlare) &
            EntranceLengthInput.TryGetValue(out var entranceLength) &
            BodyFullnessSlider.TryGetValue(out var bodyFullness) &
            BodySideShapeSlider.TryGetValue(out var bodySideShape) &
            BodyChineSlider.TryGetValue(out var bodyChine) &
            BodyFlatBottomSlider.TryGetValue(out var bodyFlatBottom) &
            SternFullnessSlider.TryGetValue(out var sternFullness) &
            SternSideShapeSlider.TryGetValue(out var sternSideShape) &
            RunLengthInput.TryGetValue(out var runLength) &
            BowDeckRiseInput.TryGetValue(out var bowDeckRise) &
            SternDeckRiseInput.TryGetValue(out var sternDeckRise) &
            BowKeelRiseInput.TryGetValue(out var bowKeelRise) &
            SternKeelRiseInput.TryGetValue(out var sternKeelRise);

        shape = new HullShapeSettings(
            new BowShapeSettings(bowFullness, bowFlare, entranceLength),
            new BodyShapeSettings(SelectedBodyStyle, bodyFullness, bodySideShape, bodyChine, bodyFlatBottom),
            new SternShapeSettings(sternFullness, sternSideShape, runLength),
            new HullProfileSettings(bowDeckRise, sternDeckRise, bowKeelRise, sternKeelRise));
        return valid;
    }

    private void UpdateProfileRiseControls()
    {
        if (HeightInput is null || BowDeckRiseInput is null)
            return;

        if (!HeightInput.TryGetValue(out var height))
            return;

        var maximum = height <= 2 ? 0 : height - 2;
        foreach (var input in new[] { BowDeckRiseInput, SternDeckRiseInput, BowKeelRiseInput, SternKeelRiseInput })
        {
            input.SliderMaximum = maximum;
            if (!input.TryGetValue(out var rise) || rise < 0 || rise > maximum)
                input.SetDisplayedValue(Math.Clamp(rise, 0, maximum));
        }
    }

    private void UpdateOverallHeightReadout()
    {
        if (OverallHeightText is null)
            return;

        OverallHeightText.Text =
            HeightInput.TryGetValue(out var height) &&
            BowDeckRiseInput.TryGetValue(out var bowRise) &&
            SternDeckRiseInput.TryGetValue(out var sternRise)
                ? $"{(long)height + Math.Max(bowRise, sternRise)} m"
                : "——";
    }

    private void UpdateHybridOffsetControls(int? occupiedHeight = null)
    {
        if (HybridOffsetPanel is null || HybridOffsetInput is null || HeightInput is null)
            return;

        HybridOffsetPanel.Visibility =
            (SmoothingBox.SelectedItem as SmoothingChoice)?.Value == SmoothingMethod.HybridSlopeFill
                ? Visibility.Visible
                : Visibility.Collapsed;
        // Keep the last measured range while an edit is being generated. The
        // generator normalizes the offset against its actual unsmoothed voxels.
        if (occupiedHeight is { } height)
            HybridOffsetInput.SliderMaximum = Math.Max(1, height - 1);
    }

    private void AddBottomArmorLayerClicked(object sender, RoutedEventArgs eventArgs)
    {
        _bottomArmorInheritsSide = false;
        AddArmorLayer(_bottomArmorRows, BottomArmorLayersPanel, SelectedLayer(_bottomArmorRows[^1]), "BOTTOM");
        QueuePreview();
    }

    private void EditorStateChanged(object? sender, EventArgs eventArgs)
    {
        _previewBoundary.MoveToRevision(_editor.Revision);
        ExportButton.IsEnabled = false;
        UpdateEditorChrome();
        RefreshArrangementWorkspace();
    }

    /// <summary>
    /// Updates the title bar's ship name and history buttons. 2.0 has no project
    /// persistence surface, so the editor session's savepoint and project path are not
    /// shown; the immutable document revision remains the internal editing boundary.
    /// </summary>
    private void UpdateEditorChrome()
    {
        if (ProjectNameText is null)
            return;
        ProjectNameText.Text = _editor.Document.Name + (_editor.IsDirty ? " *" : string.Empty);
        UndoButton.IsEnabled = _editor.CanUndo;
        RedoButton.IsEnabled = _editor.CanRedo;
        Title = $"{_editor.Document.Name}{(_editor.IsDirty ? " *" : string.Empty)} — Hull Forge";
    }

    private void UpdateSidebarEditLock()
    {
        var enabled = !_workspace.IsDraftDirty;
        SidebarSettingsPanel.IsEnabled = enabled;
        SidebarActionsPanel.IsEnabled = enabled;
        SidebarSettingsPanel.Opacity = enabled ? 1 : 0.55;
        SidebarActionsPanel.Opacity = enabled ? 1 : 0.55;
        if (_hullPresetsWindow is not null)
            _hullPresetsWindow.Browser.IsEnabled = enabled;
    }

    private bool CanEditSidebar()
    {
        if (!_workspace.IsDraftDirty) return true;
        SetStatus("Apply or Cancel the workspace draft before editing hull settings.", StatusTone.Warning);
        return false;
    }

    private bool ResolveWorkspaceDraft(string action, WorkspaceCloseChoice? explicitChoice = null)
    {
        if (!_workspace.IsDraftDirty)
        {
            _workspace.Cancel();
            return true;
        }

        var choice = explicitChoice;
        if (choice is null)
        {
            var answer = MessageBox.Show(this,
                $"Apply workspace changes before {action}?\n\nYes applies them as one undo item. No discards the whole draft. Cancel keeps editing.",
                "Workspace draft", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Yes);
            choice = answer switch
            {
                MessageBoxResult.Yes => WorkspaceCloseChoice.Apply,
                MessageBoxResult.No => WorkspaceCloseChoice.Discard,
                _ => WorkspaceCloseChoice.KeepEditing,
            };
        }

        return _workspace.TryClose(choice.Value);
    }

    private void RefreshArrangementWorkspace()
    {
        var document = _workspace.Document;
        var datum = document.EffectiveDatum;
        // The current derived frame defines the ruler datum and supported end for the visible
        // revision. Until a frame is published, the persisted datum and the nominal hull length
        // keep the pending draft usable; first Add never uses that fallback.
        DesignMeasure layoutBowZ;
        DesignMeasure centerPlaneX;
        DesignMeasure supportedEnd;
        if (_workspace.LayoutFrame is { } frame)
        {
            layoutBowZ = frame.BowDatum;
            centerPlaneX = frame.CenterPlaneX;
            supportedEnd = frame.SupportedRulerEnd;
        }
        else
        {
            layoutBowZ = datum.LayoutBowZ;
            centerPlaneX = datum.CenterPlaneX;
            supportedEnd = DesignMeasure.FromMetres(document.Hull.Length);
        }

        _workspace.SetArrangementSolution(ArrangementSolver.Solve(document.Arrangement,
            new ArrangementSolveOptions(
                layoutBowZ,
                supportedEnd,
                CenterPlaneX: centerPlaneX)));
    }

    private void WorkspaceHostToggleRequested(object sender, EventArgs eventArgs)
    {
        if (_workspace.IsDetached)
            RedockWorkspace();
        else
            DetachWorkspace();
    }

    private void DetachWorkspace()
    {
        if (_detachedWorkspace is not null)
            return;

        if (WorkspaceRow.ActualHeight >= WorkspaceRow.MinHeight)
            _dockedWorkspaceHeight = new GridLength(WorkspaceRow.ActualHeight);
        SaveWorkspaceHeight();
        WorkspaceRow.MinHeight = 0;
        WorkspaceRow.Height = new GridLength(0);
        WorkspaceSplitterRow.Height = new GridLength(0);
        DockedWorkspaceHost.Visibility = Visibility.Collapsed;
        _workspace.IsDetached = true;

        _detachedWorkspace = new DetachedWorkspaceWindow(_workspace, this, ExecuteApplicationShortcut);
        _activeInternalStructureEditor = InstallInternalStructureEditor(_detachedWorkspace.Host);
        _activeInternalStructureEditor?.SetGenerationResult(_currentShipSnapshot?.Internals);
        _detachedWorkspace.RedockRequested += (_, _) => RedockWorkspace();
        _detachedWorkspace.Show();
    }

    private void RedockWorkspace()
    {
        var detached = _detachedWorkspace;
        if (detached is null)
            return;

        _detachedWorkspace = null;
        DisposeInstalledEditor((TabItem)detached.Host.Tabs.Items[1]);
        detached.CloseForRedock();
        _workspace.IsDetached = false;
        _activeInternalStructureEditor = DockedWorkspaceHost.Tabs.Items[1] is TabItem
            { Content: ScrollViewer { Content: InternalStructureEditor editor } }
            ? editor
            : InstallInternalStructureEditor(DockedWorkspaceHost);
        _activeInternalStructureEditor?.SetGenerationResult(_currentShipSnapshot?.Internals);
        DockedWorkspaceHost.Visibility = Visibility.Visible;
        WorkspaceSplitterRow.Height = new GridLength(8);
        WorkspaceRow.MinHeight = 150;
        WorkspaceRow.Height = _dockedWorkspaceHeight.Value >= 150
            ? _dockedWorkspaceHeight
            : new GridLength(240);
        Dispatcher.BeginInvoke(() =>
        {
            ApplyWorkspaceHeightForAvailableSpace();
            DockedWorkspaceHost.FocusWorkspace();
        });
    }

    private void PreviewEmptyAreaPointerDown(object sender, MouseButtonEventArgs eventArgs)
    {
        // Viewport3D does not receive a mouse hit when the ray misses every model.
        // The transparent parent fills that gap and hands the same orbit gesture to
        // the preview; capture keeps the rest of the drag on the existing path.
        Preview.BeginHold(eventArgs.GetPosition(Preview));
        Preview.CaptureMouse();
        eventArgs.Handled = true;
    }

    private void WorkspaceSplitterDragCompleted(object sender, DragCompletedEventArgs eventArgs)
    {
        if (_workspace.IsDetached || WorkspaceRow.ActualHeight < WorkspaceRow.MinHeight)
            return;
        _dockedWorkspaceHeight = new GridLength(WorkspaceRow.ActualHeight);
        SaveWorkspaceHeight();
    }

    private void SaveWorkspaceHeight() => _workspacePreferences.Save(
        new WorkspacePresentationPreferences(_dockedWorkspaceHeight.Value));

    private void ApplyWorkspaceHeightForAvailableSpace()
    {
        if (_workspace.IsDetached || PreviewWorkspaceGrid.ActualHeight <= 0)
            return;
        var maximum = Math.Max(WorkspaceRow.MinHeight,
            PreviewWorkspaceGrid.ActualHeight - ViewportRow.MinHeight - WorkspaceSplitterRow.ActualHeight);
        WorkspaceRow.Height = new GridLength(Math.Min(_dockedWorkspaceHeight.Value, maximum));
    }

    /// <summary>
    /// Drops the export-authoritative workspace state while a draft is dirty. The visible
    /// geometry is deliberately left in place: the debounced non-authoritative draft preview
    /// replaces it, and a blank frame between edits would be worse than the last valid view.
    /// </summary>
    private void InvalidateVisibleWorkspacePreview()
    {
        _previewTimer.Stop();
        _generationCancellation?.Cancel();
        _generationSequence++;
        _currentHull = null;
        _currentShipSnapshot = null;
        _workspace.ClearResolvedGeneration();
        _activeInternalStructureEditor?.SetGenerationResult(null);
        ExportButton.IsEnabled = false;
        if (_loaded)
            SetStatus("Workspace draft pending. Apply or Cancel before preview and export.", StatusTone.Warning);
    }

    /// <summary>
    /// Debounces a non-authoritative preview of the current workspace draft. The draft has no
    /// committed revision, so this work never enters <see cref="_previewBoundary" /> and can
    /// never enable export.
    /// </summary>
    private void ScheduleDraftWorkspacePreview()
    {
        if (!_loaded || _applyingShapeState)
            return;
        _draftPreviewTimer.Stop();
        _draftPreviewTimer.Start();
    }

    private void CancelDraftPreviewWork()
    {
        _draftPreviewTimer.Stop();
        _draftPreviewCancellation?.Cancel();
        _draftPreviewCancellation?.Dispose();
        _draftPreviewCancellation = null;
        _draftPreviewSequence++;
        _visiblePreviewIsDraft = false;
    }

    private void ScheduleCommittedWorkspacePreview(long revision)
    {
        if (!_loaded || revision != _editor.Revision)
            return;
        CancelDraftPreviewWork();
        QueuePreview(captureEditorState: false);
    }

    /// <summary>
    /// Generates the visible geometry for an uncommitted workspace draft. The result is shown for
    /// inspection only: it never becomes <see cref="_currentShipSnapshot" />, never enters the
    /// export-authoritative preview boundary, and never enables export. A newer draft, an
    /// Apply/Cancel or a conflict always wins.
    /// </summary>
    private async Task GenerateDraftPreviewAsync()
    {
        _draftPreviewTimer.Stop();
        if (!_loaded || !_workspace.IsDraftDirty || _workspace.HasConflict)
            return;

        _draftPreviewCancellation?.Cancel();
        _draftPreviewCancellation?.Dispose();
        _draftPreviewCancellation = null;
        var sequence = ++_draftPreviewSequence;
        var draftDocument = _workspace.Document;

        string? unavailable = null;
        if (!InternalStructureFeatureAccess.CanPreviewOrExport(draftDocument, _featureExposure, out unavailable) ||
            !SlopeRefinementFeatureAccess.CanPreviewOrExport(draftDocument, _featureExposure, out unavailable) ||
            !EditorControlProjection.CanGenerate(draftDocument, _featureExposure, out unavailable))
        {
            Preview.SetHull(null);
            _activeInternalStructureEditor?.SetGenerationResult(null);
            _visiblePreviewIsDraft = true;
            SetStatus("Draft preview unavailable. " + unavailable, StatusTone.Warning);
            return;
        }

        var requiresComposition = draftDocument.Barbettes.Length > 0 ||
            draftDocument.Internals.Families.Any(family => family.Enabled) ||
            draftDocument.Smoothing.ExplicitRefinement is not null ||
            SmoothingMethodMapping.IsDecorative(draftDocument.Smoothing.NativeMethod);
        if (requiresComposition && _catalog is null)
        {
            Preview.SetHull(null);
            _activeInternalStructureEditor?.SetGenerationResult(null);
            _visiblePreviewIsDraft = true;
            SetStatus("Draft preview needs the installed game catalog. Apply or Cancel to finish.", StatusTone.Warning);
            return;
        }

        // The draft is non-authoritative, but it composes from the same current frame when one is
        // published. A frame-consistent in-memory datum is never persisted; with no frame the draft
        // is passed unchanged so ShipGenerationService fails closed.
        var generationDocument = draftDocument;
        if (_workspace.LayoutFrame is { } frame &&
            (draftDocument.EffectiveDatum.LayoutBowZ != frame.BowDatum ||
             draftDocument.EffectiveDatum.CenterPlaneX != frame.CenterPlaneX))
        {
            generationDocument = draftDocument with
            {
                Datum = new LayoutDatum(frame.BowDatum, frame.CenterPlaneX),
            };
        }

        // The draft is non-authoritative, but it still uses the one catalog-resolved composition
        // route for a plain hull so the displayed geometry matches the committed preview.
        var catalogResolved = _catalog is not null &&
            generationDocument.Smoothing.NativeMethod != SmoothingMethod.InvertedTriangleFill;
        var cancellation = new CancellationTokenSource();
        _draftPreviewCancellation = cancellation;
        var cancellationToken = cancellation.Token;
        try
        {
            ShipGenerationSnapshot? snapshot = null;
            GeneratedHull hull;
            if (catalogResolved)
            {
                var result = await Task.Run(() => _shipGenerator.Generate(
                    generationDocument, _editor.Revision, _catalog!, cancellationToken: cancellationToken));
                if (sequence != _draftPreviewSequence || cancellationToken.IsCancellationRequested)
                    return;
                if (!result.IsValid || result.Snapshot is null)
                {
                    _activeInternalStructureEditor?.SetGenerationDiagnostics(result.Diagnostics);
                    Preview.SetHull(null);
                    _visiblePreviewIsDraft = true;
                    SetStatus("Draft preview could not resolve: " +
                        string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message)), StatusTone.Warning);
                    return;
                }
                snapshot = result.Snapshot;
                hull = snapshot.Hull;
            }
            else
            {
                hull = await Task.Run(() => _generator.Generate(draftDocument.Hull, cancellationToken));
            }

            // A newer draft, an Apply/Cancel or a conflict owns the view now.
            if (sequence != _draftPreviewSequence ||
                cancellationToken.IsCancellationRequested ||
                !_workspace.IsDraftDirty ||
                _workspace.HasConflict ||
                !ReferenceEquals(_workspace.Document, draftDocument))
                return;

            if (snapshot is not null)
                Preview.SetResolvedShip(snapshot, highlightEdits: false);
            else
                Preview.SetHull(hull, highlightEdits: false);
            _visiblePreviewIsDraft = true;
            _activeInternalStructureEditor?.SetGenerationResult(snapshot?.Internals);
            SetStatus("Draft preview only. Apply to make this geometry authoritative; export stays disabled.",
                StatusTone.Warning);
        }
        catch (OperationCanceledException)
        {
            // A newer edit, Apply or Cancel owns the view.
        }
        catch (Exception error)
        {
            if (sequence != _draftPreviewSequence || cancellationToken.IsCancellationRequested)
                return;
            Preview.SetHull(null);
            _visiblePreviewIsDraft = true;
            SetStatus($"Draft preview failed: {error.Message}", StatusTone.Error);
        }
    }

    private void UndoClicked(object sender, RoutedEventArgs eventArgs) => TryUndo();

    private bool TryUndo(WorkspaceCloseChoice? workspaceChoice = null)
    {
        if (!ResolveWorkspaceDraft("undoing the last committed change", workspaceChoice))
            return false;
        if (!_editor.Undo())
            return false;
        _suppressEditHighlight = true;
        ApplyEditorParameters(_editor.Document.Hull, resetArmor: true);
        SetStatus("Undo applied.", StatusTone.Ready);
        return true;
    }

    private void RedoClicked(object sender, RoutedEventArgs eventArgs) => TryRedo();

    private bool TryRedo(WorkspaceCloseChoice? workspaceChoice = null)
    {
        if (!ResolveWorkspaceDraft("redoing the next committed change", workspaceChoice))
            return false;
        if (!_editor.Redo())
        {
            SetStatus("Nothing remains to redo after resolving the workspace draft.", StatusTone.Neutral);
            return false;
        }
        _suppressEditHighlight = true;
        ApplyEditorParameters(_editor.Document.Hull, resetArmor: true);
        SetStatus("Redo applied.", StatusTone.Ready);
        return true;
    }

    internal EditorSession EditorSessionForTests => _editor;

    internal WorkspaceViewModel WorkspaceForTests => _workspace;
    internal bool SidebarEditingEnabledForTests => SidebarSettingsPanel.IsEnabled && SidebarActionsPanel.IsEnabled;
    internal bool PresetsEnabledForTests => EnsureHullPresetsWindow().Browser.IsEnabled;
    internal Grid PreviewPointerSurfaceForTests => (Grid)Preview.Parent;

    internal InternalStructureEditor? InternalStructureEditorForTests => _activeInternalStructureEditor;

    internal bool ExperimentalFeaturesEnabledForTests => _featureExposure.ExperimentalFeaturesEnabled;

    // SHP01 test surface: the normal Shape V2 controls, exposed so the body-preset
    // initialization, the slider-to-Custom transition and the independence of the
    // unrelated controls can be exercised without reaching through the visual tree.
    internal ComboBox BodyFormBoxForTests => BodyStyleBox;
    internal SliderRow BodyFullnessSliderForTests => BodyFullnessSlider;
    internal SliderRow BodySideShapeSliderForTests => BodySideShapeSlider;
    internal SliderRow BodyChineSliderForTests => BodyChineSlider;
    internal SliderRow BodyFlatBottomSliderForTests => BodyFlatBottomSlider;
    internal SliderRow BowFullnessSliderForTests => BowFullnessSlider;
    internal SliderRow BowFlareSliderForTests => BowFlareSlider;
    internal SliderRow SternFullnessSliderForTests => SternFullnessSlider;
    internal SliderRow SternSideShapeSliderForTests => SternSideShapeSlider;
    internal ComboBox BowStyleBoxForTests => BowStyleBox;
    internal ComboBox SternStyleBoxForTests => SternStyleBox;
    internal CheckBox BulbCheckBoxForTests => BulbCheckBox;
    internal DimensionInput EntranceLengthInputForTests => EntranceLengthInput;
    internal DimensionInput RunLengthInputForTests => RunLengthInput;
    internal DimensionInput BowDeckRiseInputForTests => BowDeckRiseInput;
    internal DimensionInput SternDeckRiseInputForTests => SternDeckRiseInput;
    internal DimensionInput BowKeelRiseInputForTests => BowKeelRiseInput;
    internal DimensionInput SternKeelRiseInputForTests => SternKeelRiseInput;
    internal BodyStyle SelectedBodyFormForTests => SelectedBodyStyle;
    internal void SelectBodyFormForTests(BodyStyle style) => BodyStyleBox.SelectedItem = BodyStyleChoice.For(style);

    // HIS01 compatibility test surface: the normal parameter reader and the catalog apply path,
    // so a headless probe can apply an entry and observe the resulting editable state.
    internal HullParameters? ReadParametersForTests(out string? message) => ReadParameters(out message);

    // P1-C test surface: drive the real width control so the automatic parity prevention can be
    // exercised through the same path a user's edit takes.
    internal DimensionInput WidthInputForTests => WidthInput;
    internal void QueuePreviewForTests() => QueuePreview();

    internal void SelectHistoricalPresetForTests(string id)
    {
        var preset = HistoricalPresetCatalog.Find(id)
            ?? throw new InvalidOperationException($"Unknown historical preset id '{id}'.");
        ApplyHistoricalPreset(preset);
    }

    // Hull Presets product-surface test seam: drive the real browser hosted by the modeless
    // window and its native thumbnails through the same live editor path a user's click takes.
    internal SketchbookBrowser SketchbookForTests => EnsureHullPresetsWindow().Browser;

    internal void SelectSketchbookEntryForTests(string id, SketchbookSizePolicy policy) =>
        SketchbookForTests.SelectForTests(id, policy);

    internal int RenderSketchbookThumbnailsForTests(IReadOnlyList<string> ids) =>
        SketchbookForTests.PopulateThumbnailsForTests(_catalog, ids);

    internal HullPresetsWindow HullPresetsWindowForTests => EnsureHullPresetsWindow();

    internal void OpenHullPresetsForTests() => OpenHullPresetsWindow();

    /// <summary>
    /// Drives the same launch handshake the Loaded event runs, so the launch-open surface can be
    /// asserted without showing the editor window and loading the installed game catalog.
    /// </summary>
    internal void RunLaunchHandshakeForTests() => OnMainWindowLoaded();

    /// <summary>
    /// Test seam: the live editor only commits control changes once its window has loaded.
    /// Headless probes mark the window loaded so the normal commit path runs without showing
    /// a window, which would open recovery dialogs and load the installed game catalog.
    /// </summary>
    internal void MarkLoadedForTests() => _loaded = true;

    // REL01 test surface: drive the real preview pipeline headlessly so barbette dispatch, the
    // derived layout datum and the resolved snapshot can be asserted without a shown window.
    internal void SetCatalogForTests(FtdBlockCatalog catalog) =>
        AdoptCatalog(catalog, queuePreview: false);
    internal void ReplaceCatalogForTests(FtdBlockCatalog catalog) =>
        AdoptCatalog(catalog, queuePreview: true);
    internal Task GeneratePreviewForTestsAsync() => GeneratePreviewAsync();
    internal ShipGenerationSnapshot? CurrentShipSnapshotForTests => _currentShipSnapshot;
    internal DesignMeasure? MeasuredSupportedRulerEndForTests => _workspace.LayoutFrame?.SupportedRulerEnd;
    internal string StatusTextForTests => StatusText.Text;
    internal bool StatusIsWarningForTests => _statusTone == StatusTone.Warning;
    internal bool StatusIsErrorForTests => _statusTone == StatusTone.Error;
    internal string DestinationForTests
    {
        get => DestinationBox.Text;
        set => DestinationBox.Text = value;
    }
    internal void ExportForTests() => ExportClicked(this, new RoutedEventArgs());

    // P1-B live-preview test surface: drive the non-authoritative draft preview deterministically
    // and observe that it never becomes the export-authoritative result.
    internal Task GenerateDraftPreviewForTestsAsync() => GenerateDraftPreviewAsync();
    internal bool ExportEnabledForTests => ExportButton.IsEnabled;
    internal bool VisiblePreviewIsDraftForTests => _visiblePreviewIsDraft;
    internal HullPreviewControl PreviewForTests => Preview;

    private InternalStructureEditor? InstallInternalStructureEditor(WorkspaceHost host)
    {
        var componentsTab = (TabItem)host.Tabs.Items[1];
        // Moving between hosts replaces tab content, so retire the previous editor explicitly.
        // WPF's Unloaded event is not a lifetime signal: it also fires on tab switches and
        // detach/re-dock, where the same editor must stay live.
        DisposeInstalledEditor(componentsTab);
        if (!InternalStructureFeatureAccess.IsEditorAvailable(_featureExposure))
        {
            componentsTab.Content = new TextBlock
            {
                Margin = new Thickness(12),
                TextWrapping = TextWrapping.Wrap,
                Text = _featureExposure.UnavailableReason(ProductFeature.InternalStructures),
            };
            Preview.SelectInternalFamily(null);
            return null;
        }

        var editor = new InternalStructureEditor();
        editor.Attach(_workspace);
        editor.CutawayRequested += InternalStructureCutawayRequested;
        editor.SelectedFamilyChanged += InternalStructureFamilySelected;
        componentsTab.Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            CanContentScroll = false,
            Content = editor,
        };
        if (editor.ViewModel?.SelectedFamily is { } selected)
            Preview.SelectInternalFamily(selected.Family);
        return editor;
    }

    private static void DisposeInstalledEditor(TabItem componentsTab)
    {
        if (componentsTab.Content is ScrollViewer { Content: InternalStructureEditor existing })
            existing.Dispose();
    }

    private void InternalStructureFamilySelected(object? sender, InternalFamilyEventArgs eventArgs) =>
        Preview.SelectInternalFamily(eventArgs.Family);

    private void InternalStructureCutawayRequested(object? sender, InternalFamilyEventArgs eventArgs)
    {
        var (plane, view, label) = eventArgs.Family switch
        {
            Domain.Components.InternalPlaneFamily.LongitudinalBulkhead =>
                (CutawayPlane.Centreline, PreviewView.Side, "longitudinal bulkheads"),
            Domain.Components.InternalPlaneFamily.InternalDeck =>
                (CutawayPlane.Waterline, PreviewView.Top, "internal decks"),
            Domain.Components.InternalPlaneFamily.TransverseBulkhead =>
                (CutawayPlane.Station, PreviewView.Bow, "transverse bulkheads"),
            _ => (CutawayPlane.None, PreviewView.Isometric, "internal structure"),
        };
        CutawayBox.SelectedItem = CutawayChoice.All.First(choice => choice.Value == plane);
        CutawaySlider.Value = 50;
        UpdateCutaway();
        Preview.SelectInternalFamily(eventArgs.Family);
        Preview.ShowView(view);
        SetStatus($"Cutaway set to inspect {label}.", StatusTone.Neutral);
    }

    private void WindowPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs eventArgs)
    {
        var shortcut = WorkspaceInputPolicy.ResolveApplicationShortcut(
            eventArgs.Key, System.Windows.Input.Keyboard.Modifiers);
        if (shortcut is null)
            return;

        if (ExecuteApplicationShortcut(shortcut.Value))
            eventArgs.Handled = true;
    }

    internal bool ExecuteApplicationShortcut(WorkspaceApplicationShortcut shortcut) =>
        ExecuteApplicationShortcut(shortcut, workspaceChoice: null);

    internal bool ExecuteApplicationShortcut(
        WorkspaceApplicationShortcut shortcut,
        WorkspaceCloseChoice? workspaceChoice)
    {
        switch (shortcut)
        {
            case WorkspaceApplicationShortcut.Undo:
                TryUndo(workspaceChoice);
                break;
            case WorkspaceApplicationShortcut.Redo:
                TryRedo(workspaceChoice);
                break;
            default:
                return false;
        }
        return true;
    }

    private void ResetEditorClicked(object sender, RoutedEventArgs eventArgs)
    {
        if (!CanEditSidebar()) return;
        if (MessageBox.Show(this,
                "Reset dimensions, hull shape, armor, smoothing, and preview controls to the defaults?",
                "Reset editor", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        _suppressEditHighlight = true;
        CustomNameCheckBox.IsChecked = false;
        AllowEvenWidthCheckBox.IsChecked = false;
        RecordGenerationParametersCheckBox.IsChecked = false;
        ApplyEditorParameters(HullEditorSettings.Default, resetArmor: true);
        _forceInternalArmor = false;
        Preview.ShowInternalArmor = false;
        SingleBlocksCheckBox.IsChecked = false;
        CutawayBox.SelectedIndex = 0;
        CutawaySlider.Value = 50;
        Preview.ResetView();
        QueuePreview();
    }

    private async void RandomizeClicked(object sender, RoutedEventArgs eventArgs)
    {
        if (!CanEditSidebar()) return;
        var basis = ReadParameters(out var error);
        if (basis is null)
        {
            SetStatus(error!, StatusTone.Warning);
            return;
        }
        RandomizeButton.IsEnabled = false;
        var actionSequence = ++_editorActionSequence;
        SetStatus("Finding a random hull shape…", StatusTone.Working);
        try
        {
            var candidate = await Task.Run(() =>
            {
                for (var attempt = 0; attempt < 20; attempt++)
                {
                    var choice = HullEditorSettings.RandomizeShape(basis, Random.Shared);
                    try
                    {
                        _generator.Generate(choice);
                        return choice;
                    }
                    catch (HullGenerationException) { }
                }
                return null;
            });
            // Respect edits made while the random candidate was being validated.
            if (_workspace.IsDraftDirty || actionSequence != _editorActionSequence || ReadParameters(out _) != basis)
                return;
            if (candidate is null)
                SetStatus("No random shape fits this armor stack. Try fewer layers or larger dimensions.", StatusTone.Warning);
            else
            {
                _suppressEditHighlight = true;
                ApplyEditorParameters(candidate, resetArmor: false);
            }
        }
        catch (Exception exception)
        {
            SetStatus($"Could not randomize: {exception.Message}", StatusTone.Error);
        }
        finally { RandomizeButton.IsEnabled = !_workspace.IsDraftDirty; }
    }

    private void ApplyEditorParameters(HullParameters parameters, bool resetArmor)
    {
        if (resetArmor)
            _editorActionSequence++;
        ShowParametersInControls(parameters, resetArmor);
        UpdateStyleHint();
        QueuePreview();
    }

    /// <summary>
    /// Projects one parameter set into the normal controls without committing it. It deliberately
    /// does not queue a preview or refresh the style hint: callers that already own the editor
    /// transaction use this alone so the commit is never duplicated.
    /// </summary>
    private void ShowParametersInControls(HullParameters parameters, bool resetArmor)
    {
        _applyingShapeState = true;
        try
        {
            var projection = EditorControlProjection.FromHull(parameters);
            LengthInput.SetDisplayedValue(parameters.Length);
            WidthInput.SetDisplayedValue(parameters.Width);
            HeightInput.SetDisplayedValue(parameters.Height);
            BowStyleBox.SelectedItem = _bowStyleChoices.First(choice => choice.Value == parameters.BowStyle);
            SternStyleBox.SelectedItem = _sternStyleChoices.First(choice => choice.Value == parameters.SternStyle);
            ApplyShapeToControls(parameters.EffectiveShape);
            BulbCheckBox.IsChecked = parameters.HasBulb;
            BulbLengthInput.SetDisplayedValue(parameters.EffectiveBulb.LengthPercent);
            BulbWidthInput.SetDisplayedValue(parameters.EffectiveBulb.WidthPercent);
            BulbForeAftInput.SetDisplayedValue(parameters.EffectiveBulb.ForeAftPercent);
            BulbRiseInput.SetDisplayedValue(parameters.EffectiveBulb.RisePercent);
            SmoothingBox.SelectedItem = _smoothingChoices.First(choice => choice.Value == parameters.Smoothing);
            HybridOffsetInput.SetDisplayedValue(parameters.HybridFillOffset);
            SingleBlocksCheckBox.IsChecked = projection.KeepSingleBlocks;
            if (resetArmor)
            {
                ResetArmor(_hullArmorRows, HullArmorLayersPanel, parameters.HullArmor, "OUTER");
                _bottomArmorInheritsSide = projection.BottomArmorInheritsSide;
                ResetArmor(_bottomArmorRows, BottomArmorLayersPanel, projection.DisplayedBottomArmor, "BOTTOM");
                ResetArmor(_deckArmorRows, DeckArmorLayersPanel,
                    parameters.DeckArmor ?? ArmorLayout.Single(MaterialKind.Metal), "TOP");
                DeckCheckBox.IsChecked = parameters.HasDeck;
            }
            UpdateProfileRiseControls();
            UpdateOverallHeightReadout();
        }
        finally { _applyingShapeState = false; }

        void ResetArmor(List<ArmorLayerRow> rows, StackPanel panel, ArmorLayout layout, string label)
        {
            rows.Clear();
            panel.Children.Clear();
            foreach (var layer in layout.Layers)
                AddArmorLayer(rows, panel, layer, label);
        }
    }

    private void AddHullArmorLayerClicked(object sender, RoutedEventArgs eventArgs)
    {
        AddArmorLayer(_hullArmorRows, HullArmorLayersPanel, SelectedLayer(_hullArmorRows[^1]), "OUTER");
        QueuePreview();
    }

    private void AddDeckArmorLayerClicked(object sender, RoutedEventArgs eventArgs)
    {
        AddArmorLayer(_deckArmorRows, DeckArmorLayersPanel, SelectedLayer(_deckArmorRows[^1]), "TOP");
        QueuePreview();
    }

    private void DeckSettingsChanged(object sender, RoutedEventArgs eventArgs)
    {
        if (DeckArmorEditor is not null)
            DeckArmorEditor.IsEnabled = DeckCheckBox.IsChecked == true;
        UpdateOverallHeightReadout();
        QueuePreview();
    }

    private void AddArmorLayer(
        List<ArmorLayerRow> rows,
        StackPanel panel,
        ArmorLayer layer,
        string surfaceLabel)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 3) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
        };
        label.SetResourceReference(TextBlock.FontFamilyProperty, "PanelFont");
        label.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        Grid.SetRowSpan(label, 2);

        var materialBox = new ComboBox
        {
            ItemsSource = MaterialChoice.All,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 0,
            Margin = new Thickness(0),
        };
        materialBox.SelectedItem = MaterialChoice.All.First(choice => choice.Value == layer.Material);
        Grid.SetColumn(materialBox, 1);
        Grid.SetRow(materialBox, 0);

        var constructionBox = new ComboBox
        {
            ItemsSource = ArmorConstructionChoice.All,
            SelectedItem = ArmorConstructionChoice.All.First(choice => choice.Value == layer.Construction),
            IsEnabled = !layer.IsAir,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 0,
            Margin = new Thickness(0, 3, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Choose full beams, rounded poles, or a beam-slope pattern for this layer.",
        };
        constructionBox.SelectionChanged += (_, _) =>
        {
            MarkBottomArmorExplicit(rows);
            QueuePreview();
        };
        Grid.SetColumn(constructionBox, 1);
        Grid.SetColumnSpan(constructionBox, 2);
        Grid.SetRow(constructionBox, 1);

        materialBox.SelectionChanged += (_, _) =>
        {
            MarkBottomArmorExplicit(rows);
            var isStructural = (materialBox.SelectedItem as MaterialChoice)?.Value is not null;
            constructionBox.IsEnabled = isStructural;
            if (!isStructural)
                constructionBox.SelectedItem = ArmorConstructionChoice.All[0];
            QueuePreview();
        };

        var removeButton = new Button
        {
            Content = "Remove",
            Margin = new Thickness(5, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        removeButton.SetResourceReference(StyleProperty, "CompactButtonStyle");
        Grid.SetColumn(removeButton, 2);
        Grid.SetRow(removeButton, 0);

        var row = new ArmorLayerRow(grid, label, materialBox, constructionBox, removeButton);
        removeButton.Click += (_, _) =>
        {
            if (rows.Count <= 1)
                return;
            rows.Remove(row);
            panel.Children.Remove(grid);
            RefreshArmorLayerLabels(rows, surfaceLabel);
            MarkBottomArmorExplicit(rows);
            QueuePreview();
        };

        grid.Children.Add(label);
        grid.Children.Add(materialBox);
        grid.Children.Add(constructionBox);
        grid.Children.Add(removeButton);
        rows.Add(row);
        panel.Children.Add(grid);
        RefreshArmorLayerLabels(rows, surfaceLabel);
    }

    private void MarkBottomArmorExplicit(List<ArmorLayerRow> rows)
    {
        if (!_applyingShapeState && ReferenceEquals(rows, _bottomArmorRows))
            _bottomArmorInheritsSide = false;
    }

    private static ArmorLayer SelectedLayer(ArmorLayerRow row) => ReadArmorLayer(row);

    private static ArmorLayer ReadArmorLayer(ArmorLayerRow row)
    {
        var material = (row.MaterialBox.SelectedItem as MaterialChoice)?.Value;
        var construction = (row.ConstructionBox.SelectedItem as ArmorConstructionChoice)?.Value ?? ArmorConstruction.Solid;
        return new ArmorLayer(material, construction);
    }

    private static void RefreshArmorLayerLabels(IReadOnlyList<ArmorLayerRow> rows, string surfaceLabel)
    {
        for (var index = 0; index < rows.Count; index++)
        {
            rows[index].Label.Text = index == 0 ? surfaceLabel : $"L{index + 1}";
            rows[index].RemoveButton.IsEnabled = rows.Count > 1;
        }
    }

    private void BrowseDestinationClicked(object sender, RoutedEventArgs eventArgs)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose the folder where Hull Forge will write blueprints.",
            InitialDirectory = Directory.Exists(DestinationBox.Text) ? DestinationBox.Text : _paths.ConstructsDirectory,
        };
        if (dialog.ShowDialog() == true)
            DestinationBox.Text = dialog.FolderName;
    }

    private void OpenDestinationClicked(object sender, RoutedEventArgs eventArgs)
    {
        try
        {
            Directory.CreateDirectory(DestinationBox.Text);
            Process.Start(new ProcessStartInfo { FileName = DestinationBox.Text, UseShellExecute = true });
        }
        catch (Exception error)
        {
            SetStatus($"Could not open the destination: {error.Message}", StatusTone.Error);
        }
    }

    private async void BrowseGameClicked(object sender, RoutedEventArgs eventArgs)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose the From The Depths game folder (the folder containing From_The_Depths.exe).",
            InitialDirectory = _gameDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        };
        if (dialog.ShowDialog() == true)
            await LoadCatalogAsync(dialog.FolderName);
    }

    private void ExportClicked(object sender, RoutedEventArgs eventArgs)
    {
        if (!InternalStructureFeatureAccess.CanPreviewOrExport(_editor.Document,
                _featureExposure, out var unavailableReason) ||
            !SlopeRefinementFeatureAccess.CanPreviewOrExport(_editor.Document, _featureExposure, out unavailableReason))
        {
            SetStatus(unavailableReason!, StatusTone.Warning);
            ExportButton.IsEnabled = false;
            return;
        }
        if (_workspace.IsDraftDirty)
        {
            InvalidateVisibleWorkspacePreview();
            return;
        }
        // With a catalog present the export authority is the exact catalog-resolved snapshot for
        // this revision; the raw hull overload must not be reachable for an authoritative preview.
        if (_catalog is null || !_previewBoundary.TryGetCurrent(_editor.Revision, out var currentHull) ||
            currentHull is null || !ReferenceEquals(currentHull, _currentHull) ||
            _currentShipSnapshot is not { } resolvedSnapshot ||
            !resolvedSnapshot.IsCurrent(_editor.Revision) ||
            !ReferenceEquals(resolvedSnapshot.Hull, currentHull) ||
            !string.Equals(resolvedSnapshot.Document.DocumentId, _editor.Document.DocumentId,
                StringComparison.Ordinal))
        {
            SetStatus("Export needs a catalog-resolved preview for the current project revision.", StatusTone.Warning);
            return;
        }

        var parameters = ReadParameters(out var parameterError);
        if (parameters is null)
        {
            SetStatus(parameterError!, StatusTone.Warning);
            return;
        }

        if (parameters != currentHull.Parameters)
        {
            SetStatus("Preview is still updating.", StatusTone.Working);
            QueuePreview();
            return;
        }

        if (!parameters.HasSingleBlockCenterline)
        {
            var confirmation = MessageBox.Show(
                $"A {parameters.Width} m wide hull is symmetric around a plane between its two centre block columns. " +
                "It does not have a single-block centreline.\n\nExport this even-width hull anyway?",
                "Even-width hull",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (confirmation != MessageBoxResult.Yes)
            {
                SetStatus("Export cancelled.", StatusTone.Neutral);
                return;
            }
        }

        try
        {
            var result = _exporter.Export(resolvedSnapshot, resolvedSnapshot.Document, _editor.Revision,
                DestinationBox.Text, EffectiveBlueprintName(),
                RecordGenerationParametersCheckBox.IsChecked == true,
                _featureExposure);
            var parts = new List<string>();
            if (result.BeamCount > 0)
                parts.Add($"{result.BeamCount:N0} beams");
            if (result.PoleCount > 0)
                parts.Add($"{result.PoleCount:N0} poles");
            if (result.BeamSlopeCount > 0)
                parts.Add($"{result.BeamSlopeCount:N0} beam slopes");
            if (result.SlopeCount > 0)
                parts.Add($"{result.SlopeCount:N0} slopes");
            if (result.ShapeFallbackCount > 0)
                parts.Add($"{result.ShapeFallbackCount:N0} full-block fallbacks");
            var breakdown = parts.Count == 0 ? string.Empty : $" · {string.Join(" · ", parts)}";
            var parameterNotice = result.GenerationParametersPath is null
                ? string.Empty
                : $" · generation parameters → {result.GenerationParametersPath}";
            SetStatus($"Exported {result.BlockCount:N0} blocks ({result.OccupiedCellCount:N0} m){breakdown}{parameterNotice} → {result.FilePath}", StatusTone.Ready);
        }
        catch (Exception error)
        {
            SetStatus($"Export failed: {error.Message}", StatusTone.Error);
        }
    }

    /// <summary>
    /// Estimates one placement's material cost. A beam the installed catalog cannot
    /// supply is exported as separate cubes, so an unresolved multi-cell shape must be
    /// costed per cell to match what the blueprint will actually contain.
    /// </summary>
    private static double EstimateCost(FtdBlockCatalog catalog, BlockPlacement placement)
    {
        var block = catalog.Resolve(placement.Material, placement.Shape);
        return block.IsFallback ? placement.CellLength * block.MaterialCost : block.MaterialCost;
    }

    private sealed record MaterialChoice(MaterialKind? Value, string Label)
    {
        public override string ToString() => Label;

        public static IReadOnlyList<MaterialChoice> All { get; } =
        [
            new(MaterialKind.Wood, "Wood"),
            new(MaterialKind.Metal, "Metal"),
            new(MaterialKind.LightweightAlloy, "Lightweight alloy"),
            new(MaterialKind.HeavyArmor, "Heavy armor"),
            new(MaterialKind.Stone, "Stone"),
            new(MaterialKind.Lead, "Lead"),
            new(MaterialKind.Rubber, "Rubber"),
            new(MaterialKind.Glass, "Glass"),
            new(null, "Air gap"),
        ];

        public static IReadOnlyList<MaterialChoice> Structural { get; } = All.Where(choice => choice.Value is not null).ToArray();
    }

    private sealed record ArmorLayerRow(
        Grid Container,
        TextBlock Label,
        ComboBox MaterialBox,
        ComboBox ConstructionBox,
        Button RemoveButton);

    private sealed record ArmorConstructionChoice(ArmorConstruction Value, string Label)
    {
        public override string ToString() => Label;

        public static IReadOnlyList<ArmorConstructionChoice> All { get; } =
        [
            new(ArmorConstruction.Solid, "Solid / beam"),
            new(ArmorConstruction.Pole, "Pole"),
            new(ArmorConstruction.BeamSlopeUp, "Upward Slope"),
            new(ArmorConstruction.BeamSlopeDown, "Downward Slope"),
            new(ArmorConstruction.BeamSlopeSpike, "Spike Slope"),
        ];
    }

    private sealed record CutawayChoice(CutawayPlane Value, string Label)
    {
        public override string ToString() => Label;

        public static IReadOnlyList<CutawayChoice> All { get; } =
        [
            new(CutawayPlane.None, "None"),
            new(CutawayPlane.Station, "Across the length"),
            new(CutawayPlane.Centreline, "Along the centreline"),
            new(CutawayPlane.Waterline, "Horizontal"),
        ];
    }

    private sealed record SmoothingChoice(
        SmoothingMethod Value, string Label, string Hint, string Group)
    {
        public override string ToString() => Label;

        public static IReadOnlyList<SmoothingChoice> All { get; } =
        [
            new(SmoothingMethod.None, "None", "Full blocks only.", "PROVEN"),
            new(SmoothingMethod.VerticalSlopeFill, "Vertical slope fill", "Adds slope blocks on vertical steps. Matches the in-game corrected fixtures.", "PROVEN"),
            new(SmoothingMethod.HorizontalSlopeFill, "Horizontal slope fill", "Adds slope blocks on horizontal steps. Matches the in-game corrected fixtures.", "PROVEN"),
            new(SmoothingMethod.CombinedSlopeFill, "Combined slope fill", "All three fills with conflicts resolved. Reference-derived and still needs in-game review.", "REFERENCE-DERIVED"),
            new(SmoothingMethod.HybridSlopeFill, "Hybrid slope fill", "Vertical fill below the top offset, horizontal fill above it. Uses the finished voxel height.", "REFERENCE-DERIVED"),
            new(SmoothingMethod.DecoVertical, "Deco Vertical", "Vertical slope fill plus the longest valid visual decoration on each eligible native 4 m anchor. Visual only; no extra settings.", "VISUAL"),
            new(SmoothingMethod.DecoHorizontal, "Deco Horizontal", "Horizontal slope fill plus the longest valid visual decoration on each eligible native 4 m anchor. Visual only; no extra settings.", "VISUAL"),
        ];
    }

    private sealed record BowStyleChoice(BowStyle Value, string Label, string Hint)
    {
        public override string ToString() => Label;

        public static IReadOnlyList<BowStyleChoice> All { get; } =
        [
            new(BowStyle.Pointed, "Pointed", "Bow: narrows to a point with a vertical stem."),
            new(BowStyle.Raked, "Raked", "Bow: pointed, with the stem raked so the keel stops short of the deck."),
            new(BowStyle.Spoon, "Spoon", "Bow: rounded in plan with a cut-away forefoot."),
            new(BowStyle.Blunt, "Blunt", "Bow: ends in a flat vertical face like a barge."),
            new(BowStyle.Axe, "Axe", "Bow: a fine reverse stem whose lower prow reaches ahead of the deck."),
            new(BowStyle.Clipper, "Clipper", "Bow: a pointed stem with a long curved forefoot and forward deck overhang."),
        ];
    }

    private sealed record BodyStyleChoice(BodyStyle Value, string Label, string Hint)
    {
        public override string ToString() => Label;

        public static IReadOnlyList<BodyStyleChoice> All { get; } =
            Enum.GetValues<BodyStyle>()
                .Select(style => new BodyStyleChoice(style, LabelFor(style), HintFor(style)))
                .ToArray();

        public static BodyStyleChoice For(BodyStyle style) => All.First(choice => choice.Value == style);

        private static string LabelFor(BodyStyle style) => style switch
        {
            BodyStyle.V => "V-Hull",
            BodyStyle.DeepV => "Deep V-Hull",
            BodyStyle.U => "U-Hull",
            BodyStyle.FlatWide => "Flat / Wide",
            BodyStyle.HardChine => "Hard Chine",
            _ => style.ToString(),
        };

        private static string HintFor(BodyStyle style) => style switch
        {
            BodyStyle.Rounded => "Body preset: rounded all-purpose section with a soft chine. Loads the body sliders only.",
            BodyStyle.V => "Body preset: moderate V bottom with softly blended sides. Loads the body sliders only.",
            BodyStyle.DeepV => "Body preset: narrow deep-V bottom with moderate flare. Loads the body sliders only.",
            BodyStyle.U => "Body preset: full-volume U section with rounded bilges. Loads the body sliders only.",
            BodyStyle.FlatWide => "Body preset: wide, flat bottom with a firmer chine. Loads the body sliders only; every bow, stern, profile and bulb control stays free.",
            BodyStyle.HardChine => "Body preset: angular section with a pronounced hard chine. Loads the body sliders only.",
            BodyStyle.Tumblehome => "Body preset: full lower section whose upper sides draw inward. Loads the body sliders only.",
            _ => "Custom body. The sliders below are edited individually and the other shape controls are untouched.",
        };
    }

    private sealed record SternStyleChoice(SternStyle Value, string Label, string Hint)
    {
        public override string ToString() => Label;

        public static IReadOnlyList<SternStyleChoice> All { get; } =
        [
            new(SternStyle.Transom, "Transom", "Stern: a flat vertical transom at 65% beam."),
            new(SternStyle.Square, "Square", "Stern: a wide flat transom at 85% beam."),
            new(SternStyle.Counter, "Counter", "Stern: a transom sloped so the deck overhangs the keel."),
            new(SternStyle.Canoe, "Canoe", "Stern: double-ended, narrowing to a point with a rounded heel; widest station at midships."),
            new(SternStyle.Cruiser, "Cruiser", "Stern: a narrow rounded termination with a gently lifted run."),
            new(SternStyle.Fantail, "Fantail", "Stern: a broad rounded termination that keeps most of the hull beam."),
        ];
    }
}
