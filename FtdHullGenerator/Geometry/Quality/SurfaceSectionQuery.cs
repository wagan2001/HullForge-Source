using System.Numerics;

namespace FtdHullGenerator.Geometry;

/// <summary>Axis a section plane is perpendicular to.</summary>
public enum SectionAxis
{
    X,
    Y,
    Z,
}

/// <summary>One analytic intersection of a surface fragment with a section plane.</summary>
public sealed record SectionSegment(
    (int X, int Y, int Z) Cell,
    Vector3 Start,
    Vector3 End,
    Vector3 Normal,
    int PlacementIndex,
    SurfaceScope Scope)
{
    /// <summary>Segment length in metres.</summary>
    public double Length => (End - Start).Length();
}

/// <summary>
/// One stitched contour in a section plane. <see cref="IsClosed" /> is false for an open chain, which is
/// itself continuity evidence: a closed hull surface should cut into closed loops.
/// </summary>
public sealed record SectionContour(IReadOnlyList<Vector3> Points, bool IsClosed)
{
    /// <summary>Total polyline length in metres.</summary>
    public double Length
    {
        get
        {
            var total = 0d;
            for (var i = 1; i < Points.Count; i++) total += (Points[i] - Points[i - 1]).Length();
            return total;
        }
    }
}

/// <summary>
/// The intersection of the corrected (export-effective) surface with one plane. Multiple contour loops
/// and open chains are preserved, so cavities and undercuts are not lost the way outermost extents are.
/// </summary>
public sealed record SurfaceSection(
    SectionAxis Axis,
    double Offset,
    IReadOnlyList<SectionSegment> Segments,
    IReadOnlyList<SectionContour> Contours,
    IReadOnlyList<string> Notes,
    double? SamplingResolution,
    int CoplanarFaces,
    int DegenerateTouches,
    int OpenEndpoints,
    int BranchEndpoints);

/// <summary>
/// Reusable analytic intersections of <see cref="SurfaceCoverage.Fragments" /> with planes perpendicular
/// to X, Y and Z. Intersections are computed exactly from the retained fragment polygons, not by
/// sampling a voxel grid; <see cref="SurfaceSection.SamplingResolution" /> is therefore null and the
/// result is resolution-independent. The only tolerance is <see cref="PointTolerance" />, used to decide
/// whether a point lies on the plane and to stitch coincident endpoints.
/// </summary>
/// <remarks>
/// Degenerate handling is explicit. A fragment whose plane is the section plane (a coplanar face) is
/// skipped and counted in a note, because its boundary edges are produced by its non-coplanar
/// neighbours and subtracting it would double-count them. A vertex exactly on the plane is counted once
/// by the on-plane test and never also as an edge crossing. A fragment that touches the plane in fewer
/// than two distinct points is a near-degenerate touch and is counted in a note rather than emitted as a
/// zero-length segment. Endpoints within <see cref="PointTolerance" /> are stitched into one contour;
/// unmatched or branching endpoints are counted so callers can report uncertainty instead of a
/// confident verdict.
/// </remarks>
public sealed class SurfaceSectionQuery
{
    /// <summary>Default endpoint/on-plane tolerance in metres.</summary>
    public const double DefaultPointTolerance = 1e-3;

    private readonly SurfaceCoverage _coverage;
    private readonly Dictionary<int, List<int>> _bucketsX = [];
    private readonly Dictionary<int, List<int>> _bucketsY = [];
    private readonly Dictionary<int, List<int>> _bucketsZ = [];

    public SurfaceSectionQuery(SurfaceCoverage coverage, double pointTolerance = DefaultPointTolerance)
    {
        ArgumentNullException.ThrowIfNull(coverage);
        PointTolerance = pointTolerance > 0 ? pointTolerance : DefaultPointTolerance;
        _coverage = coverage;
        for (var index = 0; index < coverage.Fragments.Count; index++)
        {
            var polygon = coverage.Fragments[index].Polygon;
            if (polygon.Count < 3) continue;
            Index(_bucketsX, index, polygon, point => point.X);
            Index(_bucketsY, index, polygon, point => point.Y);
            Index(_bucketsZ, index, polygon, point => point.Z);
        }
    }

    public SurfaceSectionQuery(SurfaceQualityGeometry geometry, double pointTolerance = DefaultPointTolerance)
        : this(geometry.Coverage, pointTolerance)
    {
    }

    /// <summary>The endpoint/on-plane tolerance in metres.</summary>
    public double PointTolerance { get; }

    /// <summary>
    /// Cuts the surface with the plane <c>axis = offset</c>. Only fragments on the requested scope are
    /// included; pass null for every boundary including interior armor.
    /// </summary>
    public SurfaceSection Cut(SectionAxis axis, double offset, SurfaceScope? scope = null)
    {
        var segments = new List<SectionSegment>();
        var coplanar = 0;
        var degenerate = 0;
        foreach (var index in Candidates(axis, offset))
        {
            var fragment = _coverage.Fragments[index];
            if (scope is not null && fragment.Scope != scope) continue;
            var kind = Intersect(fragment, axis, offset, out var start, out var end);
            switch (kind)
            {
                case IntersectionKind.Segment:
                    segments.Add(new SectionSegment(
                        fragment.Cell, start, end, fragment.Normal, fragment.PlacementIndex, fragment.Scope));
                    break;
                case IntersectionKind.Coplanar:
                    coplanar++;
                    break;
                case IntersectionKind.Degenerate:
                    degenerate++;
                    break;
                case IntersectionKind.Miss:
                    break;
            }
        }

        var contours = Stitch(segments, out var openEndpoints, out var branchEndpoints);
        var notes = new List<string>();
        if (coplanar > 0)
            notes.Add($"{coplanar} coplanar face(s) lie in the section plane and were skipped; their " +
                      "boundary edges come from their non-coplanar neighbours.");
        if (degenerate > 0)
            notes.Add($"{degenerate} fragment(s) touched the plane in fewer than two points and were " +
                      "treated as near-degenerate.");
        if (openEndpoints > 0)
            notes.Add($"{openEndpoints} section endpoint(s) did not stitch to a neighbour; the surface " +
                      "may have an interrupted join or the endpoint tolerance may be too small.");
        if (branchEndpoints > 0)
            notes.Add($"{branchEndpoints} section endpoint(s) joined more than two segments; the " +
                      "intersection is ambiguous there.");

        return new SurfaceSection(axis, offset, segments, contours, notes, null,
            coplanar, degenerate, openEndpoints, branchEndpoints);
    }

    /// <summary>
    /// Cuts every integer station from the hull's minimum to maximum along the axis, offset by
    /// <paramref name="offset"/>. The default offset 0.5 samples cell centres; 0 samples shared face
    /// planes, the most degenerate choice.
    /// </summary>
    public IReadOnlyList<SurfaceSection> CutAll(
        SectionAxis axis, int step = 1, double offset = 0.0, SurfaceScope? scope = null)
    {
        var (min, max) = Bounds(axis);
        step = Math.Max(1, step);
        var sections = new List<SurfaceSection>();
        for (var station = min; station <= max; station += step)
            sections.Add(Cut(axis, station + offset, scope));
        return sections;
    }

    private (int Min, int Max) Bounds(SectionAxis axis) => axis switch
    {
        SectionAxis.X => (_coverage.EffectivePlacements.Count == 0
            ? 0
            : _coverage.EffectivePlacements.Min(placement => placement.Position.X) - 1,
            _coverage.EffectivePlacements.Count == 0
            ? 0
            : _coverage.EffectivePlacements.Max(placement => placement.Position.X) + 1),
        SectionAxis.Y => (_coverage.EffectivePlacements.Count == 0
            ? 0
            : _coverage.EffectivePlacements.Min(placement => placement.Position.Y) - 1,
            _coverage.EffectivePlacements.Count == 0
            ? 0
            : _coverage.EffectivePlacements.Max(placement => placement.Position.Y) + 1),
        _ => (_coverage.EffectivePlacements.Count == 0
            ? 0
            : _coverage.EffectivePlacements.Min(placement => placement.Position.Z) - 1,
            _coverage.EffectivePlacements.Count == 0
            ? 0
            : _coverage.EffectivePlacements.Max(placement => placement.Position.Z) + 1),
    };

    private static void Index(
        Dictionary<int, List<int>> buckets, int index, IReadOnlyList<Vector3> polygon,
        Func<Vector3, double> coordinate)
    {
        var min = int.MaxValue;
        var max = int.MinValue;
        foreach (var point in polygon)
        {
            var value = (int)Math.Floor(coordinate(point));
            if (value < min) min = value;
            if (value > max) max = value;
        }

        for (var bucket = min; bucket <= max; bucket++)
        {
            if (!buckets.TryGetValue(bucket, out var list))
            {
                list = [];
                buckets[bucket] = list;
            }

            list.Add(index);
        }
    }

    private IEnumerable<int> Candidates(SectionAxis axis, double offset)
    {
        var buckets = axis switch
        {
            SectionAxis.X => _bucketsX,
            SectionAxis.Y => _bucketsY,
            _ => _bucketsZ,
        };
        var bucket = (int)Math.Floor(offset);
        return buckets.TryGetValue(bucket, out var list) ? list : [];
    }

    private enum IntersectionKind
    {
        Segment,
        Coplanar,
        Degenerate,
        Miss,
    }

    private IntersectionKind Intersect(
        SurfaceFragment fragment, SectionAxis axis, double offset, out Vector3 start, out Vector3 end)
    {
        start = default;
        end = default;
        var polygon = fragment.Polygon;
        if (polygon.Count < 3) return IntersectionKind.Degenerate;

        var points = new List<Vector3>(4);
        var allOnPlane = true;
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            var da = Coordinate(a, axis) - offset;
            var db = Coordinate(b, axis) - offset;
            if (Math.Abs(da) > PointTolerance) allOnPlane = false;
            // A vertex exactly on the plane is added once by the on-plane test, never also as a crossing.
            if (Math.Abs(da) <= PointTolerance) points.Add(a);
            if ((da > PointTolerance && db < -PointTolerance) ||
                (da < -PointTolerance && db > PointTolerance))
            {
                var t = da / (da - db);
                points.Add(a + (b - a) * (float)t);
            }
        }

        if (allOnPlane) return IntersectionKind.Coplanar;
        if (points.Count == 0) return IntersectionKind.Miss;
        var distinct = Deduplicate(points);
        if (distinct.Count < 2) return IntersectionKind.Degenerate;

        var best = -1d;
        for (var i = 0; i < distinct.Count; i++)
        for (var j = i + 1; j < distinct.Count; j++)
        {
            var distance = (distinct[i] - distinct[j]).LengthSquared();
            if (distance <= best) continue;
            best = distance;
            start = distinct[i];
            end = distinct[j];
        }

        return best <= PointTolerance * PointTolerance ? IntersectionKind.Degenerate : IntersectionKind.Segment;
    }

    private List<Vector3> Deduplicate(List<Vector3> points)
    {
        var result = new List<Vector3>(points.Count);
        foreach (var point in points)
        {
            var duplicate = false;
            foreach (var existing in result)
            {
                if ((existing - point).LengthSquared() > PointTolerance * PointTolerance) continue;
                duplicate = true;
                break;
            }

            if (!duplicate) result.Add(point);
        }

        return result;
    }

    private static double Coordinate(Vector3 point, SectionAxis axis) => axis switch
    {
        SectionAxis.X => point.X,
        SectionAxis.Y => point.Y,
        _ => point.Z,
    };

    /// <summary>
    /// Stitches segments into contours by matching quantized endpoints. Each endpoint key records the
    /// incident segment ends; a key with one incident end is open, a key with more than two is a branch.
    /// </summary>
    private List<SectionContour> Stitch(List<SectionSegment> segments, out int openEndpoints, out int branchEndpoints)
    {
        var endpoints = new Dictionary<(int X, int Y, int Z), List<int>>();
        for (var index = 0; index < segments.Count; index++)
        {
            AddEndpoint(endpoints, Key(segments[index].Start), index);
            AddEndpoint(endpoints, Key(segments[index].End), index);
        }

        openEndpoints = endpoints.Values.Count(list => list.Count == 1);
        branchEndpoints = endpoints.Values.Count(list => list.Count > 2);

        var used = new bool[segments.Count];
        var contours = new List<SectionContour>();
        for (var seed = 0; seed < segments.Count; seed++)
        {
            if (used[seed]) continue;
            used[seed] = true;
            var points = new List<Vector3> { segments[seed].Start, segments[seed].End };
            var closed = false;
            var current = segments[seed].End;
            while (true)
            {
                var key = Key(current);
                var next = NextUnused(endpoints, key, used);
                if (next < 0) break;
                used[next] = true;
                var segment = segments[next];
                var other = Key(segment.Start) == key ? segment.End : segment.Start;
                if (Key(other) == Key(points[0]))
                {
                    closed = true;
                    break;
                }

                points.Add(other);
                current = other;
            }

            if (!closed)
            {
                var back = points[0];
                while (true)
                {
                    var key = Key(back);
                    var previous = NextUnused(endpoints, key, used);
                    if (previous < 0) break;
                    used[previous] = true;
                    var segment = segments[previous];
                    var other = Key(segment.Start) == key ? segment.End : segment.Start;
                    if (Key(other) == Key(points[^1]))
                    {
                        closed = true;
                        break;
                    }

                    points.Insert(0, other);
                    back = other;
                }
            }

            contours.Add(new SectionContour(points, closed));
        }

        return contours;
    }

    private static void AddEndpoint(
        Dictionary<(int X, int Y, int Z), List<int>> endpoints, (int X, int Y, int Z) key, int segment)
    {
        if (!endpoints.TryGetValue(key, out var list))
        {
            list = [];
            endpoints[key] = list;
        }

        list.Add(segment);
    }

    private static int NextUnused(
        Dictionary<(int X, int Y, int Z), List<int>> endpoints, (int X, int Y, int Z) key, bool[] used)
    {
        if (!endpoints.TryGetValue(key, out var list)) return -1;
        var best = -1;
        foreach (var index in list)
        {
            if (used[index]) continue;
            if (best < 0 || index < best) best = index;
        }

        return best;
    }

    private (int X, int Y, int Z) Key(Vector3 point) => (
        (int)Math.Round(point.X / PointTolerance),
        (int)Math.Round(point.Y / PointTolerance),
        (int)Math.Round(point.Z / PointTolerance));
}
