namespace FtdHullGenerator.Geometry;

/// <summary>
/// Finds bounded patches where the candidate's exposed or step area worsens versus the baseline. Unlike
/// a whole-hull or whole-component net, evidence is measured inside fixed-size, overlapping spatial
/// windows plus a face-neighbour support collar, so a remote improvement cannot hide a local defect: the
/// detector reports the worst local window, never a global net. Positive windows that overlap are merged
/// into one finding only after they have been measured, and the finding keeps the worst window's exact
/// measurement and location.
/// </summary>
/// <remarks>
/// Additive exposed-area growth is classified, not blanket-suppressed. A window that adds step faces is
/// a new ledge, lip or spike and is reported whether or not any baseline cell was removed; a window that
/// worsens exposed area while removing baseline material is a dent. Only exposed growth that reduces step
/// area with no removal is a fairing (a slope that only adds a smooth surface), and that is counted as
/// fairing evidence rather than reported as a regression. This is a measurement distinction, not a
/// "pure additive" exemption.
/// </remarks>
public static class LocalSurfaceRegressionDetector
{
    public const string DetectorName = SmoothingDetectorOrder.LocalRegression;

    private const double AreaTolerance = 1e-9;

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

        var baselineAreas = baseline.MeasureExposedSurface(out _);
        var candidateAreas = candidate.MeasureExposedSurface(out _);

        // Per-cell deltas over the union of both occupancy sets. A cell absent from a hull is zero.
        var cells = baselineAreas.Keys.Concat(candidateAreas.Keys).Distinct()
            .OrderBy(cell => cell.X).ThenBy(cell => cell.Y).ThenBy(cell => cell.Z).ToArray();
        var exposedDelta = new Dictionary<(int X, int Y, int Z), double>();
        var stepDelta = new Dictionary<(int X, int Y, int Z), double>();
        var changed = new List<(int X, int Y, int Z)>();
        foreach (var cell in cells)
        {
            var before = Get(baselineAreas, cell);
            var after = Get(candidateAreas, cell);
            var exposed = after.Exposed - before.Exposed;
            var step = after.Step - before.Step;
            if (Math.Abs(exposed) <= AreaTolerance && Math.Abs(step) <= AreaTolerance) continue;
            exposedDelta[cell] = exposed;
            stepDelta[cell] = step;
            changed.Add(cell);
        }

        var metrics = new Dictionary<string, double>
        {
            ["regression.patches"] = 0,
            ["regression.worstExposedDelta"] = 0,
            ["regression.worstStepDelta"] = 0,
            ["regression.fairingWindows"] = 0,
            ["regression.fairingExposedDelta"] = 0,
            ["regression.windowsMeasured"] = 0,
        };
        if (changed.Count == 0)
            return new DetectorResult([], metrics);

        var size = Math.Max(1, options.RegressionWindowSize);
        var stride = Math.Max(1, options.RegressionWindowStride);
        var collar = Math.Max(0, options.RegressionSupportRadius);
        var minimumCells = Math.Max(1, options.RegressionMinCells);

        var minX = changed.Min(cell => cell.X);
        var maxX = changed.Max(cell => cell.X);
        var minY = changed.Min(cell => cell.Y);
        var maxY = changed.Max(cell => cell.Y);
        var minZ = changed.Min(cell => cell.Z);
        var maxZ = changed.Max(cell => cell.Z);

        // Accumulate each changed cell's delta into every window whose window-plus-collar region covers
        // it. Only changed cells carry area, so this is O(changed cells) instead of O(volume / stride^3).
        var netExposed = new Dictionary<(int X, int Y, int Z), double>();
        var netStep = new Dictionary<(int X, int Y, int Z), double>();
        var windowCells = new Dictionary<(int X, int Y, int Z), int>();
        foreach (var cell in changed)
        {
            var exposed = exposedDelta[cell];
            var step = stepDelta[cell];
            for (var ox = OriginLow(cell.X, minX, size, stride, collar);
                 ox <= cell.X + collar;
                 ox += stride)
            for (var oy = OriginLow(cell.Y, minY, size, stride, collar);
                 oy <= cell.Y + collar;
                 oy += stride)
            for (var oz = OriginLow(cell.Z, minZ, size, stride, collar);
                 oz <= cell.Z + collar;
                 oz += stride)
            {
                var origin = (ox, oy, oz);
                netExposed[origin] = netExposed.GetValueOrDefault(origin) + exposed;
                netStep[origin] = netStep.GetValueOrDefault(origin) + step;
                if (cell.X >= ox && cell.X < ox + size &&
                    cell.Y >= oy && cell.Y < oy + size &&
                    cell.Z >= oz && cell.Z < oz + size)
                    windowCells[origin] = windowCells.GetValueOrDefault(origin) + 1;
            }
        }

        metrics["regression.windowsMeasured"] = netExposed.Count;

        // Classify every window. A positive step delta is new roughness (ledge/lip/spike) and is always
        // a regression. Positive exposed area with a removal is a dent; positive exposed with added step
        // is a lip. Positive exposed with removed step and no removal is a fairing, counted not reported.
        var positive = new List<WindowMeasurement>();
        var fairingWindows = 0;
        var protectedWindows = 0;
        var worstFairingExposed = 0d;
        foreach (var origin in netExposed.Keys
                     .OrderBy(value => value.X).ThenBy(value => value.Y).ThenBy(value => value.Z))
        {
            if (windowCells.GetValueOrDefault(origin) < minimumCells) continue;
            var windowExposed = netExposed[origin];
            var windowStep = netStep[origin];
            var exposedCrosses = windowExposed >= options.RegressionMinExposedArea;
            var stepCrosses = windowStep >= options.RegressionMinStepArea;
            if (!exposedCrosses && !stepCrosses) continue;

            var detail = MeasureWindow(baselineAreas, candidateAreas, exposedDelta, stepDelta,
                origin, size, collar);
            // A fairing adds diagonal exposed area while removing step faces; a lip or spike adds
            // mostly step-like surface. Classifying by the step share of the growth keeps a fairing out
            // of the regression list without the old "no removal means additive" blanket exemption.
            var additiveLip = windowStep > 0 &&
                              windowStep >= windowExposed * options.RegressionAdditiveStepFraction;
            if (!stepCrosses && detail.RemovedCells == 0 && !additiveLip)
            {
                fairingWindows++;
                worstFairingExposed = Math.Max(worstFairingExposed, windowExposed);
                continue;
            }

            // A window the caller declared intentional (for example a designed chine or flare) is not
            // attributed to smoothing. It is counted, never silently dropped.
            if (context.IsRegionProtected(detail.ChangedCells))
            {
                protectedWindows++;
                continue;
            }

            var useExposed = windowExposed - options.RegressionMinExposedArea >=
                             windowStep - options.RegressionMinStepArea;
            var crossing = useExposed ? windowExposed : windowStep;
            var warningThreshold = useExposed
                ? options.RegressionWarningExposedArea
                : options.RegressionWarningStepArea;
            var severity = Severity(crossing, warningThreshold, options);
            positive.Add(new WindowMeasurement(origin, size, useExposed, crossing, severity, detail));
        }

        metrics["regression.fairingWindows"] = fairingWindows;
        metrics["regression.fairingExposedDelta"] = worstFairingExposed;
        metrics["regression.protectedWindows"] = protectedWindows;

        var findings = MergeWindows(positive, baseline, candidate, options, size, metrics);
        return new DetectorResult(findings, metrics);
    }

    /// <summary>
    /// The lowest window origin on the global stride grid whose window-plus-collar still covers the cell.
    /// Origins are anchored at the changed-region minimum so they are deterministic.
    /// </summary>
    private static int OriginLow(int cell, int regionMin, int size, int stride, int collar)
    {
        var lowest = cell - (size - 1) - collar;
        if (lowest <= regionMin) return regionMin;
        var steps = (lowest - regionMin + stride - 1) / stride;
        return regionMin + steps * stride;
    }

    /// <summary>
    /// Merges overlapping positive windows into one finding per connected group. The group keeps the
    /// worst window's exact measurement, bounds and evidence cells; only the grouping happens after the
    /// measurement, so a large improvement cannot dilute the reported defect.
    /// </summary>
    private static List<SmoothingFinding> MergeWindows(
        List<WindowMeasurement> positive,
        SurfaceQualityGeometry baseline,
        SurfaceQualityGeometry candidate,
        SmoothingQualityOptions options,
        int size,
        Dictionary<string, double> metrics)
    {
        var findings = new List<SmoothingFinding>();
        if (positive.Count == 0) return findings;

        var ordered = positive
            .OrderBy(window => window.Origin.X)
            .ThenBy(window => window.Origin.Y)
            .ThenBy(window => window.Origin.Z)
            .ToList();
        var index = new Dictionary<(int X, int Y, int Z), int>();
        for (var i = 0; i < ordered.Count; i++) index[ordered[i].Origin] = i;
        var parent = Enumerable.Range(0, ordered.Count).ToArray();
        var stride = Math.Max(1, options.RegressionWindowStride);
        var reach = (ordered[0].Size - 1 + stride - 1) / stride;
        for (var i = 0; i < ordered.Count; i++)
        {
            var origin = ordered[i].Origin;
            for (var dx = -reach; dx <= reach; dx++)
            for (var dy = -reach; dy <= reach; dy++)
            for (var dz = -reach; dz <= reach; dz++)
            {
                if (dx == 0 && dy == 0 && dz == 0) continue;
                var neighbour = (origin.X + dx * stride, origin.Y + dy * stride, origin.Z + dz * stride);
                if (index.TryGetValue(neighbour, out var other)) Union(parent, i, other);
            }
        }

        var groups = new Dictionary<int, List<int>>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var root = Find(parent, i);
            if (!groups.TryGetValue(root, out var group))
            {
                group = [];
                groups[root] = group;
            }

            group.Add(i);
        }

        double worstExposed = 0, worstStep = 0;
        foreach (var root in groups.Keys.OrderBy(key => key))
        {
            var group = groups[root];
            var worst = group
                .Select(position => ordered[position])
                .OrderByDescending(window => (int)window.Severity)
                .ThenByDescending(window => window.Crossing)
                .ThenBy(window => window.Origin.X)
                .ThenBy(window => window.Origin.Y)
                .ThenBy(window => window.Origin.Z)
                .First();
            var detail = worst.Detail;
            worstExposed = Math.Max(worstExposed, worst.UseExposed ? worst.Crossing : detail.NetExposed);
            worstStep = Math.Max(worstStep, worst.UseExposed ? detail.NetStep : worst.Crossing);
            var measurement = worst.UseExposed ? "regression.exposedDelta" : "regression.stepDelta";
            var threshold = worst.Severity switch
            {
                SmoothingFindingSeverity.Error => options.RegressionErrorArea,
                SmoothingFindingSeverity.Warning => worst.UseExposed
                    ? options.RegressionWarningExposedArea
                    : options.RegressionWarningStepArea,
                _ => worst.UseExposed ? options.RegressionMinExposedArea : options.RegressionMinStepArea,
            };
            var evidence = detail.ChangedCells
                .OrderBy(cell => cell.X).ThenBy(cell => cell.Y).ThenBy(cell => cell.Z)
                .ToList();
            var bounds = HullCellBounds.FromCells(evidence.Count > 0 ? evidence : [worst.Origin]);
            var baselineValue = worst.UseExposed ? detail.OriginalExposed : detail.OriginalStep;
            var candidateValue = worst.UseExposed ? detail.RevisedExposed : detail.RevisedStep;
            var classification = baselineValue <= AreaTolerance
                ? BaselineClassification.Introduced
                : BaselineClassification.Worsened;
            var (placementIds, placementDescription) = PlacementEvidence.Describe(baseline, candidate, evidence);
            var kind = worst.UseExposed
                ? detail.RemovedCells > 0 ? "a dent or rough replacement" : "an added exposed lip"
                : "added step roughness";
            var worstPatchBounds = new HullCellBounds(
                worst.Origin.X, worst.Origin.X + size - 1,
                worst.Origin.Y, worst.Origin.Y + size - 1,
                worst.Origin.Z, worst.Origin.Z + size - 1);
            findings.Add(new SmoothingFinding
            {
                Code = worst.UseExposed ? "REGRESSION_EXPOSED" : "REGRESSION_STEP",
                Detector = DetectorName,
                Severity = worst.Severity,
                MeasurementName = measurement,
                MeasuredValue = worst.Crossing,
                Threshold = threshold,
                Classification = classification,
                BaselineValue = baselineValue,
                CandidateValue = candidateValue,
                SurfaceScope = SurfaceScope.HullSkin,
                SamplingResolution = null,
                SourcePlacementIds = placementIds,
                PlacementDescription = placementDescription,
                Explanation =
                    $"Local regression window {size}x{size}x{size} at origin {worst.Origin} " +
                    $"({evidence.Count} changed cell(s), bounds {bounds.ToDisplay()}): " +
                    $"exposed area {detail.OriginalExposed:0.###} -> {detail.RevisedExposed:0.###} m^2 " +
                    $"(delta {detail.NetExposed:0.###}), step area {detail.OriginalStep:0.###} -> " +
                    $"{detail.RevisedStep:0.###} m^2 (delta {detail.NetStep:0.###}), " +
                    $"{detail.RemovedCells} baseline cell(s) removed; this is {kind} and " +
                    $"{measurement} {worst.Crossing:0.###} crossed the {worst.Severity} threshold of " +
                    $"{threshold:0.###}. Classification {classification}.",
                Bounds = bounds,
                Cells = evidence,
                WorstPatchBounds = worstPatchBounds,
                WorstPatch =
                    $"regression window {size}x{size}x{size} at origin {worst.Origin} ({worstPatchBounds.ToDisplay()})",
            });
        }

        metrics["regression.patches"] = findings.Count;
        metrics["regression.worstExposedDelta"] = worstExposed;
        metrics["regression.worstStepDelta"] = worstStep;
        return findings;
    }

    /// <summary>Recomputes the full measurement for one window (the collar plus the window proper).</summary>
    private static WindowDetail MeasureWindow(
        IReadOnlyDictionary<(int X, int Y, int Z), SurfaceCellArea> baselineAreas,
        IReadOnlyDictionary<(int X, int Y, int Z), SurfaceCellArea> candidateAreas,
        IReadOnlyDictionary<(int X, int Y, int Z), double> exposedDelta,
        IReadOnlyDictionary<(int X, int Y, int Z), double> stepDelta,
        (int X, int Y, int Z) origin,
        int size,
        int collar)
    {
        double originalExposed = 0, revisedExposed = 0, originalStep = 0, revisedStep = 0,
            netExposed = 0, netStep = 0;
        var removed = 0;
        var changed = new List<(int X, int Y, int Z)>();
        for (var x = origin.X - collar; x <= origin.X + size - 1 + collar; x++)
        for (var y = origin.Y - collar; y <= origin.Y + size - 1 + collar; y++)
        for (var z = origin.Z - collar; z <= origin.Z + size - 1 + collar; z++)
        {
            var cell = (x, y, z);
            var exposed = exposedDelta.GetValueOrDefault(cell);
            var step = stepDelta.GetValueOrDefault(cell);
            if (Math.Abs(exposed) > AreaTolerance || Math.Abs(step) > AreaTolerance) changed.Add(cell);
            netExposed += exposed;
            netStep += step;
            if (baselineAreas.TryGetValue(cell, out var before))
            {
                originalExposed += before.Exposed;
                originalStep += before.Step;
                if (!candidateAreas.ContainsKey(cell)) removed++;
            }

            if (candidateAreas.TryGetValue(cell, out var after))
            {
                revisedExposed += after.Exposed;
                revisedStep += after.Step;
            }
        }

        return new WindowDetail(originalExposed, revisedExposed, originalStep, revisedStep,
            netExposed, netStep, removed, changed);
    }

    private static SurfaceCellArea Get(
        IReadOnlyDictionary<(int X, int Y, int Z), SurfaceCellArea> areas, (int X, int Y, int Z) cell) =>
        areas.TryGetValue(cell, out var value) ? value : default;

    private static int Find(int[] parent, int index)
    {
        while (parent[index] != index)
        {
            parent[index] = parent[parent[index]];
            index = parent[index];
        }

        return index;
    }

    private static void Union(int[] parent, int left, int right)
    {
        var leftRoot = Find(parent, left);
        var rightRoot = Find(parent, right);
        if (leftRoot != rightRoot) parent[Math.Max(leftRoot, rightRoot)] = Math.Min(leftRoot, rightRoot);
    }

    private static SmoothingFindingSeverity Severity(double crossing, double warningThreshold,
        SmoothingQualityOptions options)
    {
        if (options.RegressionErrorArea > 0 && crossing >= options.RegressionErrorArea)
            return SmoothingFindingSeverity.Error;
        return crossing >= warningThreshold ? SmoothingFindingSeverity.Warning : SmoothingFindingSeverity.Info;
    }

    private sealed record WindowMeasurement(
        (int X, int Y, int Z) Origin,
        int Size,
        bool UseExposed,
        double Crossing,
        SmoothingFindingSeverity Severity,
        WindowDetail Detail);

    private sealed record WindowDetail(
        double OriginalExposed,
        double RevisedExposed,
        double OriginalStep,
        double RevisedStep,
        double NetExposed,
        double NetStep,
        int RemovedCells,
        IReadOnlyList<(int X, int Y, int Z)> ChangedCells);
}
