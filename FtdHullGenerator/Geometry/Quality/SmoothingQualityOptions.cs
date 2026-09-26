namespace FtdHullGenerator.Geometry;

/// <summary>Ordered severity of a smoothing quality finding. Ordinal order is Info, Warning, Error.</summary>
public enum SmoothingFindingSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// Thresholds for <see cref="SmoothingQualityValidator" />. Every default is documented next to the
/// property it controls; the validator never derives a hidden score from them, so changing a value
/// only moves the named measurement's severity boundary.
/// </summary>
public sealed record SmoothingQualityOptions
{
    /// <summary>
    /// Version of the detector set and its threshold/reporting semantics. Bump this whenever a
    /// detector's measurement or a default threshold changes, so a report can be pinned to the
    /// semantics that produced it.
    /// </summary>
    public const int DetectorVersion = 1;

    /// <summary>The shared immutable defaults used when a caller passes no options.</summary>
    public static SmoothingQualityOptions Default { get; } = new();

    // ----- Overhang detector: candidate-created ledges that the unsmoothed hull did not have. -----

    /// <summary>
    /// Minimum number of outward cells a row must protrude beyond the row below before an introduced
    /// overhang is considered at all. Default 3: deliberately conservative, so only a three-metre or
    /// deeper ledge is reported and routine one- and two-cell bevel steps stay silent. The detector
    /// prefers false negatives to routine false positives.
    /// </summary>
    public int OverhangMinProtrusionCells { get; init; } = 3;

    /// <summary>
    /// Minimum number of 6-connected evidence cells an overhang region must cover. Default 3:
    /// deliberately conservative, so a lone or paired overhanging cell is not treated as a
    /// surface-quality signal.
    /// </summary>
    public int OverhangMinAreaCells { get; init; } = 3;

    /// <summary>
    /// Region cell count at or above which an overhang becomes a warning. Default 8: deliberately
    /// conservative, so only a sustained eight-cell ledge warns and smaller ledges stay Info.
    /// </summary>
    public int OverhangWarningAreaCells { get; init; } = 8;

    /// <summary>
    /// Region cell count at or above which an overhang becomes an error. Default 0 disables the
    /// error severity; a non-positive value never promotes a finding.
    /// </summary>
    public int OverhangErrorAreaCells { get; init; } = 0;

    // ----- Jagged detector: short-period one-cell teeth and inward/outward reversals. -----

    /// <summary>Maximum absolute first difference that still counts as a one-cell tooth. Default 1.</summary>
    public int JagMaxToothDelta { get; init; } = 1;

    /// <summary>
    /// Maximum index gap between consecutive nonzero deltas for their sign change to be a reversal.
    /// Default 3: slower direction changes are deliberate shaping rather than jaggedness.
    /// </summary>
    public int JagMaxPeriod { get; init; } = 3;

    /// <summary>
    /// Minimum number of reversals in a cluster before it is reported. Default 3: a single reversal is
    /// an intentional chine or shoulder, not a tooth.
    /// </summary>
    public int JagMinReversals { get; init; } = 3;

    /// <summary>
    /// Window length, in contour samples, over which a reversal cluster may form. Default 8.
    /// </summary>
    public int JagWindow { get; init; } = 8;

    /// <summary>
    /// Maximum range (max - min) of the contour values inside a cluster. Default 2: larger excursions
    /// are real shape change rather than one-cell chatter.
    /// </summary>
    public int JagMaxRange { get; init; } = 2;

    /// <summary>
    /// Number of samples at each end of a contour sequence that are ignored when looking for
    /// reversals, so a bow/stern taper is not mistaken for a tooth. Default 2.
    /// </summary>
    public int JagIgnoreEnds { get; init; } = 2;

    // ----- Local regression detector: bounded overlapping windows whose exposed/step area worsens. -----

    /// <summary>
    /// Edge length, in cells, of the fixed spatial window the regression detector measures. Default 4:
    /// large enough that a dent's removed cell and the support its neighbours gained are measured
    /// together, small enough that a remote improvement cannot net a local defect away. Windows are
    /// overlapping, so a defect never straddles a boundary unmeasured.
    /// </summary>
    public int RegressionWindowSize { get; init; } = 4;

    /// <summary>
    /// Step, in cells, between consecutive overlapping regression windows. Default 2: half the window
    /// size, so every changed cell is covered by several windows and a one-cell defect cannot fall
    /// between them. A non-positive value is treated as 1.
    /// </summary>
    public int RegressionWindowStride { get; init; } = 2;

    /// <summary>
    /// Radius, in cells, of the face-neighbour support measured around each window. Default 1: a face
    /// whose area moved to the cell next to it is still counted, so coverage accounting is correct at a
    /// window edge. The reported location stays the window, not the support collar.
    /// </summary>
    public int RegressionSupportRadius { get; init; } = 1;

    /// <summary>Minimum number of cells in a positive-delta patch before it is considered. Default 2.</summary>
    public int RegressionMinCells { get; init; } = 2;

    /// <summary>
    /// Summed exposed-area delta (m^2) at or above which a patch is reported. Default 1.0.
    /// </summary>
    public double RegressionMinExposedArea { get; init; } = 1.0;

    /// <summary>
    /// Summed step-area delta (m^2) at or above which a patch is reported. Default 3.0: a fitted bevel
    /// can raise the diagonal exposed area while still removing steps, so the step measure is the
    /// stronger like-for-like signal and is held higher to keep proven additive fills clean.
    /// </summary>
    public double RegressionMinStepArea { get; init; } = 3.0;

    /// <summary>Crossing exposed delta (m^2) at or above which a patch becomes a warning. Default 4.0.</summary>
    public double RegressionWarningExposedArea { get; init; } = 4.0;

    /// <summary>Crossing step delta (m^2) at or above which a patch becomes a warning. Default 4.0.</summary>
    public double RegressionWarningStepArea { get; init; } = 4.0;

    /// <summary>
    /// Crossing area (m^2) at or above which a patch becomes an error. Default 0 disables the error
    /// severity; a non-positive value never promotes a finding.
    /// </summary>
    public double RegressionErrorArea { get; init; } = 0;

    /// <summary>
    /// Minimum step-area share of the exposed-area growth for an additive window to count as a lip or
    /// spike rather than a fairing. Default 0.5: a fairing trades step faces for diagonal (non-step)
    /// surface, so its step growth is a small share of its exposed growth, while a lip or spike adds
    /// mostly step-like surface. This is a measured shape distinction, not a removal-based exemption.
    /// </summary>
    public double RegressionAdditiveStepFraction { get; init; } = 0.5;

    // ----- Landmark drift detector: deck edge, keel, maximum beam, chine, flat bottom. -----

    /// <summary>
    /// Maximum per-station landmark movement, in cells, that is still accepted as unchanged.
    /// Default 1: a two-cell move is a real landmark shift.
    /// </summary>
    public int LandmarkToleranceCells { get; init; } = 1;

    /// <summary>
    /// When true a landmark present in the baseline but absent from the candidate is an error;
    /// otherwise it is a warning. Default false.
    /// </summary>
    public bool LandmarkLossIsError { get; init; } = false;

    /// <summary>
    /// Minimum number of consecutive stations a keel line must hold constant before it is treated as
    /// an intentional flat bottom. Default 3.
    /// </summary>
    public int FlatBottomMinRun { get; init; } = 3;

    /// <summary>
    /// Minimum number of consecutive stations the baseline's widest-point height must hold constant
    /// before it is treated as a sustained chine/shoulder landmark. Default 3: a single station whose
    /// widest point sits between deck and keel is not enough evidence to call a chine.
    /// </summary>
    public int ChineMinRun { get; init; } = 3;

    // ----- Section queries and continuity/coverage evidence. -----

    /// <summary>
    /// Sub-cell offset, in cells, added to every section plane coordinate. Default 0.0 samples cell
    /// centres, which cuts through the cell interior and avoids the shared face planes; 0.5 lands exactly
    /// on the shared face planes, the most degenerate choice. A caller can override it through
    /// <see cref="SmoothingDiagnosticContext.SectionOffset" />.
    /// </summary>
    public double SectionOffset { get; init; } = 0.0;

    /// <summary>
    /// Station step, in cells, between consecutive section planes the continuity detector samples.
    /// Default 1: every lattice station is sampled so a one-cell defect cannot fall between sections.
    /// </summary>
    public int SectionStep { get; init; } = 1;

    /// <summary>
    /// Distance, in metres, below which two section endpoints are considered the same point and stitched
    /// into one contour loop. Default 1e-3. Independent of every detector threshold.
    /// </summary>
    public double SectionPointTolerance { get; init; } = 1e-3;

    /// <summary>
    /// Turning angle, in degrees, at or above which a section contour vertex is a corner rather than a
    /// smooth continuation. Default 45: shallow facet joins stay smooth, right-angle lattice steps count.
    /// </summary>
    public double ContinuitySharpAngleDegrees { get; init; } = 45;

    /// <summary>
    /// Edge length, in cells, at or below which an edge between two sharp corners is a short ledge or
    /// residual cap rather than a deliberate run. Default 1.0: a full lattice edge is a deliberate run;
    /// only a sub-cell residual edge is suspicious.
    /// </summary>
    public double ContinuityShortEdgeCells { get; init; } = 1.0;

    /// <summary>
    /// Minimum number of continuity events in a connected region before it is reported. Default 3: a
    /// single isolated cap or corner is routine lattice detail; a run of at least three is a localized
    /// surface-fairness signal.
    /// </summary>
    public int ContinuityMinEvents { get; init; } = 3;

    // ----- Reporting limits. -----

    /// <summary>
    /// Maximum findings retained per detector. Default 50. Excess findings collapse into one
    /// <c>DETECTOR_FINDINGS_TRUNCATED</c> Info finding that carries the true total.
    /// </summary>
    public int MaxFindingsPerDetector { get; init; } = 50;

    /// <summary>
    /// Maximum cells listed on a single finding. Default 256. The bounds and measurements still
    /// describe the full region; only the cell list is shortened and flagged.
    /// </summary>
    public int MaxCellsPerFinding { get; init; } = 256;
}
