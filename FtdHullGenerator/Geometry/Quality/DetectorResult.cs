namespace FtdHullGenerator.Geometry;

/// <summary>
/// Internal detector output: the findings plus the aggregate named metrics the report emits. Keeping
/// the aggregates separate from the findings lets the validator compute authoritative totals before
/// it caps or truncates any display list.
/// </summary>
internal sealed record DetectorResult(
    IReadOnlyList<SmoothingFinding> Findings,
    IReadOnlyDictionary<string, double> Metrics)
{
    public static DetectorResult Empty { get; } =
        new([], new Dictionary<string, double>());
}
