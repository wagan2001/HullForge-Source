using FtdHullGenerator.Domain;

namespace FtdHullGenerator.Geometry;

/// <summary>
/// Deterministic Surface Fairness Validator. Compares an unsmoothed baseline hull with a candidate
/// smoothing/fitting result of the same hull and reports named measurements plus localized findings.
/// It never modifies generation, export or the smoothing passes; it only reads their output.
/// </summary>
public static class SmoothingQualityValidator
{
    public static SmoothingQualityReport Validate(
        GeneratedHull baseline, GeneratedHull candidate, SmoothingQualityOptions? options = null) =>
        Validate(baseline, candidate, options, null);

    /// <summary>
    /// Validates with an optional <see cref="SmoothingDiagnosticContext" /> carrying protected feature
    /// masks/regions and sampling offsets. Passing null is identical to the three-argument overload.
    /// </summary>
    public static SmoothingQualityReport Validate(
        GeneratedHull baseline,
        GeneratedHull candidate,
        SmoothingQualityOptions? options,
        SmoothingDiagnosticContext? context)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);
        options ??= SmoothingQualityOptions.Default;
        var effectiveContext = context ?? new SmoothingDiagnosticContext { SectionOffset = options.SectionOffset };

        var baselineGeometry = new SurfaceQualityGeometry(baseline);
        var candidateGeometry = new SurfaceQualityGeometry(candidate);

        var overhang = IntroducedOverhangDetector.DetectDetailed(
            baselineGeometry, candidateGeometry, options, effectiveContext);
        var jagged = JaggedBlendDetector.DetectDetailed(
            baselineGeometry, candidateGeometry, options, effectiveContext);
        var regression = LocalSurfaceRegressionDetector.DetectDetailed(
            baselineGeometry, candidateGeometry, options, effectiveContext);
        var continuity = SurfaceContinuityDetector.DetectDetailed(
            baselineGeometry, candidateGeometry, options, effectiveContext);
        var landmark = LandmarkDriftDetector.DetectDetailed(
            baselineGeometry, candidateGeometry, options, effectiveContext);

        baselineGeometry.MeasureExposedSurface(out var baselineTotals);
        candidateGeometry.MeasureExposedSurface(out var candidateTotals);
        baselineGeometry.MeasureBoundarySurface(out var baselineBoundary);
        candidateGeometry.MeasureBoundarySurface(out var candidateBoundary);

        var metrics = BuildMetrics(baselineGeometry, candidateGeometry, baselineTotals, candidateTotals,
            baselineBoundary, candidateBoundary, overhang, jagged, regression, continuity, landmark);

        var ordered = overhang.Findings
            .Concat(jagged.Findings)
            .Concat(regression.Findings)
            .Concat(continuity.Findings)
            .Concat(landmark.Findings)
            .OrderBy(finding => finding, Comparer<SmoothingFinding>.Create(Compare))
            .ToArray();

        var capped = CapAndTruncate(ordered, options);
        return new SmoothingQualityReport(metrics, capped.Findings, options)
        {
            UncappedSeverityCounts = capped.Uncapped,
            UncappedDetectorCounts = capped.UncappedByDetector,
        };
    }

    private static IReadOnlyList<SmoothingQualityMetric> BuildMetrics(
        SurfaceQualityGeometry baseline,
        SurfaceQualityGeometry candidate,
        SurfaceAreaTotals baselineTotals,
        SurfaceAreaTotals candidateTotals,
        SurfaceAreaTotals baselineBoundary,
        SurfaceAreaTotals candidateBoundary,
        DetectorResult overhang,
        DetectorResult jagged,
        DetectorResult regression,
        DetectorResult continuity,
        DetectorResult landmark)
    {
        var merged = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var source in new[]
                 {
                     overhang.Metrics, jagged.Metrics, regression.Metrics, continuity.Metrics, landmark.Metrics,
                 })
        foreach (var pair in source)
            merged[pair.Key] = pair.Value;

        double Value(string name) => merged.TryGetValue(name, out var value) ? value : 0;

        var notes = baseline.Coverage.Notes.Concat(candidate.Coverage.Notes).ToArray();

        return
        [
            new("cells.baseline", baseline.Occupied.Count, "cells",
                "Occupied lattice cells in the unsmoothed baseline hull."),
            new("cells.candidate", candidate.Occupied.Count, "cells",
                "Occupied lattice cells in the candidate smoothing/fitting result."),
            new("cells.added", candidate.Occupied.Count(cell => !baseline.IsOccupied(cell)), "cells",
                "Candidate cells that the baseline did not occupy."),
            new("cells.removed", baseline.Occupied.Count(cell => !candidate.IsOccupied(cell)), "cells",
                "Baseline cells that the candidate no longer occupies."),
            new SmoothingQualityMetric("surface.scope", 0, "text",
                "What every surface metric measures.") { TextValue = SurfaceCoverage.ReportScope },
            new("surface.exposedArea.original", baselineTotals.Exposed, "m^2",
                "Baseline exterior hull-skin exposed area, cell-attributed; interior armor and enclosed cavities are excluded."),
            new("surface.exposedArea.revised", candidateTotals.Exposed, "m^2",
                "Candidate exterior hull-skin exposed area, cell-attributed; interior armor and enclosed cavities are excluded."),
            new("surface.exposedArea.delta", candidateTotals.Exposed - baselineTotals.Exposed, "m^2",
                "Change in exterior hull-skin exposed area (revised - original)."),
            new("surface.stepArea.original", baselineTotals.Step, "m^2",
                "Baseline exterior hull-skin step-face area (Y- and Z-facing squares), cell-attributed."),
            new("surface.stepArea.revised", candidateTotals.Step, "m^2",
                "Candidate exterior hull-skin step-face area (Y- and Z-facing squares), cell-attributed."),
            new("surface.stepArea.delta", candidateTotals.Step - baselineTotals.Step, "m^2",
                "Change in exterior hull-skin step-face area (revised - original)."),
            new("surface.boundaryArea.original", baselineBoundary.Exposed, "m^2",
                "Baseline all-boundary exposed area (hull skin plus interior armor), cell-attributed."),
            new("surface.boundaryArea.revised", candidateBoundary.Exposed, "m^2",
                "Candidate all-boundary exposed area (hull skin plus interior armor), cell-attributed."),
            new("surface.boundaryArea.delta", candidateBoundary.Exposed - baselineBoundary.Exposed, "m^2",
                "Change in all-boundary exposed area (revised - original)."),
            new("surface.boundaryStepArea.original", baselineBoundary.Step, "m^2",
                "Baseline all-boundary step-face area (hull skin plus interior armor), cell-attributed."),
            new("surface.boundaryStepArea.revised", candidateBoundary.Step, "m^2",
                "Candidate all-boundary step-face area (hull skin plus interior armor), cell-attributed."),
            new("surface.boundaryStepArea.delta", candidateBoundary.Step - baselineBoundary.Step, "m^2",
                "Change in all-boundary step-face area (revised - original)."),
            new("surface.notes.count", notes.Length, "notes",
                "Unattributable residual-area notes across both hulls; non-zero means the measurement could not fully attribute some face area."),
            new SmoothingQualityMetric("surface.notes", notes.Length == 0 ? 0 : 1, "text",
                notes.Length == 0
                    ? "No unattributable-area notes."
                    : "Unattributable-area notes are present and reported verbatim.")
            {
                TextValue = notes.Length == 0 ? null : string.Join(" | ", notes),
            },
            new("overhang.introducedRegions", Value("overhang.introducedRegions"), "regions",
                "Distinct introduced-overhang regions after symmetric merging."),
            new("overhang.introducedCells", Value("overhang.introducedCells"), "cells",
                "Total evidence cells across introduced-overhang regions."),
            new("overhang.maxProtrusionCells", Value("overhang.maxProtrusionCells"), "cells",
                "Largest outward protrusion of any introduced-overhang region."),
            new("jagged.affectedLines", Value("jagged.affectedLines"), "lines",
                "Contour lines with at least one jagged cluster."),
            new("jagged.oscillations", Value("jagged.oscillations"), "oscillations",
                "Total reversals across reported jagged clusters."),
            new("jagged.worstChangeCells", Value("jagged.worstChangeCells"), "cells",
                "Largest single first-difference magnitude in any reported jagged cluster."),
            new("regression.patches", Value("regression.patches"), "patches",
                "Local windows whose exposed or step area worsened versus the baseline, merged after measurement."),
            new("regression.worstExposedDelta", Value("regression.worstExposedDelta"), "m^2",
                "Largest local-window exposed-area increase of any reported regression window."),
            new("regression.worstStepDelta", Value("regression.worstStepDelta"), "m^2",
                "Largest local-window step-area increase of any reported regression window."),
            new("regression.fairingWindows", Value("regression.fairingWindows"), "windows",
                "Windows whose exposed area grew while step area improved with no removal; classified as fairing, not a regression."),
            new("regression.fairingExposedDelta", Value("regression.fairingExposedDelta"), "m^2",
                "Largest exposed-area increase among fairing windows."),
            new("regression.protectedWindows", Value("regression.protectedWindows"), "windows",
                "Regression windows suppressed because every evidence cell was declared protected by the caller."),
            new("regression.windowsMeasured", Value("regression.windowsMeasured"), "windows",
                "Overlapping local windows measured before any merge."),
            new("continuity.events", Value("continuity.events"), "events",
                "Continuity events on the candidate's analytic section contours."),
            new("continuity.introduced", Value("continuity.introduced"), "events",
                "Continuity events classified Introduced or Worsened versus the identical baseline sections."),
            new("continuity.presentInBaseline", Value("continuity.presentInBaseline"), "events",
                "Continuity events already present in the baseline; hull-generation evidence, not a smoothing regression."),
            new("continuity.improved", Value("continuity.improved"), "events",
                "Baseline continuity events the candidate removed."),
            new("continuity.coplanarFaces", Value("continuity.coplanarFaces"), "faces",
                "Section-plane coplanar faces that were skipped and made the intersection ambiguous."),
            new("continuity.degenerateTouches", Value("continuity.degenerateTouches"), "touches",
                "Fragments that touched a section plane in fewer than two distinct points."),
            new("continuity.missingSections", Value("continuity.missingSections"), "sections",
                "Stations where the baseline cut a contour but the candidate produced no segments."),
            new("continuity.uncertain", Value("continuity.uncertain"), "flag",
                "1 when a section intersection was ambiguous and continuity could not be judged confidently."),
            new("landmark.driftRegions", Value("landmark.driftRegions"), "regions",
                "Distinct landmark drift regions after contiguous grouping."),
            new("landmark.maxDriftCells", Value("landmark.maxDriftCells"), "cells",
                "Largest landmark movement in any reported drift region."),
            new("landmark.lostLandmarks", Value("landmark.lostLandmarks"), "landmarks",
                "Baseline landmarks absent from the candidate."),
            new("landmark.chineUncertain", Value("landmark.chineUncertain"), "flag",
                "1 when no sustained baseline chine or protected chine mask exists, so chine drift is not asserted."),
        ];
    }

    /// <summary>
    /// Keeps at most <see cref="SmoothingQualityOptions.MaxFindingsPerDetector" /> findings per detector,
    /// shortens each cell list to <see cref="SmoothingQualityOptions.MaxCellsPerFinding" />, and adds one
    /// <c>DETECTOR_FINDINGS_TRUNCATED</c> finding when real findings were dropped. The truncation finding
    /// carries the worst dropped severity, the true total and the per-severity dropped counts, so
    /// <see cref="SmoothingQualityReport.HasErrors" />/<see cref="SmoothingQualityReport.HasWarnings" />
    /// cannot be fooled by the cap. The returned <see cref="CappedFindings.Uncapped" /> counts describe
    /// every pre-cap finding.
    /// </summary>
    private static CappedFindings CapAndTruncate(
        IReadOnlyList<SmoothingFinding> ordered, SmoothingQualityOptions options)
    {
        var result = new List<SmoothingFinding>(ordered.Count);
        foreach (var group in ordered.GroupBy(finding => finding.Detector)
                     .OrderBy(group => SmoothingDetectorOrder.Ordinal(group.Key))
                     .ThenBy(group => group.Key, StringComparer.Ordinal))
        {
            var detectorFindings = group.ToArray();
            var kept = options.MaxFindingsPerDetector > 0
                ? detectorFindings.Take(Math.Max(0, options.MaxFindingsPerDetector - 1)).ToArray()
                : [];
            result.AddRange(kept);
            if (detectorFindings.Length <= kept.Length) continue;

            var dropped = detectorFindings.Skip(kept.Length).ToArray();
            var droppedInfo = dropped.Count(finding => finding.Severity == SmoothingFindingSeverity.Info);
            var droppedWarning = dropped.Count(finding => finding.Severity == SmoothingFindingSeverity.Warning);
            var droppedError = dropped.Count(finding => finding.Severity == SmoothingFindingSeverity.Error);
            var worstDropped = droppedError > 0
                ? SmoothingFindingSeverity.Error
                : droppedWarning > 0
                    ? SmoothingFindingSeverity.Warning
                    : SmoothingFindingSeverity.Info;
            var bounds = kept.Length > 0 ? kept[0].Bounds : HullCellBounds.FromCells([]);
            result.Add(new SmoothingFinding
            {
                Code = "DETECTOR_FINDINGS_TRUNCATED",
                Detector = group.Key,
                Severity = worstDropped,
                MeasurementName = "findings.total",
                MeasuredValue = detectorFindings.Length,
                Threshold = options.MaxFindingsPerDetector,
                Explanation =
                    $"{group.Key} produced {detectorFindings.Length} findings; only {kept.Length} were " +
                    $"retained at the {options.MaxFindingsPerDetector}-finding cap. Dropped {dropped.Length} " +
                    $"finding(s): Info={droppedInfo}, Warning={droppedWarning}, Error={droppedError}; " +
                    $"worst dropped severity {worstDropped}.",
                Bounds = bounds,
                Cells = [],
                Truncation = new SmoothingTruncation(
                    detectorFindings.Length, kept.Length, droppedInfo, droppedWarning, droppedError),
            });
        }

        var findings = result
            .Select(finding => finding.Cells.Count > options.MaxCellsPerFinding
                ? finding with
                {
                    Cells = finding.Cells.Take(options.MaxCellsPerFinding).ToArray(),
                    CellsTruncated = true,
                }
                : finding)
            .ToArray();

        var uncapped = new SmoothingSeverityCounts(
            ordered.Count(finding => finding.Severity == SmoothingFindingSeverity.Info),
            ordered.Count(finding => finding.Severity == SmoothingFindingSeverity.Warning),
            ordered.Count(finding => finding.Severity == SmoothingFindingSeverity.Error));
        var uncappedByDetector = ordered
            .GroupBy(finding => finding.Detector)
            .OrderBy(group => SmoothingDetectorOrder.Ordinal(group.Key))
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new SmoothingDetectorSeverityCounts(
                group.Key,
                group.Count(finding => finding.Severity == SmoothingFindingSeverity.Info),
                group.Count(finding => finding.Severity == SmoothingFindingSeverity.Warning),
                group.Count(finding => finding.Severity == SmoothingFindingSeverity.Error)))
            .ToArray();

        return new CappedFindings(findings, uncapped, uncappedByDetector);
    }

    private readonly record struct CappedFindings(
        IReadOnlyList<SmoothingFinding> Findings,
        SmoothingSeverityCounts Uncapped,
        IReadOnlyList<SmoothingDetectorSeverityCounts> UncappedByDetector);

    private static int Compare(SmoothingFinding left, SmoothingFinding right)
    {
        var comparison = SmoothingDetectorOrder.Ordinal(left.Detector)
            .CompareTo(SmoothingDetectorOrder.Ordinal(right.Detector));
        if (comparison != 0) return comparison;
        comparison = string.CompareOrdinal(left.Code, right.Code);
        if (comparison != 0) return comparison;
        comparison = left.Bounds.MinZ.CompareTo(right.Bounds.MinZ);
        if (comparison != 0) return comparison;
        comparison = left.Bounds.MinY.CompareTo(right.Bounds.MinY);
        if (comparison != 0) return comparison;
        comparison = left.Bounds.MinX.CompareTo(right.Bounds.MinX);
        if (comparison != 0) return comparison;
        return string.CompareOrdinal(left.Explanation, right.Explanation);
    }
}
