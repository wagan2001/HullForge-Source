using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Components;
using FtdHullGenerator.Domain.Design;
using FtdHullGenerator.Domain.Layout;
using FtdHullGenerator.Domain.Projects;
using FtdHullGenerator.Geometry.Components;
using FtdHullGenerator.UI.Workspace;

namespace FtdHullGenerator.UI.Components;

/// <summary>A display choice for a frozen barbette enumeration.</summary>
public sealed record BarbetteEditorChoice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One product-facing diagnostic line shown beside the ruler.</summary>
public sealed record BarbetteDiagnosticLine(string Text, bool IsBlocking);

/// <summary>
/// The frozen 2.0 barbette workspace state: create, select, edit and remove centerline barbettes and
/// drag each one independently along the bow-to-stern ruler. Every edit goes through the one shared
/// <see cref="WorkspaceViewModel"/> draft, so Apply/Cancel/undo keep their existing atomic behavior.
/// </summary>
public sealed class BarbetteEditorViewModel : INotifyPropertyChanged, IDisposable
{
    private static readonly ArmorLayout DefaultArmor = ArmorLayout.Single(MaterialKind.Metal);

    private readonly WorkspaceViewModel _workspace;
    private BarbetteEditorItem? _selectedBarbette;
    private BarbetteRulerModel _ruler = BarbetteRulerModel.Empty;
    private string _summary = "No centerline barbettes yet.";
    private bool _refreshing;
    private IReadOnlyList<BarbetteDiagnosticLine> _frameDiagnostics = [];
    private BarbetteDiagnosticLine? _clearDiameterRejection;

    public BarbetteEditorViewModel(WorkspaceViewModel workspace)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _workspace.DocumentChanged += WorkspaceDocumentChanged;
        _workspace.PropertyChanged += WorkspacePropertyChanged;
        Refresh();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<BarbetteEditorItem> Barbettes { get; } = [];

    public ObservableCollection<string> Readouts { get; } = [];

    /// <summary>Live ruler/placement diagnostics. Deliberately separate from resolved generation findings.</summary>
    public ObservableCollection<BarbetteDiagnosticLine> Diagnostics { get; } = [];

    /// <summary>
    /// Findings from the last revision-matched resolved generation: the selected barbette's
    /// warnings plus any document-global finding. Kept apart from <see cref="Diagnostics"/> so a
    /// live ruler diagnosis is never conflated with an accepted generation compromise.
    /// </summary>
    public ObservableCollection<BarbetteDiagnosticLine> GenerationDiagnostics { get; } = [];

    public BarbetteRulerModel Ruler => _ruler;

    public BarbetteEditorItem? SelectedBarbette
    {
        get => _selectedBarbette;
        set
        {
            var next = value;
            if (ReferenceEquals(_selectedBarbette, next))
                return;
            _selectedBarbette = next;
            // A clear-diameter rejection belonged to the previously selected barbette.
            _clearDiameterRejection = null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(SelectedBarbetteId));
            RaiseReadouts();
        }
    }

    public string? SelectedBarbetteId
    {
        get => _selectedBarbette?.Id;
        set => SelectBarbette(value);
    }

    public bool HasSelection => _selectedBarbette is not null;

    public bool HasDiagnostics => Diagnostics.Count > 0;

    public bool HasGenerationDiagnostics => GenerationDiagnostics.Count > 0;

    public string Summary => _summary;

    /// <summary>
    /// A centerline barbette needs a real centre voxel column, so the normal editor only offers
    /// creation on an odd-width hull. Even-width intent that arrives from a direct, imported or
    /// API document is retained and rejected downstream; it is never created here.
    /// </summary>
    public bool CanAddBarbette =>
        _workspace.Document.Hull.HasSingleBlockCenterline &&
        _workspace.Document.Barbettes.Length < DesignLimits.MaxBarbettesPerDocument;

    /// <summary>Creates a barbette at the nearest clear ruler metre and selects it.</summary>
    public BarbetteEditorItem? AddBarbette()
    {
        if (!CanAddBarbette)
            return null;

        var document = _workspace.Document;
        // First Add must place against the current derived frame. It never reinterprets the
        // persisted datum or the nominal hull length, and it creates nothing when no frame exists.
        var frame = _workspace.LayoutFrame;
        if (frame is null)
        {
            var resolution = _workspace.ResolveLayoutFrameNow();
            frame = _workspace.LayoutFrame;
            if (frame is null)
            {
                _frameDiagnostics = resolution.Diagnostics
                    .Where(diagnostic => diagnostic.Severity != DesignSeverity.Info)
                    .OrderBy(diagnostic => diagnostic.Severity == DesignSeverity.Error ? 0 : 1)
                    .Select(diagnostic => new BarbetteDiagnosticLine(
                        $"{diagnostic.Code}: {diagnostic.Message}",
                        diagnostic.Severity == DesignSeverity.Error))
                    .ToArray();
                Refresh();
                return null;
            }
        }

        _frameDiagnostics = [];
        var datum = new LayoutDatum(frame.BowDatum, frame.CenterPlaneX);
        var definition = NewDefinition(document);
        var center = ChooseInitialCenter(document, definition, frame);

        _refreshing = true;
        try
        {
            // The measured frame datum is persisted inside the one arrangement user transaction, so
            // one user action is one history item and the draft document stays frame-coherent.
            _workspace.UpdateDraft(current => current with
            {
                Barbettes = current.Barbettes.Add(definition),
                Arrangement = AddNode(current.Arrangement, definition, center, datum),
                Datum = datum,
            });
        }
        finally
        {
            _refreshing = false;
        }

        Refresh();
        SelectedBarbette = Barbettes.FirstOrDefault(item => item.Id == definition.Id);
        return SelectedBarbette;
    }

    public bool RemoveSelectedBarbette()
    {
        if (_selectedBarbette is not { } selected)
            return false;
        var id = selected.Id;
        var nodeId = selected.NodeId;
        _refreshing = true;
        try
        {
            _workspace.UpdateDraft(document => document with
            {
                Barbettes = document.Barbettes.Where(item => item.Id != id).ToImmutableArray(),
                Arrangement = document.Arrangement with
                {
                    Nodes = document.Arrangement.Nodes
                        .Where(node => !string.Equals(node.Id, nodeId, StringComparison.Ordinal))
                        .ToImmutableArray(),
                },
            });
        }
        finally
        {
            _refreshing = false;
        }

        Refresh();
        SelectedBarbette = Barbettes.FirstOrDefault();
        return true;
    }

    public void SelectBarbette(string? barbetteId) =>
        SelectedBarbette = barbetteId is null
            ? null
            : Barbettes.FirstOrDefault(item => string.Equals(item.Id, barbetteId, StringComparison.Ordinal));

    /// <summary>
    /// Moves exactly one barbette to the requested ruler position, snapped to a whole-metre cell
    /// anchor. No neighbouring barbette is consulted or rewritten.
    /// </summary>
    public bool MoveBarbette(string barbetteId, DesignMeasure requestedRulerCenter)
    {
        var document = _workspace.Document;
        var barbette = document.Barbettes.FirstOrDefault(item => item.Id == barbetteId);
        if (barbette is null)
            return false;
        var node = document.Arrangement.FindNode(barbette.NodeId);
        if (node is null)
            return false;

        var snapped = _ruler.SnapToCellAnchor(requestedRulerCenter);
        if (node.RequestedCenter == snapped)
            return false;

        var datum = CurrentDatum(document);
        var extent = MeasureHalfExtent(barbette, snapped, datum, datum.LayoutBowZ)
            ?? node.OuterHalfExtent;
        var frameDatum = FrameDatumOrNull();
        _refreshing = true;
        try
        {
            _workspace.UpdateDraft(current =>
            {
                var arrangement = current.Arrangement with
                {
                    Nodes = current.Arrangement.Nodes
                        .Select(item => string.Equals(item.Id, node.Id, StringComparison.Ordinal)
                            ? item with { RequestedCenter = snapped, OuterHalfExtent = extent }
                            : item)
                        .ToImmutableArray(),
                };
                return frameDatum is { } persisted
                    ? current with { Arrangement = arrangement, Datum = persisted }
                    : current with { Arrangement = arrangement };
            });
        }
        finally
        {
            _refreshing = false;
        }

        Refresh();
        return true;
    }

    internal void CommitBarbette(BarbetteDefinition next)
    {
        var frameDatum = FrameDatumOrNull();
        _refreshing = true;
        try
        {
            _workspace.UpdateDraft(document =>
            {
                var barbettes = document.Barbettes
                    .Select(item => item.Id == next.Id ? next : item)
                    .ToImmutableArray();
                return frameDatum is { } persisted
                    ? document with { Barbettes = barbettes, Datum = persisted }
                    : document with { Barbettes = barbettes };
            });
        }
        finally
        {
            _refreshing = false;
        }

        Refresh();
    }

    public void Dispose()
    {
        _workspace.DocumentChanged -= WorkspaceDocumentChanged;
        _workspace.PropertyChanged -= WorkspacePropertyChanged;
    }

    internal static DesignMeasure RulerEnd(ShipDocument document) =>
        DesignMeasure.FromMetres(document.Hull.Length);

    /// <summary>
    /// The one derived frame for the committed revision, when the workspace has published one. Ruler,
    /// move, arrangement and preview all consume this same identity-gated frame.
    /// </summary>
    private ResolvedLayoutFrame? CurrentFrame => _workspace.LayoutFrame;

    /// <summary>
    /// The placement datum for the visible document: the current derived frame when one is
    /// published, otherwise the persisted datum. The fallback is for pending-draft display only and
    /// is never used to create the first barbette.
    /// </summary>
    private LayoutDatum CurrentDatum(ShipDocument document) =>
        CurrentFrame is { } frame ? new LayoutDatum(frame.BowDatum, frame.CenterPlaneX) : document.EffectiveDatum;

    /// <summary>The frame datum to persist in an arrangement transaction, or null when no frame is current.</summary>
    private LayoutDatum? FrameDatumOrNull() =>
        CurrentFrame is { } frame ? new LayoutDatum(frame.BowDatum, frame.CenterPlaneX) : null;

    /// <summary>
    /// The ruler stern end: the current derived frame's supported interval, else the last solved
    /// arrangement's supported end, else the nominal hull length while a pending draft has no frame.
    /// </summary>
    private DesignMeasure EffectiveRulerEnd(ShipDocument document) =>
        CurrentFrame?.SupportedRulerEnd ?? _workspace.ArrangementSolution?.SupportedRulerEnd ?? RulerEnd(document);

    private void WorkspaceDocumentChanged(object? sender, EventArgs eventArgs)
    {
        if (_refreshing)
            return;
        Refresh();
    }

    private void WorkspacePropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is nameof(WorkspaceViewModel.LayoutFrame) or
            nameof(WorkspaceViewModel.ArrangementSolution))
        {
            if (!_refreshing)
                Refresh();
            return;
        }

        if (eventArgs.PropertyName is nameof(WorkspaceViewModel.ResolvedGenerationDiagnostics) or
            nameof(WorkspaceViewModel.ResolvedGenerationRevision) or
            nameof(WorkspaceViewModel.ResolvedCatalogVersion) or
            nameof(WorkspaceViewModel.ResolvedCatalogFingerprint))
            RaiseReadouts();
    }

    private void Refresh()
    {
        var document = _workspace.Document;
        var selectedId = _selectedBarbette?.Id;
        // A frame that is now current supersedes the last fail-closed first-Add reason: a hull that
        // regained its supported deck interval must not keep showing a blocking LAY010 line.
        if (_workspace.LayoutFrame is not null)
            _frameDiagnostics = [];
        _ruler = BuildRuler(document);

        var ordered = _ruler.Entries.Count > 0
            ? _ruler.Entries.Select(entry => entry.BarbetteId).ToArray()
            : document.Barbettes.Select(item => item.Id).ToArray();
        var definitions = document.Barbettes.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var desired = ordered.Where(definitions.ContainsKey).ToArray();

        // Keep the same item instances while the bow-to-stern order is unchanged, so an editor
        // control mid-edit is not rebound to a fresh object after every property write.
        if (!desired.SequenceEqual(Barbettes.Select(item => item.Id), StringComparer.Ordinal))
            Barbettes.Clear();

        for (var index = 0; index < desired.Length; index++)
        {
            var definition = definitions[desired[index]];
            var entry = _ruler.FindEntry(desired[index]);
            var center = entry?.RulerCenter ?? DesignMeasure.Zero;
            if (index < Barbettes.Count)
                Barbettes[index].Reset(definition, index + 1, center, entry);
            else
                Barbettes.Add(new BarbetteEditorItem(this, definition, index + 1, center, entry));
        }

        _selectedBarbette = selectedId is null
            ? null
            : Barbettes.FirstOrDefault(item => string.Equals(item.Id, selectedId, StringComparison.Ordinal));
        OnPropertyChanged(nameof(SelectedBarbette));
        OnPropertyChanged(nameof(SelectedBarbetteId));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanAddBarbette));
        OnPropertyChanged(nameof(Ruler));
        RaiseReadouts();
    }

    private BarbetteRulerModel BuildRuler(ShipDocument document)
    {
        var datum = CurrentDatum(document);
        var layoutBowZ = datum.LayoutBowZ;
        var centerPlaneX = datum.CenterPlaneX;
        var requests = new List<BarbetteRulerRequest>(document.Barbettes.Length);
        foreach (var barbette in document.Barbettes)
        {
            var node = document.Arrangement.FindNode(barbette.NodeId);
            var center = node?.RequestedCenter
                         ?? _workspace.ArrangementSolution?.FindNode(barbette.NodeId)?.RulerCenter
                         ?? DesignMeasure.Zero;
            requests.Add(new BarbetteRulerRequest(barbette.Id, barbette.NodeId, barbette, center));
        }

        return BarbetteRulerModel.Build(requests, EffectiveRulerEnd(document), layoutBowZ, centerPlaneX);
    }

    private void RaiseReadouts()
    {
        var document = _workspace.Document;
        Readouts.Clear();
        Diagnostics.Clear();
        if (!document.Hull.HasSingleBlockCenterline)
        {
            Diagnostics.Add(new BarbetteDiagnosticLine(
                $"{BarbetteDiagnosticCodes.OddHullWidthRequired}: Centerline barbettes require an odd hull width. " +
                "Return the hull to an odd width before adding one.",
                IsBlocking: true));
        }
        if (_ruler.Entries.Count == 0)
        {
            _summary = "No centerline barbettes yet. Add one, then drag it along the ruler.";
            Readouts.Add(_summary);
        }
        else
        {
            _summary =
                $"Bow margin {Format(_ruler.BowMargin)} · Stern margin {Format(_ruler.SternMargin)} · " +
                $"{_ruler.Segments.Count} clear-space gap(s)";
            if (_ruler.BowMargin is { } bow)
                Readouts.Add($"Foremost barbette: {Format(bow)} from its centre to the bow end.");
            foreach (var segment in _ruler.Segments)
                Readouts.Add($"{segment.ForwardBarbetteId} → {segment.AftBarbetteId}: " +
                             $"{Format(segment.ClearGap)} clear separation ({Describe(segment.State)}).");
            if (_ruler.SternMargin is { } stern)
                Readouts.Add($"Rearmost barbette: {Format(stern)} from its centre to the stern end.");
        }

        if (_selectedBarbette is { } selected)
        {
            Readouts.Add(selected.PlacementSummary);
            if (selected.Entry?.BowMargin is { } selectedBow)
                Readouts.Add($"{selected.DisplayName} is foremost: {Format(selectedBow)} to the bow end.");
            if (selected.Entry?.SternMargin is { } selectedStern)
                Readouts.Add($"{selected.DisplayName} is rearmost: {Format(selectedStern)} to the stern end.");
        }

        foreach (var diagnostic in _ruler.Diagnostics
                     .Where(item => item.Severity != DesignSeverity.Info)
                     .OrderBy(item => item.Severity == DesignSeverity.Error ? 0 : 1)
                     .ThenBy(item => item.Code, StringComparer.Ordinal))
        {
            var line = new BarbetteDiagnosticLine($"{diagnostic.Code}: {diagnostic.Message}",
                diagnostic.Severity == DesignSeverity.Error);
            if (!Diagnostics.Contains(line))
                Diagnostics.Add(line);
        }

        // A frame that could not be resolved for the current revision is a visible blocking reason
        // the first Add created nothing; it is never silently replaced by a zeroed origin.
        foreach (var line in _frameDiagnostics)
            if (!Diagnostics.Contains(line))
                Diagnostics.Add(line);

        // An explicitly rejected clear-diameter entry stays visible until a legal value is committed
        // or the selection changes.
        if (_clearDiameterRejection is { } rejection && !Diagnostics.Contains(rejection))
            Diagnostics.Add(rejection);

        BuildGenerationDiagnostics(document);

        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(HasDiagnostics));
        OnPropertyChanged(nameof(HasGenerationDiagnostics));
    }

    /// <summary>
    /// Projects the revision-matched resolved generation findings the workspace published. A
    /// barbette finding names either the barbette node id or the older barbette id, so both are
    /// matched. Document-global findings are always shown so a catalog fallback is not hidden.
    /// </summary>
    private void BuildGenerationDiagnostics(ShipDocument document)
    {
        GenerationDiagnostics.Clear();
        if (_workspace.ResolvedGenerationRevision != _workspace.Session.Revision)
            return;

        var barbetteIds = document.Barbettes
            .SelectMany(barbette => new[] { barbette.NodeId, barbette.Id })
            .ToHashSet(StringComparer.Ordinal);
        foreach (var diagnostic in _workspace.ResolvedGenerationDiagnostics
                     .Where(item => item.Severity != DesignSeverity.Info)
                     .OrderBy(item => item.Severity == DesignSeverity.Error ? 0 : 1)
                     .ThenBy(item => item.Code, StringComparer.Ordinal))
        {
            var matchesSelected = _selectedBarbette is { } selected &&
                (string.Equals(diagnostic.NodeId, selected.NodeId, StringComparison.Ordinal) ||
                 string.Equals(diagnostic.NodeId, selected.Id, StringComparison.Ordinal));
            var isGlobal = diagnostic.NodeId is null || !barbetteIds.Contains(diagnostic.NodeId);
            if (_selectedBarbette is not null && !matchesSelected && !isGlobal)
                continue;

            var line = new BarbetteDiagnosticLine(FormatGenerationDiagnostic(diagnostic),
                diagnostic.Severity == DesignSeverity.Error);
            if (!GenerationDiagnostics.Contains(line))
                GenerationDiagnostics.Add(line);
        }
    }

    private static string FormatGenerationDiagnostic(DesignDiagnostic diagnostic)
    {
        var values = diagnostic.Requested is null && diagnostic.Realized is null
            ? string.Empty
            : $" (requested {Format(diagnostic.Requested)}, realized {Format(diagnostic.Realized)})";
        return $"{diagnostic.Code}: {diagnostic.Message}{values}";
    }

    /// <summary>
    /// Records the explicit rejection of a typed clear diameter. The domain BAR009 message is
    /// derived from a probe definition so the product line and the persisted/imported validation
    /// never diverge, and the raw typed value is included so nothing is silently rounded or clamped.
    /// </summary>
    internal void ReportClearDiameterRejection(BarbetteEditorItem item, double requested)
    {
        var diagnostic = ClearDiameterDiagnostic(item.Definition, requested);
        var raw = double.IsFinite(requested)
            ? requested.ToString("0.###", CultureInfo.InvariantCulture)
            : requested.ToString(CultureInfo.InvariantCulture);
        _clearDiameterRejection = new BarbetteDiagnosticLine(
            $"{diagnostic.Code}: {diagnostic.Message} Typed value: {raw} m.", IsBlocking: true);
        RaiseReadouts();
    }

    /// <summary>A legal clear diameter (or a new selection) clears the pending rejection line.</summary>
    internal void ClearClearDiameterRejection()
    {
        if (_clearDiameterRejection is null)
            return;
        _clearDiameterRejection = null;
        RaiseReadouts();
    }

    private static DesignDiagnostic ClearDiameterDiagnostic(BarbetteDefinition definition, double requested)
    {
        if (double.IsFinite(requested) && Math.Abs(requested) <= DesignLimits.MaxDesignTwiceMetres / 2d)
        {
            var probe = definition with { ClearDiameter = DesignMeasure.FromMetres(requested) };
            var diagnostic = probe.Validate()
                .FirstOrDefault(item => item.Code == DesignDiagnosticCodes.BarbetteClearDiameterInvalid);
            if (diagnostic is not null)
                return diagnostic;
        }

        return new DesignDiagnostic(DesignDiagnosticCodes.BarbetteClearDiameterInvalid, DesignSeverity.Error,
            $"Barbette '{definition.Id}' clear diameter must be a whole odd number of metres " +
            "(1, 3, 5, 7, 9, 11, … m).",
            definition.Id, nameof(BarbetteDefinition.ClearDiameter), null, null,
            "Choose a whole odd clear diameter; an even or fractional bore is never rounded or clamped.");
    }

    private static string Describe(BarbetteRulerSegmentState state) => state switch
    {
        BarbetteRulerSegmentState.Overlapping => "clear volumes overlap",
        BarbetteRulerSegmentState.BelowMinimumSeparation => "below the 1 m minimum",
        _ => "valid",
    };

    private static string Format(DesignMeasure? value) =>
        value is { } measure ? $"{measure.Metres:0.#} m" : "—";

    private static BarbetteDefinition NewDefinition(ShipDocument document)
    {
        var used = document.Barbettes.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var index = 1;
        while (used.Contains($"barbette-{index}"))
            index++;
        var id = $"barbette-{index}";
        return BarbetteDefinition.Create(id, $"{id}-node", DesignMeasure.FromMetres(7), 4,
            topOffsetMetres: 0, neckClearSizeMetres: 3);
    }

    private static Arrangement AddNode(
        Arrangement arrangement,
        BarbetteDefinition definition,
        DesignMeasure center,
        LayoutDatum datum)
    {
        var extent = MeasureHalfExtent(definition, center, datum, datum.LayoutBowZ)
                     ?? DesignMeasure.FromMetres(definition.ClearDiameter.Metres / 2 + definition.SideArmorThicknessMetres);
        var node = new ArrangementNode(definition.NodeId, ArrangementNodeKind.Barbette, definition.Id,
            extent, [], center);
        return arrangement with { Nodes = arrangement.Nodes.Add(node) };
    }

    private static DesignMeasure? MeasureHalfExtent(
        BarbetteDefinition definition,
        DesignMeasure rulerCenter,
        LayoutDatum datum,
        DesignMeasure layoutBowZ)
    {
        var worldZ = layoutBowZ - rulerCenter;
        var measured = BarbetteGenerator.Measure(definition, datum.CenterPlaneX, worldZ);
        return measured.Measurement?.LongitudinalHalfExtent;
    }

    /// <summary>
    /// Creation placement: start at the snapped ruler midpoint of the current derived frame and walk
    /// outward in one-metre steps until the new cavity clears every existing one. This chooses a
    /// position for a new barbette only; it never relocates an existing requested position.
    /// </summary>
    private DesignMeasure ChooseInitialCenter(
        ShipDocument document,
        BarbetteDefinition definition,
        ResolvedLayoutFrame frame)
    {
        var rulerEnd = frame.SupportedRulerEnd;
        var middle = SnapToCellAnchor(frame, DesignMeasure.FromTwiceMetres(rulerEnd.TwiceMetres / 2));
        if (document.Barbettes.Length == 0)
            return middle;

        for (var step = 0; step <= rulerEnd.Metres; step++)
        {
            foreach (var candidate in step == 0
                         ? new[] { middle }
                         : new[] { middle - DesignMeasure.FromMetres(step), middle + DesignMeasure.FromMetres(step) })
            {
                if (candidate < DesignMeasure.Zero || candidate > rulerEnd)
                    continue;
                if (IsClear(document, definition, candidate, frame))
                    return candidate;
            }
        }

        return middle;
    }

    /// <summary>Snaps a ruler centre to the whole-metre world-Z cell anchor of the current frame.</summary>
    private static DesignMeasure SnapToCellAnchor(ResolvedLayoutFrame frame, DesignMeasure requestedRulerCenter)
    {
        var worldZ = frame.RulerCenterToWorldZ(requestedRulerCenter);
        var wholeMetres = (int)Math.Round(worldZ.Metres, MidpointRounding.AwayFromZero);
        return frame.RulerCenterToWorldZ(DesignMeasure.FromMetres(wholeMetres));
    }

    private static bool IsClear(
        ShipDocument document,
        BarbetteDefinition definition,
        DesignMeasure candidate,
        ResolvedLayoutFrame frame)
    {
        var requests = new List<BarbetteRulerRequest>(document.Barbettes.Length + 1)
        {
            new(definition.Id, definition.NodeId, definition, candidate),
        };
        foreach (var barbette in document.Barbettes)
        {
            var node = document.Arrangement.FindNode(barbette.NodeId);
            if (node?.RequestedCenter is { } center)
                requests.Add(new BarbetteRulerRequest(barbette.Id, barbette.NodeId, barbette, center));
        }

        var model = BarbetteRulerModel.Build(requests, frame.SupportedRulerEnd, frame.BowDatum,
            frame.CenterPlaneX);
        var entry = model.FindEntry(definition.Id);
        return entry is { IsBlocking: false } &&
               model.Segments.All(segment => segment.State == BarbetteRulerSegmentState.Valid);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One barbette projected for the property editor. Every setter writes the shared draft.</summary>
public sealed class BarbetteEditorItem : INotifyPropertyChanged
{
    private readonly BarbetteEditorViewModel _owner;
    private BarbetteDefinition _definition;
    private bool _resetting;

    internal BarbetteEditorItem(
        BarbetteEditorViewModel owner,
        BarbetteDefinition definition,
        int ordinal,
        DesignMeasure rulerCenter,
        BarbetteRulerEntry? entry)
    {
        _owner = owner;
        _definition = definition;
        Ordinal = ordinal;
        RulerCenter = rulerCenter;
        Entry = entry;
        Stacks =
        [
            new BarbetteArmorStackViewModel(this, BarbetteArmorRole.Side, "Side", definition.SideArmor),
            new BarbetteArmorStackViewModel(this, BarbetteArmorRole.Roof, "Roof", definition.RoofArmor),
            new BarbetteArmorStackViewModel(this, BarbetteArmorRole.Bottom, "Bottom", definition.BottomArmor),
            new BarbetteArmorStackViewModel(this, BarbetteArmorRole.Neck, "Neck", definition.NeckArmor),
        ];
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id => _definition.Id;
    public string NodeId => _definition.NodeId;
    public int Ordinal { get; private set; }
    public DesignMeasure RulerCenter { get; private set; }
    public BarbetteRulerEntry? Entry { get; private set; }

    public string DisplayName => $"Barbette {Ordinal} ({Id})";

    public IReadOnlyList<BarbetteArmorStackViewModel> Stacks { get; }

    public IReadOnlyList<int> NeckClearSizeChoices { get; } = BarbetteDefinition.SupportedNeckClearSizesMetres;

    public double ClearDiameterMetres
    {
        get => _definition.ClearDiameter.Metres;
        set
        {
            if (_resetting)
                return;
            if (!TryAcceptClearDiameter(value))
            {
                // Never round, clamp or mutate the definition: the raw typed value is rejected with
                // the domain BAR009 line, and the control is told to restore the last legal value.
                _owner.ReportClearDiameterRejection(this, value);
                OnPropertyChanged(nameof(ClearDiameterMetres));
                return;
            }

            _owner.ClearClearDiameterRejection();
            Commit(_definition with { ClearDiameter = DesignMeasure.FromMetres(value) });
        }
    }

    private static bool TryAcceptClearDiameter(double value)
    {
        if (!double.IsFinite(value) || Math.Abs(value) > DesignLimits.MaxDesignTwiceMetres / 2d)
            return false;
        var measure = DesignMeasure.FromMetres(value);
        // Exact rounding detection: every whole/half metre is exactly representable in binary, so a
        // value the half-metre lattice cannot represent exactly (3.0000001, 4.5, 3.25, …) is
        // rejected outright, never snapped to a nearby legal diameter.
        if (measure.Metres != value)
            return false;
        return BarbetteDefinition.IsSupportedClearDiameter(measure);
    }

    public int ClearDepthMetres
    {
        get => _definition.ClearDepthMetres;
        set
        {
            if (_resetting || value < 1 || value == _definition.ClearDepthMetres)
                return;
            Commit(_definition with { ClearDepthMetres = value });
        }
    }

    public int TopOffsetMetres
    {
        get => _definition.TopOffsetMetres;
        set
        {
            if (_resetting || value < 0 || value == _definition.TopOffsetMetres)
                return;
            Commit(_definition with { TopOffsetMetres = value });
        }
    }

    public int NeckClearSizeMetres
    {
        get => _definition.NeckClearSizeMetres;
        set
        {
            if (_resetting || !BarbetteDefinition.SupportedNeckClearSizesMetres.Contains(value) ||
                value == _definition.NeckClearSizeMetres)
                return;
            Commit(_definition with { NeckClearSizeMetres = value });
        }
    }

    public string PlacementSummary
    {
        get
        {
            var worldZ = Entry?.WorldZCenter ?? DesignMeasure.Zero;
            var extents = Entry is null
                ? string.Empty
                : $" · clear {Entry.ClearBowExtent.Metres:0.#} m bow / {Entry.ClearSternExtent.Metres:0.#} m stern";
            return $"Placed at world Z {worldZ.Metres:0.#} m (ruler {RulerCenter.Metres:0.#} m from the bow datum){extents}.";
        }
    }

    public void AddLayer(BarbetteArmorRole role) =>
        Stacks.First(stack => stack.Role == role).AddLayer();

    public void RemoveLayer(BarbetteArmorRole role) =>
        Stacks.First(stack => stack.Role == role).RemoveLastLayer();

    internal BarbetteDefinition Definition => _definition;

    internal void Reset(BarbetteDefinition definition, int ordinal, DesignMeasure rulerCenter,
        BarbetteRulerEntry? entry)
    {
        Ordinal = ordinal;
        RulerCenter = rulerCenter;
        Entry = entry;
        _resetting = true;
        try
        {
            _definition = definition;
            foreach (var stack in Stacks)
                stack.Reset(StackFor(definition, stack.Role));
        }
        finally
        {
            _resetting = false;
        }
        RaiseAll();
    }

    internal void Commit(BarbetteDefinition next)
    {
        _definition = next;
        _resetting = true;
        try
        {
            RaiseAll();
        }
        finally
        {
            _resetting = false;
        }

        _owner.CommitBarbette(next);
    }

    internal void CommitStack(BarbetteArmorRole role, ArmorLayout layout)
    {
        var next = role switch
        {
            BarbetteArmorRole.Side => _definition with { SideArmor = layout },
            BarbetteArmorRole.Roof => _definition with { RoofArmor = layout },
            BarbetteArmorRole.Bottom => _definition with { BottomArmor = layout },
            _ => _definition with { NeckArmor = layout },
        };
        Commit(next);
    }

    internal static ArmorLayout StackFor(BarbetteDefinition definition, BarbetteArmorRole role) => role switch
    {
        BarbetteArmorRole.Side => definition.SideArmor,
        BarbetteArmorRole.Roof => definition.RoofArmor,
        BarbetteArmorRole.Bottom => definition.BottomArmor,
        _ => definition.NeckArmor,
    };

    private void RaiseAll()
    {
        foreach (var name in new[]
                 {
                     nameof(ClearDiameterMetres), nameof(ClearDepthMetres), nameof(TopOffsetMetres),
                     nameof(NeckClearSizeMetres), nameof(PlacementSummary), nameof(DisplayName),
                 })
            OnPropertyChanged(name);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One independently layered barbette armor system.</summary>
public sealed class BarbetteArmorStackViewModel : INotifyPropertyChanged
{
    private readonly BarbetteEditorItem _owner;
    private ArmorLayout _layout;
    private bool _resetting;

    internal BarbetteArmorStackViewModel(BarbetteEditorItem owner, BarbetteArmorRole role, string label,
        ArmorLayout layout)
    {
        _owner = owner;
        Role = role;
        Label = label;
        _layout = layout;
        Layers = new ObservableCollection<BarbetteArmorLayerViewModel>(
            layout.Layers.Select((layer, index) => new BarbetteArmorLayerViewModel(this, index, layer)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public BarbetteArmorRole Role { get; }
    public string Label { get; }
    public ObservableCollection<BarbetteArmorLayerViewModel> Layers { get; }

    public int ThicknessMetres => _layout.Thickness;

    public string Header => $"{Label} armor · {ThicknessMetres} m";

    public void AddLayer() => Apply(new ArmorLayout(_layout.Layers.Append(new ArmorLayer(MaterialKind.Metal))));

    public void RemoveLastLayer()
    {
        if (_layout.Thickness <= 1)
            return;
        Apply(new ArmorLayout(_layout.Layers.Take(_layout.Thickness - 1)));
    }

    internal void Reset(ArmorLayout layout)
    {
        _resetting = true;
        try
        {
            _layout = layout;
            if (Layers.Count == layout.Thickness)
            {
                for (var index = 0; index < layout.Thickness; index++)
                    Layers[index].Reset(layout.Layers[index]);
            }
            else
            {
                Layers.Clear();
                var index = 0;
                foreach (var layer in layout.Layers)
                    Layers.Add(new BarbetteArmorLayerViewModel(this, index++, layer));
            }
        }
        finally
        {
            _resetting = false;
        }
        RaiseAll();
    }

    internal bool Replace(int index, ArmorLayer layer)
    {
        if (_resetting || index < 0 || index >= _layout.Thickness)
            return false;
        var layers = _layout.Layers.ToArray();
        layers[index] = layer;
        Apply(new ArmorLayout(layers));
        return true;
    }

    private void Apply(ArmorLayout layout)
    {
        if (_resetting || layout == _layout)
            return;
        _layout = layout;
        _owner.CommitStack(Role, layout);
        RaiseAll();
    }

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(ThicknessMetres));
        OnPropertyChanged(nameof(Header));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One one-metre layer inside a barbette armor stack.</summary>
public sealed class BarbetteArmorLayerViewModel : INotifyPropertyChanged
{
    private readonly BarbetteArmorStackViewModel _owner;
    private ArmorLayer _layer;

    internal BarbetteArmorLayerViewModel(BarbetteArmorStackViewModel owner, int index, ArmorLayer layer)
    {
        _owner = owner;
        Index = index;
        _layer = layer;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Index { get; }
    public string Label => $"Layer {Index + 1}";

    public IReadOnlyList<BarbetteEditorChoice<MaterialKind?>> MaterialChoices { get; } = BuildMaterialChoices();

    public BarbetteEditorChoice<MaterialKind?> MaterialChoice
    {
        get => MaterialChoices.First(choice => choice.Value == _layer.Material);
        set
        {
            if (value is null || value.Value == _layer.Material)
                return;
            // Frozen 2.0 barbette armor is Solid only; the material control never changes
            // construction, and no normal UI path can create a pole or beam slope here.
            var previous = _layer;
            var next = new ArmorLayer(value.Value, ArmorConstruction.Solid);
            // CommitBarbette refreshes the parent editor synchronously. Update this layer before
            // entering that path so the binding that initiated the edit always sees its own
            // current value while the parent synchronizes the rest of the draft.
            _layer = next;
            try
            {
                if (!_owner.Replace(Index, next))
                {
                    _layer = previous;
                    return;
                }
            }
            catch
            {
                _layer = previous;
                throw;
            }
            RaiseAll();
        }
    }

    internal void Reset(ArmorLayer layer)
    {
        _layer = layer;
        RaiseAll();
    }

    private static IReadOnlyList<BarbetteEditorChoice<MaterialKind?>> BuildMaterialChoices()
    {
        var choices = new List<BarbetteEditorChoice<MaterialKind?>>();
        foreach (var material in Enum.GetValues<MaterialKind>())
            choices.Add(new BarbetteEditorChoice<MaterialKind?>(material,
                material == MaterialKind.LightweightAlloy ? "Lightweight alloy" : material.ToString()));
        choices.Add(new BarbetteEditorChoice<MaterialKind?>(null, "Air (void)"));
        return choices;
    }

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(MaterialChoice));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
