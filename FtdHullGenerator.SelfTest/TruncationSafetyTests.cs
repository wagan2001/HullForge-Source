using FtdHullGenerator.Domain;
using FtdHullGenerator.Geometry;
using static SlopeFillTestSupport;

// Milestone 3 adversarial verification 12: a per-detector finding cap must never change aggregate
// severity, hide an error, or hide the location of the worst local patch. Fixtures are small synthetic
// hulls built directly from placements, so the cap and the worst-patch fields are exercised without
// depending on generation behaviour.
internal static class TruncationSafetyTests
{
    public static void Run()
    {
        CappingPreservesErrorSeverityAndTotals();
        CappingDoesNotChangeUncappedCounts();
        DetectorsExposeWorstPatchCoordinates();
        Console.WriteLine(
            "Truncation safety: 3 pre-cap severity, error-status, and worst-patch-coordinate cases passed.");
    }

    /// <summary>
    /// Three separate introduced-overhang regions are promoted to Error by a low error threshold, then
    /// the per-detector cap is set below the region count. The retained finding plus the truncation
    /// finding must still report the error, and the pre-cap counts must carry the true total.
    /// </summary>
    private static void CappingPreservesErrorSeverityAndTotals()
    {
        var (baseline, candidate) = OverhangFixture();
        var options = SmoothingQualityOptions.Default with
        {
            OverhangErrorAreaCells = 3,
            MaxFindingsPerDetector = 2,
        };
        var report = SmoothingQualityValidator.Validate(baseline, candidate, options);

        var truncated = report.Findings.Single(finding =>
            finding.Code == "DETECTOR_FINDINGS_TRUNCATED" && finding.Detector == "overhang");
        Require(truncated.Severity == SmoothingFindingSeverity.Error,
            $"The overhang truncation finding was {truncated.Severity} instead of Error.");
        Require(truncated.Truncation is not null, "The truncation finding carried no truncation detail.");
        Require(truncated.Truncation!.TrueTotal == 3,
            $"The truncation finding reported {truncated.Truncation.TrueTotal} findings instead of 3.");
        Require(truncated.Truncation.Retained == 1,
            $"The truncation finding reported {truncated.Truncation.Retained} retained findings instead of 1.");
        Require(truncated.Truncation.DroppedError == 2 && truncated.Truncation.DroppedTotal == 2,
            $"The truncation finding dropped {truncated.Truncation.DroppedError} error(s) of {truncated.Truncation.DroppedTotal} total.");
        Require(truncated.MeasuredValue == 3,
            $"The truncation finding measured {truncated.MeasuredValue} instead of the true total 3.");

        Require(report.UncappedSeverityCounts.Error >= 3,
            $"Pre-cap error count was {report.UncappedSeverityCounts.Error} instead of at least 3.");
        var overhangCounts = report.UncappedDetectorCounts.Single(counts => counts.Detector == "overhang");
        Require(overhangCounts.Total == 3 && overhangCounts.Error == 3,
            $"Pre-cap overhang counts were total {overhangCounts.Total} / error {overhangCounts.Error} instead of 3 / 3.");
        Require(report.HasErrors, "Capping hid an Error from HasErrors.");
        Require(report.HasWarnings, "Capping hid the warning/error level from HasWarnings.");

        // The capped finding list alone would understate the errors, so the pre-cap path the audit uses
        // is load-bearing rather than vacuous.
        var cappedErrorCount = report.Findings.Count(finding => finding.Severity == SmoothingFindingSeverity.Error);
        Require(cappedErrorCount < report.UncappedSeverityCounts.Error,
            $"The fixture did not actually drop an Error (capped {cappedErrorCount}, uncapped {report.UncappedSeverityCounts.Error}).");
    }

    /// <summary>
    /// The uncapped severity counts and per-detector totals must be identical whether or not the cap
    /// actually drops findings, so an aggregate result cannot depend on MaxFindingsPerDetector.
    /// </summary>
    private static void CappingDoesNotChangeUncappedCounts()
    {
        var (baseline, candidate) = OverhangFixture();
        var capped = SmoothingQualityValidator.Validate(baseline, candidate, SmoothingQualityOptions.Default with
        {
            OverhangErrorAreaCells = 3,
            MaxFindingsPerDetector = 2,
        });
        var uncapped = SmoothingQualityValidator.Validate(baseline, candidate, SmoothingQualityOptions.Default with
        {
            OverhangErrorAreaCells = 3,
            MaxFindingsPerDetector = 50,
        });

        Require(capped.UncappedSeverityCounts == uncapped.UncappedSeverityCounts,
            "The pre-cap severity counts changed with the per-detector cap.");
        Require(capped.UncappedDetectorCounts.SequenceEqual(uncapped.UncappedDetectorCounts),
            "The pre-cap per-detector counts changed with the per-detector cap.");
        Require(capped.HasErrors == uncapped.HasErrors && capped.HasWarnings == uncapped.HasWarnings,
            "Capping changed the error or warning status of the report.");
    }

    /// <summary>
    /// The regression and continuity detectors must expose the single worst local patch alongside the
    /// aggregate bounds, so a capped cell list never hides the worst location.
    /// </summary>
    private static void DetectorsExposeWorstPatchCoordinates()
    {
        var wall = new List<BlockPlacement>();
        for (var y = 0; y <= 2; y++)
        for (var z = 0; z <= 2; z++)
            wall.Add(Cube(0, y, z));
        var baseline = MakeHull(wall);
        var candidate = MakeHull(wall.Where(block => block.Position != (0, 1, 1)));
        var regressionReport = SmoothingQualityValidator.Validate(baseline, candidate);
        var regression = regressionReport.Findings.Single(finding => finding.Detector == "local-regression");
        Require(regression.WorstPatchBounds is { } regressionPatch &&
                regressionPatch.MaxX - regressionPatch.MinX == 3 &&
                regressionPatch.MaxY - regressionPatch.MinY == 3 &&
                regressionPatch.MaxZ - regressionPatch.MinZ == 3,
            "The local-regression worst patch was not the 4x4x4 measured window.");
        Require(regression.WorstPatch is not null && regression.WorstPatch.Contains("regression window"),
            $"The local-regression worst patch was '{regression.WorstPatch}'.");

        var capCandidate = MakeHull(wall.Append(Cube(1, 1, 1)));
        var capReport = SmoothingQualityValidator.Validate(baseline, capCandidate);
        var cap = capReport.Findings.First(finding => finding.Code == "CONTINUITY_RESIDUAL_CAP");
        Require(cap.WorstPatchBounds is not null, "The continuity finding carried no worst-patch bounds.");
        Require(cap.WorstPatch is not null && cap.WorstPatch.Contains("section"),
            $"The continuity worst patch was '{cap.WorstPatch}'.");
        Require(Contains(cap.Bounds, cap.WorstPatchBounds!.Value),
            "The continuity worst-patch bounds were not inside the aggregate evidence bounds.");
    }

    /// <summary>True when every corner of <paramref name="inner" /> lies inside <paramref name="outer" />.</summary>
    private static bool Contains(HullCellBounds outer, HullCellBounds inner) =>
        inner.MinX >= outer.MinX && inner.MaxX <= outer.MaxX &&
        inner.MinY >= outer.MinY && inner.MaxY <= outer.MaxY &&
        inner.MinZ >= outer.MinZ && inner.MaxZ <= outer.MaxZ;

    /// <summary>
    /// A baseline two rows high with three separated three-cell overhangs added on top. Each overhang is
    /// its own 6-connected region and each is at or above a three-cell error threshold.
    /// </summary>
    private static (GeneratedHull Baseline, GeneratedHull Candidate) OverhangFixture()
    {
        var baselineBlocks = new List<BlockPlacement>();
        for (var z = 0; z <= 8; z++)
        {
            baselineBlocks.Add(Cube(0, 0, z));
            baselineBlocks.Add(Cube(0, 1, z));
        }

        var candidateBlocks = new List<BlockPlacement>(baselineBlocks);
        foreach (var z in new[] { 0, 3, 6 })
        for (var x = 0; x <= 3; x++)
            candidateBlocks.Add(Cube(x, 2, z));

        return (MakeHull(baselineBlocks), MakeHull(candidateBlocks));
    }

    private static GeneratedHull MakeHull(params BlockPlacement[] blocks) =>
        MakeHull((IEnumerable<BlockPlacement>)blocks);

    private static GeneratedHull MakeHull(IEnumerable<BlockPlacement> blocks)
    {
        var list = blocks.ToArray();
        var cells = list.SelectMany(block => block.OccupiedCells).ToArray();
        Require(cells.Length > 0, "A synthetic hull fixture must contain at least one cell.");
        return new GeneratedHull(
            HullParameters.Default,
            list,
            cells.Min(cell => cell.X), cells.Max(cell => cell.X),
            cells.Min(cell => cell.Y), cells.Max(cell => cell.Y),
            cells.Min(cell => cell.Z), cells.Max(cell => cell.Z));
    }

    private static BlockPlacement Cube(int x, int y, int z) =>
        new(BlockShape.Cube, MaterialKind.Metal, x, y, z, 0);
}
