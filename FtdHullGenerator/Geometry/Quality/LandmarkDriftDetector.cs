namespace FtdHullGenerator.Geometry;

/// <summary>
/// Detects unexpected movement or loss of deck edge, keel, maximum beam, chine/shoulder and flat-bottom
/// runs. It reads only occupancy landmarks, never preset names. Pure additive fills skip only the
/// per-station chine/shoulder comparison, whose widest-point Y legitimately moves as surface is added;
/// deck edge, keel, maximum beam, flat-bottom runs, and lost stations are always compared.
/// </summary>
public static class LandmarkDriftDetector
{
    public const string DetectorName = SmoothingDetectorOrder.LandmarkDrift;

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

        var baselineProfiles = BuildProfiles(baseline);
        var candidateProfiles = BuildProfiles(candidate);
        var sharedStations = baselineProfiles.Keys.Intersect(candidateProfiles.Keys).OrderBy(z => z).ToArray();

        // A chine is never inferred from the widest point alone. It is compared only when the baseline
        // shows a sustained chine/shoulder run or the caller supplies an explicit chine mask. When
        // neither exists the chine comparison is uncertain and reported as such, not as confident drift.
        var sustainedChine = HasSustainedChine(baseline, options.ChineMinRun);
        var chineEvidence = sustainedChine || context.HasChineEvidence;
        var chineClassification = sustainedChine
            ? BaselineClassification.Worsened
            : BaselineClassification.Introduced;

        var findings = new List<SmoothingFinding>();
        var maxDrift = 0d;

        CollectDrift("LANDMARK_DECK_EDGE", "deck edge", sharedStations, baselineProfiles, candidateProfiles,
            (before, after) => Max(
                Math.Abs(after.TopY - before.TopY),
                Math.Abs(after.DeckMaxX - before.DeckMaxX),
                Math.Abs(after.DeckMinX - before.DeckMinX)),
            profile => [(profile.DeckMaxX, profile.TopY, 0), (profile.DeckMinX, profile.TopY, 0)],
            options, context, BaselineClassification.Worsened, findings, ref maxDrift);

        CollectDrift("LANDMARK_KEEL", "keel", sharedStations, baselineProfiles, candidateProfiles,
            (before, after) => Max(
                Math.Abs(after.BottomY - before.BottomY),
                Math.Abs(after.KeelMaxX - before.KeelMaxX),
                Math.Abs(after.KeelMinX - before.KeelMinX)),
            profile => [(profile.KeelMaxX, profile.BottomY, 0), (profile.KeelMinX, profile.BottomY, 0)],
            options, context, BaselineClassification.Worsened, findings, ref maxDrift);

        CollectDrift("LANDMARK_MAX_BEAM", "maximum beam", sharedStations, baselineProfiles, candidateProfiles,
            (before, after) => Max(Math.Abs(after.MaxX - before.MaxX), Math.Abs(after.MinX - before.MinX)),
            profile => [(profile.MaxX, profile.StarboardBeamY, 0), (profile.MinX, profile.PortBeamY, 0)],
            options, context, BaselineClassification.Worsened, findings, ref maxDrift);

        if (chineEvidence)
            CollectDrift("LANDMARK_CHINE", "chine/shoulder", sharedStations, baselineProfiles, candidateProfiles,
                (before, after) => Max(
                    Math.Abs(after.StarboardBeamY - before.StarboardBeamY),
                    Math.Abs(after.PortBeamY - before.PortBeamY)),
                profile => [(profile.MaxX, profile.StarboardBeamY, 0), (profile.MinX, profile.PortBeamY, 0)],
                options, context, chineClassification, findings, ref maxDrift);

        var lostLandmarks = AddLostStationFindings(baseline, baselineProfiles, candidateProfiles, options, findings);
        lostLandmarks += AddFlatBottomFindings(
            baselineProfiles, candidateProfiles, options, findings, ref maxDrift);

        var metrics = new Dictionary<string, double>
        {
            ["landmark.driftRegions"] = findings.Count - lostLandmarks,
            ["landmark.maxDriftCells"] = maxDrift,
            ["landmark.lostLandmarks"] = lostLandmarks,
            ["landmark.chineUncertain"] = chineEvidence ? 0 : 1,
        };
        return new DetectorResult(findings, metrics);
    }

    /// <summary>
    /// True when the baseline has a strict interior shoulder (the widest X extent at some Y is strictly
    /// greater than the extent one cell below and one cell above) at the same Y over at least
    /// <paramref name="minimumRun" /> consecutive stations. A rounded hull whose widest point simply
    /// rises to the deck is not called a chine, because the widest point is not supporting evidence of
    /// an intended chine landmark on its own.
    /// </summary>
    private static bool HasSustainedChine(SurfaceQualityGeometry baseline, int minimumRun)
    {
        var byStation = new SortedDictionary<int, List<(int X, int Y, int Z)>>();
        foreach (var cell in baseline.Occupied)
        {
            if (!byStation.TryGetValue(cell.Z, out var cells))
            {
                cells = [];
                byStation[cell.Z] = cells;
            }

            cells.Add(cell);
        }

        var run = 0;
        int? previousShoulder = null;
        foreach (var (_, cells) in byStation)
        {
            var shoulder = FindShoulderY(cells);
            if (shoulder is not null && shoulder == previousShoulder)
                run++;
            else
                run = shoulder is null ? 0 : 1;
            previousShoulder = shoulder;
            if (run >= Math.Max(1, minimumRun)) return true;
        }

        return false;
    }

    /// <summary>
    /// The Y of a strict interior shoulder in the station's starboard X-extent profile, or null when the
    /// profile is monotone (a rounded or flared hull with no hard chine).
    /// </summary>
    private static int? FindShoulderY(IReadOnlyList<(int X, int Y, int Z)> cells)
    {
        var extentByY = new SortedDictionary<int, int>();
        foreach (var cell in cells)
        {
            if (!extentByY.TryGetValue(cell.Y, out var extent) || cell.X > extent)
                extentByY[cell.Y] = cell.X;
        }

        foreach (var pair in extentByY)
        {
            if (!extentByY.TryGetValue(pair.Key - 1, out var below)) continue;
            if (!extentByY.TryGetValue(pair.Key + 1, out var above)) continue;
            if (pair.Value > below && pair.Value > above) return pair.Key;
        }

        return null;
    }

    /// <summary>
    /// Groups contiguous stations whose landmark moved more than the tolerance into one finding and
    /// records the maximum drift. Stations missing from either hull are skipped.
    /// </summary>
    private static void CollectDrift(
        string code,
        string label,
        IReadOnlyList<int> stations,
        IReadOnlyDictionary<int, StationProfile> baseline,
        IReadOnlyDictionary<int, StationProfile> candidate,
        Func<StationProfile, StationProfile, int> drift,
        Func<StationProfile, (int X, int Y, int Z)[]> cellsAt,
        SmoothingQualityOptions options,
        SmoothingDiagnosticContext context,
        BaselineClassification classification,
        List<SmoothingFinding> findings,
        ref double maxDrift)
    {
        var affected = new List<(int Z, int Drift, (int X, int Y, int Z)[] Cells)>();
        foreach (var z in stations)
        {
            var movement = drift(baseline[z], candidate[z]);
            if (movement <= options.LandmarkToleranceCells) continue;
            affected.Add((z, movement, cellsAt(candidate[z])));
        }

        var start = 0;
        while (start < affected.Count)
        {
            var end = start;
            while (end + 1 < affected.Count && affected[end + 1].Z == affected[end].Z + 1) end++;
            var group = affected.GetRange(start, end - start + 1);
            start = end + 1;

            var movement = group.Max(entry => entry.Drift);
            maxDrift = Math.Max(maxDrift, movement);
            var cells = group
                .SelectMany(entry => entry.Cells.Select(cell => (cell.X, cell.Y, entry.Z)))
                .Distinct()
                .OrderBy(cell => cell.X).ThenBy(cell => cell.Y).ThenBy(cell => cell.Z)
                .ToList();
            if (context.IsRegionProtected(cells)) continue;
            var bounds = HullCellBounds.FromCells(cells);
            findings.Add(new SmoothingFinding
            {
                Code = code,
                Detector = DetectorName,
                Severity = SmoothingFindingSeverity.Warning,
                MeasurementName = "landmark.driftCells",
                MeasuredValue = movement,
                Threshold = options.LandmarkToleranceCells,
                Classification = classification,
                BaselineValue = classification == BaselineClassification.PresentInBaseline ||
                                classification == BaselineClassification.Worsened
                    ? options.LandmarkToleranceCells
                    : 0,
                CandidateValue = movement,
                SurfaceScope = SurfaceScope.HullSkin,
                Explanation =
                    $"{label} drifted {movement} cell(s) over {group.Count} station(s) at " +
                    $"{bounds.ToDisplay()}; drift {movement} cells crossed the warning threshold of " +
                    $"{options.LandmarkToleranceCells} cells. Classification {classification}.",
                Bounds = bounds,
                Cells = cells,
            });
        }
    }

    /// <summary>
    /// Reports each contiguous run of baseline stations that the candidate does not occupy as a lost
    /// landmark. The run's cells and bounds come from the baseline stations that disappeared. Returns
    /// the number of lost runs so the caller can count them in <c>landmark.lostLandmarks</c>.
    /// </summary>
    private static int AddLostStationFindings(
        SurfaceQualityGeometry baseline,
        IReadOnlyDictionary<int, StationProfile> baselineProfiles,
        IReadOnlyDictionary<int, StationProfile> candidateProfiles,
        SmoothingQualityOptions options,
        List<SmoothingFinding> findings)
    {
        var lostStations = baselineProfiles.Keys
            .Where(z => !candidateProfiles.ContainsKey(z))
            .OrderBy(z => z)
            .ToArray();
        var lost = 0;
        var start = 0;
        while (start < lostStations.Length)
        {
            var end = start;
            while (end + 1 < lostStations.Length && lostStations[end + 1] == lostStations[end] + 1) end++;
            var runStart = lostStations[start];
            var runEnd = lostStations[end];
            var length = runEnd - runStart + 1;
            start = end + 1;

            var cells = baseline.Occupied
                .Where(cell => cell.Z >= runStart && cell.Z <= runEnd)
                .OrderBy(cell => cell.X).ThenBy(cell => cell.Y).ThenBy(cell => cell.Z)
                .ToList();
            var bounds = HullCellBounds.FromCells(cells);
            lost++;
            findings.Add(new SmoothingFinding
            {
                Code = "LANDMARK_LOST",
                Detector = DetectorName,
                Severity = options.LandmarkLossIsError
                    ? SmoothingFindingSeverity.Error
                    : SmoothingFindingSeverity.Warning,
                MeasurementName = "landmark.lostStations",
                MeasuredValue = length,
                Threshold = 1,
                Explanation =
                    $"Baseline stations z={runStart}..{runEnd} ({length} station(s)) are absent from the " +
                    $"candidate at {bounds.ToDisplay()}; lost stations {length} crossed the loss threshold of 1.",
                Bounds = bounds,
                Cells = cells,
            });
        }

        return lost;
    }

    /// <summary>
    /// Compares flat-bottom runs (contiguous stations whose keel Y and keel extents are constant).
    /// A baseline run with no matching candidate run is a loss; a run whose constant keel values
    /// changed beyond tolerance is a drift. Returns the number of loss findings.
    /// </summary>
    private static int AddFlatBottomFindings(
        IReadOnlyDictionary<int, StationProfile> baseline,
        IReadOnlyDictionary<int, StationProfile> candidate,
        SmoothingQualityOptions options,
        List<SmoothingFinding> findings,
        ref double maxDrift)
    {
        var baselineRuns = FlatBottomRuns(baseline, options.FlatBottomMinRun);
        var candidateRuns = FlatBottomRuns(candidate, options.FlatBottomMinRun);
        var lost = 0;
        foreach (var run in baselineRuns)
        {
            var overlapping = candidateRuns.Where(other => other.Start <= run.End && other.End >= run.Start).ToArray();
            if (overlapping.Length == 0)
            {
                lost++;
                var cells = Enumerable.Range(run.Start, run.End - run.Start + 1)
                    .Where(z => candidate.ContainsKey(z) || baseline.ContainsKey(z))
                    .SelectMany(z =>
                    {
                        var profile = baseline.TryGetValue(z, out var value) ? value : candidate[z];
                        return new[]
                        {
                            (profile.KeelMaxX, profile.BottomY, z),
                            (profile.KeelMinX, profile.BottomY, z),
                        };
                    })
                    .Distinct()
                    .OrderBy(cell => cell.Item1).ThenBy(cell => cell.Item2).ThenBy(cell => cell.Item3)
                    .ToList();
                findings.Add(new SmoothingFinding
                {
                    Code = "LANDMARK_FLAT_BOTTOM",
                    Detector = DetectorName,
                    Severity = options.LandmarkLossIsError
                        ? SmoothingFindingSeverity.Error
                        : SmoothingFindingSeverity.Warning,
                    MeasurementName = "landmark.flatBottomRunStations",
                    MeasuredValue = run.End - run.Start + 1,
                    Threshold = options.FlatBottomMinRun,
                    Explanation =
                        $"Flat bottom run of {run.End - run.Start + 1} station(s) from z={run.Start} to " +
                        $"z={run.End} was lost at {HullCellBounds.FromCells(cells).ToDisplay()}; the run " +
                        $"crossed the loss threshold of {options.FlatBottomMinRun} stations.",
                    Bounds = HullCellBounds.FromCells(cells),
                    Cells = cells,
                });
                continue;
            }

            var movement = overlapping.Max(other => Max(
                Math.Abs(other.BottomY - run.BottomY),
                Math.Abs(other.KeelMaxX - run.KeelMaxX),
                Math.Abs(other.KeelMinX - run.KeelMinX)));
            if (movement <= options.LandmarkToleranceCells) continue;
            maxDrift = Math.Max(maxDrift, movement);
            var runCells = Enumerable.Range(run.Start, run.End - run.Start + 1)
                .Where(candidate.ContainsKey)
                .SelectMany(z =>
                {
                    var profile = candidate[z];
                    return new[]
                    {
                        (profile.KeelMaxX, profile.BottomY, z),
                        (profile.KeelMinX, profile.BottomY, z),
                    };
                })
                .Distinct()
                .OrderBy(cell => cell.Item1).ThenBy(cell => cell.Item2).ThenBy(cell => cell.Item3)
                .ToList();
            var bounds = HullCellBounds.FromCells(runCells);
            findings.Add(new SmoothingFinding
            {
                Code = "LANDMARK_FLAT_BOTTOM",
                Detector = DetectorName,
                Severity = SmoothingFindingSeverity.Warning,
                MeasurementName = "landmark.driftCells",
                MeasuredValue = movement,
                Threshold = options.LandmarkToleranceCells,
                Explanation =
                    $"Flat bottom keel values drifted {movement} cell(s) from z={run.Start} to z={run.End} " +
                    $"at {bounds.ToDisplay()}; drift {movement} cells crossed the warning threshold of " +
                    $"{options.LandmarkToleranceCells} cells.",
                Bounds = bounds,
                Cells = runCells,
            });
        }

        return lost;
    }

    private static List<(int Start, int End, int BottomY, int KeelMaxX, int KeelMinX)> FlatBottomRuns(
        IReadOnlyDictionary<int, StationProfile> profiles, int minimumRun)
    {
        var runs = new List<(int Start, int End, int BottomY, int KeelMaxX, int KeelMinX)>();
        var stations = profiles.Keys.OrderBy(z => z).ToArray();
        var start = 0;
        while (start < stations.Length)
        {
            var first = profiles[stations[start]];
            var end = start;
            while (end + 1 < stations.Length &&
                   stations[end + 1] == stations[end] + 1 &&
                   profiles[stations[end + 1]].BottomY == first.BottomY &&
                   profiles[stations[end + 1]].KeelMaxX == first.KeelMaxX &&
                   profiles[stations[end + 1]].KeelMinX == first.KeelMinX)
                end++;
            if (end - start + 1 >= minimumRun)
                runs.Add((stations[start], stations[end], first.BottomY, first.KeelMaxX, first.KeelMinX));
            start = end + 1;
        }

        return runs;
    }

    private static Dictionary<int, StationProfile> BuildProfiles(SurfaceQualityGeometry geometry)
    {
        var byStation = new SortedDictionary<int, List<(int X, int Y, int Z)>>();
        foreach (var cell in geometry.Occupied)
        {
            if (!byStation.TryGetValue(cell.Z, out var cells))
            {
                cells = [];
                byStation[cell.Z] = cells;
            }

            cells.Add(cell);
        }

        var profiles = new Dictionary<int, StationProfile>();
        foreach (var (z, cells) in byStation)
        {
            var topY = cells.Max(cell => cell.Y);
            var bottomY = cells.Min(cell => cell.Y);
            var maxX = cells.Max(cell => cell.X);
            var minX = cells.Min(cell => cell.X);
            var deck = cells.Where(cell => cell.Y == topY).ToArray();
            var keel = cells.Where(cell => cell.Y == bottomY).ToArray();
            profiles[z] = new StationProfile(
                topY,
                deck.Max(cell => cell.X),
                deck.Min(cell => cell.X),
                bottomY,
                keel.Max(cell => cell.X),
                keel.Min(cell => cell.X),
                maxX,
                minX,
                cells.Where(cell => cell.X == maxX).Min(cell => cell.Y),
                cells.Where(cell => cell.X == minX).Min(cell => cell.Y));
        }

        return profiles;
    }

    private static int Max(params int[] values) => values.Max();

    private readonly record struct StationProfile(
        int TopY, int DeckMaxX, int DeckMinX,
        int BottomY, int KeelMaxX, int KeelMinX,
        int MaxX, int MinX, int StarboardBeamY, int PortBeamY);
}
