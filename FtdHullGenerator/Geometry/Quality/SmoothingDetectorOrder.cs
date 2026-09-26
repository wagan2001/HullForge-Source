namespace FtdHullGenerator.Geometry;

/// <summary>
/// Fixed detector identifiers and their report order. Findings and per-detector counts are sorted by
/// this ordinal, then by string ordinal, so no dictionary enumeration order can leak into a report.
/// </summary>
internal static class SmoothingDetectorOrder
{
    public const string Overhang = "overhang";
    public const string Jagged = "jagged";
    public const string LocalRegression = "local-regression";
    public const string Continuity = "continuity";
    public const string LandmarkDrift = "landmark-drift";

    private static readonly string[] Ordered = [Overhang, Jagged, LocalRegression, Continuity, LandmarkDrift];

    public static int Ordinal(string detector)
    {
        var index = Array.IndexOf(Ordered, detector);
        return index >= 0 ? index : int.MaxValue;
    }
}
