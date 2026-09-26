using FtdHullGenerator.Domain.Composition;
using FtdHullGenerator.Domain.Design;

namespace FtdHullGenerator.Domain.Components;

/// <summary>One integer X/Z cell in a barbette's planar mask.</summary>
public readonly record struct BarbettePlanCell(int X, int Z);

/// <summary>
/// Inclusive voxel-face bounds for a planar barbette mask. Face coordinates use twice-metre
/// design units, so the half-cell faces around integer block anchors remain exact.
/// </summary>
public sealed record BarbettePlanBounds(
    DesignMeasure MinX,
    DesignMeasure MaxX,
    DesignMeasure MinZ,
    DesignMeasure MaxZ)
{
    public DesignMeasure Width => MaxX - MinX;
    public DesignMeasure Length => MaxZ - MinZ;
}

/// <summary>Which independently layered armor system a solid cell belongs to.</summary>
public enum BarbetteArmorRole
{
    Side = 0,
    Roof = 1,
    Bottom = 2,
    Neck = 3,
}

/// <summary>
/// The exact local plan result of conservative circular rasterization plus the concentric square
/// neck. Clear bounds describe usable empty space; exterior bounds describe the derived armored
/// footprint. Armor is never counted inside the clear bounds.
/// </summary>
public sealed record BarbetteMeasurement(
    DesignMeasure CenterX,
    DesignMeasure CenterZ,
    DesignMeasure RequestedClearDiameter,
    int RequestedClearDepthMetres,
    int TopOffsetMetres,
    int NeckClearSizeMetres,
    BarbettePlanBounds ClearBounds,
    BarbettePlanBounds ExteriorBounds,
    BarbettePlanBounds NeckClearBounds,
    BarbettePlanBounds NeckExteriorBounds,
    IReadOnlyList<BarbettePlanCell> ClearCells,
    IReadOnlyList<BarbettePlanCell> SideArmorCells,
    IReadOnlyList<BarbettePlanCell> NeckClearCells,
    IReadOnlyList<BarbettePlanCell> NeckArmorCells)
{
    public DesignMeasure RealizedClearWidth => ClearBounds.Width;
    public DesignMeasure RealizedClearLength => ClearBounds.Length;
    public DesignMeasure RealizedExteriorWidth => ExteriorBounds.Width;
    public DesignMeasure RealizedExteriorLength => ExteriorBounds.Length;
    public DesignMeasure RealizedNeckClearWidth => NeckClearBounds.Width;
    public DesignMeasure RealizedNeckClearLength => NeckClearBounds.Length;
    public DesignMeasure RealizedNeckExteriorWidth => NeckExteriorBounds.Width;
    public DesignMeasure RealizedNeckExteriorLength => NeckExteriorBounds.Length;

    /// <summary>The measured longitudinal half-extent of the protected clear volume, for the ruler.</summary>
    public DesignMeasure ClearLongitudinalHalfExtent => DesignMeasure.Max(
        CenterZ - ClearBounds.MinZ,
        ClearBounds.MaxZ - CenterZ);

    /// <summary>The measured longitudinal half-extent of the armored exterior footprint.</summary>
    public DesignMeasure LongitudinalHalfExtent => DesignMeasure.Max(
        CenterZ - ExteriorBounds.MinZ,
        ExteriorBounds.MaxZ - CenterZ);
}

/// <summary>
/// The deterministic vertical placement of one local barbette. <see cref="ReferenceDeckY"/> is the
/// ship-wide reference deck plane expressed as the lowest reference deck-skin cell; zero top offset
/// therefore puts the roof top cell immediately beneath that deck skin. Clear depth is measured from
/// the underside of the roof to the top face of the flat protected floor and never includes armor.
/// </summary>
public sealed record BarbetteVerticalLayout(
    int ReferenceDeckY,
    int TopOffsetMetres,
    int RoofTopY,
    int RoofBottomY,
    int ClearTopY,
    int FloorTopY,
    int BottomArmorBottomY,
    int NeckBottomY,
    int NeckTopY,
    int RoofThicknessMetres,
    int SideThicknessMetres,
    int BottomThicknessMetres,
    int NeckThicknessMetres,
    int RequestedClearDepthMetres)
{
    /// <summary>The realized flat clear cavity height, roof underside to protected floor top.</summary>
    public int RealizedClearDepthMetres => ClearTopY - FloorTopY;

    /// <summary>The highest clear cavity cell; the roof underside is the face above it.</summary>
    public int ClearVolumeMaxY => ClearTopY;

    /// <summary>The lowest clear cavity cell; the protected floor top is the face below it.</summary>
    public int ClearVolumeMinY => FloorTopY + 1;
}

/// <summary>A new physical cube owned by one independent barbette armor system.</summary>
public sealed record BarbetteSolidIntent(
    string OwnerId,
    HullCell Cell,
    MaterialKind Material,
    BarbetteArmorRole Role,
    int LayerIndex);

/// <summary>A cell that must remain empty for the protected clear volume or a deliberate armor air gap.</summary>
public sealed record BarbetteVoidIntent(
    string OwnerId,
    HullCell Cell);

/// <summary>
/// A narrowly authorized cut through deck-owned structural armor where the concentric square neck
/// passes through the deck skin. This is the local neck-opening intent; hull/deck reconciliation and
/// exterior-skin protection belong to the ownership stage.
/// </summary>
public sealed record BarbetteDeckCutIntent(
    string OwnerId,
    HullCell Cell,
    MaterialKind Material,
    int ArmorDepth);

/// <summary>
/// Requested versus realized armor layers after the evaluated-hull ownership pass. Realized never
/// exceeds requested. Side armor now truncates per cell and per layer, outermost-first: a locally
/// truncated outer layer is dropped without dropping any inner mandatory cell, and an outer cell
/// never survives where its inward support was dropped. <see cref="RealizedSideLayers"/> is the
/// boundary-complete scalar summary: the mandatory innermost layer counts when every position is an
/// emitted side-armor cell or a retained protected-skin substitution, and each optional outer layer
/// counts only when every position is an emitted side-armor cell. It is not itself the placement
/// truth. Bottom armor keeps the whole-layer outermost-first rule.
/// </summary>
public sealed record BarbetteArmorRealization(
    int RequestedSideLayers,
    int RealizedSideLayers,
    int RequestedBottomLayers,
    int RealizedBottomLayers)
{
    /// <summary>
    /// True when the hull boundary left any requested side layer incomplete (including a layer that
    /// is only locally truncated, or one satisfied by a retained protected-skin boundary).
    /// </summary>
    public bool SideTruncated => RealizedSideLayers < RequestedSideLayers;

    /// <summary>True when the hull boundary forced the outermost bottom layers off.</summary>
    public bool BottomTruncated => RealizedBottomLayers < RequestedBottomLayers;

    /// <summary>True when any requested armor layer was truncated.</summary>
    public bool AnyTruncated => SideTruncated || BottomTruncated;
}

/// <summary>Fail-closed memory limits for one pure barbette generation.</summary>
public sealed record BarbetteGenerationLimits(
    int MaxPlanCells = 250_000,
    int MaxOutputIntents = 1_000_000)
{
    public static BarbetteGenerationLimits Default { get; } = new();
}

/// <summary>A pure mask/footprint measurement, before any vertical or hull support is consulted.</summary>
public sealed record BarbetteMeasurementResult(
    BarbetteMeasurement? Measurement,
    IReadOnlyList<DesignDiagnostic> Diagnostics)
{
    public bool IsValid => Measurement is not null && !Diagnostics.HasErrors();
}

/// <summary>
/// Atomic local component output. Invalid requests retain their measurement when one could be
/// computed, but never expose partial solid, void or deck-cut intent.
/// </summary>
public sealed record BarbetteGenerationResult(
    BarbetteMeasurement? Measurement,
    BarbetteVerticalLayout? Layout,
    IReadOnlyList<BarbetteSolidIntent> Solids,
    IReadOnlyList<BarbetteVoidIntent> RequiredVoids,
    IReadOnlyList<BarbetteDeckCutIntent> AuthorizedDeckCuts,
    IReadOnlyList<DesignDiagnostic> Diagnostics)
{
    public bool IsValid => Measurement is not null && Layout is not null && !Diagnostics.HasErrors();

    /// <summary>
    /// Requested versus realized armor layers once the evaluated hull boundary has been applied, or
    /// <c>null</c> for a pure local generation with no hull ownership pass.
    /// </summary>
    public BarbetteArmorRealization? ArmorRealization { get; init; }

    /// <summary>
    /// Ordinary interior hull-armor cells this barbette supersedes because the barbette is locally
    /// dominant. Protected side/bottom skin and deliberate armor air never appear here; deck cells
    /// removed by the neck aperture travel through <see cref="AuthorizedDeckCuts"/> instead.
    /// </summary>
    public IReadOnlyList<HullCell> SupersededArmorCells { get; init; } = [];

    /// <summary>
    /// The world-space envelope of the protected clear volume, or <c>null</c> whenever the result is
    /// invalid or has no layout. An invalid result never exposes bounds even when its measurement and
    /// layout were retained for diagnostics, so no rejected request can expand downstream
    /// reservations. Later ownership/ruler work consumes this instead of re-deriving the clear bounds.
    /// </summary>
    public DesignBounds? ClearVolumeBounds => !IsValid || Measurement is null || Layout is null
        ? null
        : new DesignBounds(
            Measurement.ClearBounds.MinX,
            Measurement.ClearBounds.MaxX,
            DesignMeasure.FromCellAnchor(Layout.ClearVolumeMinY),
            DesignMeasure.FromCellAnchor(Layout.ClearVolumeMaxY),
            Measurement.ClearBounds.MinZ,
            Measurement.ClearBounds.MaxZ);
}

/// <summary>Stable task-local barbette diagnostics. BAR001-BAR015 live in <see cref="DesignDiagnosticCodes"/>.</summary>
public static class BarbetteDiagnosticCodes
{
    public const string OddHullWidthRequired = DesignDiagnosticCodes.BarbetteOddHullWidthRequired;
    public const string EvenWidthCenterline = DesignDiagnosticCodes.BarbetteEvenWidthCenterline;

    /// <summary>
    /// Two protected clear volumes overlap in the placement/ruler pass (BAR016, blocking). Distinct
    /// from <see cref="ClearVolumeOverlap"/> (BAR211), which the ownership pass raises.
    /// </summary>
    public const string PlacementClearVolumeOverlap = DesignDiagnosticCodes.BarbetteClearVolumeOverlap;

    /// <summary>Adjacent protected clear volumes are closer than the one-metre separator (blocking).</summary>
    public const string MinimumClearSeparation = DesignDiagnosticCodes.BarbetteMinimumClearSeparation;

    /// <summary>A protected clear volume extends past the bow or stern ruler end (blocking).</summary>
    public const string ClearVolumeOutsideRuler = DesignDiagnosticCodes.BarbetteClearVolumeOutsideRuler;

    public const string InvalidBudget = "BAR101";
    public const string PlanBudgetExceeded = "BAR102";
    public const string OutputBudgetExceeded = "BAR103";
    public const string DegenerateMask = "BAR104";
    public const string RingDisconnected = "BAR105";
    public const string CenterlineMismatch = "BAR106";
    public const string CenterParityMismatch = "BAR107";

    // Retained historical codes. The frozen clear-volume model no longer produces them, but they
    // remain named so an older persisted or reviewed finding cannot be silently reinterpreted.
    public const string OuterEnvelopeTooSmall = "BAR108";
    public const string DeckUnavailable = "BAR109";
    public const string DeckNotFlat = "BAR110";
    public const string SupportMissing = "BAR111";
    public const string WellHitsProtectedCell = "BAR112";
    public const string WellLeavesHull = "BAR113";
    public const string ShellCollision = "BAR114";
    public const string HullContextInvalid = "BAR115";
    public const string WellDoesNotReachCavity = "BAR116";

    /// <summary>The requested neck top plane is not above the barbette roof.</summary>
    public const string NeckTopBelowRoof = "BAR117";

    // BAR02 ownership/diagnostic codes. Red codes make the protected design impossible; yellow
    // codes describe a usable result that had to compromise a requested non-clearance value.
    /// <summary>Red: the protected clear cavity would consume protected exterior side/bottom skin.</summary>
    public const string ClearCavityExteriorBoundary = "BAR201";

    /// <summary>Red: the protected clear cavity would leave the evaluated hull solid.</summary>
    public const string ClearCavityOutsideHull = "BAR202";

    /// <summary>Red: the protected clear cavity would consume a deliberate armor-air reservation.</summary>
    public const string ClearCavityReservedAir = "BAR203";

    /// <summary>Red: not even one metre of protected clear cavity can exist inside the hull.</summary>
    public const string ClearCavityImpossible = "BAR204";

    /// <summary>Yellow: the deepest valid clear cavity is shallower than requested.</summary>
    public const string ClearDepthShortfall = "BAR205";

    /// <summary>Yellow: the outermost requested side armor layers do not fit inside the hull.</summary>
    public const string SideArmorTruncated = "BAR206";

    /// <summary>Yellow: the outermost requested bottom armor layers do not fit inside the hull.</summary>
    public const string BottomArmorTruncated = "BAR207";

    /// <summary>Red: the main roof would protrude through a lower local exterior surface.</summary>
    public const string RoofAboveLocalDeck = "BAR208";

    /// <summary>Red: a mandatory roof armor cell is outside the hull, on skin or in armor air.</summary>
    public const string RoofObstructed = "BAR209";

    /// <summary>Red: the complete square neck prism cannot pass through the hull/deck as requested.</summary>
    public const string NeckObstructed = "BAR210";

    /// <summary>Red: two protected clear volumes overlap.</summary>
    public const string ClearVolumeOverlap = "BAR211";

    /// <summary>Red: two protected clear volumes are closer than the required one-metre separator.</summary>
    public const string ClearVolumeSeparation = "BAR212";

    /// <summary>Yellow: overlapping barbette armor merged onto one deterministic owner.</summary>
    public const string ArmorOwnershipMerged = "BAR213";

    /// <summary>Red: one barbette's armor would penetrate another barbette's protected clear volume.</summary>
    public const string ArmorPenetratesClearVolume = "BAR214";

    /// <summary>
    /// Red: no valid configuration can realize at least one metre of protected clear cavity together
    /// with the mandatory innermost bottom-armor floor. The requested barbette is rejected atomically
    /// rather than emitted with a missing floor.
    /// </summary>
    public const string BottomArmorFloorImpossible = "BAR215";

    /// <summary>
    /// Red: a mandatory innermost side-boundary position is neither usable hull interior for barbette
    /// side armor nor a retained protected side/bottom hull-skin cell. The requested barbette is
    /// rejected atomically rather than emitted with a protected clear cavity whose side boundary is
    /// open, outside the hull or deliberately reserved as armor air. The clear diameter and the
    /// requested placement are never changed.
    /// </summary>
    public const string MandatorySideBoundaryImpossible = "BAR216";
}
