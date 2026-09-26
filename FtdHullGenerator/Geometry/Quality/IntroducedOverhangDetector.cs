namespace FtdHullGenerator.Geometry;

/// <summary>
/// Finds ledges or undercuts the candidate introduced relative to the unsmoothed baseline. A ledge
/// that exists in both hulls cancels out, so an intentional feature is never reported. Symmetric
/// port/starboard evidence merges into a single finding.
/// </summary>
public static class IntroducedOverhangDetector
{
    public const string DetectorName = SmoothingDetectorOrder.Overhang;

    public static IReadOnlyList<SmoothingFinding> Detect(
        SurfaceQualityGeometry baseline, SurfaceQualityGeometry candidate, SmoothingQualityOptions options) =>
        DetectDetailed(baseline, candidate, options, SmoothingDiagnosticContext.Empty).Findings;

    internal static DetectorResult DetectDetailed(
        SurfaceQualityGeometry baseline,
        SurfaceQualityGeometry candidate,
        SmoothingQualityOptions options,
        SmoothingDiagnosticContext context)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(context);

        // The protrusion at an evidence cell, keyed by the cell, so mirror-merging can keep the
        // largest protrusion without re-deriving it from the geometry.
        var evidence = new Dictionary<(int X, int Y, int Z), int>();
        foreach (var side in Sides)
        foreach (var row in candidate.MaxXByRow.Keys)
        {
            var (y, z) = row;
            var extent = Extent(side, candidate, row);
            var candidateLedge = Ledge(side, candidate, row);

            // A baseline row that does not exist has no ledge of its own.
            var baselineLedge = baseline.MaxXByRow.ContainsKey(row)
                ? Ledge(side, baseline, row)
                : 0;
            var introduced = candidateLedge - baselineLedge;
            if (introduced < options.OverhangMinProtrusionCells) continue;

            // A positive introduced ledge implies the candidate's row below exists, so its outward
            // extent is safe to read and gives each evidence cell its protrusion depth.
            var belowExtent = Extent(side, candidate, (y - 1, z));
            for (var coordinate = extent - introduced + 1; coordinate <= extent; coordinate++)
            {
                var x = ToX(side, coordinate);
                var cell = (X: x, Y: y, Z: z);
                if (!candidate.IsOccupied(cell)) continue;
                if (candidate.IsOccupied((x, y - 1, z))) continue;
                if (!candidate.IsOccupied((x - side.Sign, y, z))) continue;
                evidence[cell] = Math.Max(evidence.TryGetValue(cell, out var existing) ? existing : 0,
                    coordinate - belowExtent);
            }
        }

        // Merge mirrored port/starboard evidence first, then apply the minimum-area filter to the
        // merged region. A symmetric pair whose halves are each below the minimum can still form a
        // reportable region once merged, so filtering first would silently drop real evidence.
        var merged = CellComponents.MergeMirrored(
                CellComponents.Build(evidence.Keys), candidate.MinX + candidate.MaxX)
            .Where(component => component.Count >= options.OverhangMinAreaCells)
            .ToList();

        var findings = new List<SmoothingFinding>(merged.Count);
        var totalCells = 0;
        var maxProtrusion = 0;
        var protectedRegions = 0;
        foreach (var component in merged)
        {
            if (context.IsRegionProtected(component))
            {
                protectedRegions++;
                continue;
            }

            var area = component.Count;
            var protrusion = component.Max(cell => evidence[cell]);
            totalCells += area;
            maxProtrusion = Math.Max(maxProtrusion, protrusion);
            var severity = Severity(area, options);
            var threshold = Threshold(severity, options);
            var thresholdLabel = severity switch
            {
                SmoothingFindingSeverity.Error => "error threshold",
                SmoothingFindingSeverity.Warning => "warning threshold",
                _ => "reporting minimum",
            };
            var bounds = HullCellBounds.FromCells(component);
            var zSpan = bounds.MaxZ - bounds.MinZ + 1;
            var (placementIds, placementDescription) = PlacementEvidence.Describe(baseline, candidate, component);
            findings.Add(new SmoothingFinding
            {
                Code = "OVERHANG_INTRODUCED",
                Detector = DetectorName,
                Severity = severity,
                MeasurementName = "overhang.areaCells",
                MeasuredValue = area,
                Threshold = threshold,
                Classification = BaselineClassification.Introduced,
                BaselineValue = 0,
                CandidateValue = area,
                SurfaceScope = SurfaceScope.HullSkin,
                SamplingResolution = null,
                SourcePlacementIds = placementIds,
                PlacementDescription = placementDescription,
                Explanation =
                    $"Introduced overhang of {area} cells spanning {zSpan} station(s) with a maximum " +
                    $"protrusion of {protrusion} cells at {bounds.ToDisplay()}; area {area} cells crossed " +
                    $"the {thresholdLabel} of {threshold}. Classification Introduced.",
                Bounds = bounds,
                Cells = component,
            });
        }

        var metrics = new Dictionary<string, double>
        {
            ["overhang.introducedRegions"] = findings.Count,
            ["overhang.introducedCells"] = totalCells,
            ["overhang.maxProtrusionCells"] = maxProtrusion,
            ["overhang.protectedRegions"] = protectedRegions,
        };
        return new DetectorResult(findings, metrics);
    }

    private static readonly Side[] Sides = [new(1), new(-1)];

    private static int Extent(Side side, SurfaceQualityGeometry geometry, (int Y, int Z) row) =>
        side.Sign > 0 ? geometry.MaxXByRow[row] : -geometry.MinXByRow[row];

    /// <summary>
    /// Outward ledge of a row relative to the row directly beneath it. A row with nothing beneath it
    /// (the hull's bottom/keel row, or a detached row) has no material to overhang, so its ledge is
    /// zero. That keeps a widened keel from being reported as an introduced overhang; only a row that
    /// protrudes beyond real material below counts.
    /// </summary>
    private static int Ledge(Side side, SurfaceQualityGeometry geometry, (int Y, int Z) row)
    {
        var (y, z) = row;
        var below = (y - 1, z);
        return geometry.MaxXByRow.ContainsKey(below)
            ? Math.Max(0, Extent(side, geometry, row) - Extent(side, geometry, below))
            : 0;
    }

    private static int ToX(Side side, int coordinate) => side.Sign > 0 ? coordinate : -coordinate;

    private static SmoothingFindingSeverity Severity(int area, SmoothingQualityOptions options)
    {
        if (options.OverhangErrorAreaCells > 0 && area >= options.OverhangErrorAreaCells)
            return SmoothingFindingSeverity.Error;
        return area >= options.OverhangWarningAreaCells
            ? SmoothingFindingSeverity.Warning
            : SmoothingFindingSeverity.Info;
    }

    /// <summary>
    /// The cell-count threshold actually crossed for the reported severity: the reporting minimum for
    /// Info, the warning count for Warning, and the error count for Error. The threshold is never
    /// reported as a boundary the finding did not reach.
    /// </summary>
    private static int Threshold(SmoothingFindingSeverity severity, SmoothingQualityOptions options) =>
        severity switch
        {
            SmoothingFindingSeverity.Error => options.OverhangErrorAreaCells,
            SmoothingFindingSeverity.Warning => options.OverhangWarningAreaCells,
            _ => options.OverhangMinAreaCells,
        };

    private readonly record struct Side(int Sign);
}
