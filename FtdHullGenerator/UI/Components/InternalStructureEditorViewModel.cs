using System.Collections.ObjectModel;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Components;
using FtdHullGenerator.Domain.Design;
using FtdHullGenerator.Geometry.Components;
using FtdHullGenerator.UI.Workspace;

namespace FtdHullGenerator.UI.Components;

public sealed record InternalEditorChoice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Transaction-backed projection of the three immutable internal-plane families. Every setter
/// updates the one Workspace draft; no control owns a second copy of project intent.
/// </summary>
public sealed class InternalStructureEditorViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly WorkspaceViewModel _workspace;
    private InternalFamilyEditorViewModel? _selectedFamily;
    private InternalStructureGenerationResult? _realization;
    private ImmutableArray<DesignDiagnostic> _generationDiagnostics = [];
    private bool _updatingDocument;

    public InternalStructureEditorViewModel(WorkspaceViewModel workspace)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        Families = new ObservableCollection<InternalFamilyEditorViewModel>(
            Enum.GetValues<InternalPlaneFamily>().Select(family =>
                new InternalFamilyEditorViewModel(this, RequiredFamily(workspace.Document, family))));
        _selectedFamily = Families[0];
        _workspace.DocumentChanged += WorkspaceDocumentChanged;
        RefreshDiagnostics();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<InternalFamilyEditorViewModel> Families { get; }

    internal bool SingleBlockCenterline => _workspace.Document.Hull.HasSingleBlockCenterline;

    public InternalFamilyEditorViewModel? SelectedFamily
    {
        get => _selectedFamily;
        set
        {
            if (ReferenceEquals(_selectedFamily, value))
                return;
            _selectedFamily = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedFamilyGuide));
            RefreshDiagnostics();
        }
    }

    public string SelectedFamilyGuide => SelectedFamily?.Family switch
    {
        InternalPlaneFamily.LongitudinalBulkhead =>
            "Lengthwise walls divide the hull into side-by-side compartments, in matching port and starboard pairs.",
        InternalPlaneFamily.InternalDeck =>
            "Horizontal floors repeat upward from the bottom of the hull. Clear spacing is the room height between floors.",
        InternalPlaneFamily.TransverseBulkhead =>
            "Crosswise walls divide the hull into compartments from stern to bow.",
        _ => "Select an internal plane family.",
    };

    public ObservableCollection<string> SelectedDiagnostics { get; } = [];

    public bool HasDiagnostics => SelectedDiagnostics.Count > 0;

    public void SetGenerationResult(InternalStructureGenerationResult? result)
    {
        _realization = result;
        _generationDiagnostics = result?.Diagnostics ?? [];
        RefreshRealization();
        RefreshDiagnostics();
    }

    public void SetGenerationDiagnostics(IEnumerable<DesignDiagnostic>? diagnostics)
    {
        _realization = null;
        _generationDiagnostics = diagnostics?.ToImmutableArray() ?? [];
        RefreshRealization();
        RefreshDiagnostics();
    }

    public void Dispose() => _workspace.DocumentChanged -= WorkspaceDocumentChanged;

    internal void CommitFamily(InternalStructureFamily family)
    {
        _updatingDocument = true;
        try
        {
            _workspace.UpdateDraft(document => document with
            {
                Internals = document.Internals with
                {
                    Families = document.Internals.Families
                        .Select(existing => existing.Family == family.Family ? family : existing)
                        .ToImmutableArray(),
                },
            });
        }
        finally
        {
            _updatingDocument = false;
        }
        _realization = null;
        _generationDiagnostics = [];
        RefreshRealization();
        RefreshDiagnostics();
    }

    private void WorkspaceDocumentChanged(object? sender, EventArgs eventArgs)
    {
        if (_updatingDocument)
            return;
        foreach (var editor in Families)
            editor.Reset(RequiredFamily(_workspace.Document, editor.Family));
        _realization = null;
        _generationDiagnostics = [];
        RefreshRealization();
        RefreshDiagnostics();
    }

    private void RefreshRealization()
    {
        foreach (var editor in Families)
        {
            var planes = _realization?.Planes.Where(plane => plane.Family == editor.Family).ToArray() ?? [];
            editor.SetRealization(_realization is not null, planes.Count(plane => plane.RealizedCellCount > 0),
                planes.Sum(plane => plane.CavityClippedCellCount),
                planes.Sum(plane => plane.RequiredVoidCellCount));
        }
    }

    private void RefreshDiagnostics()
    {
        SelectedDiagnostics.Clear();
        if (SelectedFamily is not { } selected)
        {
            OnPropertyChanged(nameof(HasDiagnostics));
            return;
        }

        var family = selected.Snapshot;
        foreach (var diagnostic in family.Validate().Where(item => item.Severity != DesignSeverity.Info))
            AddDiagnostic(diagnostic);

        if (family.Enabled && family.Family == InternalPlaneFamily.LongitudinalBulkhead &&
            family.IncludeCentralPlane &&
            (_workspace.Document.Hull.HasSingleBlockCenterline != ((family.Thickness & 1) != 0)))
        {
            var compatible = family.Thickness == 1 ? 2 : family.Thickness - 1;
            SelectedDiagnostics.Add(
                $"{InternalStructureDiagnosticCodes.CenterSlabParity}: A {family.Thickness}-cell centre slab " +
                $"is not representable on this hull width. Use {compatible} cells or omit the centre slab.");
        }

        foreach (var diagnostic in _generationDiagnostics.Where(item =>
                     item.Code.StartsWith("INT", StringComparison.Ordinal) &&
                     (item.NodeId is null || item.NodeId.Contains(FamilyKey(selected.Family),
                         StringComparison.OrdinalIgnoreCase))))
            AddDiagnostic(diagnostic);

        if (SelectedDiagnostics.Count == 0)
            SelectedDiagnostics.Add(_realization is null
                ? "Apply the draft to resolve realized plane counts and clipping."
                : "No diagnostics for this family.");
        OnPropertyChanged(nameof(HasDiagnostics));
    }

    private void AddDiagnostic(DesignDiagnostic diagnostic)
    {
        var text = $"{diagnostic.Code}: {diagnostic.Message}";
        if (!SelectedDiagnostics.Contains(text, StringComparer.Ordinal))
            SelectedDiagnostics.Add(text);
    }

    private static string FamilyKey(InternalPlaneFamily family) => family switch
    {
        InternalPlaneFamily.LongitudinalBulkhead => "longitudinal",
        InternalPlaneFamily.InternalDeck => "deck",
        InternalPlaneFamily.TransverseBulkhead => "transverse",
        _ => "unknown",
    };

    private static InternalStructureFamily RequiredFamily(
        FtdHullGenerator.Domain.Projects.ShipDocument document,
        InternalPlaneFamily family) => document.Internals.Find(family) ??
                                        InternalStructureFamily.Disabled(family);

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class InternalFamilyEditorViewModel : INotifyPropertyChanged
{
    private readonly InternalStructureEditorViewModel _owner;
    private InternalStructureFamily _family;
    private int _realizedCount;
    private int _cavityClippedCount;
    private int _wellClippedCount;
    private bool _resetting;
    private bool _hasRealization;

    internal InternalFamilyEditorViewModel(
        InternalStructureEditorViewModel owner,
        InternalStructureFamily family)
    {
        _owner = owner;
        _family = family;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public InternalPlaneFamily Family => _family.Family;
    internal InternalStructureFamily Snapshot => _family;

    public string DisplayName => Family switch
    {
        InternalPlaneFamily.LongitudinalBulkhead => "Longitudinal bulkheads",
        InternalPlaneFamily.InternalDeck => "Internal decks",
        InternalPlaneFamily.TransverseBulkhead => "Transverse bulkheads",
        _ => Family.ToString(),
    };

    public bool Enabled
    {
        get => _family.Enabled;
        set
        {
            if (_resetting || value == Enabled) return;
            _owner.SelectedFamily = this;
            Change(value ? SimpleInternalLayout.Create(_family with { Enabled = true }, PreferredGap)
                : _family with { Enabled = false });
        }
    }

    public IReadOnlyList<int> ClearGapChoices => SimpleInternalLayout.GapChoices(Family, _owner.SingleBlockCenterline);
    private int PreferredGap => ClearGapChoices.MinBy(gap => Math.Abs(gap -
        (_family.Count == 0 ? Family == InternalPlaneFamily.InternalDeck ? 3 : 7 :
            Math.Max(2, _family.EffectivePitch.Metres - Thickness))));
    public int? ClearGap
    {
        get => UsesSimpleLayout ? (int)SpacingMetres : null;
        set
        {
            if (_resetting || value is not { } gap || !ClearGapChoices.Contains(gap)) return;
            Change(SimpleInternalLayout.Create(_family, gap));
        }
    }
    public bool UsesSimpleLayout => ClearGapChoices.Contains((int)SpacingMetres) &&
        _family == SimpleInternalLayout.Create(_family, (int)SpacingMetres);
    public bool HasCustomLayout => Enabled && !UsesSimpleLayout;
    public string LayoutSummary => HasCustomLayout
        ? "Saved custom layout. Choose clear spacing or use automatic layout to replace its placement settings."
        : "1 m thick · automatic placement · up to 64 planes that fit. Hull armor and reserved openings stay clear.";
    public void UseAutomaticLayout() => Change(SimpleInternalLayout.Create(_family, PreferredGap));
    public int Thickness { get => _family.Thickness; set => Change(_family with { Thickness = value }); }
    public MaterialKind Material { get => _family.Material; set => Change(_family with { Material = value }); }
    public double SpacingMetres { get => _family.Spacing.Metres; set => Change(_family with { Spacing = DesignMeasure.FromMetres(value) }); }
    public InternalSpacingKind SpacingKind { get => _family.SpacingKind; set => Change(_family with { SpacingKind = value }); }
    public int Count { get => _family.Count; set => Change(_family with { Count = value }); }
    public InternalPlaneCountMode CountMode { get => _family.CountMode; set => Change(_family with { CountMode = value }); }
    public double OffsetMetres { get => _family.Offset.Metres; set => Change(_family with { Offset = DesignMeasure.FromMetres(value) }); }
    public InternalPlaneDatum Datum { get => _family.EffectiveDatum; set => Change(_family with { Datum = value }); }
    public InternalRepeatDirection Direction { get => _family.EffectiveDirection; set => Change(_family with { Direction = value }); }
    public bool IncludeCentralPlane { get => _family.IncludeCentralPlane; set => Change(_family with { IncludeCentralPlane = value }); }

    public bool IsLongitudinal => Family == InternalPlaneFamily.LongitudinalBulkhead;
    public bool HasDirectionalControls => !IsLongitudinal;
    public string EffectiveSpacing => SpacingKind == InternalSpacingKind.ClearCompartmentGap
        ? $"{SpacingMetres} m clear · {_family.EffectivePitch.Metres:0.#} m centre pitch"
        : $"{SpacingMetres} m centre pitch · {Math.Max(0, _family.EffectivePitch.Metres - Thickness):0.#} m clear";
    public string RealizedSummary => Enabled
        ? !_hasRealization ? "Apply to preview this layout."
        : _realizedCount == 0 ? "No planes fit in the available interior. Try smaller spacing or a larger hull."
        : $"Realized: {_realizedCount} plane(s) · clipped {_cavityClippedCount:N0} cavity / {_wellClippedCount:N0} reserved cells"
        : "Disabled · saved settings are retained.";

    public IReadOnlyList<int> ThicknessChoices => RangeWithCurrent(1, 12, Thickness);
    public IReadOnlyList<int> CountChoices => RangeWithCurrent(1, DesignLimits.MaxInternalPlanesPerDocument, Count);
    public IReadOnlyList<InternalEditorChoice<MaterialKind>> MaterialChoices { get; } =
        Enum.GetValues<MaterialKind>().Select(value => new InternalEditorChoice<MaterialKind>(value,
            value == MaterialKind.LightweightAlloy ? "Lightweight alloy" : value.ToString())).ToArray();
    public IReadOnlyList<InternalEditorChoice<InternalSpacingKind>> SpacingKindChoices { get; } =
    [
        new(InternalSpacingKind.CenterPitch, "Centre pitch"),
        new(InternalSpacingKind.ClearCompartmentGap, "Clear compartment gap"),
    ];
    public IReadOnlyList<InternalEditorChoice<InternalPlaneCountMode>> CountModeChoices { get; } =
    [
        new(InternalPlaneCountMode.FixedCount, "Fixed count"),
        new(InternalPlaneCountMode.RepeatToBoundary, "Repeat to boundary"),
    ];
    public IReadOnlyList<InternalEditorChoice<InternalPlaneDatum>> DatumChoices => IsLongitudinal
        ? [new(InternalPlaneDatum.CenterPlane, "Hull centre plane")]
        :
        [
            new(InternalPlaneDatum.HullOrigin, "Hull origin"),
            new(InternalPlaneDatum.MinimumHullExtent, "Minimum hull extent"),
            new(InternalPlaneDatum.MaximumHullExtent, "Maximum hull extent"),
        ];
    public IReadOnlyList<InternalEditorChoice<InternalRepeatDirection>> DirectionChoices { get; } =
    [
        new(InternalRepeatDirection.Positive, "Positive axis"),
        new(InternalRepeatDirection.Negative, "Negative axis"),
        new(InternalRepeatDirection.Both, "Both directions"),
    ];

    internal void Reset(InternalStructureFamily family)
    {
        _resetting = true;
        _family = family;
        RaiseAll();
        _resetting = false;
    }

    internal void SetRealization(bool hasRealization, int count, int cavityClipped, int wellClipped)
    {
        _hasRealization = hasRealization;
        _realizedCount = count;
        _cavityClippedCount = cavityClipped;
        _wellClippedCount = wellClipped;
        OnPropertyChanged(nameof(RealizedSummary));
    }

    private void Change(InternalStructureFamily next)
    {
        if (_resetting || _family == next)
            return;
        _family = next;
        if (!_resetting)
            _owner.CommitFamily(next);
        _resetting = true;
        try { RaiseAll(); }
        finally { _resetting = false; }
    }

    private void RaiseAll()
    {
        foreach (var name in new[]
                 {
                     nameof(Enabled), nameof(Thickness), nameof(Material), nameof(SpacingMetres),
                     nameof(SpacingKind), nameof(Count), nameof(CountMode), nameof(OffsetMetres),
                     nameof(Datum), nameof(Direction), nameof(IncludeCentralPlane),
                     nameof(EffectiveSpacing), nameof(RealizedSummary), nameof(ThicknessChoices),
                     nameof(CountChoices),
                     nameof(ClearGapChoices), nameof(ClearGap), nameof(UsesSimpleLayout),
                     nameof(HasCustomLayout), nameof(LayoutSummary),
                 })
            OnPropertyChanged(name);
    }

    private static IReadOnlyList<int> RangeWithCurrent(int minimum, int maximum, int current) =>
        Enumerable.Range(minimum, maximum - minimum + 1).Append(current).Distinct().Order().ToArray();

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
