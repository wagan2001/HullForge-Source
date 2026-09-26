namespace FtdHullGenerator.Geometry;

/// <summary>
/// How the candidate's evidence for a finding relates to the baseline's corresponding evidence.
/// The validator measures both hulls with identical settings and classifies the comparison instead of
/// attributing every abnormality to smoothing: a defect that both hulls share is
/// <see cref="PresentInBaseline" /> hull-generation evidence, not a smoothing regression.
/// </summary>
public enum BaselineClassification
{
    /// <summary>No baseline comparison was possible or meaningful for this finding.</summary>
    Unknown,

    /// <summary>The evidence is absent from the baseline and present in the candidate.</summary>
    Introduced,

    /// <summary>The evidence is present in both hulls but measurably worse in the candidate.</summary>
    Worsened,

    /// <summary>The evidence is present in both hulls at essentially the same magnitude.</summary>
    PresentInBaseline,

    /// <summary>The evidence is present in the baseline and measurably better in the candidate.</summary>
    Improved,
}

/// <summary>
/// One evidence-bearing quality finding. Every field is deterministic for a given baseline,
/// candidate and options set, so reports can be compared byte-for-byte between runs.
/// </summary>
public sealed record SmoothingFinding
{
    /// <summary>Stable machine code, e.g. <c>OVERHANG_INTRODUCED</c> or <c>LANDMARK_KEEL</c>.</summary>
    public required string Code { get; init; }

    /// <summary>Owning detector, one of <c>overhang</c>, <c>jagged</c>, <c>local-regression</c>, <c>continuity</c>, <c>landmark-drift</c>.</summary>
    public required string Detector { get; init; }

    public SmoothingFindingSeverity Severity { get; init; }

    /// <summary>Name of the single measurement that crossed its threshold.</summary>
    public required string MeasurementName { get; init; }

    /// <summary>Measured value of <see cref="MeasurementName" />.</summary>
    public double MeasuredValue { get; init; }

    /// <summary>Threshold that <see cref="MeasuredValue" /> crossed for the reported severity.</summary>
    public double Threshold { get; init; }

    /// <summary>States what failed, where it is, and which measurement crossed which threshold.</summary>
    public required string Explanation { get; init; }

    /// <summary>Inclusive bounds of every evidence cell, before any display truncation.</summary>
    public HullCellBounds Bounds { get; init; }

    /// <summary>Evidence cells, ordered by X then Y then Z. May be shortened by the reporter.</summary>
    public IReadOnlyList<(int X, int Y, int Z)> Cells { get; init; } = [];

    /// <summary>True when <see cref="Cells" /> is shorter than the evidence set because of a display cap.</summary>
    public bool CellsTruncated { get; init; }

    // ----- Baseline-aware classification evidence (Milestone 2). All optional so existing callers
    // and fixtures are unaffected; a detector fills only what it actually measured. -----

    /// <summary>How this evidence relates to the same evidence measured in the baseline hull.</summary>
    public BaselineClassification Classification { get; init; } = BaselineClassification.Unknown;

    /// <summary>The baseline's corresponding measured value, when a baseline measurement was taken.</summary>
    public double? BaselineValue { get; init; }

    /// <summary>The candidate's corresponding measured value, when a candidate measurement was taken.</summary>
    public double? CandidateValue { get; init; }

    /// <summary>
    /// The solid boundary the evidence lies on (<see cref="SurfaceScope.HullSkin" /> or
    /// <see cref="SurfaceScope.InternalArmor" />), when the detector knows it.
    /// </summary>
    public SurfaceScope? SurfaceScope { get; init; }

    /// <summary>
    /// Sampling resolution in metres, or null when the evidence came from an exact analytic
    /// measurement. A detector that samples reports the step it used so the verdict can be reproduced.
    /// </summary>
    public double? SamplingResolution { get; init; }

    /// <summary>
    /// Deterministic identifiers of the source placements the evidence came from, ordered. Empty when
    /// the detector could not attribute the evidence to placements.
    /// </summary>
    public IReadOnlyList<string> SourcePlacementIds { get; init; } = [];

    /// <summary>
    /// A reproducible, human-readable description of where the source placements are (for example the
    /// owning placement index and anchor cell), so a reader can recreate the fixture without the report.
    /// </summary>
    public string? PlacementDescription { get; init; }

    // ----- Worst local patch coordinates (Milestone 3). The aggregate <see cref="Bounds" /> still
    // describes the whole evidence set; these fields name the single worst patch the detector measured,
    // so a report never has to hide the location behind cell-list truncation. -----

    /// <summary>
    /// Bounds of the single worst local patch the detector measured, when that patch is narrower than
    /// <see cref="Bounds" /> (for example the worst local-regression window or a continuity cap cell).
    /// Null when the detector has no separate worst-patch location.
    /// </summary>
    public HullCellBounds? WorstPatchBounds { get; init; }

    /// <summary>Deterministic coordinate string for <see cref="WorstPatchBounds" />; null when it is null.</summary>
    public string? WorstPatch { get; init; }

    // ----- Truncation safety (Milestone 3). -----

    /// <summary>
    /// Present only on a synthetic <c>DETECTOR_FINDINGS_TRUNCATED</c> finding: the detector's true
    /// finding total, how many were retained, and the per-severity counts of the findings the
    /// per-detector cap dropped. Null on every real finding.
    /// </summary>
    public SmoothingTruncation? Truncation { get; init; }
}

/// <summary>
/// How a per-detector finding cap shortened one detector's results. It is carried only by the synthetic
/// <c>DETECTOR_FINDINGS_TRUNCATED</c> finding, whose own severity is the worst dropped severity, so no
/// dropped error or warning can be hidden by capping.
/// </summary>
public sealed record SmoothingTruncation(
    int TrueTotal,
    int Retained,
    int DroppedInfo,
    int DroppedWarning,
    int DroppedError)
{
    /// <summary>Total findings dropped by the cap.</summary>
    public int DroppedTotal => DroppedInfo + DroppedWarning + DroppedError;
}
