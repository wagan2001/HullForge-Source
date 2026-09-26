namespace FtdHullGenerator.Geometry;

/// <summary>
/// Detects short-period one-cell teeth and alternating inward/outward reversals along the candidate's
/// outer contours. A monotone slope or a single deliberate chine is not flagged; a cluster needs at
/// least <see cref="SmoothingQualityOptions.JagMinReversals" /> reversals of at most
/// <see cref="SmoothingQualityOptions.JagMaxToothDelta" /> cells within a bounded range and window.
/// </summary>
public static class JaggedBlendDetector
{
    public const string DetectorName = SmoothingDetectorOrder.Jagged;

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

        var contours = BuildContours(candidate);
        var partials = new List<PartialJag>();
        foreach (var contour in contours)
            partials.AddRange(FindClusters(contour, options));

        var merged = Merge(partials, candidate.MinX + candidate.MaxX);

        // The same contour extraction on the baseline lets every candidate cluster be classified as
        // introduced, worsened, present in the baseline, or improved. A baseline abnormality is never
        // attributed to smoothing just because both hulls contain it; it is reported as baseline evidence.
        var baselinePartials = new List<PartialJag>();
        foreach (var contour in BuildContours(baseline))
            baselinePartials.AddRange(FindClusters(contour, options));
        var baselineMerged = Merge(baselinePartials, baseline.MinX + baseline.MaxX);

        var findings = new List<SmoothingFinding>(merged.Count);
        var totalOscillations = 0;
        var worstChange = 0;
        var introduced = 0;
        var presentInBaseline = 0;
        var improved = 0;
        var matchedBaseline = new bool[baselineMerged.Count];
        foreach (var partial in merged)
        {
            totalOscillations += partial.Oscillations;
            worstChange = Math.Max(worstChange, partial.WorstChange);

            var baselineOscillations = 0;
            for (var index = 0; index < baselineMerged.Count; index++)
            {
                if (!Related(partial, baselineMerged[index], candidate.MinX + candidate.MaxX)) continue;
                matchedBaseline[index] = true;
                baselineOscillations += baselineMerged[index].Oscillations;
            }

            var classification = baselineOscillations == 0
                ? BaselineClassification.Introduced
                : partial.Oscillations > baselineOscillations
                    ? BaselineClassification.Worsened
                    : partial.Oscillations == baselineOscillations
                        ? BaselineClassification.PresentInBaseline
                        : BaselineClassification.Improved;
            switch (classification)
            {
                case BaselineClassification.Introduced:
                case BaselineClassification.Worsened:
                    introduced++;
                    break;
                case BaselineClassification.PresentInBaseline:
                    presentInBaseline++;
                    break;
                default:
                    improved++;
                    break;
            }

            var severity = partial.Oscillations == options.JagMinReversals
                ? SmoothingFindingSeverity.Info
                : SmoothingFindingSeverity.Warning;
            var threshold = severity == SmoothingFindingSeverity.Info
                ? options.JagMinReversals
                : options.JagMinReversals + 1;
            var cells = partial.Cells.OrderBy(cell => cell.X).ThenBy(cell => cell.Y).ThenBy(cell => cell.Z).ToList();
            var bounds = HullCellBounds.FromCells(cells);
            var code = partial.Direction switch
            {
                "longitudinal" => "JAG_LONGITUDINAL",
                "vertical" => "JAG_VERTICAL",
                _ => "JAG_TRANSVERSE",
            };
            var (placementIds, placementDescription) = PlacementEvidence.Describe(baseline, candidate, cells);
            findings.Add(new SmoothingFinding
            {
                Code = code,
                Detector = DetectorName,
                Severity = severity,
                MeasurementName = "jagged.oscillations",
                MeasuredValue = partial.Oscillations,
                Threshold = threshold,
                Classification = classification,
                BaselineValue = baselineOscillations,
                CandidateValue = partial.Oscillations,
                SurfaceScope = SurfaceScope.HullSkin,
                SamplingResolution = 1.0,
                SourcePlacementIds = placementIds,
                PlacementDescription = placementDescription,
                Explanation =
                    $"{partial.Direction} contour ({partial.Axis} axis) at {partial.Label} oscillates " +
                    $"{partial.Oscillations} time(s) over a range of {partial.Range} cells with a worst " +
                    $"change of {partial.WorstChange} cell(s) at {bounds.ToDisplay()}; oscillations " +
                    $"{partial.Oscillations} crossed the {severity} threshold of {threshold}. " +
                    $"Baseline oscillated {baselineOscillations} time(s): {classification}.",
                Bounds = bounds,
                Cells = cells,
            });
        }

        var baselineOnly = 0;
        for (var index = 0; index < baselineMerged.Count; index++)
            if (!matchedBaseline[index]) baselineOnly++;

        var metrics = new Dictionary<string, double>
        {
            ["jagged.affectedLines"] = findings.Count,
            ["jagged.oscillations"] = totalOscillations,
            ["jagged.worstChangeCells"] = worstChange,
            ["jagged.introducedOrWorsened"] = introduced,
            ["jagged.presentInBaseline"] = presentInBaseline,
            ["jagged.improved"] = improved,
            ["jagged.baselineOnly"] = baselineOnly,
        };
        return new DetectorResult(findings, metrics);
    }

    /// <summary>
    /// Builds every candidate contour sequence: side extents over Z and over Y, deck top Y over Z and
    /// keel bottom Y over Z. Empty runs are skipped, so a gap never bridges two contours.
    /// </summary>
    private static List<Contour> BuildContours(SurfaceQualityGeometry geometry)
    {
        var contours = new List<Contour>();
        var rowsByY = GroupStations(geometry.MaxXByRow.Keys.Select(key => (Group: key.Y, Station: key.Z)));
        var rowsByZ = GroupStations(geometry.MaxXByRow.Keys.Select(key => (Group: key.Z, Station: key.Y)));
        var columnsByX = GroupStations(geometry.MaxYByColumn.Keys.Select(key => (Group: key.X, Station: key.Z)));

        foreach (var (y, stations) in rowsByY)
        foreach (var side in Sides)
        foreach (var run in SplitRuns(stations))
        {
            var points = run.Select(z => (
                Station: z,
                Value: Extent(side, geometry.MaxXByRow[(y, z)], geometry.MinXByRow[(y, z)]),
                Cell: (X: side > 0 ? geometry.MaxXByRow[(y, z)] : geometry.MinXByRow[(y, z)], Y: y, Z: z))).ToList();
            if (points.Count >= 2)
                contours.Add(new Contour("Z", "longitudinal", $"{SideName(side)} side at y={y}", points));
        }

        foreach (var (z, stations) in rowsByZ)
        foreach (var side in Sides)
        foreach (var run in SplitRuns(stations))
        {
            var points = run.Select(y => (
                Station: y,
                Value: Extent(side, geometry.MaxXByRow[(y, z)], geometry.MinXByRow[(y, z)]),
                Cell: (X: side > 0 ? geometry.MaxXByRow[(y, z)] : geometry.MinXByRow[(y, z)], Y: y, Z: z))).ToList();
            if (points.Count >= 2)
                contours.Add(new Contour("Y", "vertical", $"{SideName(side)} side at z={z}", points));
        }

        foreach (var (x, stations) in columnsByX)
        foreach (var run in SplitRuns(stations))
        {
            var points = run.Select(z => (
                Station: z,
                Value: geometry.MaxYByColumn[(x, z)],
                Cell: (X: x, Y: geometry.MaxYByColumn[(x, z)], Z: z))).ToList();
            if (points.Count >= 2)
                contours.Add(new Contour("Z", "longitudinal", $"deck at x={x}", points));
        }

        foreach (var (x, stations) in columnsByX)
        foreach (var run in SplitRuns(stations))
        {
            var points = run.Select(z => (
                Station: z,
                Value: geometry.MinYByColumn[(x, z)],
                Cell: (X: x, Y: geometry.MinYByColumn[(x, z)], Z: z))).ToList();
            if (points.Count >= 2)
                contours.Add(new Contour("Z", "longitudinal", $"keel at x={x}", points));
        }

        return contours;
    }

    private static List<PartialJag> FindClusters(Contour contour, SmoothingQualityOptions options)
    {
        var points = contour.Points;
        var count = points.Count;
        if (count < 2) return [];

        // First differences with zero deltas dropped: only direction changes of the occupied contour matter.
        var deltas = new List<(int Index, int Value)>();
        for (var i = 0; i + 1 < count; i++)
        {
            var delta = points[i + 1].Value - points[i].Value;
            if (delta != 0) deltas.Add((i, delta));
        }

        if (deltas.Count < 2) return [];

        var reversals = new List<(int Position, int Point)>();
        for (var k = 1; k < deltas.Count; k++)
        {
            var left = deltas[k - 1];
            var right = deltas[k];
            if (Math.Sign(left.Value) == Math.Sign(right.Value)) continue;
            if (right.Index - left.Index > options.JagMaxPeriod) continue;
            // The direction change happens at the shared vertex, point index right.Index.
            if (right.Index < options.JagIgnoreEnds) continue;
            if (right.Index > count - 1 - options.JagIgnoreEnds) continue;
            reversals.Add((k, right.Index));
        }

        var partials = new List<PartialJag>();
        var start = 0;
        while (start < reversals.Count)
        {
            var end = start;
            while (end + 1 < reversals.Count && reversals[end + 1].Position == reversals[end].Position + 1)
                end++;
            var cluster = reversals.GetRange(start, end - start + 1);
            start = end + 1;
            if (cluster.Count < options.JagMinReversals) continue;

            // Deltas involved in the cluster, by their original point indices.
            var firstDelta = deltas[cluster[0].Position - 1].Index;
            var lastDelta = deltas[cluster[^1].Position].Index;
            var firstPoint = firstDelta;
            var lastPoint = lastDelta + 1;
            if (lastPoint - firstPoint + 1 > options.JagWindow) continue;

            var range = points.Skip(firstPoint).Take(lastPoint - firstPoint + 1).Max(point => point.Value) -
                        points.Skip(firstPoint).Take(lastPoint - firstPoint + 1).Min(point => point.Value);
            var worst = 0;
            for (var k = cluster[0].Position - 1; k <= cluster[^1].Position; k++)
                worst = Math.Max(worst, Math.Abs(deltas[k].Value));
            if (worst > options.JagMaxToothDelta) continue;
            if (range > options.JagMaxRange) continue;

            var cells = new HashSet<(int X, int Y, int Z)>();
            for (var index = firstPoint; index <= lastPoint; index++)
                cells.Add(points[index].Cell);
            partials.Add(new PartialJag
            {
                Axis = contour.Axis,
                Direction = contour.Direction,
                Label = contour.Label,
                Cells = cells,
                Oscillations = cluster.Count,
                WorstChange = worst,
                Range = range,
            });
        }

        return partials;
    }

    /// <summary>
    /// Merges partial findings whose cells overlap or whose cell sets are X-mirror images. The union
    /// sums oscillations, keeps the largest change/range, and preserves the lexicographically first
    /// label, so the result is order-independent.
    /// </summary>
    private static List<PartialJag> Merge(List<PartialJag> partials, int mirrorSum)
    {
        var ordered = partials
            .OrderBy(partial => partial.Axis, StringComparer.Ordinal)
            .ThenBy(partial => partial.Direction, StringComparer.Ordinal)
            .ThenBy(partial => partial.Label, StringComparer.Ordinal)
            .ThenBy(partial => partial.Cells.Min(cell => cell.X))
            .ThenBy(partial => partial.Cells.Min(cell => cell.Y))
            .ThenBy(partial => partial.Cells.Min(cell => cell.Z))
            .ToList();
        var parent = Enumerable.Range(0, ordered.Count).ToArray();
        for (var i = 0; i < ordered.Count; i++)
        for (var j = i + 1; j < ordered.Count; j++)
        {
            if (!Related(ordered[i], ordered[j], mirrorSum)) continue;
            Union(parent, i, j);
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

        var merged = new List<PartialJag>(groups.Count);
        foreach (var root in groups.Keys.OrderBy(key => key))
        {
            var group = groups[root];
            var first = ordered[group[0]];
            var cells = new HashSet<(int X, int Y, int Z)>();
            var oscillations = 0;
            var worst = 0;
            var range = 0;
            foreach (var index in group)
            {
                cells.UnionWith(ordered[index].Cells);
                oscillations += ordered[index].Oscillations;
                worst = Math.Max(worst, ordered[index].WorstChange);
                range = Math.Max(range, ordered[index].Range);
            }

            merged.Add(new PartialJag
            {
                Axis = first.Axis,
                Direction = first.Direction,
                Label = first.Label,
                Cells = cells,
                Oscillations = oscillations,
                WorstChange = worst,
                Range = range,
            });
        }

        return merged;
    }

    private static bool Related(PartialJag left, PartialJag right, int mirrorSum)
    {
        if (left.Cells.Overlaps(right.Cells)) return true;
        if (left.Cells.Count != right.Cells.Count) return false;
        return left.Cells.All(cell => right.Cells.Contains((mirrorSum - cell.X, cell.Y, cell.Z)));
    }

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

    private static int Extent(int side, int maxX, int minX) => side > 0 ? maxX : -minX;

    private static string SideName(int side) => side > 0 ? "starboard" : "port";

    private static Dictionary<int, List<int>> GroupStations(IEnumerable<(int Group, int Station)> pairs)
    {
        var groups = new Dictionary<int, List<int>>();
        foreach (var (group, station) in pairs)
        {
            if (!groups.TryGetValue(group, out var stations))
            {
                stations = [];
                groups[group] = stations;
            }

            stations.Add(station);
        }

        foreach (var stations in groups.Values) stations.Sort();

        return groups;
    }

    private static IEnumerable<List<int>> SplitRuns(List<int> sorted)
    {
        var run = new List<int>();
        foreach (var station in sorted)
        {
            if (run.Count > 0 && station != run[^1] + 1)
            {
                yield return run;
                run = [];
            }

            run.Add(station);
        }

        if (run.Count > 0) yield return run;
    }

    private static readonly int[] Sides = [1, -1];

    private sealed record Contour(string Axis, string Direction, string Label,
        List<(int Station, int Value, (int X, int Y, int Z) Cell)> Points);

    private sealed class PartialJag
    {
        public required string Axis { get; init; }
        public required string Direction { get; init; }
        public required string Label { get; init; }
        public required HashSet<(int X, int Y, int Z)> Cells { get; init; }
        public required int Oscillations { get; init; }
        public required int WorstChange { get; init; }
        public required int Range { get; init; }
    }
}
