namespace FtdHullGenerator.Geometry;

/// <summary>A single named, unit-bearing quality measurement. Metrics are authoritative; the report
/// never folds them into an opaque weighted score.</summary>
/// <remarks>
/// <see cref="Value" /> is always present so numeric consumers stay simple. A metric whose payload is
/// text (for example a report-scope or attribution-note signal) carries it in <see cref="TextValue" />.
/// </remarks>
public sealed record SmoothingQualityMetric(string Name, double Value, string Unit, string Description)
{
    /// <summary>Optional text payload for scope or note metrics whose value is not numeric.</summary>
    public string? TextValue { get; init; }
}
