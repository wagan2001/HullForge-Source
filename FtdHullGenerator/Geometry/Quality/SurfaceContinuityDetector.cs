using System.Numerics;

namespace FtdHullGenerator.Geometry;

/// <summary>
/// Continuity and coverage evidence from analytic section contours. It cuts the corrected hull skin with
/// planes perpendicular to X, Y and Z, stitches the segments into contour loops, and looks for interrupted
/// joins (a dangling open endpoint where a closed hull should cut into a loop) and residual caps (a short
/// edge between two sharp corners where the contour reverses direction, so the surface folds back on
/// itself). A sustained chine, a flare or an open deck edge is not a defect: a sharp corner between two
/// long edges is a deliberate run, and every candidate event is matched against the identical baseline
/// section, so a feature present in both hulls is never attributed to smoothing.
/// </summary>
/// <remarks>
/// X and Z sections cover bow/stern and vertical transitions as well as Y sections cover lateral ledges,
/// so a defect a lateral X-extent check misses is still seen. Section intersections are analytic, so the
/// reported <see cref="SmoothingFinding.SamplingResolution" /> is null.
/// </remarks>
public static class SurfaceContinuityDetector
{
    public const string DetectorName = SmoothingDetectorOrder.Continuity;

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

        var baselineQuery = new SurfaceSectionQuery(baseline, options.SectionPointTolerance);
        var candidateQuery = new SurfaceSectionQuery(candidate, options.SectionPointTolerance);
        var offset = context.SectionOffset;

        var baselineEvents = new List<ContinuityEvent>();
        var candidateEvents = new List<ContinuityEvent>();
        var coplanar = 0;
        var degenerate = 0;
        var missingSections = 0;
        foreach (var axis in new[] { SectionAxis.X, SectionAxis.Y, SectionAxis.Z })
        {
            var baselineSections = baselineQuery.CutAll(axis, options.SectionStep, offset, SurfaceScope.HullSkin);
            var candidateSections = candidateQuery.CutAll(axis, options.SectionStep, offset, SurfaceScope.HullSkin);
            var count = Math.Max(baselineSections.Count, candidateSections.Count);
            for (var index = 0; index < count; index++)
            {
                var baselineSection = index < baselineSections.Count ? baselineSections[index] : null;
                var candidateSection = index < candidateSections.Count ? candidateSections[index] : null;
                if (candidateSection is not null)
                {
                    coplanar += candidateSection.CoplanarFaces;
                    degenerate += candidateSection.DegenerateTouches;
                    Extract(candidateSection, candidateEvents, options);
                }

                if (baselineSection is not null) Extract(baselineSection, baselineEvents, options);
                if (candidateSection is not null && candidateSection.Segments.Count == 0 &&
                    baselineSection is not null && baselineSection.Segments.Count > 0)
                    missingSections++;
            }
        }

        // Match every candidate event against the identical baseline section. A baseline event within one
        // cell means the candidate preserved a baseline feature; only unmatched events are introduced.
        var baselineIndex = BuildIndex(baselineEvents);
        var introduced = new List<ContinuityEvent>();
        var presentInBaseline = 0;
        var matchedBaseline = new HashSet<ContinuityEvent>();
        foreach (var continuityEvent in candidateEvents)
        {
            var match = FindMatch(baselineIndex, continuityEvent);
            if (match is null)
            {
                introduced.Add(continuityEvent);
                continue;
            }

            presentInBaseline++;
            matchedBaseline.Add(match);
        }

        var improved = baselineEvents.Count(continuityEvent => !matchedBaseline.Contains(continuityEvent));
        var findings = MergeEvents(introduced, baseline, candidate, options);

        var uncertain = coplanar > 0 || missingSections > 0;
        if (uncertain)
        {
            var reason = coplanar > 0
                ? $"{coplanar} face(s) were coplanar with a section plane"
                : $"{missingSections} section(s) lost all segments";
            findings.Add(new SmoothingFinding
            {
                Code = "CONTINUITY_UNCERTAIN",
                Detector = DetectorName,
                Severity = SmoothingFindingSeverity.Info,
                MeasurementName = "continuity.uncertain",
                MeasuredValue = 1,
                Threshold = 1,
                Classification = BaselineClassification.Unknown,
                SurfaceScope = SurfaceScope.HullSkin,
                SamplingResolution = null,
                Explanation =
                    $"Continuity evidence is uncertain: {reason}. The section intersection is ambiguous, " +
                    "so no confident continuity verdict is issued for that station.",
                Bounds = HullCellBounds.FromCells([]),
                Cells = [],
            });
        }

        var metrics = new Dictionary<string, double>
        {
            ["continuity.events"] = candidateEvents.Count,
            ["continuity.introduced"] = introduced.Count,
            ["continuity.presentInBaseline"] = presentInBaseline,
            ["continuity.improved"] = improved,
            ["continuity.coplanarFaces"] = coplanar,
            ["continuity.degenerateTouches"] = degenerate,
            ["continuity.missingSections"] = missingSections,
            ["continuity.uncertain"] = uncertain ? 1 : 0,
        };
        return new DetectorResult(findings, metrics);
    }

    private enum ContinuityEventKind
    {
        InterruptedJoin,
        ResidualCap,
    }

    private sealed record ContinuityEvent(
        SectionAxis Axis,
        ContinuityEventKind Kind,
        (int X, int Y, int Z) Cell,
        double Magnitude);

    /// <summary>
    /// Extracts high-confidence continuity events. An interrupted join requires a genuinely dangling
    /// endpoint (the section reported at least one open endpoint), so a branch where several fragments
    /// meet is not mistaken for a gap. A residual cap requires a short edge whose two adjacent long edges
    /// run back on themselves: the surface reverses rather than continuing smoothly, which is what
    /// separates a cap from an intentional chine or a lattice corner.
    /// </summary>
    private static void Extract(
        SurfaceSection section, List<ContinuityEvent> events, SmoothingQualityOptions options)
    {
        var sharpCosine = Math.Cos(Math.Clamp(options.ContinuitySharpAngleDegrees, 0, 180) * Math.PI / 180.0);
        var shortEdge = Math.Max(0, options.ContinuityShortEdgeCells);
        foreach (var contour in section.Contours)
        {
            var points = contour.Points;
            if (points.Count < 2) continue;
            var count = points.Count;
            if (!contour.IsClosed && section.OpenEndpoints > 0)
            {
                events.Add(new ContinuityEvent(section.Axis, ContinuityEventKind.InterruptedJoin,
                    ToCell(points[0]), 0));
                events.Add(new ContinuityEvent(section.Axis, ContinuityEventKind.InterruptedJoin,
                    ToCell(points[^1]), 0));
            }

            var edges = contour.IsClosed ? count : count - 1;
            for (var i = 0; i < edges; i++)
            {
                var a = points[i];
                var b = points[(i + 1) % count];
                var length = (b - a).Length();
                if (length > shortEdge) continue;
                if (!IsSharp(points, i, contour.IsClosed, count, sharpCosine)) continue;
                if (!IsSharp(points, (i + 1) % count, contour.IsClosed, count, sharpCosine)) continue;
                if (!IsReversal(points, i, contour.IsClosed, count, sharpCosine)) continue;
                events.Add(new ContinuityEvent(section.Axis, ContinuityEventKind.ResidualCap,
                    ToCell((a + b) * 0.5f), length));
            }
        }
    }

    private static bool IsSharp(
        IReadOnlyList<Vector3> points, int index, bool closed, int count, double sharpCosine)
    {
        if (!closed && (index == 0 || index == count - 1)) return false;
        var previous = points[(index - 1 + count) % count];
        var current = points[index];
        var next = points[(index + 1) % count];
        var incoming = current - previous;
        var outgoing = next - current;
        if (incoming.LengthSquared() < 1e-18 || outgoing.LengthSquared() < 1e-18) return false;
        var cosine = Vector3.Dot(Vector3.Normalize(incoming), Vector3.Normalize(outgoing));
        return cosine <= sharpCosine;
    }

    /// <summary>
    /// True when the edges on either side of the short edge run back on themselves, which is the
    /// signature of a cap rather than a smooth continuation. Skipping the short edge, the incoming and
    /// outgoing long edges must point in nearly opposite directions.
    /// </summary>
    private static bool IsReversal(
        IReadOnlyList<Vector3> points, int index, bool closed, int count, double sharpCosine)
    {
        if (!closed && (index == 0 || index == count - 2)) return false;
        var before = points[(index - 1 + count) % count];
        var a = points[index];
        var b = points[(index + 1) % count];
        var after = points[(index + 2) % count];
        var incoming = a - before;
        var outgoing = after - b;
        if (incoming.LengthSquared() < 1e-18 || outgoing.LengthSquared() < 1e-18) return false;
        var cosine = Vector3.Dot(Vector3.Normalize(incoming), Vector3.Normalize(outgoing));
        return cosine <= -sharpCosine;
    }

    private static Dictionary<(SectionAxis Axis, ContinuityEventKind Kind, int X, int Y, int Z), List<ContinuityEvent>>
        BuildIndex(List<ContinuityEvent> events)
    {
        var index = new Dictionary<(SectionAxis, ContinuityEventKind, int, int, int), List<ContinuityEvent>>();
        foreach (var continuityEvent in events)
        {
            var key = (continuityEvent.Axis, continuityEvent.Kind,
                continuityEvent.Cell.X, continuityEvent.Cell.Y, continuityEvent.Cell.Z);
            if (!index.TryGetValue(key, out var list))
            {
                list = [];
                index[key] = list;
            }

            list.Add(continuityEvent);
        }

        return index;
    }

    private static ContinuityEvent? FindMatch(
        Dictionary<(SectionAxis Axis, ContinuityEventKind Kind, int X, int Y, int Z), List<ContinuityEvent>> index,
        ContinuityEvent candidate)
    {
        for (var dx = -1; dx <= 1; dx++)
        for (var dy = -1; dy <= 1; dy++)
        for (var dz = -1; dz <= 1; dz++)
        {
            var key = (candidate.Axis, candidate.Kind,
                candidate.Cell.X + dx, candidate.Cell.Y + dy, candidate.Cell.Z + dz);
            if (index.TryGetValue(key, out var list) && list.Count > 0) return list[0];
        }

        return null;
    }

    private static List<SmoothingFinding> MergeEvents(
        List<ContinuityEvent> introduced,
        SurfaceQualityGeometry baseline,
        SurfaceQualityGeometry candidate,
        SmoothingQualityOptions options)
    {
        var findings = new List<SmoothingFinding>();
        if (introduced.Count == 0) return findings;

        var ordered = introduced
            .OrderBy(entry => entry.Axis)
            .ThenBy(entry => entry.Kind)
            .ThenBy(entry => entry.Cell.X)
            .ThenBy(entry => entry.Cell.Y)
            .ThenBy(entry => entry.Cell.Z)
            .ToList();
        var consumed = new bool[ordered.Count];
        for (var seed = 0; seed < ordered.Count; seed++)
        {
            if (consumed[seed]) continue;
            var queue = new Queue<int>();
            queue.Enqueue(seed);
            consumed[seed] = true;
            var group = new List<ContinuityEvent>();
            while (queue.Count > 0)
            {
                var index = queue.Dequeue();
                group.Add(ordered[index]);
                var current = ordered[index];
                for (var other = 0; other < ordered.Count; other++)
                {
                    if (consumed[other]) continue;
                    var candidateEvent = ordered[other];
                    if (candidateEvent.Axis != current.Axis || candidateEvent.Kind != current.Kind) continue;
                    if (!Adjacent(candidateEvent.Cell, current.Cell)) continue;
                    consumed[other] = true;
                    queue.Enqueue(other);
                }
            }

            // A lone cap is routine lattice detail; a run of caps is a localized signal. Interrupted
            // joins are always reported because a dangling contour endpoint is high-confidence evidence.
            if (group[0].Kind == ContinuityEventKind.ResidualCap &&
                group.Count < Math.Max(1, options.ContinuityMinEvents))
                continue;

            findings.Add(BuildFinding(group, baseline, candidate, options));
        }

        return findings;
    }

    private static SmoothingFinding BuildFinding(
        List<ContinuityEvent> group,
        SurfaceQualityGeometry baseline,
        SurfaceQualityGeometry candidate,
        SmoothingQualityOptions options)
    {
        var first = group[0];
        var cells = group.Select(entry => entry.Cell).Distinct()
            .OrderBy(cell => cell.X).ThenBy(cell => cell.Y).ThenBy(cell => cell.Z)
            .ToList();
        var bounds = HullCellBounds.FromCells(cells);
        var code = first.Kind == ContinuityEventKind.InterruptedJoin
            ? "CONTINUITY_INTERRUPTED_JOIN"
            : "CONTINUITY_RESIDUAL_CAP";
        var severity = first.Kind == ContinuityEventKind.InterruptedJoin
            ? SmoothingFindingSeverity.Warning
            : group.Count >= 4
                ? SmoothingFindingSeverity.Warning
                : SmoothingFindingSeverity.Info;
        var label = first.Kind == ContinuityEventKind.InterruptedJoin ? "interrupted join" : "residual cap";
        var worstPatchBounds = HullCellBounds.FromCells([first.Cell]);
        var (placementIds, placementDescription) = PlacementEvidence.Describe(baseline, candidate, cells);
        return new SmoothingFinding
        {
            Code = code,
            Detector = DetectorName,
            Severity = severity,
            MeasurementName = "continuity.events",
            MeasuredValue = group.Count,
            Threshold = options.ContinuityMinEvents,
            Classification = BaselineClassification.Introduced,
            BaselineValue = 0,
            CandidateValue = group.Count,
            SurfaceScope = SurfaceScope.HullSkin,
            SamplingResolution = null,
            SourcePlacementIds = placementIds,
            PlacementDescription = placementDescription,
            Explanation =
                $"{label} on {first.Axis} section at {bounds.ToDisplay()}: {group.Count} event(s) absent " +
                "from the identical baseline sections. A sustained chine, flare or open deck edge is not " +
                "reported; only a dangling contour endpoint or a short edge whose surface reverses counts.",
            Bounds = bounds,
            Cells = cells,
            WorstPatchBounds = worstPatchBounds,
            WorstPatch = $"{label} on {first.Axis} section at cell {first.Cell} ({worstPatchBounds.ToDisplay()})",
        };
    }

    private static bool Adjacent((int X, int Y, int Z) left, (int X, int Y, int Z) right)
    {
        var dx = Math.Abs(left.X - right.X);
        var dy = Math.Abs(left.Y - right.Y);
        var dz = Math.Abs(left.Z - right.Z);
        return dx + dy + dz <= 1;
    }

    private static (int X, int Y, int Z) ToCell(Vector3 point) => (
        (int)Math.Floor(point.X + 0.5),
        (int)Math.Floor(point.Y + 0.5),
        (int)Math.Floor(point.Z + 0.5));
}
