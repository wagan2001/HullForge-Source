using System.Collections.Immutable;
using System.Text.Json.Serialization;
using FtdHullGenerator.Domain.Design;

namespace FtdHullGenerator.Domain.Components;

/// <summary>The axis-normal family of a solid internal structural plane.</summary>
public enum InternalPlaneFamily
{
    /// <summary>YZ planes running fore-and-aft, repeated across X.</summary>
    LongitudinalBulkhead = 0,

    /// <summary>XZ decks, repeated vertically along Y.</summary>
    InternalDeck = 1,

    /// <summary>XY bulkheads, repeated fore-and-aft along Z.</summary>
    TransverseBulkhead = 2,
}

/// <summary>How <see cref="InternalStructureFamily.Spacing"/> is interpreted.</summary>
public enum InternalSpacingKind
{
    /// <summary>Distance between the centres of adjacent slabs.</summary>
    CenterPitch = 0,

    /// <summary>Clear face-to-face compartment gap; slab thickness is added to obtain pitch.</summary>
    ClearCompartmentGap = 1,
}

/// <summary>A stable base coordinate for a repeated plane family.</summary>
public enum InternalPlaneDatum
{
    /// <summary>The unchanged Hull Forge world origin on the relevant axis.</summary>
    HullOrigin = 0,

    /// <summary>The real lateral hull centre plane. Valid only for longitudinal bulkheads.</summary>
    CenterPlane = 1,

    /// <summary>The base hull's minimum occupied coordinate on the repetition axis.</summary>
    MinimumHullExtent = 2,

    /// <summary>The base hull's maximum occupied coordinate on the repetition axis.</summary>
    MaximumHullExtent = 3,
}

/// <summary>Direction in which planes repeat away from their datum plus offset.</summary>
public enum InternalRepeatDirection
{
    Positive = 0,
    Negative = 1,
    Both = 2,
}

/// <summary>Whether a family has an exact requested count or repeats to its bounded interval.</summary>
public enum InternalPlaneCountMode
{
    FixedCount = 0,

    /// <summary>
    /// Repeat only while complete slabs fit the bounded interval; <see cref="InternalStructureFamily.Count"/>
    /// remains the hard maximum so a request is bounded before rasterization.
    /// </summary>
    RepeatToBoundary = 1,
}

/// <summary>Stable B01 diagnostics. Codes are append-only under contract C10.</summary>
public static class InternalStructureDiagnosticCodes
{
    public const string CenterSlabParity = "INT005";
    public const string InvalidDatum = "INT006";
    public const string PlaneBudgetExceeded = "INT007";
    public const string CandidateBudgetExceeded = "INT008";
    public const string PlaneOverlap = "INT009";
    public const string RequiredVoidInvalid = "INT010";
    public const string UnsupportedRegion = "INT011";
    public const string FixedCountDoesNotFit = "INT012";
    public const string JunctionPriorityInvalid = "INT013";
    public const string ExtentInvalid = "INT014";
    public const string FamilyConfigurationInvalid = "INT015";
}

/// <summary>First-pass internal-engine bounds. These are product safety limits, not FtD limits.</summary>
public static class InternalStructureLimits
{
    public const int MaxPlaneThickness = DesignLimits.MaxDesignTwiceMetres / 2;
}

/// <summary>
/// One internal family's immutable design intent. The positional members retain the F01/F03
/// compatibility surface; B01's additional settings are init-only so existing callers keep the
/// original disabled/default meaning until project/UI adapters opt in explicitly.
/// </summary>
public sealed record InternalStructureFamily(
    InternalPlaneFamily Family,
    bool Enabled,
    int Thickness,
    MaterialKind Material,
    DesignMeasure Spacing,
    int Count,
    DesignMeasure Offset,
    bool MirrorSymmetry)
{
    /// <summary>Whether spacing is centre pitch or clear face-to-face space.</summary>
    public InternalSpacingKind SpacingKind { get; init; } = InternalSpacingKind.CenterPitch;

    /// <summary>
    /// Stable datum on the repetition axis. Longitudinal families must use the real centre plane;
    /// the other families may use the origin or either base-hull extent.
    /// </summary>
    public InternalPlaneDatum? Datum { get; init; }

    /// <summary>Direction of repetition for decks/transverse walls. Longitudinal pairs use Both.</summary>
    public InternalRepeatDirection? Direction { get; init; }

    /// <summary>Exact requested count, or repeat until the bounded interval is exhausted.</summary>
    public InternalPlaneCountMode CountMode { get; init; } = InternalPlaneCountMode.FixedCount;

    /// <summary>
    /// For longitudinal bulkheads, request the exactly centred slab before mirrored pairs.
    /// It is never silently shifted when lattice parity makes it unrepresentable.
    /// </summary>
    public bool IncludeCentralPlane { get; init; }

    /// <summary>
    /// Optional closed centre-coordinate interval on the repetition axis. Null uses the base hull
    /// bounds. It constrains plane centres; every slab is still clipped to actual usable cavity.
    /// </summary>
    public DesignSpan? RepetitionExtent { get; init; }

    public static InternalStructureFamily Disabled(InternalPlaneFamily family) => new(
        family, false, 1, MaterialKind.Metal, DesignMeasure.FromMetres(8), 0,
        DesignMeasure.Zero, true);

    /// <summary>Compatibility default for F01/F03 documents written before B01 added datums.</summary>
    [JsonIgnore]
    public InternalPlaneDatum EffectiveDatum => Datum ??
        (Family == InternalPlaneFamily.LongitudinalBulkhead
            ? InternalPlaneDatum.CenterPlane
            : InternalPlaneDatum.HullOrigin);

    /// <summary>Compatibility default for F01/F03 documents written before B01 added direction.</summary>
    [JsonIgnore]
    public InternalRepeatDirection EffectiveDirection => Direction ??
        (Family == InternalPlaneFamily.LongitudinalBulkhead
            ? InternalRepeatDirection.Both
            : InternalRepeatDirection.Positive);

    /// <summary>The centre-to-centre pitch after applying the spacing-kind contract.</summary>
    [JsonIgnore]
    public DesignMeasure EffectivePitch => SpacingKind == InternalSpacingKind.ClearCompartmentGap
        ? Spacing + DesignMeasure.FromMetres(Thickness)
        : Spacing;

    public IEnumerable<DesignDiagnostic> Validate()
    {
        if (!Enum.IsDefined(Family))
        {
            yield return DesignDiagnostic.Error(DesignDiagnosticCodes.InternalStructureDisabledFamily,
                "An internal family declares an unsupported family kind.");
            yield break;
        }

        if (!Enabled)
        {
            if (Count != 0)
                yield return new DesignDiagnostic(DesignDiagnosticCodes.InternalStructureDisabledFamily,
                    DesignSeverity.Warning,
                    $"The {Family} family is disabled but still requests {Count} plane(s).",
                    Field: nameof(Count));
            yield break;
        }

        var validThickness = Thickness is >= 1 and <= InternalStructureLimits.MaxPlaneThickness;
        if (!validThickness)
            yield return new DesignDiagnostic(DesignDiagnosticCodes.InternalStructureInvalidThickness,
                DesignSeverity.Error,
                $"The {Family} family thickness must be between 1 and " +
                $"{InternalStructureLimits.MaxPlaneThickness} cells.",
                Field: nameof(Thickness),
                SuggestedCorrection: $"Choose 1–{InternalStructureLimits.MaxPlaneThickness} cells.");
        if (!Enum.IsDefined(Material))
            yield return DesignDiagnostic.Error(DesignDiagnosticCodes.InternalStructureMaterialUnsupported,
                $"The {Family} family declares an unsupported material.", field: nameof(Material));
        if (!Enum.IsDefined(SpacingKind) || Datum is { } datum && !Enum.IsDefined(datum) ||
            Direction is { } direction && !Enum.IsDefined(direction) || !Enum.IsDefined(CountMode))
            yield return DesignDiagnostic.Error(InternalStructureDiagnosticCodes.FamilyConfigurationInvalid,
                $"The {Family} family contains an unsupported spacing, datum, direction or count mode.");
        if (!Spacing.IsWithinDesignBounds || !Offset.IsWithinDesignBounds)
            yield return DesignDiagnostic.Error(DesignDiagnosticCodes.MeasureOutOfRange,
                $"The {Family} spacing or offset exceeds the design-coordinate budget.");
        if (Count < 1)
            yield return DesignDiagnostic.Error(DesignDiagnosticCodes.InternalStructureInvalidSpacing,
                $"The enabled {Family} family must request at least one plane or repeat limit.",
                field: nameof(Count));
        if (validThickness && (Count > 1 || CountMode == InternalPlaneCountMode.RepeatToBoundary) &&
            EffectivePitch <= DesignMeasure.Zero)
            yield return DesignDiagnostic.Error(DesignDiagnosticCodes.InternalStructureInvalidSpacing,
                $"The {Family} family needs a positive effective pitch to repeat planes.",
                field: nameof(Spacing));
        if (SpacingKind == InternalSpacingKind.ClearCompartmentGap && Spacing < DesignMeasure.Zero)
            yield return DesignDiagnostic.Error(DesignDiagnosticCodes.InternalStructureInvalidSpacing,
                $"The {Family} family cannot request a negative clear compartment gap.",
                field: nameof(Spacing));
        if (RepetitionExtent is { } extent &&
            (!extent.Start.IsWithinDesignBounds || !extent.End.IsWithinDesignBounds))
            yield return DesignDiagnostic.Error(InternalStructureDiagnosticCodes.ExtentInvalid,
                $"The {Family} repetition extent must stay inside the design-coordinate budget.",
                field: nameof(RepetitionExtent));

        if (Family == InternalPlaneFamily.LongitudinalBulkhead)
        {
            if (!MirrorSymmetry || EffectiveDatum != InternalPlaneDatum.CenterPlane ||
                EffectiveDirection != InternalRepeatDirection.Both)
                yield return new DesignDiagnostic(InternalStructureDiagnosticCodes.FamilyConfigurationInvalid,
                    DesignSeverity.Error,
                    "Longitudinal bulkheads must reflect about the real centre plane.",
                    Field: nameof(MirrorSymmetry),
                    SuggestedCorrection: "Use CenterPlane, Both and mirror symmetry.");
            if (CountMode == InternalPlaneCountMode.FixedCount &&
                ((IncludeCentralPlane && (Count & 1) == 0) || (!IncludeCentralPlane && (Count & 1) != 0)))
                yield return new DesignDiagnostic(InternalStructureDiagnosticCodes.FamilyConfigurationInvalid,
                    DesignSeverity.Error,
                    "A fixed longitudinal count must be odd with a central plane and even without one.",
                    Field: nameof(Count),
                    SuggestedCorrection: IncludeCentralPlane
                        ? "Choose an odd count or omit the central plane."
                        : "Choose an even count or include the central plane.");
        }
        else if (IncludeCentralPlane)
        {
            yield return DesignDiagnostic.Error(InternalStructureDiagnosticCodes.FamilyConfigurationInvalid,
                $"IncludeCentralPlane applies only to longitudinal bulkheads.",
                field: nameof(IncludeCentralPlane));
        }
        else if (EffectiveDatum == InternalPlaneDatum.CenterPlane)
        {
            yield return DesignDiagnostic.Error(InternalStructureDiagnosticCodes.InvalidDatum,
                $"The lateral centre plane is not a valid {Family} datum.", field: nameof(Datum));
        }
    }
}

/// <summary>All three internal families and the persisted mixed-material junction rule.</summary>
public sealed record InternalStructure(ImmutableArray<InternalStructureFamily> Families)
{
    public static ImmutableArray<InternalPlaneFamily> DefaultJunctionPriority { get; } =
    [
        InternalPlaneFamily.TransverseBulkhead,
        InternalPlaneFamily.LongitudinalBulkhead,
        InternalPlaneFamily.InternalDeck,
    ];

    public static InternalStructure Disabled { get; } = new([
        InternalStructureFamily.Disabled(InternalPlaneFamily.LongitudinalBulkhead),
        InternalStructureFamily.Disabled(InternalPlaneFamily.InternalDeck),
        InternalStructureFamily.Disabled(InternalPlaneFamily.TransverseBulkhead),
    ]);

    /// <summary>
    /// Highest-to-lowest material priority at an internal-family junction. It never applies to
    /// hull armor or required voids.
    /// </summary>
    public ImmutableArray<InternalPlaneFamily> JunctionPriority { get; init; } = DefaultJunctionPriority;

    /// <summary>Planes the enabled families request. Compared against the first-pass safety cap.</summary>
    [JsonIgnore]
    public long RequestedPlaneCount =>
        Families.Where(family => family.Enabled).Sum(family => (long)Math.Max(family.Count, 0));

    public InternalStructureFamily? Find(InternalPlaneFamily family) =>
        Families.FirstOrDefault(entry => entry.Family == family);

    public IEnumerable<DesignDiagnostic> Validate()
    {
        var errors = new List<DesignDiagnostic>();
        if (RequestedPlaneCount > DesignLimits.MaxInternalPlanesPerDocument)
            errors.Add(new DesignDiagnostic(DesignDiagnosticCodes.CountLimitExceeded, DesignSeverity.Error,
                $"The document requests {RequestedPlaneCount} internal planes; the first-pass cap is " +
                $"{DesignLimits.MaxInternalPlanesPerDocument}.",
                Field: nameof(RequestedPlaneCount)));

        foreach (var family in Families)
            errors.AddRange(family.Validate());

        foreach (var family in Enum.GetValues<InternalPlaneFamily>())
        {
            var matches = Families.Count(entry => entry.Family == family);
            if (matches == 0)
                errors.Add(DesignDiagnostic.Error(DesignDiagnosticCodes.InternalStructureDisabledFamily,
                    $"The document has no {family} family entry. Disabled families must still be represented."));
            else if (matches > 1)
                errors.Add(DesignDiagnostic.Error(InternalStructureDiagnosticCodes.FamilyConfigurationInvalid,
                    $"The document declares {matches} {family} family entries; exactly one is required."));
        }

        if (JunctionPriority.IsDefault || JunctionPriority.Length != 3 ||
            JunctionPriority.Distinct().Count() != 3 || JunctionPriority.Any(family => !Enum.IsDefined(family)))
            errors.Add(new DesignDiagnostic(InternalStructureDiagnosticCodes.JunctionPriorityInvalid,
                DesignSeverity.Error,
                "The internal junction priority must contain each of the three families exactly once.",
                Field: nameof(JunctionPriority),
                SuggestedCorrection: "Use transverse, longitudinal, then internal deck."));

        return errors;
    }
}
