using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Components;
using FtdHullGenerator.UI.Scenes;

namespace FtdHullGenerator.UI;

/// <summary>A standing camera position for the lattice view.</summary>
public enum PreviewView
{
    /// <summary>The default three-quarter view.</summary>
    Isometric,

    /// <summary>Square on to the side, with the hull's length across the screen.</summary>
    Side,

    /// <summary>Looking down, with the bow up the screen.</summary>
    Top,

    /// <summary>Square on to the bow, which the generator places at positive Z.</summary>
    Bow,

    /// <summary>Looking up from beneath the keel for structural inspection.</summary>
    Underside,
}

/// <summary>
/// A plane the cutaway view can section the hull on. The half of the hull beyond the
/// plane is left out so the layers behind the exposed shell can be seen from a standing view.
/// </summary>
public enum CutawayPlane
{
    /// <summary>Draw the whole hull.</summary>
    None,

    /// <summary>Section across the length, leaving out the bow side; the bow view looks at the cut face.</summary>
    Station,

    /// <summary>Section along the centreline, leaving out the starboard side; the side view looks at the cut face.</summary>
    Centreline,

    /// <summary>Section horizontally, leaving out everything above; the top view looks down on the cut face.</summary>
    Waterline,
}

/// <summary>
/// Batched preview of the exact block placement list that will be exported. The camera
/// orbits, zooms, and rests in a gentle turn that a grab stops and a throw feeds.
/// </summary>
public sealed class HullPreviewControl : Viewport3D
{
    private readonly ModelVisual3D _content = new();
    private readonly ModelVisual3D _highlight = new();
    private readonly PreviewSceneVisualRoot _sceneRoot;
    private readonly PreviewScenePreferenceStore? _scenePreferences;
    private PreviewSceneSettings _sceneSettings = PreviewSceneSettings.Grid;
    private PreviewSceneSelectorAdorner? _sceneSelector;
    private AdornerLayer? _sceneSelectorLayer;
    private bool _sceneSelectorAttachQueued;
    private bool _sceneSelectorAttachRetried;
    private PreviewScenePresentationState _scenePresentationState = new(
        PreviewSceneKind.Grid,
        PreviewSceneOcclusion.Normal,
        "Grid inspection scene.");
    private bool _scenePreferencesLoaded;
    private bool _loadingScenePreferences;
    private GeneratedHull? _hull;
    private IReadOnlyCollection<BlockPlacement> _previousBlocks = Array.Empty<BlockPlacement>();
    private HashSet<BlockPlacement>? _changedBlocks;
    private string? _selectedInternalOwnerPrefix;
    private string? _selectedBarbetteId;
    private readonly DispatcherTimer _highlightTimer = new() { Interval = EditHighlightDuration };
    private bool _framed;
    private bool _showInternalArmor;
    private CutawayPlane _cutaway = CutawayPlane.None;
    private double _cutawayFraction = 0.5;
    private readonly PerspectiveCamera _camera = new() { FieldOfView = 50 };
    private readonly PointLight _keyLight = new(Color.FromRgb(244, 236, 220), new Point3D());
    private readonly PointLight _rimLight = new(Color.FromRgb(126, 146, 184), new Point3D());
    private readonly AmbientLight _ambientLight = new(Color.FromRgb(38, 44, 60));
    private readonly DirectionalLight _fillLight = new(Color.FromRgb(152, 156, 168), new Vector3D(-0.4, -0.8, -0.25));

    /// <summary>
    /// A dim light along the view direction. The key and rim lights model the hull from
    /// fixed positions, which leaves a square-on preset lit only at a grazing angle; this
    /// keeps those views readable without flattening the modelling.
    /// </summary>
    private readonly DirectionalLight _headLight = new(Color.FromRgb(58, 62, 74), new Vector3D(0, 0, -1));
    private readonly EventHandler<AppTheme> _themeChangedHandler;
    private Point _lastPointer;
    private bool _dragging;
    private double _yaw = IsometricYaw;
    private double _pitch = IsometricPitch;
    private double _yawRate;
    private double _pitchRate;
    private bool _autoRotate;
    private bool _pausedByView;
    private readonly List<(Point Position, long Timestamp)> _pointerTrail = [];
    private TimeSpan? _lastFrame;
    private double _distance = 120;
    private double _fitDistance = 120;
    private Point3D _center;
    private int _hullLength;
    private int _hullWidth;
    private int _hullHeight;

    /// <summary>
    /// The most internal armor a hull may have and still be drawn complete without being
    /// asked. Measured on the software renderer: at this count a full rebuild with the
    /// interior costs about 20 ms over the shell alone (roughly a 300 m hull with three
    /// layers), which keeps a cutaway slider drag smooth; the 500 x 250 x 100 maximum with
    /// three layers has ten times as many cells and costs 300 ms per rebuild, so past the
    /// limit the interior is left out until the debug option forces it.
    /// </summary>
    public const int AutomaticInternalArmorCellLimit = 30_000;

    private const double IsometricYaw = 0.95;
    private const double IsometricPitch = 0.52;

    /// <summary>
    /// How far the camera may tilt from the horizon. A pitch of exactly a quarter turn
    /// would put the view direction on the up vector and leave the camera undefined, so
    /// the top view stops just short of straight down and drag orbiting clamps to the
    /// same limit.
    /// </summary>
    private const double MaximumPitch = 1.38;

    /// <summary>
    /// The gentle turn the view rests in, radians per second — a full turn about every
    /// 45 seconds. Slow enough to read as a turntable rather than an animation, and slow
    /// enough that an edit's changed-block fade stays what draws the eye.
    /// </summary>
    internal const double GentleTurnRate = 0.14;

    /// <summary>
    /// How long a released throw takes to ease back to <see cref="GentleTurnRate" />.
    /// Long enough that the throw's own speed clearly carries the view and the return
    /// reads as settling rather than braking.
    /// </summary>
    private const double TurnSettleSeconds = 1.9;

    /// <summary>
    /// How long a release's tilt inertia takes to decay. The tilt itself is never pulled
    /// anywhere: a vertical throw keeps whatever angle it settles on.
    /// </summary>
    private const double TiltSettleSeconds = 0.9;

    /// <summary>The pointer travel at the end of a drag that a release reads as a throw.</summary>
    private const double ThrowWindowSeconds = 0.15;

    /// <summary>
    /// Orbit sensitivity: radians of yaw or pitch per pixel of drag or throw. Deliberately
    /// coarse — roughly half the original throw — so an ordinary drag re-aims the view
    /// without whipping it around.
    /// </summary>
    private const double OrbitRadiansPerPixel = 0.005;

    /// <summary>
    /// How much of a drag's pointer speed carries into the release spin. Well under the
    /// drag's own orbit mapping, so letting go adds a short coast rather than a flick that
    /// sends the view skidding past where the pointer left it.
    /// </summary>
    private const double ThrowGain = 0.4;

    /// <summary>
    /// A release slower than this pointer speed is a placement, not a throw, and hands over
    /// no spin. The dead zone absorbs the ordinary cursor speeds of a repositioning drag, so
    /// only a deliberate flick keeps the view coasting.
    /// </summary>
    private const double ThrowDeadZoneSpeed = 150d;

    /// <summary>The fastest spin a throw may hand over, so one hard flick cannot blur the view.</summary>
    private const double MaximumThrowRate = 3.5d;

    /// <summary>A step longer than this is trimmed, so a stalled frame cannot jump the view.</summary>
    private const double MaximumFrameStep = 0.1;

    /// <summary>
    /// The rate below which an easing turn counts as stopped. Reaching it lets the view
    /// sit exactly still — no camera updates, no redraws — until the turn is asked for
    /// again.
    /// </summary>
    private const double RestingRate = 0.0015;

    public HullPreviewControl() : this(null, new PreviewScenePreferenceStore())
    {
    }

    /// <summary>
    /// Test seam for exercising real WPF hit testing with environment geometry while V01
    /// intentionally ships only the empty Grid environment. Production always uses the
    /// parameterless constructor and its closed scene factory.
    /// </summary>
    internal HullPreviewControl(Func<PreviewSceneSettings, Model3D?> createEnvironment)
        : this(createEnvironment, null)
    {
    }

    /// <summary>Test seam for exercising the production scene factory with an isolated preference file.</summary>
    internal HullPreviewControl(PreviewScenePreferenceStore scenePreferences)
        : this(null, scenePreferences)
    {
        ArgumentNullException.ThrowIfNull(scenePreferences);
    }

    private HullPreviewControl(
        Func<PreviewSceneSettings, Model3D?>? createEnvironment,
        PreviewScenePreferenceStore? scenePreferences)
    {
        _scenePreferences = scenePreferences;
        ClipToBounds = true;
        Camera = _camera;
        _sceneRoot = new PreviewSceneVisualRoot(
        [
            new ModelVisual3D { Content = _ambientLight },
            new ModelVisual3D { Content = _fillLight },
            new ModelVisual3D { Content = _keyLight },
            new ModelVisual3D { Content = _rimLight },
            new ModelVisual3D { Content = _headLight },
        ], _content, _highlight, createEnvironment ?? CreateEnvironment);
        _sceneRoot.Apply(_sceneSettings);
        Children.Add(_sceneRoot.Visual);
        // One timer for the whole edit highlight: the fade is animated per material, but
        // the seamed finish only comes back once, when the last of them has landed.
        _highlightTimer.Tick += (_, _) =>
        {
            _highlightTimer.Stop();
            _changedBlocks = null;
            RebuildModel();
        };
        ApplyTheme(ThemeManager.Current);
        _themeChangedHandler = (_, theme) => ApplyTheme(theme);
        ThemeManager.ThemeChanged += _themeChangedHandler;
        MouseLeftButtonDown += OnPointerDown;
        MouseLeftButtonUp += OnPointerUp;
        MouseMove += OnPointerMove;
        MouseWheel += OnMouseWheel;
        // A stolen capture ends the grab the same way letting go does, so the hull never
        // stays silently stuck to a pointer the control no longer has.
        LostMouseCapture += (_, _) => EndHold();
        // The gentle turn is advanced from the composition clock, so it is stepped by the
        // time actually between frames rather than a timer's nominal interval. The control
        // only turns while it is in a rendered tree: the offscreen renderer never loads
        // it, so its frames stay deterministic.
        Loaded += (_, _) =>
        {
            LoadScenePreferences();
            AttachSceneSelector();
            _lastFrame = null;
            CompositionTarget.Rendering -= OnAnimationFrame;
            CompositionTarget.Rendering += OnAnimationFrame;
        };
        Unloaded += (_, _) =>
        {
            CompositionTarget.Rendering -= OnAnimationFrame;
            if (_sceneSelector is not null && _sceneSelectorLayer is not null)
                _sceneSelectorLayer.Remove(_sceneSelector);
            _sceneSelector = null;
            _sceneSelectorLayer = null;
            _sceneSelectorAttachQueued = false;
            _sceneSelectorAttachRetried = false;
        };
        IsHitTestVisible = true;
    }

    private Model3D? CreateEnvironment(PreviewSceneSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return PreviewSceneFactory.Create(settings, new PreviewSceneContext(
            SceneBounds(),
            OcclusionFor(settings.Kind),
            ThemeManager.Current == AppTheme.Workbench));
    }

    /// <summary>
    /// Presentation-only scene settings. Applying a scene replaces only the environment
    /// branch: it does not rebuild the ship model, reframe the camera, or touch the cached
    /// physical hull. Grid is the sole V01 scene and reproduces the existing presentation.
    /// </summary>
    public PreviewSceneSettings SceneSettings
    {
        get => _sceneSettings;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            value.Validate();
            _sceneRoot.Apply(value);
            _sceneSettings = value;
            UpdateScenePresentationState();
            if (IsLoaded && !_loadingScenePreferences && _scenePreferences is not null)
            {
                try
                {
                    _scenePreferences.Save(value);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // A device preference must never make the preview or editor unusable.
                }
            }
            ScenePresentationChanged?.Invoke(this, _scenePresentationState);
        }
    }

    /// <summary>The current scene's explicit inspection/occluder state.</summary>
    public PreviewScenePresentationState ScenePresentationState => _scenePresentationState;

    /// <summary>Raised after presentation-only settings or inspection state change.</summary>
    public event EventHandler<PreviewScenePresentationState>? ScenePresentationChanged;

    private PreviewSceneBounds SceneBounds() => _hull is null
        ? PreviewSceneBounds.Empty
        : new PreviewSceneBounds(
            _hull.MinX,
            _hull.MaxX + 1,
            _hull.MinY,
            _hull.MaxY + 1,
            _hull.MinZ,
            _hull.MaxZ + 1);

    private PreviewSceneOcclusion OcclusionFor(PreviewSceneKind kind)
    {
        if (kind == PreviewSceneKind.Grid)
            return PreviewSceneOcclusion.Normal;
        if (_cutaway != CutawayPlane.None)
            return PreviewSceneOcclusion.Hidden;
        return _pitch < -0.12 ? PreviewSceneOcclusion.Faded : PreviewSceneOcclusion.Normal;
    }

    private void RefreshEnvironmentForInspection(bool force = false)
    {
        if (_sceneRoot is null)
            return;
        var occlusion = OcclusionFor(_sceneSettings.Kind);
        if (!force && occlusion == _scenePresentationState.Occlusion)
            return;
        _sceneRoot.Apply(_sceneSettings);
        UpdateScenePresentationState();
        ScenePresentationChanged?.Invoke(this, _scenePresentationState);
    }

    private void UpdateScenePresentationState()
    {
        var occlusion = OcclusionFor(_sceneSettings.Kind);
        var status = occlusion switch
        {
            PreviewSceneOcclusion.Hidden => "Environment hidden for cutaway inspection. Grid remains one click away.",
            PreviewSceneOcclusion.Faded => "Environment faded for underside inspection. Ship selection stays active.",
            _ when _sceneSettings.Kind == PreviewSceneKind.Grid => "Grid inspection scene. Environment furniture is off.",
            _ when _sceneSettings.Kind == PreviewSceneKind.Ocean && SceneDeckDatum is { } datum =>
                $"Ocean scene · freeboard {PreviewSceneFreeboard.FromWaterline(datum.ReferenceDeckElevationMetres, _sceneSettings.Waterline):0.##} m · waterline Y {_sceneSettings.Waterline:0.##} m · presentation only.",
            _ => $"{_sceneSettings.Kind} scene · waterline Y {_sceneSettings.Waterline:0.##} m · presentation only.",
        };
        if (_sceneSettings.ShowGridOverlay)
            status += " Grid lines on.";
        if (_sceneSettings.LowDetail)
            status += " Low detail.";
        if (_sceneSettings.ReducedMotion)
            status += " Reduced motion.";
        _scenePresentationState = new PreviewScenePresentationState(_sceneSettings.Kind, occlusion, status);
        _sceneSelector?.Update(_sceneSettings, _scenePresentationState);
    }

    private void LoadScenePreferences()
    {
        if (_scenePreferencesLoaded || _scenePreferences is null)
            return;
        _scenePreferencesLoaded = true;
        _loadingScenePreferences = true;
        try
        {
            // A preference saved by an older build may name a dormant scene the normal
            // 2.0 surface does not offer; fail closed to Grid instead of starting there.
            SceneSettings = PreviewSceneSettings.ForProduct(_scenePreferences.LoadOrDefault());
        }
        finally
        {
            _loadingScenePreferences = false;
        }
    }

    private void AttachSceneSelector()
    {
        if (_sceneSelector is not null)
            return;
        if (AdornerLayer.GetAdornerLayer(this) is not { } layer)
        {
            if (!_sceneSelectorAttachQueued && !_sceneSelectorAttachRetried)
            {
                _sceneSelectorAttachRetried = true;
                _sceneSelectorAttachQueued = true;
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                {
                    _sceneSelectorAttachQueued = false;
                    AttachSceneSelector();
                }));
            }
            return;
        }

        _sceneSelectorAttachRetried = false;
        _sceneSelectorLayer = layer;
        _sceneSelector = new PreviewSceneSelectorAdorner(
            this,
            () => SceneSettings,
            settings => SceneSettings = settings,
            () => ShowView(PreviewView.Underside),
            () => SceneDeckDatum);
        layer.Add(_sceneSelector);
        _sceneSelector.Update(_sceneSettings, _scenePresentationState);
        ToolTip = "Scene controls are at the top-left. Grid remains the exact inspection view.";
    }

    /// <summary>
    /// Classifies a hit-test model without allowing environment furniture into ship
    /// selection. Selection overlays can use the same branch boundary in later V2 work.
    /// </summary>
    internal bool IsShipSelectableModel(Model3D? model) => _sceneRoot.IsShipModel(model);

    /// <summary>
    /// Finds the first ship model under a viewport point while deliberately walking past
    /// environment hits. Scene furniture may occlude a ray visually without acquiring ship
    /// selection identity.
    /// </summary>
    internal Model3D? HitTestShip(Point point)
    {
        Model3D? hit = null;
        VisualTreeHelper.HitTest(
            this,
            null,
            result =>
            {
                if (result is RayHitTestResult ray && IsShipSelectableModel(ray.ModelHit))
                {
                    hit = ray.ModelHit;
                    return HitTestResultBehavior.Stop;
                }
                return HitTestResultBehavior.Continue;
            },
            new PointHitTestParameters(point));
        return hit;
    }

    /// <summary>The exact physical hull cached for drawing; scene changes never replace it.</summary>
    internal GeneratedHull? CurrentHull => _hull;

    /// <summary>
    /// Presentation-only bridge to the authoritative ship-wide reference deck datum, or null
    /// before a hull is set. The elevation mirrors <c>HullBuildContext.ReferenceDeckY</c>
    /// (<c>HullParameters.Height - 1</c>); it is deliberately not <c>GeneratedHull.MaxY</c> or
    /// <c>OverallHeight</c>. <c>MinY</c> only bounds the Ocean freeboard slider travel.
    /// </summary>
    internal PreviewSceneDeckDatum? SceneDeckDatum => _hull is null
        ? null
        : new PreviewSceneDeckDatum(_hull.Parameters.Height - 1, _hull.MinY);

    /// <summary>Scene furniture only, exposed to the invariance tests.</summary>
    internal Model3D? EnvironmentModel => _sceneRoot.EnvironmentContent;

    /// <summary>The settled ship model, exposed to the invariance tests.</summary>
    internal Model3D? ShipModel => _content.Content;

    /// <summary>The edit or persistent selection overlay, exposed only to presentation tests.</summary>
    internal Model3D? ShipHighlightModel => _highlight.Content;

    /// <summary>The persistent presentation-only family selection currently emphasized.</summary>
    internal string? SelectedInternalOwnerPrefix => _selectedInternalOwnerPrefix;

    /// <summary>The persistent presentation-only barbette selection currently emphasized.</summary>
    internal string? SelectedBarbetteId => _selectedBarbetteId;

    /// <summary>
    /// Persistently emphasizes the resolved placements owned by one barbette. Ownership comes from
    /// the exact composed snapshot provenance the export path already uses, so the highlight cannot
    /// become a second geometry truth. Pass <c>null</c> to clear it.
    /// </summary>
    public void SelectBarbette(string? barbetteId)
    {
        var normalized = string.IsNullOrWhiteSpace(barbetteId) ? null : barbetteId;
        if (string.Equals(_selectedBarbetteId, normalized, StringComparison.Ordinal))
            return;
        _selectedBarbetteId = normalized;
        if (normalized is not null)
            _selectedInternalOwnerPrefix = null;
        RebuildModel();
    }

    /// <summary>
    /// Persistently emphasizes the resolved placements owned by one internal family. Ownership is
    /// read from the exact composed snapshot; names, material and mesh appearance are never used
    /// to guess selection identity.
    /// </summary>
    public void SelectInternalFamily(InternalPlaneFamily? family)
    {
        var prefix = family switch
        {
            InternalPlaneFamily.LongitudinalBulkhead => "internal:longitudinal:",
            InternalPlaneFamily.InternalDeck => "internal:deck:",
            InternalPlaneFamily.TransverseBulkhead => "internal:transverse:",
            null => null,
            _ => throw new ArgumentOutOfRangeException(nameof(family)),
        };
        if (string.Equals(_selectedInternalOwnerPrefix, prefix, StringComparison.Ordinal))
            return;
        _selectedInternalOwnerPrefix = prefix;
        if (prefix is not null)
            _selectedBarbetteId = null;
        RebuildModel();
    }

    /// <summary>
    /// Forces internal armor to be drawn regardless of hull size. A hull within
    /// <see cref="AutomaticInternalArmorCellLimit" /> draws its interior anyway, so this
    /// only matters for a large design, where the interior is otherwise left out to keep
    /// the view responsive. Internal parts take a distinct hatched finish and their own
    /// material colours so they never pass for shell.
    /// </summary>
    public bool ShowInternalArmor
    {
        get => _showInternalArmor;
        set
        {
            if (_showInternalArmor == value)
                return;
            _showInternalArmor = value;
            if (_hull is not null && !DrawsInternalArmorAutomatically(_hull))
                RebuildModel();
        }
    }

    /// <summary>Whether the current hull's internal armor is actually being drawn, by size or by request.</summary>
    public bool InternalArmorDrawn => _hull is not null && (_showInternalArmor || DrawsInternalArmorAutomatically(_hull));

    /// <summary>Whether a hull is small enough for its internal armor to be drawn without being asked.</summary>
    public static bool DrawsInternalArmorAutomatically(GeneratedHull hull)
    {
        ArgumentNullException.ThrowIfNull(hull);
        return hull.InternalArmorCellCount <= AutomaticInternalArmorCellLimit;
    }

    /// <summary>The plane the hull is sectioned on, or <see cref="CutawayPlane.None" /> to draw all of it.</summary>
    public CutawayPlane Cutaway
    {
        get => _cutaway;
        set
        {
            if (_cutaway == value)
                return;
            _cutaway = value;
            RebuildModel();
            RefreshEnvironmentForInspection();
        }
    }

    /// <summary>
    /// Where along the cutaway axis the section falls, from 0 (only the first metre is
    /// kept) to 1 (nothing is removed). The value is clamped to that range.
    /// </summary>
    public double CutawayFraction
    {
        get => _cutawayFraction;
        set
        {
            var clamped = double.IsNaN(value) ? 0.5 : Math.Clamp(value, 0, 1);
            if (_cutawayFraction == clamped)
                return;
            _cutawayFraction = clamped;
            if (_cutaway != CutawayPlane.None)
                RebuildModel();
        }
    }

    /// <summary>
    /// Whether the view turns by itself while it is not being held — the gentle turn it
    /// rests in, which shows the whole hull a side at a time without any input. Off by
    /// default, so a fresh preview holds the frame the user is given until they ask it to
    /// turn. Switching it off lets the current motion ease to a stop; switching it back on
    /// gives the turn back immediately, including out of a standing view's pause. The
    /// release inertia belongs to the turn, so with it switched off a drag moves the view
    /// only while the pointer is down and the hull cannot be flicked into a free spin.
    /// </summary>
    public bool AutoRotate
    {
        get => _autoRotate;
        set
        {
            if (_autoRotate == value)
                return;
            _autoRotate = value;
            if (value)
                _pausedByView = false;
        }
    }

    /// <summary>
    /// Shows a hull, keeping the camera where the user left it. Only the first hull of a
    /// session frames itself; after that an edit re-centres on the new geometry but keeps
    /// the orbit and the zoom, so changing an option no longer throws away the view the
    /// user set up to judge the change. <see cref="ResetView" /> and the standing view
    /// buttons are how a frame is asked for explicitly.
    /// </summary>
    /// <param name="highlightEdits">
    /// Whether blocks this hull does not share with the one before it should flash. An
    /// edit passes true; a reset or a first load passes false, since everything would be
    /// new and the whole hull would flash for no information.
    /// </param>
    private FtdHullGenerator.Geometry.Refinement.ResolvedSlopeRefinement? _refinement;

    /// <summary>
    /// How many times the native placement list has been turned into a drawn model. Exposed to
    /// the performance regression tests: an edit that assigns a hull and its decorative
    /// refinement together must build the native model exactly once, not once per assignment.
    /// </summary>
    internal int NativeBuildCountForTests { get; private set; }

    /// <summary>
    /// Shows a composed snapshot and its decorative refinement in one atomic step. The hull and
    /// the refinement are assigned before the single rebuild, so the native placement list is
    /// never drawn twice for one edit.
    /// </summary>
    public void SetResolvedShip(FtdHullGenerator.Geometry.Composition.ShipGenerationSnapshot snapshot, bool highlightEdits = true)
    {
        snapshot.Refinement?.ValidateFor(snapshot);
        ApplyHull(snapshot.Hull, snapshot.Refinement, highlightEdits);
    }

    private Model3DGroup BuildModelWithRefinement(IReadOnlyList<BlockPlacement> blocks)
    {
        var native = BuildModel(blocks);
        if (_refinement is null || _refinement.Extensions.IsEmpty || _cutaway != CutawayPlane.None) return native;
        var result = new Model3DGroup();
        result.Children.Add(native);
        result.Children.Add(BuildRefinementModel(_refinement));
        result.Freeze();
        return result;
    }

    /// <summary>
    /// Builds the decorative refinement as one batch per material rather than one model per
    /// extension. Every extension is still transformed by its own anchor, and a shared frozen
    /// face material keeps the finish and the two-sided back face identical.
    /// </summary>
    internal static Model3DGroup BuildRefinementModel(FtdHullGenerator.Geometry.Refinement.ResolvedSlopeRefinement refinement)
    {
        var group = new Model3DGroup();
        if (refinement.Extensions.IsEmpty)
        {
            group.Freeze();
            return group;
        }

        var builders = new Dictionary<MaterialKind, MeshChunkBuilder>();
        foreach (var extension in refinement.Extensions)
        {
            var material = extension.Anchor.Placement.Material;
            if (!builders.TryGetValue(material, out var builder))
                builders[material] = builder = new MeshChunkBuilder();
            var shape = extension.SourceMeshLengthMetres switch { 1 => BlockShape.Slope1, 2 => BlockShape.Slope2, 3 => BlockShape.Slope3, _ => BlockShape.Slope4 };
            foreach (var face in BlockEnvelope.Faces(shape))
                builder.AddTransformedPolygon(face.Points, face.TextureCoordinates, extension.TransformMeshPoint);
        }

        // Iterating in material-enum order keeps the drawn children deterministic.
        foreach (var material in builders.Keys.OrderBy(value => (int)value))
        {
            var faceMaterial = CreateFaceMaterial(material, 0);
            foreach (var mesh in builders[material].Build())
                group.Children.Add(new GeometryModel3D(mesh, faceMaterial) { BackMaterial = faceMaterial });
        }

        group.Freeze();
        return group;
    }

    public void SetHull(GeneratedHull? hull, bool highlightEdits = true) =>
        ApplyHull(hull, null, highlightEdits);

    /// <summary>
    /// Assigns the hull and its decorative refinement before rebuilding once. A plain hull
    /// passes <c>null</c>; a composed snapshot passes its resolved refinement, so the native
    /// model is built a single time for either case.
    /// </summary>
    private void ApplyHull(
        GeneratedHull? hull,
        FtdHullGenerator.Geometry.Refinement.ResolvedSlopeRefinement? refinement,
        bool highlightEdits)
    {
        _refinement = refinement;
        var previous = _previousBlocks;
        _hull = hull;
        _previousBlocks = hull?.Blocks ?? (IReadOnlyCollection<BlockPlacement>)Array.Empty<BlockPlacement>();
        _changedBlocks = highlightEdits && hull is not null && previous.Count > 0
            ? ChangedBlocks(previous, hull.Blocks)
            : null;
        _highlightTimer.Stop();
        RebuildModel();
        if (hull is null)
            return;

        if (_changedBlocks is { Count: > 0 })
            _highlightTimer.Start();

        _center = new Point3D((hull.MinX + hull.MaxX + 1) / 2d, (hull.MinY + hull.MaxY + 1) / 2d, (hull.MinZ + hull.MaxZ + 1) / 2d);
        _hullLength = hull.OccupiedLength;
        _hullWidth = hull.OccupiedWidth;
        _hullHeight = hull.OccupiedHeight;
        _fitDistance = Math.Max(18, Math.Max(_hullLength, Math.Max(_hullWidth, _hullHeight)) * 1.45);
        if (!_framed)
        {
            _distance = _fitDistance;
            _framed = true;
        }

        PositionLights(hull);
        RefreshEnvironmentForInspection(force: true);
        UpdateCamera();
    }

    /// <summary>
    /// Returns the camera to the opening isometric view and reframes the hull. This is
    /// what the editor's Reset does; an ordinary edit deliberately leaves the camera alone.
    /// </summary>
    public void ResetView()
    {
        _yaw = IsometricYaw;
        _pitch = IsometricPitch;
        _distance = _fitDistance;
        _framed = true;
        PauseTurn();
        UpdateCamera();
    }

    /// <summary>
    /// The placements the new hull does not share with the old one. A placement is a value,
    /// so an unmoved block of the same shape, rotation, material, and depth compares equal
    /// and is left alone; anything added, moved, re-cut, or re-materialled is what flashes.
    /// </summary>
    private static HashSet<BlockPlacement> ChangedBlocks(
        IReadOnlyCollection<BlockPlacement> previous,
        IReadOnlyCollection<BlockPlacement> current)
    {
        var before = new HashSet<BlockPlacement>(previous);
        var changed = new HashSet<BlockPlacement>();
        foreach (var block in current)
        {
            if (!before.Contains(block))
                changed.Add(block);
        }

        return changed;
    }

    /// <summary>
    /// Moves the camera to a standing view and reframes the whole hull. Comparing the same
    /// hull from a fixed view before and after a geometry change is what makes the preview
    /// usable as evidence; an orbited camera is not repeatable. The gentle turn pauses
    /// here for the same reason, and comes back on the next grab-and-release.
    /// </summary>
    public void ShowView(PreviewView view)
    {
        (_yaw, _pitch) = view switch
        {
            PreviewView.Side => (0d, 0d),
            PreviewView.Top => (-Math.PI / 2, MaximumPitch),
            PreviewView.Bow => (Math.PI / 2, 0d),
            PreviewView.Underside => (-Math.PI / 2, -MaximumPitch),
            _ => (IsometricYaw, IsometricPitch),
        };
        _distance = FitDistance(view);
        PauseTurn();
        UpdateCamera();
    }

    /// <summary>
    /// Parks the gentle turn at the camera's current place. Any motion in flight is
    /// cleared, not merely overridden: a standing view that is still coasting from a
    /// throw would be no more repeatable than an orbited one.
    /// </summary>
    private void PauseTurn()
    {
        _pausedByView = true;
        _yawRate = 0d;
        _pitchRate = 0d;
    }

    /// <summary>
    /// Gets a distance that frames the whole hull for a view. A square-on preset puts a
    /// hull axis straight across or straight up the screen, where the pane's aspect ratio
    /// decides which one crops first, so the two are measured separately rather than from
    /// the longest axis alone.
    /// </summary>
    private double FitDistance(PreviewView view)
    {
        if (view == PreviewView.Isometric)
            return _fitDistance;

        var (across, up, depth) = view switch
        {
            PreviewView.Side => (_hullLength, _hullHeight, _hullWidth),
            PreviewView.Bow => (_hullWidth, _hullHeight, _hullLength),
            _ => (_hullWidth, _hullLength, _hullHeight),
        };

        // FieldOfView is the horizontal angle, so the vertical half-angle follows the pane.
        var acrossTangent = Math.Tan(_camera.FieldOfView * Math.PI / 360);
        var aspect = ActualWidth > 0 && ActualHeight > 0 ? ActualHeight / ActualWidth : 0.7;
        var upTangent = acrossTangent * aspect;

        // The frame has to hold the near face, not the centre plane, so the view's own
        // depth is added before the margin. The margin then covers the small tilt the top
        // view keeps to stay off the up vector.
        var toNearFace = Math.Max(across / (2 * acrossTangent), up / (2 * upTangent));
        return Math.Max(18, (toNearFace + depth / 2d) * 1.15);
    }

    /// <summary>
    /// How long a new or changed block takes to settle from the edit colour into its own.
    /// Long enough to follow where a change landed after looking away from the controls,
    /// short enough that it is over before the next edit.
    /// </summary>
    public static readonly TimeSpan EditHighlightDuration = TimeSpan.FromSeconds(3);

    private void RebuildModel()
    {
        if (_hull is null)
        {
            _content.Content = null;
            _highlight.Content = null;
            return;
        }

        var drawn = SelectDrawnPlacements(_hull, InternalArmorDrawn, _cutaway, _cutawayFraction);
        if (_selectedBarbetteId is not null)
        {
            var selected = SelectBarbettePlacements(_hull, drawn, _selectedBarbetteId);
            var selectedSet = selected.ToHashSet();
            _content.Content = BuildModelWithRefinement(drawn.Where(block => !selectedSet.Contains(block)).ToArray());
            _highlight.Content = BuildSelectionHighlightModel(selected);
            return;
        }
        if (_selectedInternalOwnerPrefix is not null)
        {
            var selected = SelectInternalFamilyPlacements(_hull, drawn, _selectedInternalOwnerPrefix);
            var selectedSet = selected.ToHashSet();
            _content.Content = BuildModelWithRefinement(drawn.Where(block => !selectedSet.Contains(block)).ToArray());
            _highlight.Content = BuildSelectionHighlightModel(selected);
            return;
        }
        if (_changedBlocks is not { Count: > 0 })
        {
            _content.Content = BuildModelWithRefinement(drawn);
            _highlight.Content = null;
            return;
        }

        // The changed blocks are lifted into their own visual so the settled hull can stay
        // frozen while they animate. Matching is by value against the diff, so a beam the
        // cutaway shortened no longer matches the placement the diff saw and stays settled:
        // the part of a sectioned hull that straddles the cut plane does not flash.
        var settled = new List<BlockPlacement>(drawn.Count);
        var changed = new List<BlockPlacement>();
        foreach (var block in drawn)
            (_changedBlocks.Contains(block) ? changed : settled).Add(block);

        _content.Content = BuildModelWithRefinement(settled);
        _highlight.Content = BuildEditHighlightModel(changed);
    }

    /// <summary>
    /// Builds the changed blocks with an animated face finish: every one starts at the
    /// palette's edit red and runs to the finish it settles into over
    /// <see cref="EditHighlightDuration" />, seams and all. Each seam is animated from the
    /// colour its own fill starts at, so the separation lines emerge with the fade instead
    /// of arriving in a single frame when the highlight expires and the whole hull is
    /// rebuilt settled.
    /// </summary>
    internal static Model3DGroup BuildEditHighlightModel(IReadOnlyList<BlockPlacement> blocks)
    {
        var group = new Model3DGroup();
        if (blocks.Count == 0)
            return group;

        var builders = new Dictionary<(MaterialKind Material, int Depth), MeshChunkBuilder>();
        foreach (var block in blocks)
        {
            var key = (block.Material, block.ArmorDepth);
            if (!builders.TryGetValue(key, out var builder))
                builders[key] = builder = new MeshChunkBuilder();

            var axes = AxesFor(block.Rotation);
            foreach (var face in BlockEnvelope.Faces(DrawnShape(block)))
                builder.AddPolygon(block, axes.Right, axes.Up, axes.Forward, face);
        }

        foreach (var ((material, depth), builder) in builders)
        {
            var faceMaterial = CreateEditHighlightMaterial(material, depth);
            foreach (var mesh in builder.Build())
                group.Children.Add(new GeometryModel3D(mesh, faceMaterial) { BackMaterial = faceMaterial });
        }

        return group;
    }

    internal static IReadOnlyList<BlockPlacement> SelectInternalFamilyPlacements(
        GeneratedHull hull,
        IReadOnlyList<BlockPlacement> drawn,
        string ownerPrefix)
    {
        ArgumentNullException.ThrowIfNull(hull);
        ArgumentNullException.ThrowIfNull(drawn);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerPrefix);
        var selectedCells = hull.CellProvenance
            .Where(item => item.Owners.Any(owner => owner.Role == PhysicalCellRole.InternalStructure &&
                owner.OwnerId.StartsWith(ownerPrefix, StringComparison.Ordinal)))
            .Select(item => (item.Cell.X, item.Cell.Y, item.Cell.Z))
            .ToHashSet();
        return drawn.Where(block => block.OccupiedCells.Any(selectedCells.Contains)).ToArray();
    }

    /// <summary>
    /// Selects the resolved placements one barbette owns. Ownership is read from the exact composed
    /// snapshot provenance, so the highlight matches preview and export rather than re-deriving
    /// barbette geometry in the UI.
    /// </summary>
    internal static IReadOnlyList<BlockPlacement> SelectBarbettePlacements(
        GeneratedHull hull,
        IReadOnlyList<BlockPlacement> drawn,
        string barbetteId)
    {
        ArgumentNullException.ThrowIfNull(hull);
        ArgumentNullException.ThrowIfNull(drawn);
        ArgumentException.ThrowIfNullOrWhiteSpace(barbetteId);
        var selectedCells = hull.CellProvenance
            .Where(item => item.Owners.Any(owner => owner.Role == PhysicalCellRole.Barbette &&
                string.Equals(owner.OwnerId, barbetteId, StringComparison.Ordinal)))
            .Select(item => (item.Cell.X, item.Cell.Y, item.Cell.Z))
            .ToHashSet();
        return drawn.Where(block => block.OccupiedCells.Any(selectedCells.Contains)).ToArray();
    }

    private static Model3DGroup BuildSelectionHighlightModel(IReadOnlyList<BlockPlacement> blocks)
    {
        var group = new Model3DGroup();
        if (blocks.Count == 0)
            return group;
        var builder = new MeshChunkBuilder();
        foreach (var block in blocks)
        {
            var axes = AxesFor(block.Rotation);
            foreach (var face in BlockEnvelope.Faces(DrawnShape(block)))
                builder.AddPolygon(block, axes.Right, axes.Up, axes.Forward, face);
        }

        var brush = new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x28));
        var material = new MaterialGroup();
        material.Children.Add(new DiffuseMaterial(brush));
        material.Children.Add(new EmissiveMaterial(new SolidColorBrush(Color.FromRgb(0x48, 0x24, 0x00))));
        foreach (var mesh in builder.Build())
            group.Children.Add(new GeometryModel3D(mesh, material) { BackMaterial = material });
        return group;
    }

    /// <summary>
    /// Chooses which placements the view draws, and in what form. Internal armor is left
    /// out unless <paramref name="showInternalArmor" /> says otherwise; the control passes
    /// the size rule and the debug override combined. A cutaway keeps the cells on the near side of its plane: a
    /// beam or pole that straddles the plane is drawn as the shorter member its kept
    /// cells make, while a slope that straddles it is kept or dropped whole by its anchor
    /// cell, since there is no shorter wedge that would still show the right face. The
    /// result is a view of the export list, not a new list: nothing here reaches the exporter.
    /// </summary>
    internal static IReadOnlyList<BlockPlacement> SelectDrawnPlacements(
        GeneratedHull hull,
        bool showInternalArmor,
        CutawayPlane cutaway,
        double cutawayFraction)
    {
        var drawn = new List<BlockPlacement>(hull.Blocks.Count);
        var limit = CutawayLimit(hull, cutaway, cutawayFraction);
        foreach (var block in hull.Blocks)
        {
            if (block.IsInternalArmor && !showInternalArmor)
                continue;
            if (cutaway == CutawayPlane.None)
            {
                drawn.Add(block);
                continue;
            }

            if (ClipToCutaway(block, cutaway, limit) is { } clipped)
                drawn.Add(clipped);
        }

        return drawn;
    }

    /// <summary>
    /// Gets the last lattice coordinate kept along the cutaway axis. The fraction maps
    /// onto the hull's own extent, so the section always keeps at least the first metre
    /// and reaches the far face exactly at one.
    /// </summary>
    private static int CutawayLimit(GeneratedHull hull, CutawayPlane cutaway, double fraction)
    {
        var (minimum, maximum) = cutaway switch
        {
            CutawayPlane.Station => (hull.MinZ, hull.MaxZ),
            CutawayPlane.Centreline => (hull.MinX, hull.MaxX),
            CutawayPlane.Waterline => (hull.MinY, hull.MaxY),
            _ => (0, 0),
        };
        return minimum + (int)Math.Round(Math.Clamp(fraction, 0, 1) * (maximum - minimum));
    }

    private static int CutawayCoordinate((int X, int Y, int Z) cell, CutawayPlane cutaway) => cutaway switch
    {
        CutawayPlane.Station => cell.Z,
        CutawayPlane.Centreline => cell.X,
        CutawayPlane.Waterline => cell.Y,
        _ => int.MinValue,
    };

    private static BlockPlacement? ClipToCutaway(BlockPlacement block, CutawayPlane cutaway, int limit)
    {
        // A one-cell part, and a slope of any length, is kept or dropped whole. The slope
        // is kept while its anchor cell is on the near side because that is the cell its
        // descending face belongs to.
        if (block.CellLength == 1 || !IsStructuralMember(block.Shape))
            return CutawayCoordinate(block.Position, cutaway) <= limit ? block : null;

        // Members run along one axis, so the kept cells form one contiguous span. Find
        // the first and last kept step along the forward axis.
        var first = -1;
        var last = -1;
        var step = 0;
        foreach (var cell in block.OccupiedCells)
        {
            if (CutawayCoordinate(cell, cutaway) <= limit)
            {
                if (first < 0)
                    first = step;
                last = step;
            }
            step++;
        }

        if (first < 0)
            return null;
        var keptLength = last - first + 1;
        if (keptLength == block.CellLength)
            return block;

        var forward = BlockRotations.GetRotationAxes(block.Rotation).Forward;
        return block with
        {
            Shape = MemberShapeFor(keptLength, block.UsePoles),
            X = block.X + forward.X * first,
            Y = block.Y + forward.Y * first,
            Z = block.Z + forward.Z * first,
        };
    }

    private static bool IsStructuralMember(BlockShape shape) => shape is
        BlockShape.Beam2 or BlockShape.Beam3 or BlockShape.Beam4 or
        BlockShape.Pole2 or BlockShape.Pole3 or BlockShape.Pole4;

    private static BlockShape MemberShapeFor(int cellLength, bool usePoles) => (cellLength, usePoles) switch
    {
        (1, false) => BlockShape.Cube,
        (2, false) => BlockShape.Beam2,
        (3, false) => BlockShape.Beam3,
        (1, true) => BlockShape.Pole1,
        (2, true) => BlockShape.Pole2,
        (3, true) => BlockShape.Pole3,
        _ => usePoles ? BlockShape.Pole4 : BlockShape.Beam4,
    };

    /// <summary>
    /// Builds the hull from each placement's real envelope: beams and multi-metre slopes are
    /// one part rather than a run of cubes, and smoothing wedges keep their descending face.
    /// A placement the exporter will flatten is drawn as the full block the blueprint will
    /// actually contain, so the preview never claims geometry the export does not carry.
    /// Two passes are needed because a face may only be dropped once it is known that a
    /// neighbouring part covers the whole boundary square behind it. Parts are batched by
    /// material and armor depth, so internal armor gets its own finish per material.
    /// </summary>
    private Model3DGroup BuildModel(IReadOnlyList<BlockPlacement> blocks)
    {
        NativeBuildCountForTests++;
        var covered = new HashSet<(int X, int Y, int Z, int Direction)>();
        foreach (var block in blocks)
        {
            var axes = AxesFor(block.Rotation);
            foreach (var face in BlockEnvelope.Faces(DrawnShape(block)))
            {
                if (face is { FillsCellFace: true, Boundary: { } boundary })
                    covered.Add(WorldFace(block, axes, boundary));
            }
        }

        var builders = new Dictionary<(MaterialKind Material, int Depth), MeshChunkBuilder>();
        foreach (var block in blocks)
        {
            var key = (block.Material, block.ArmorDepth);
            if (!builders.TryGetValue(key, out var builder))
                builders[key] = builder = new MeshChunkBuilder();

            var axes = AxesFor(block.Rotation);
            var right = axes.Right;
            var up = axes.Up;
            var forward = axes.Forward;
            foreach (var face in BlockEnvelope.Faces(DrawnShape(block)))
            {
                // A face inside the hull is dropped only when the neighbouring cell's
                // opposing square is completely filled. A partial neighbour, such as the
                // half square a triangle corner presents, leaves this face visible.
                if (face.Boundary is { } boundary && covered.Contains(OpposingFace(block, axes, boundary)))
                    continue;
                builder.AddPolygon(block, right, up, forward, face);
            }
        }

        var group = new Model3DGroup();
        foreach (var ((material, depth), builder) in builders)
        {
            var modelMaterial = CreateFaceMaterial(material, depth);
            foreach (var mesh in builder.Build())
            {
                var model = new GeometryModel3D(mesh, modelMaterial) { BackMaterial = modelMaterial };
                model.Freeze();
                group.Children.Add(model);
            }
        }
        group.Freeze();
        return group;
    }

    /// <summary>
    /// Gets the shape a placement will reach the blueprint as. A fitted shell candidate is
    /// exported as a full block because its exact game rotation has not passed an in-game
    /// fixture, so it is drawn as one. A cube envelope is the same under every rotation, so
    /// the placement's own rotation stays usable for the transform.
    /// </summary>
    private static BlockShape DrawnShape(BlockPlacement block) =>
        block.KeepsFittedShape ? block.Shape : BlockShape.Cube;

    /// <summary>
    /// Gets the world axes for a placement. An out-of-range rotation is drawn unrotated
    /// instead of throwing, so a hull the geometry validator would reject stays visible
    /// while it is being diagnosed.
    /// </summary>
    private static RotationAxes AxesFor(int rotation) =>
        BlockRotations.GetRotationAxes(BlockRotations.IsValid(rotation) ? rotation : 0);

    /// <summary>Locates one of a placement's boundary squares in world lattice terms.</summary>
    private static (int X, int Y, int Z, int Direction) WorldFace(
        BlockPlacement block,
        RotationAxes axes,
        CellFace boundary)
    {
        var forward = axes.Forward;
        var direction = WorldDirection(boundary.Direction, axes);
        return (
            block.X + forward.X * boundary.CellIndex,
            block.Y + forward.Y * boundary.CellIndex,
            block.Z + forward.Z * boundary.CellIndex,
            DirectionIndex(direction));
    }

    /// <summary>Locates the square the neighbouring cell presents back to a boundary square.</summary>
    private static (int X, int Y, int Z, int Direction) OpposingFace(
        BlockPlacement block,
        RotationAxes axes,
        CellFace boundary)
    {
        var forward = axes.Forward;
        var direction = WorldDirection(boundary.Direction, axes);
        return (
            block.X + forward.X * boundary.CellIndex + direction.X,
            block.Y + forward.Y * boundary.CellIndex + direction.Y,
            block.Z + forward.Z * boundary.CellIndex + direction.Z,
            DirectionIndex(direction.Negate()));
    }

    private static AxisDirection WorldDirection(AxisDirection local, RotationAxes axes)
    {
        var right = axes.Right;
        var up = axes.Up;
        var forward = axes.Forward;
        return new AxisDirection(
            local.X * right.X + local.Y * up.X + local.Z * forward.X,
            local.X * right.Y + local.Y * up.Y + local.Z * forward.Y,
            local.X * right.Z + local.Y * up.Z + local.Z * forward.Z);
    }

    private static int DirectionIndex(AxisDirection direction) =>
        direction.X != 0 ? direction.X > 0 ? 0 : 1
        : direction.Y != 0 ? direction.Y > 0 ? 2 : 3
        : direction.Z > 0 ? 4 : 5;

    private void PositionLights(GeneratedHull hull)
    {
        var span = Math.Max(hull.OccupiedLength, Math.Max(hull.OccupiedWidth, hull.OccupiedHeight));
        _keyLight.Position = new Point3D(
            _center.X + hull.OccupiedWidth * 1.2,
            _center.Y + span * 0.65,
            _center.Z - span * 0.35);
        _keyLight.Range = span * 3.5;
        _keyLight.ConstantAttenuation = 0.4;
        _keyLight.LinearAttenuation = 0.006;
        _keyLight.QuadraticAttenuation = 0;

        _rimLight.Position = new Point3D(
            _center.X - hull.OccupiedWidth * 0.95,
            _center.Y + hull.OccupiedHeight * 1.4,
            _center.Z + span * 0.65);
        _rimLight.Range = span * 3.5;
        _rimLight.ConstantAttenuation = 0.65;
        _rimLight.LinearAttenuation = 0.008;
        _rimLight.QuadraticAttenuation = 0;
    }

    /// <summary>
    /// The colours and border metrics of one material/depth batch's face finish. Every
    /// face of a part is a fill, the internal armor hatch where one applies, and a seam
    /// border inset far enough that the border of the part behind it still reads as its
    /// own line.
    /// </summary>
    private readonly record struct FaceFinish(
        Color Fill,
        Color? Hatch,
        Color Seam,
        double SeamThickness,
        double SeamInset,
        bool Clip)
    {
        /// <summary>The shell finish: the material's paint behind a seam in half its shade.</summary>
        public static FaceFinish Shell(MaterialKind material)
        {
            var paint = MaterialColor(material);
            return new FaceFinish(paint, null, Scale(paint, ShellSeamShade), ShellSeamThickness, ShellSeamInset, false);
        }

        /// <summary>
        /// The internal armor finish: a per-material tint that darkens a step per metre of
        /// depth, a hatch in a darker shade of that tint, and a pale seam.
        /// </summary>
        public static FaceFinish Internal(MaterialKind material, int depth)
        {
            var tint = Scale(InternalArmorColor(material), Math.Pow(InternalTintShade, depth - 1));
            return new FaceFinish(
                tint,
                WithAlpha(Scale(tint, HatchShade), HatchAlpha),
                Lighten(tint),
                InternalSeamThickness,
                InternalSeamInset,
                true);
        }
    }

    private const double ShellSeamShade = 0.52;
    private const double ShellSeamThickness = 0.035;
    private const double ShellSeamInset = 0.0175;
    private const double InternalTintShade = 0.82;
    private const double InternalSeamThickness = 0.05;
    private const double InternalSeamInset = 0.025;
    private const double HatchShade = 0.45;
    private const double HatchThickness = 0.06;
    private const byte HatchAlpha = 150;

    private static FaceFinish FinishFor(MaterialKind material, int depth) =>
        depth == 0 ? FaceFinish.Shell(material) : FaceFinish.Internal(material, depth);

    /// <summary>
    /// The paint a face is drawn with: the fill, the hatch where the finish has one, and
    /// the seam border. The settled model and the edit highlight are the same drawing, so
    /// a part that finishes fading holds the exact face the settled rebuild will draw.
    /// </summary>
    private static DrawingGroup CreateFaceDrawing(FaceFinish finish, Brush fill, Brush seam, Brush? hatch)
    {
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(
            fill, null, new RectangleGeometry(new Rect(0, 0, 1, 1))));

        if (finish.Hatch is not null && hatch is not null)
        {
            var lines = new GeometryGroup();
            for (var offset = -1.0; offset <= 1.0; offset += 0.25)
                lines.Children.Add(new LineGeometry(new Point(offset, 0), new Point(offset + 1, 1)));
            drawing.Children.Add(new GeometryDrawing(
                null, new Pen(hatch, HatchThickness), lines));
        }

        drawing.Children.Add(new GeometryDrawing(
            null,
            new Pen(seam, finish.SeamThickness),
            new RectangleGeometry(new Rect(
                finish.SeamInset, finish.SeamInset, 1 - 2 * finish.SeamInset, 1 - 2 * finish.SeamInset))));
        if (finish.Clip)
            drawing.ClipGeometry = new RectangleGeometry(new Rect(0, 0, 1, 1));

        return drawing;
    }

    /// <summary>
    /// Wraps a face drawing into the brush every part is textured with. The drawing covers
    /// the part rather than each face cube, so the seam runs along a beam the same way it
    /// does along a cube and the hatch stretches with it.
    /// </summary>
    private static DrawingBrush CreateFaceBrush(DrawingGroup drawing) => new(drawing)
    {
        Viewbox = new Rect(0, 0, 1, 1),
        ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
        Viewport = new Rect(0, 0, 1, 1),
        ViewportUnits = BrushMappingMode.RelativeToBoundingBox,
        Stretch = Stretch.Fill,
    };

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(MaterialKind Material, int Depth), Material>
        FaceMaterialCache = new();

    /// <summary>
    /// The settled finish for one material/depth batch: the shell paints the material's own
    /// colour, while internal armor takes its own palette hue with a stepped tint, a hatch,
    /// and a pale seam, so a layer behind the shell reads as inside at a glance even where
    /// it happens to share the shell's material. The depth tint is what lets adjacent
    /// layers of one material be counted in a cutaway. The result is frozen and shared, so a
    /// hull or a decoration set with many placements of one material does not rebuild the
    /// same brush per placement.
    /// </summary>
    internal static Material CreateFaceMaterial(MaterialKind material, int depth) =>
        FaceMaterialCache.GetOrAdd((material, depth), key => BuildFaceMaterial(key.Material, key.Depth));

    private static Material BuildFaceMaterial(MaterialKind material, int depth)
    {
        var finish = FinishFor(material, depth);
        var drawing = CreateFaceDrawing(
            finish,
            new SolidColorBrush(finish.Fill),
            new SolidColorBrush(finish.Seam),
            finish.Hatch is { } hatch ? new SolidColorBrush(hatch) : null);
        drawing.Freeze();

        var brush = CreateFaceBrush(drawing);
        brush.Freeze();

        var result = new MaterialGroup();
        result.Children.Add(new DiffuseMaterial(brush));
        result.Children.Add(AccentFor(finish, depth));
        result.Freeze();
        return result;
    }

    /// <summary>
    /// The animated counterpart of <see cref="CreateFaceMaterial" />, for the blocks an
    /// edit has just changed: the same drawing with its fill, hatch, and seam brushes all
    /// running from the edit red to their settled colours. The seam starts on the fill's
    /// own colour, which is what makes it fade in rather than appear.
    /// </summary>
    private static Material CreateEditHighlightMaterial(MaterialKind material, int depth)
    {
        var finish = FinishFor(material, depth);
        var drawing = CreateFaceDrawing(
            finish,
            AnimatedHighlightBrush(finish.Fill),
            AnimatedHighlightBrush(finish.Seam),
            finish.Hatch is { } hatch ? AnimatedHighlightBrush(hatch) : null);

        var result = new MaterialGroup();
        result.Children.Add(new DiffuseMaterial(CreateFaceBrush(drawing)));
        result.Children.Add(AccentFor(finish, depth));
        return result;
    }

    /// <summary>
    /// The second half of a face material: a sheen on the shell, and a touch of
    /// self-illumination on internal armor, which keeps the inside of a sectioned hull
    /// readable where the fixed lights only reach at a grazing angle.
    /// </summary>
    private static Material AccentFor(FaceFinish finish, int depth) => depth == 0
        ? new SpecularMaterial(new SolidColorBrush(Color.FromArgb(80, 255, 246, 228)), 18)
        : new EmissiveMaterial(new SolidColorBrush(Scale(finish.Fill, 0.16)));

    /// <summary>
    /// A brush that runs from the highlight colour to the colour a part settles into. The
    /// brush's own colour is that settled value, and the animation holds its end, so the
    /// frame the highlight expires on is the frame the settled model draws: no seam appears
    /// or disappears with the swap. Most of the travel happens early, so the change reads
    /// immediately and then settles rather than sitting red for three seconds.
    /// </summary>
    private static SolidColorBrush AnimatedHighlightBrush(Color settled)
    {
        var brush = new SolidColorBrush(settled);
        brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation
        {
            From = EditHighlightColor,
            To = settled,
            Duration = new Duration(EditHighlightDuration),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
        return brush;
    }

    private static Color Scale(Color color, double factor) =>
        Color.FromRgb(Scale(color.R, factor), Scale(color.G, factor), Scale(color.B, factor));

    private static Color Lighten(Color color) =>
        Color.FromRgb(Lighten(color.R), Lighten(color.G), Lighten(color.B));

    private static Color WithAlpha(Color color, byte alpha) =>
        Color.FromArgb(alpha, color.R, color.G, color.B);

    private static byte Scale(byte channel, double factor) => (byte)Math.Clamp(channel * factor, 0, 255);

    private static byte Lighten(byte channel) => (byte)Math.Clamp(channel + (255 - channel) * 0.55, 0, 255);

    private void OnPointerDown(object sender, MouseButtonEventArgs eventArgs)
    {
        CaptureMouse();
        BeginHold(eventArgs.GetPosition(this));
        eventArgs.Handled = true;
    }

    private void OnPointerUp(object sender, MouseButtonEventArgs eventArgs)
    {
        EndHold();
        ReleaseMouseCapture();
    }

    private void OnPointerMove(object sender, MouseEventArgs eventArgs)
    {
        if (_dragging)
            MoveHold(eventArgs.GetPosition(this));
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs eventArgs)
    {
        _distance = Math.Max(5, _distance * (eventArgs.Delta > 0 ? 0.88 : 1.14));
        UpdateCamera();
    }

    /// <summary>
    /// Takes hold of the hull: the gentle turn stops dead and the pointer drives the orbit
    /// directly until <see cref="EndHold" />. The overload taking a timestamp is what the
    /// self-test uses, so the release math can be exercised without a real clock.
    /// </summary>
    internal void BeginHold(Point position) => BeginHold(position, Stopwatch.GetTimestamp());

    /// <summary>Orbits the view under the pointer, recording the travel a release reads as a throw.</summary>
    internal void MoveHold(Point position) => MoveHold(position, Stopwatch.GetTimestamp());

    internal void BeginHold(Point position, long timestamp)
    {
        _dragging = true;
        _yawRate = 0d;
        _pitchRate = 0d;
        _lastPointer = position;
        _pointerTrail.Clear();
        RecordPointer(position, timestamp);
    }

    internal void MoveHold(Point position, long timestamp)
    {
        _yaw += (position.X - _lastPointer.X) * OrbitRadiansPerPixel;
        _pitch = Math.Clamp(_pitch + (position.Y - _lastPointer.Y) * OrbitRadiansPerPixel, -MaximumPitch, MaximumPitch);
        _lastPointer = position;
        RecordPointer(position, timestamp);
        UpdateCamera();
    }

    /// <summary>
    /// Lets go. The pointer's own recent travel is handed to the turn as speed, so letting
    /// the hull go mid-swing coasts on the way it was thrown and then eases back to the
    /// gentle rate — the preview slowly returns to its initial rotation rather than
    /// stopping where it was left. A release below the dead zone simply picks the gentle
    /// turn back up, and with the turn switched off a release hands over nothing at all —
    /// the view stays exactly where the drag left it and cannot be flicked into a spin.
    /// Either way a release clears a standing view's pause: taking hold and letting go is
    /// the way back to the turn. The overload taking a timestamp is what the self-test uses.
    /// </summary>
    internal void EndHold() => EndHold(Stopwatch.GetTimestamp());

    internal void EndHold(long timestamp)
    {
        if (!_dragging)
            return;
        _dragging = false;
        _pausedByView = false;
        (_yawRate, _pitchRate) = ReleaseRates(timestamp);
        _pointerTrail.Clear();
    }

    private void RecordPointer(Point position, long timestamp)
    {
        _pointerTrail.Add((position, timestamp));
        var stale = (long)(2 * ThrowWindowSeconds * Stopwatch.Frequency);
        while (_pointerTrail.Count > 1 && timestamp - _pointerTrail[0].Timestamp > stale)
            _pointerTrail.RemoveAt(0);
    }

    /// <summary>
    /// The turn a release hands over: the pointer's average speed over the last moments of
    /// the drag, mapped onto the orbit at a fraction of a drag's own sensitivity. Both ends
    /// of the sample window matter — a pointer whose last travel has scrolled out of it, or
    /// that was already resting when it was let go, is a placement rather than a throw and
    /// hands over nothing. The inertia belongs to the gentle turn, so with that turn
    /// switched off every release is a placement and the view stops dead under the pointer.
    /// </summary>
    private (double YawRate, double PitchRate) ReleaseRates(long timestamp)
    {
        if (!_autoRotate || _pointerTrail.Count < 2)
            return (0d, 0d);

        var newest = _pointerTrail[^1];
        var window = (long)(ThrowWindowSeconds * Stopwatch.Frequency);
        if (timestamp - newest.Timestamp > window)
            return (0d, 0d);

        var oldest = newest;
        for (var index = _pointerTrail.Count - 2; index >= 0; index--)
        {
            if (newest.Timestamp - _pointerTrail[index].Timestamp > window)
                break;
            oldest = _pointerTrail[index];
        }

        var seconds = (newest.Timestamp - oldest.Timestamp) / (double)Stopwatch.Frequency;
        if (seconds <= 0)
            return (0d, 0d);

        return (
            ThrowRate((newest.Position.X - oldest.Position.X) / seconds),
            ThrowRate((newest.Position.Y - oldest.Position.Y) / seconds));
    }

    /// <summary>
    /// Maps a pointer speed to an angular rate: below the dead zone a release is a
    /// placement and hands over nothing, and above the limit a flick is capped so one
    /// hard throw cannot spin the view into a blur. A release reads only a fraction of the
    /// drag's own orbit mapping, so it colours the motion without flinging the view.
    /// </summary>
    internal static double ThrowRate(double pixelsPerSecond)
    {
        if (Math.Abs(pixelsPerSecond) < ThrowDeadZoneSpeed)
            return 0d;
        var rate = pixelsPerSecond * OrbitRadiansPerPixel * ThrowGain;
        return Math.Clamp(rate, -MaximumThrowRate, MaximumThrowRate);
    }

    private void OnAnimationFrame(object? sender, EventArgs eventArgs)
    {
        var now = eventArgs is RenderingEventArgs rendering ? rendering.RenderingTime : TimeSpan.Zero;
        var seconds = _lastFrame is { } last ? (now - last).TotalSeconds : 0d;
        _lastFrame = now;
        if (seconds > 0)
            AdvanceOrbit(Math.Min(seconds, MaximumFrameStep));
    }

    /// <summary>
    /// Advances the gentle turn by one frame's worth of time. The spin eases toward the
    /// rate the view should rest at — the gentle rate, or a full stop while the turn is
    /// switched off, held, or parked at a standing view — so a throw coasts first and then
    /// returns to the initial rotation instead of stopping wherever it was let go. The
    /// tilt keeps whatever a release left it at: its own inertia decays to nothing and
    /// nothing pulls the angle back. The self-test steps this directly, which is why it
    /// is not private.
    /// </summary>
    internal void AdvanceOrbit(double deltaSeconds)
    {
        if (_dragging || _hull is null)
            return;

        var target = _autoRotate && !_pausedByView ? GentleTurnRate : 0d;
        _yawRate += (target - _yawRate) * (1 - Math.Exp(-deltaSeconds / TurnSettleSeconds));
        if (target == 0d && Math.Abs(_yawRate) < RestingRate)
            _yawRate = 0d;

        var moved = false;
        if (_yawRate != 0d)
        {
            _yaw += _yawRate * deltaSeconds;
            moved = true;
        }

        if (_pitchRate != 0d)
        {
            _pitch = Math.Clamp(_pitch + _pitchRate * deltaSeconds, -MaximumPitch, MaximumPitch);
            _pitchRate *= Math.Exp(-deltaSeconds / TiltSettleSeconds);
            if (Math.Abs(_pitchRate) < RestingRate)
                _pitchRate = 0d;
            moved = true;
        }

        if (moved)
            UpdateCamera();
    }

    /// <summary>The camera's current orbit angles and speeds. Read by the self-test's motion checks.</summary>
    internal double ViewYaw => _yaw;
    internal bool IsDraggingForTests => _dragging;

    internal double ViewPitch => _pitch;

    internal double TurnRate => _yawRate;

    internal double TiltRate => _pitchRate;

    private void UpdateCamera()
    {
        var cosPitch = Math.Cos(_pitch);
        var position = new Point3D(
            _center.X + _distance * Math.Cos(_yaw) * cosPitch,
            _center.Y + _distance * Math.Sin(_pitch),
            _center.Z + _distance * Math.Sin(_yaw) * cosPitch);
        _camera.Position = position;
        _camera.LookDirection = _center - position;
        _camera.UpDirection = new Vector3D(0, 1, 0);
        _headLight.Direction = _camera.LookDirection;
        RefreshEnvironmentForInspection();
    }

    /// <summary>
    /// The colour a new or changed block starts at. This is the palette's error red rather
    /// than a fresh hue, so the flash reads as the same signal the status lamp uses.
    /// </summary>
    private static readonly Color EditHighlightColor = Color.FromRgb(0xD3, 0x27, 0x2F);

    /// <summary>
    /// Detaches the process-wide theme subscription. A test that abandons a probe control on
    /// its own thread uses this so a later palette swap on another thread cannot reach a
    /// control it no longer owns.
    /// </summary>
    internal void DetachThemeSubscriptionForTests() => ThemeManager.ThemeChanged -= _themeChangedHandler;

    /// <summary>
    /// Re-lights the view for a palette. The hull itself keeps its material colours in
    /// both — a metal block is the same metal block — but the light around it changes:
    /// the light bench lights the lattice like a figure on white paper, while the standard
    /// dark palette keeps the cooler, dimmer room the navy viewport implies.
    /// </summary>
    public void ApplyTheme(AppTheme theme)
    {
        var light = theme == AppTheme.Workbench;
        _ambientLight.Color = light ? Color.FromRgb(96, 100, 110) : Color.FromRgb(38, 44, 60);
        _fillLight.Color = light ? Color.FromRgb(170, 174, 182) : Color.FromRgb(152, 156, 168);
        _keyLight.Color = light ? Color.FromRgb(255, 252, 245) : Color.FromRgb(244, 236, 220);
        _rimLight.Color = light ? Color.FromRgb(150, 170, 200) : Color.FromRgb(126, 146, 184);
        _headLight.Color = light ? Color.FromRgb(40, 42, 48) : Color.FromRgb(58, 62, 74);
        if (_sceneRoot is not null)
            RefreshEnvironmentForInspection(force: true);
    }

    private static Color MaterialColor(MaterialKind material) => material switch
    {
        MaterialKind.Wood => Color.FromRgb(162, 109, 67),
        MaterialKind.Metal => Color.FromRgb(109, 135, 151),
        MaterialKind.LightweightAlloy => Color.FromRgb(154, 178, 195),
        MaterialKind.HeavyArmor => Color.FromRgb(68, 78, 89),
        MaterialKind.Stone => Color.FromRgb(121, 115, 106),
        MaterialKind.Lead => Color.FromRgb(84, 97, 119),
        MaterialKind.Rubber => Color.FromRgb(61, 67, 73),
        MaterialKind.Glass => Color.FromRgb(87, 183, 200),
        _ => Colors.SlateGray,
    };

    /// <summary>
    /// Internal armor colours. Each is a distinct hue from the shell palette entry for
    /// the same material so an inner metal layer never looks like the outer metal skin,
    /// while the materials stay told apart from one another: warm orange for wood, cyan
    /// for metal, pale mint for alloy, violet for heavy armor, sand for stone, indigo for
    /// lead, olive for rubber, and aqua for glass.
    /// </summary>
    internal static Color InternalArmorColor(MaterialKind material) => material switch
    {
        MaterialKind.Wood => Color.FromRgb(232, 150, 70),
        MaterialKind.Metal => Color.FromRgb(72, 186, 214),
        MaterialKind.LightweightAlloy => Color.FromRgb(170, 226, 196),
        MaterialKind.HeavyArmor => Color.FromRgb(150, 96, 204),
        MaterialKind.Stone => Color.FromRgb(214, 190, 130),
        MaterialKind.Lead => Color.FromRgb(96, 108, 214),
        MaterialKind.Rubber => Color.FromRgb(150, 168, 72),
        MaterialKind.Glass => Color.FromRgb(120, 232, 220),
        _ => Colors.Orchid,
    };

    private sealed class MeshChunkBuilder
    {
        private const int MaxVerticesPerMesh = 36_000;
        private readonly List<MeshGeometry3D> _meshes = [];
        private readonly List<Point3D> _positions = [];
        private readonly List<Point> _textureCoordinates = [];
        private readonly List<int> _indices = [];

        /// <summary>
        /// Adds one convex envelope polygon, transformed into world space by the placement's
        /// anchor and rotation and triangulated as a fan.
        /// </summary>
        public void AddPolygon(
            BlockPlacement block,
            AxisDirection right,
            AxisDirection up,
            AxisDirection forward,
            EnvelopeFace face)
        {
            if (_positions.Count + face.Points.Count > MaxVerticesPerMesh)
                Flush();

            var start = _positions.Count;
            for (var index = 0; index < face.Points.Count; index++)
            {
                var local = face.Points[index];
                _positions.Add(new Point3D(
                    block.X + 0.5 + local.X * right.X + local.Y * up.X + local.Z * forward.X,
                    block.Y + 0.5 + local.X * right.Y + local.Y * up.Y + local.Z * forward.Y,
                    block.Z + 0.5 + local.X * right.Z + local.Y * up.Z + local.Z * forward.Z));
                _textureCoordinates.Add(face.TextureCoordinates[index]);
            }

            for (var index = 1; index + 1 < face.Points.Count; index++)
                _indices.AddRange([start, start + index, start + index + 1]);
        }

        /// <summary>
        /// Adds one convex envelope polygon already expressed in a decoration's own world
        /// transform, triangulated as a fan exactly like a placement's face. Batches every
        /// extension of one material into the same mesh run instead of one model each.
        /// </summary>
        public void AddTransformedPolygon(
            IReadOnlyList<Point3D> points,
            IReadOnlyList<Point> textureCoordinates,
            Func<System.Numerics.Vector3, System.Numerics.Vector3> transform)
        {
            if (_positions.Count + points.Count > MaxVerticesPerMesh)
                Flush();

            var start = _positions.Count;
            for (var index = 0; index < points.Count; index++)
            {
                var local = points[index];
                var world = transform(new System.Numerics.Vector3((float)local.X, (float)local.Y, (float)local.Z));
                _positions.Add(new Point3D(world.X + .5, world.Y + .5, world.Z + .5));
                _textureCoordinates.Add(textureCoordinates[index]);
            }

            for (var index = 1; index + 1 < points.Count; index++)
                _indices.AddRange([start, start + index, start + index + 1]);
        }

        public IReadOnlyList<MeshGeometry3D> Build()
        {
            Flush();
            return _meshes;
        }

        private void Flush()
        {
            if (_positions.Count == 0)
                return;
            var mesh = new MeshGeometry3D
            {
                Positions = new Point3DCollection(_positions),
                TriangleIndices = new Int32Collection(_indices),
                TextureCoordinates = new PointCollection(_textureCoordinates),
            };
            mesh.Freeze();
            _meshes.Add(mesh);
            _positions.Clear();
            _textureCoordinates.Clear();
            _indices.Clear();
        }
    }
}
