using System.Text.Json.Serialization;

namespace FtdHullGenerator.Domain.Design;

public enum DesignSeverity
{
    Info = 0,
    Warning = 1,
    Error = 2,
}

/// <summary>
/// The world-space envelope a diagnostic refers to, in twice-metre design units. Contract C10 asks a
/// diagnostic to name the affected bounds so a caller can highlight the offending region instead of
/// re-deriving where the problem is.
/// </summary>
public sealed record DesignBounds(
    DesignMeasure MinX,
    DesignMeasure MaxX,
    DesignMeasure MinY,
    DesignMeasure MaxY,
    DesignMeasure MinZ,
    DesignMeasure MaxZ)
{
    public static DesignBounds FromRulerSpan(DesignSpan span, DesignMeasure centerPlaneX) => new(
        centerPlaneX, centerPlaneX,
        DesignMeasure.Zero, DesignMeasure.Zero,
        span.Start, span.End);
}

/// <summary>
/// A structured design-time diagnosis. Contract C10: "does not fit" alone is not a
/// usable answer, so a diagnostic carries a stable code, the feature or node it belongs
/// to, the field, the requested and realized values, and a suggested correction.
/// </summary>
public sealed record DesignDiagnostic(
    string Code,
    DesignSeverity Severity,
    string Message,
    string? NodeId = null,
    string? Field = null,
    DesignMeasure? Requested = null,
    DesignMeasure? Realized = null,
    string? SuggestedCorrection = null,
    DesignBounds? AffectedBounds = null)
{
    [JsonIgnore]
    public bool IsError => Severity == DesignSeverity.Error;

    public static DesignDiagnostic Error(string code, string message, string? nodeId = null, string? field = null) =>
        new(code, DesignSeverity.Error, message, nodeId, field);

    public static DesignDiagnostic Warning(string code, string message, string? nodeId = null, string? field = null) =>
        new(code, DesignSeverity.Warning, message, nodeId, field);

    public override string ToString()
    {
        var location = (NodeId, Field) switch
        {
            (null, null) => string.Empty,
            (not null, null) => $" [{NodeId}]",
            (null, not null) => $" [{Field}]",
            _ => $" [{NodeId}.{Field}]",
        };
        var values = Requested is null && Realized is null
            ? string.Empty
            : $" (requested {Requested?.ToString() ?? "n/a"}, realized {Realized?.ToString() ?? "n/a"})";
        var suggestion = SuggestedCorrection is null ? string.Empty : $" {SuggestedCorrection}";
        return $"{Severity}: {Code}{location} {Message}{values}{suggestion}";
    }
}

/// <summary>
/// Stable diagnostic codes. A code is part of the contract: callers and tests may match
/// on it, so codes are added, never repurposed.
/// </summary>
public static class DesignDiagnosticCodes
{
    public const string SchemaVersionUnsupported = "DOC001";
    public const string DocumentNameMissing = "DOC002";
    public const string DocumentNameTooLong = "DOC003";
    public const string IdentifierMissing = "DOC004";
    public const string IdentifierDuplicate = "DOC005";
    public const string CountLimitExceeded = "DOC006";
    public const string ExtensionBudgetExceeded = "DOC007";
    public const string MeasureOutOfRange = "DOC008";
    public const string HullParametersInvalid = "DOC009";
    public const string HistoricalSourceIncomplete = "DOC010";
    public const string SmoothingMethodUnsupported = "DOC011";
    public const string SmoothingAlgorithmVersionUnsupported = "DOC012";
    public const string RefinementRunOutOfRange = "DOC013";
    public const string StyleProvenanceInvalid = "DOC014";

    public const string ArrangementGapCountMismatch = "LAY001";
    public const string ArrangementUnknownNode = "LAY002";
    public const string ArrangementUnknownNamedGap = "LAY003";
    public const string ArrangementNegativeGap = "LAY004";
    public const string ArrangementMeasureMismatch = "LAY005";
    public const string ArrangementNegativeExtent = "LAY006";
    public const string ArrangementCenterlineUnrepresentable = "LAY007";
    public const string ArrangementChildOutsideParent = "LAY008";

    /// <summary>An arrangement enumeration (anchor or resize policy) holds an unsupported value.</summary>
    public const string ArrangementUnsupportedEnumeration = "LAY009";

    public const string InternalStructureDisabledFamily = "INT001";
    public const string InternalStructureInvalidThickness = "INT002";
    public const string InternalStructureInvalidSpacing = "INT003";

    public const string BarbetteUnknownNode = "BAR001";
    public const string BarbetteBoreNotInsideOuter = "BAR002";
    public const string BarbetteInvalidWallThickness = "BAR003";
    public const string BarbetteInvalidDepth = "BAR004";
    public const string BarbetteOuterDiameterInvalid = "BAR005";
    public const string BarbetteHeightInvalid = "BAR006";
    public const string BarbetteMaterialUnsupported = "BAR007";

    /// <summary>Centerline barbettes require a real centre voxel column.</summary>
    public const string BarbetteOddHullWidthRequired = "BAR008";

    /// <summary>The frozen clear internal diameter is missing, zero or out of range.</summary>
    public const string BarbetteClearDiameterInvalid = "BAR009";

    /// <summary>The frozen requested clear internal depth is missing or below one metre.</summary>
    public const string BarbetteClearDepthInvalid = "BAR010";

    /// <summary>The vertical top offset below the reference deck plane is negative or out of range.</summary>
    public const string BarbetteTopOffsetInvalid = "BAR011";

    /// <summary>The square neck clear opening is not exactly 1 m, 3 m or 5 m.</summary>
    public const string BarbetteNeckSizeUnsupported = "BAR012";

    /// <summary>A side, roof, bottom or neck armor stack is missing or empty.</summary>
    public const string BarbetteArmorStackInvalid = "BAR013";

    /// <summary>The innermost armor layer against the clear volume is an air gap, not structural armor.</summary>
    public const string BarbetteClearBoundaryNotArmor = "BAR014";

    /// <summary>The centerline has no real centre voxel column, so the square neck cannot be concentric.</summary>
    public const string BarbetteEvenWidthCenterline = "BAR015";

    /// <summary>Two distinct protected clear volumes overlap; the requested positions are retained.</summary>
    public const string BarbetteClearVolumeOverlap = "BAR016";

    /// <summary>Adjacent protected clear volumes are closer than the frozen one-metre separator.</summary>
    public const string BarbetteMinimumClearSeparation = "BAR017";

    /// <summary>A protected clear volume extends beyond the ship's bow or stern ruler end.</summary>
    public const string BarbetteClearVolumeOutsideRuler = "BAR018";

    /// <summary>A barbette armor layer requests a longitudinal construction other than Solid.</summary>
    public const string BarbetteConstructionUnsupported = "BAR019";

    public const string SuperstructureModuleTooSmall = "SUP001";
    public const string SuperstructureModuleInvalidThickness = "SUP002";
    public const string SuperstructureRootUnknownSpan = "SUP003";
    public const string SuperstructureTowerOutsideRoot = "SUP004";
    public const string SuperstructureMaterialUnsupported = "SUP005";

    public const string InternalStructureMaterialUnsupported = "INT004";

    /// <summary>The wrapped hull parameters and the document's smoothing intent disagree.</summary>
    public const string SmoothingIntentDiverges = "DOC015";
    public const string DecorativeRefinementUnavailable = "DOC016";
}

public static class DesignDiagnosticExtensions
{
    public static bool HasErrors(this IEnumerable<DesignDiagnostic> diagnostics) =>
        diagnostics.Any(diagnostic => diagnostic.IsError);

    public static IReadOnlyList<DesignDiagnostic> OnlyErrors(this IEnumerable<DesignDiagnostic> diagnostics) =>
        diagnostics.Where(diagnostic => diagnostic.IsError).ToArray();

    public static string Summary(this IEnumerable<DesignDiagnostic> diagnostics)
    {
        var all = diagnostics as IReadOnlyCollection<DesignDiagnostic> ?? diagnostics.ToArray();
        if (all.Count == 0)
            return "no findings";

        var errors = all.Count(diagnostic => diagnostic.IsError);
        var warnings = all.Count(diagnostic => diagnostic.Severity == DesignSeverity.Warning);
        return $"{errors} error(s), {warnings} warning(s), {all.Count} finding(s)";
    }
}

/// <summary>First-pass safety caps. These are Hull Forge limits, not game limits.</summary>
public static class DesignLimits
{
    public const int MaxBarbettesPerDocument = 32;
    public const int MaxInternalPlanesPerDocument = 256;
    public const int MaxSuperstructureBoxesPerDocument = 128;
    public const int MaxDocumentExtensionBytes = 8 * 1024;
    public const int MaxDocumentNameLength = 80;
    public const int MaxIdentifierLength = 64;
    // Retained persisted defaults only. They do not define supported refined-smoothing behavior.
    public const int LegacyRefinementRunMetres = 4;

    /// <summary>Half of the largest design coordinate Hull Forge will accept, in twice-metres (100 km).</summary>
    public const int MaxDesignTwiceMetres = 200_000;
}
