namespace FtdHullGenerator.Geometry;

/// <summary>Severity counts of a finding set, used for both capped and uncapped counts.</summary>
public readonly record struct SmoothingSeverityCounts(int Info, int Warning, int Error)
{
    /// <summary>Total findings across every severity.</summary>
    public int Total => Info + Warning + Error;
}

/// <summary>One detector's severity counts, ordered by the detector's fixed ordinal.</summary>
public sealed record SmoothingDetectorSeverityCounts(string Detector, int Info, int Warning, int Error)
{
    /// <summary>Total findings for this detector.</summary>
    public int Total => Info + Warning + Error;
}

/// <summary>
/// The deterministic result of one baseline/candidate comparison. Named metrics are authoritative:
/// <see cref="RankingScore" /> is intentionally null unless a caller supplies one, so no hidden
/// weighted score can be mistaken for a measurement.
/// </summary>
public sealed record SmoothingQualityReport(
    IReadOnlyList<SmoothingQualityMetric> Metrics,
    IReadOnlyList<SmoothingFinding> Findings,
    SmoothingQualityOptions Options)
{
    /// <summary>Optional caller-supplied ranking value; the validator never computes one.</summary>
    public double? RankingScore { get; init; }

    /// <summary>
    /// Severity counts over every finding the detectors produced, before the per-detector cap. Capping
    /// never changes these, so aggregate severity is independent of
    /// <see cref="SmoothingQualityOptions.MaxFindingsPerDetector" />. Defaults to an empty count when a
    /// caller builds a report without running the validator.
    /// </summary>
    public SmoothingSeverityCounts UncappedSeverityCounts { get; init; }

    /// <summary>
    /// Per-detector severity counts over every finding the detectors produced, before the per-detector
    /// cap, ordered by the detector's fixed ordinal. Empty when no uncapped counts were supplied.
    /// </summary>
    public IReadOnlyList<SmoothingDetectorSeverityCounts> UncappedDetectorCounts { get; init; } = [];

    /// <summary>
    /// True when any finding has <see cref="SmoothingFindingSeverity.Error" />. The pre-cap counts are
    /// consulted first so capping can never hide an error behind the retained-finding list.
    /// </summary>
    public bool HasErrors =>
        UncappedSeverityCounts.Error > 0 ||
        Findings.Any(finding => finding.Severity == SmoothingFindingSeverity.Error);

    /// <summary>
    /// True when any finding is a warning or an error (anything above Info). The pre-cap counts are
    /// consulted first so capping can never hide a warning or error behind the retained-finding list.
    /// </summary>
    public bool HasWarnings =>
        UncappedSeverityCounts.Warning > 0 ||
        UncappedSeverityCounts.Error > 0 ||
        Findings.Any(finding => finding.Severity != SmoothingFindingSeverity.Info);

    /// <summary>Counts findings of exactly the requested severity.</summary>
    public int CountBySeverity(SmoothingFindingSeverity severity) =>
        Findings.Count(finding => finding.Severity == severity);

    /// <summary>
    /// Counts findings per detector, ordered by the detector's fixed ordinal so the sequence is
    /// independent of finding order. Detectors with no findings are omitted.
    /// </summary>
    public IReadOnlyList<(string Detector, int Count)> CountByDetector() =>
        Findings
            .GroupBy(finding => finding.Detector)
            .OrderBy(group => SmoothingDetectorOrder.Ordinal(group.Key))
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => (group.Key, group.Count()))
            .ToArray();
}
