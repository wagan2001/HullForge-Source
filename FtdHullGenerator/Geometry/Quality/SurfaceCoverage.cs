using System.Numerics;
using FtdHullGenerator.Domain;

namespace FtdHullGenerator.Geometry;

/// <summary>Which solid boundary a surface fragment belongs to.</summary>
public enum SurfaceScope
{
    /// <summary>
    /// Exterior hull skin: the owning placement is at armor depth zero and the empty cell across the
    /// fragment's face reaches the unbounded exterior.
    /// </summary>
    HullSkin,

    /// <summary>
    /// Everything that is not exterior hull skin: interior armor, an enclosed cavity or air gap, or a
    /// residual whose across-cell is neither empty nor exterior-reachable.
    /// </summary>
    InternalArmor,
}

/// <summary>
/// One attributed piece of an exposed solid boundary: a convex polygon on one face, clipped to a
/// single lattice cell of the placement that owns the face.
/// </summary>
public sealed record SurfaceFragment(
    (int X, int Y, int Z) Cell,
    double Area,
    bool IsStep,
    SurfaceScope Scope,
    int PlacementIndex,
    Vector3 Normal,
    Vector3 Centroid,
    IReadOnlyList<Vector3> Polygon);

/// <summary>
/// Export-effective exposed-surface coverage. Measures the planned export geometry: every placement
/// is reduced to the shape the exporter will actually write before any face is generated, so a
/// one-metre fitted shell candidate contributes a cube while a smoothing slope or beam keeps its
/// native envelope. Faces that touch a different placement's opposing coplanar face lose the shared
/// area; the residual is split geometrically across the owning placement's occupied cells.
/// </summary>
/// <remarks>
/// Catalog availability is not consulted. The audit has no installed game catalog, so this reports
/// <see cref="ReportScope" /> and never evaluates a catalog fallback. Tolerances are explicit and
/// independent of every detector threshold: <see cref="AreaTolerance" /> decides whether a clipped
/// polygon is real, and <see cref="PlaneTolerance" /> buckets coplanar faces in a hash join. No
/// detector threshold is changed to compensate for this measurement.
/// </remarks>
public sealed class SurfaceCoverage
{
    /// <summary>Human-readable description of what the report measures, repeated in every consumer.</summary>
    public const string ReportScope =
        "planned export geometry (catalog-independent flatten; catalog fallback not evaluated)";

    /// <summary>Smallest polygon area, in square metres, that is kept as a real residual sliver.</summary>
    public const double AreaTolerance = 1e-7;

    /// <summary>
    /// Largest expanded-bounds volume, in cells, for which exterior reachability is flooded. Above
    /// this the flood fill is skipped and every empty across-cell is treated as exterior (the
    /// pre-reachability behaviour), with a note. This bounds memory and time on pathological hulls
    /// rather than hanging. The largest supported hull (500x250x100) expands to about 12.9M cells.
    /// </summary>
    public const long MaxExteriorFloodCells = 32_000_000;

    /// <summary>Plane-bucketing tolerance for canonical normal sign and quantized plane offset.</summary>
    public const double PlaneTolerance = 1e-4;

    private const double GeometryEpsilon = 1e-9;
    private const double NormalZero = 1e-6;

    // Clip intersection points are single-precision, so a large face far from the origin can lose a
    // few 1e-6 m^2 to rounding when it is split across cells. Real unattributable area is many orders
    // of magnitude larger; this relative floor keeps the notes channel about geometry, not rounding.
    private const double AttributionRelativeTolerance = 1e-4;

    private readonly List<SurfaceFragment> _fragments = [];
    private readonly List<string> _notes = [];
    private readonly Dictionary<(int X, int Y, int Z), SurfaceCellArea> _cellAreas = [];
    private readonly Dictionary<(int X, int Y, int Z), SurfaceCellArea> _hullSkinCellAreas = [];
    private readonly HashSet<(int X, int Y, int Z)> _occupied = [];

    // Exterior reachability of empty cells, flooded once per hull over the bounds expanded by one
    // cell. A null array means the flood fill was skipped by the volume cap, in which case every
    // empty cell is treated as exterior.
    private bool[]? _exterior;
    private int _exteriorMinX;
    private int _exteriorMinY;
    private int _exteriorMinZ;
    private int _exteriorSizeX;
    private int _exteriorSizeY;
    private int _exteriorSizeZ;

    public SurfaceCoverage(GeneratedHull hull)
    {
        ArgumentNullException.ThrowIfNull(hull);

        var effective = new BlockPlacement[hull.Blocks.Count];
        for (var i = 0; i < hull.Blocks.Count; i++)
            effective[i] = Effective(hull.Blocks[i]);
        EffectivePlacements = effective;

        foreach (var placement in effective)
        foreach (var cell in placement.OccupiedCells)
            _occupied.Add(cell);
        BuildExteriorReachability(hull);

        var entries = new List<FaceEntry>();
        for (var i = 0; i < effective.Length; i++)
        {
            var placement = effective[i];
            var cells = placement.OccupiedCells.ToArray();
            var faces = StructuralShapeGeometry.WorldFaces(placement);
            for (var f = 0; f < faces.Count; f++)
            {
                var face = faces[f];
                var normal = face.Normal;
                entries.Add(new FaceEntry(
                    i, f, placement, face, cells,
                    PlaneKeyOf(normal, Vector3.Dot(normal, face.Points[0])), NormalKey(normal)));
            }
        }

        // Hash join: a face only ever looks at faces sharing its canonical plane and its exact
        // opposite normal. Within that group a 2D grid over the plane keeps the lookup local, so a
        // large solid's coplanar internal faces never become a whole-plane all-pairs comparison.
        var planes = new Dictionary<PlaneKey, Dictionary<(int X, int Y, int Z), FaceGroup>>();
        foreach (var entry in entries)
        {
            var key = entry.Plane;
            if (!planes.TryGetValue(key, out var byNormal))
            {
                byNormal = [];
                planes[key] = byNormal;
            }

            var normalKey = entry.Normal;
            if (!byNormal.TryGetValue(normalKey, out var group))
            {
                group = new FaceGroup(entry.Face.Normal);
                byNormal[normalKey] = group;
            }

            group.Add(entry);
        }

        var accumulated = new Dictionary<(int X, int Y, int Z), (double Exposed, double Step)>();
        var skinAccumulated = new Dictionary<(int X, int Y, int Z), (double Exposed, double Step)>();
        foreach (var placement in effective)
        foreach (var cell in placement.OccupiedCells)
        {
            accumulated.TryAdd(cell, (0, 0));
            skinAccumulated.TryAdd(cell, (0, 0));
        }

        double totalExposed = 0, totalStep = 0, skinExposed = 0, skinStep = 0;

        foreach (var entry in entries)
        {
            var normal = entry.Face.Normal;
            var normalKey = entry.Normal;
            var step = IsStepFace(normal);

            var byNormal = planes[entry.Plane];
            var covererKey = (-normalKey.X, -normalKey.Y, -normalKey.Z);
            FaceEntry[] coverers = byNormal.TryGetValue(covererKey, out var group)
                ? group.CoverersOf(entry)
                : [];

            var residuals = coverers.Length == 0
                ? new List<List<Vector3>> { entry.Face.Points.ToList() }
                : SubtractAll(entry.Face.Points, coverers);

            foreach (var residual in residuals)
            {
                var area = StructuralShapeGeometry.PolygonArea(residual);
                if (area <= AreaTolerance) continue;

                var attributed = 0d;
                foreach (var cell in entry.Cells)
                {
                    var clipped = ClipToCell(residual, cell);
                    var cellArea = StructuralShapeGeometry.PolygonArea(clipped);
                    if (cellArea <= AreaTolerance) continue;

                    attributed += cellArea;
                    var scope = ClassifyScope(entry.Placement, cell, normal);
                    _fragments.Add(new SurfaceFragment(
                        cell, cellArea, step, scope, entry.PlacementIndex, normal, Centroid(clipped), clipped));

                    var current = accumulated[cell];
                    accumulated[cell] = step
                        ? (current.Exposed + cellArea, current.Step + cellArea)
                        : (current.Exposed + cellArea, current.Step);

                    totalExposed += cellArea;
                    if (step) totalStep += cellArea;
                    if (scope == SurfaceScope.HullSkin)
                    {
                        skinExposed += cellArea;
                        if (step) skinStep += cellArea;
                        var skinCurrent = skinAccumulated[cell];
                        skinAccumulated[cell] = step
                            ? (skinCurrent.Exposed + cellArea, skinCurrent.Step + cellArea)
                            : (skinCurrent.Exposed + cellArea, skinCurrent.Step);
                    }
                }

                // A sliver that leaves the owning footprint, or that is eaten by the clip tolerance,
                // must be reported rather than silently dropped. The relative floor absorbs the
                // single-precision rounding of a large face split across cells.
                if (Math.Abs(attributed - area) > Math.Max(AreaTolerance, area * AttributionRelativeTolerance))
                {
                    _notes.Add(
                        $"Placement {entry.PlacementIndex} face {entry.FaceIndex} residual area " +
                        $"{area:0.########} was attributed as {attributed:0.########} across its cells " +
                        $"(imbalance {attributed - area:0.########}).");
                }
            }
        }

        foreach (var cell in accumulated.Keys
                     .OrderBy(cell => cell.X).ThenBy(cell => cell.Y).ThenBy(cell => cell.Z))
        {
            var value = accumulated[cell];
            _cellAreas[cell] = new SurfaceCellArea(value.Exposed, value.Step);
            var skin = skinAccumulated[cell];
            _hullSkinCellAreas[cell] = new SurfaceCellArea(skin.Exposed, skin.Step);
        }

        Totals = new SurfaceAreaTotals(totalExposed, totalStep);
        HullSkinTotals = new SurfaceAreaTotals(skinExposed, skinStep);
    }

    /// <summary>Every attributed boundary fragment, in deterministic placement/face/cell order.</summary>
    public IReadOnlyList<SurfaceFragment> Fragments => _fragments;

    /// <summary>Deterministic notes about residual slivers that could not be fully attributed.</summary>
    public IReadOnlyList<string> Notes => _notes;

    /// <summary>Exposed and step area per occupied cell over every solid boundary, including interior armor.</summary>
    public IReadOnlyDictionary<(int X, int Y, int Z), SurfaceCellArea> CellAreas => _cellAreas;

    /// <summary>
    /// Exposed and step area per occupied cell over every solid boundary, skin and interior armor
    /// together. The clearly named alias for <see cref="CellAreas" /> used by all-boundary consumers.
    /// </summary>
    public IReadOnlyDictionary<(int X, int Y, int Z), SurfaceCellArea> BoundaryCellAreas => _cellAreas;

    /// <summary>
    /// Exposed and step area per occupied cell on the exterior hull skin only; interior armor,
    /// enclosed cavities and covered residuals contribute zero.
    /// </summary>
    public IReadOnlyDictionary<(int X, int Y, int Z), SurfaceCellArea> HullSkinCellAreas => _hullSkinCellAreas;

    /// <summary>All exposed solid boundary, skin and interior armor together.</summary>
    public SurfaceAreaTotals Totals { get; }

    /// <summary>Only the exterior hull skin; interior armor and enclosed cavities are excluded.</summary>
    public SurfaceAreaTotals HullSkinTotals { get; }

    /// <summary>The export-effective placements actually measured.</summary>
    public IReadOnlyList<BlockPlacement> EffectivePlacements { get; }

    /// <summary>
    /// Reduces a placement to the shape the exporter writes: beams, poles, multi-cell parts and
    /// smoothing assemblies keep their fitted envelope, while a one-metre shell candidate is a cube.
    /// </summary>
    private static BlockPlacement Effective(BlockPlacement placement) =>
        placement.KeepsFittedShape
            ? placement
            : placement with { Shape = BlockShape.Cube, Rotation = 0 };

    /// <summary>
    /// Classifies one per-cell fragment. A placement at armor depth greater than zero is always
    /// internal armor. Otherwise the fragment is exterior hull skin unless the cell across its face is
    /// empty and cannot reach the unbounded exterior (an enclosed cavity or air gap). When the
    /// across-cell is occupied the residual was not covered by an opposing coplanar face — the
    /// subtraction would have removed it — so it stays on the exposed boundary; that is what keeps the
    /// uncovered half of a shell face next to a partially covering fitted shape as hull skin.
    /// </summary>
    private SurfaceScope ClassifyScope(BlockPlacement placement, (int X, int Y, int Z) cell, Vector3 normal)
    {
        if (placement.ArmorDepth > 0) return SurfaceScope.InternalArmor;
        var across = AcrossCell(cell, normal);
        if (_occupied.Contains(across)) return SurfaceScope.HullSkin;
        return IsExteriorReachable(across) ? SurfaceScope.HullSkin : SurfaceScope.InternalArmor;
    }

    /// <summary>The cell the fragment's face opens into, from the face normal rounded to the lattice.</summary>
    private static (int X, int Y, int Z) AcrossCell((int X, int Y, int Z) cell, Vector3 normal) => (
        cell.X + (int)Math.Round(normal.X),
        cell.Y + (int)Math.Round(normal.Y),
        cell.Z + (int)Math.Round(normal.Z));

    /// <summary>True when an empty cell reaches the unbounded exterior, or when the flood fill was skipped.</summary>
    private bool IsExteriorReachable((int X, int Y, int Z) cell)
    {
        if (_exterior is null) return true;
        var x = cell.X - _exteriorMinX;
        var y = cell.Y - _exteriorMinY;
        var z = cell.Z - _exteriorMinZ;
        if (x < 0 || y < 0 || z < 0 || x >= _exteriorSizeX || y >= _exteriorSizeY || z >= _exteriorSizeZ)
            return true;
        return _exterior[(x * _exteriorSizeY + y) * _exteriorSizeZ + z];
    }

    /// <summary>
    /// Floods empty cells from the hull bounds expanded by one cell. Every empty cell reachable from
    /// the expanded boundary is exterior; enclosed air gaps and cavities are not. Runs once per hull
    /// in O(expanded volume) and is bounded by <see cref="MaxExteriorFloodCells" />.
    /// </summary>
    private void BuildExteriorReachability(GeneratedHull hull)
    {
        var sizeX = hull.MaxX - hull.MinX + 3;
        var sizeY = hull.MaxY - hull.MinY + 3;
        var sizeZ = hull.MaxZ - hull.MinZ + 3;
        var volume = (long)sizeX * sizeY * sizeZ;
        if (volume > MaxExteriorFloodCells)
        {
            _notes.Add(
                $"Exterior reachability was skipped because the expanded bounds volume {volume} exceeds " +
                $"the {MaxExteriorFloodCells}-cell cap; enclosed cavities may be classified as exterior.");
            return;
        }

        var originX = hull.MinX - 1;
        var originY = hull.MinY - 1;
        var originZ = hull.MinZ - 1;
        var occupied = new bool[volume];
        foreach (var placement in EffectivePlacements)
        foreach (var cell in placement.OccupiedCells)
            occupied[((cell.X - originX) * sizeY + (cell.Y - originY)) * sizeZ + (cell.Z - originZ)] = true;

        var exterior = new bool[volume];
        var queue = new Queue<int>();
        exterior[0] = true;
        queue.Enqueue(0);
        while (queue.Count > 0)
        {
            var index = queue.Dequeue();
            var x = index / (sizeY * sizeZ);
            var remainder = index % (sizeY * sizeZ);
            var y = remainder / sizeZ;
            var z = remainder % sizeZ;
            Visit(index - sizeY * sizeZ, x > 0);
            Visit(index + sizeY * sizeZ, x + 1 < sizeX);
            Visit(index - sizeZ, y > 0);
            Visit(index + sizeZ, y + 1 < sizeY);
            Visit(index - 1, z > 0);
            Visit(index + 1, z + 1 < sizeZ);
        }

        _exterior = exterior;
        _exteriorMinX = originX;
        _exteriorMinY = originY;
        _exteriorMinZ = originZ;
        _exteriorSizeX = sizeX;
        _exteriorSizeY = sizeY;
        _exteriorSizeZ = sizeZ;

        void Visit(int neighbour, bool inRange)
        {
            if (!inRange || occupied[neighbour] || exterior[neighbour]) return;
            exterior[neighbour] = true;
            queue.Enqueue(neighbour);
        }
    }

    /// <summary>Matches <c>SurfacePatchMetrics.IsStepFace</c>: only Y- and Z-facing squares are steps.</summary>
    private static bool IsStepFace(Vector3 normal) =>
        Math.Abs(normal.X) < 1e-5 &&
        (Math.Abs(normal.Y) > .99999 || Math.Abs(normal.Z) > .99999);

    /// <summary>Subtracts a union of convex coverers by folding a disjoint convex decomposition.</summary>
    private static List<List<Vector3>> SubtractAll(
        IReadOnlyList<Vector3> polygon, IReadOnlyList<FaceEntry> coverers)
    {
        var residual = new List<List<Vector3>> { polygon.ToList() };
        foreach (var coverer in coverers)
        {
            var next = new List<List<Vector3>>();
            foreach (var piece in residual)
                next.AddRange(SubtractConvex(piece, coverer.Face.Points, coverer.Face.Normal));
            residual = next;
            if (residual.Count == 0) break;
        }

        return residual;
    }

    /// <summary>
    /// Returns convex polygons whose disjoint union is <paramref name="polygon" /> minus
    /// <paramref name="coverer" />. Both polygons are convex and coplanar; the coverer's own winding
    /// about <paramref name="covererNormal" /> gives its inward half-planes. Piece k is
    /// <c>polygon ∩ inside(H_0..H_(k-1)) ∩ outside(H_k)</c>, so each point outside the coverer lands
    /// in exactly one piece and overlapping coverers are never subtracted twice by the caller.
    /// </summary>
    internal static List<List<Vector3>> SubtractConvex(
        IReadOnlyList<Vector3> polygon,
        IReadOnlyList<Vector3> coverer, Vector3 covererNormal)
    {
        var result = new List<List<Vector3>>();
        if (polygon.Count < 3 || coverer.Count < 3) return result;

        var working = new List<List<Vector3>> { polygon.ToList() };
        for (var k = 0; k < coverer.Count; k++)
        {
            var a = coverer[k];
            var b = coverer[(k + 1) % coverer.Count];
            var inward = Vector3.Cross(covererNormal, b - a);
            if (inward.LengthSquared() < GeometryEpsilon) continue;
            var offset = Vector3.Dot(inward, a);

            var next = new List<List<Vector3>>(working.Count);
            foreach (var piece in working)
            {
                var inside = ClipHalfSpace(piece, inward, offset, keepInside: true);
                if (inside.Count >= 3 && StructuralShapeGeometry.PolygonArea(inside) > AreaTolerance)
                    next.Add(inside);
            }

            foreach (var piece in working)
            {
                var outside = ClipHalfSpace(piece, inward, offset, keepInside: false);
                if (outside.Count >= 3 && StructuralShapeGeometry.PolygonArea(outside) > AreaTolerance)
                    result.Add(outside);
            }

            working = next;
            if (working.Count == 0) break;
        }

        return result;
    }

    /// <summary>
    /// Clips a polygon against one half-space. With <paramref name="keepInside" /> the kept side is
    /// <c>dot(normal, x) &gt;= offset</c>; otherwise it is <c>dot(normal, x) &lt;= offset</c>.
    /// </summary>
    private static List<Vector3> ClipHalfSpace(
        IReadOnlyList<Vector3> polygon, Vector3 normal, double offset, bool keepInside)
    {
        var sign = keepInside ? 1d : -1d;
        var crosses = false;
        foreach (var point in polygon)
        {
            if (sign * (Vector3.Dot(normal, point) - offset) < -GeometryEpsilon)
            {
                crosses = true;
                break;
            }
        }

        if (!crosses) return polygon as List<Vector3> ?? polygon.ToList();

        var result = new List<Vector3>(polygon.Count + 4);
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            var da = sign * (Vector3.Dot(normal, a) - offset);
            var db = sign * (Vector3.Dot(normal, b) - offset);
            if (da >= -GeometryEpsilon) result.Add(a);
            if ((da > GeometryEpsilon) != (db > GeometryEpsilon))
            {
                var t = da / (da - db);
                result.Add(a + (b - a) * (float)t);
            }
        }

        return result;
    }

    /// <summary>Clips a planar residual polygon against the six inclusive half-spaces of one cell cube.</summary>
    private static List<Vector3> ClipToCell(IReadOnlyList<Vector3> polygon, (int X, int Y, int Z) cell)
    {
        var result = polygon.ToList();
        result = ClipHalfSpace(result, Vector3.UnitX, cell.X - 0.5, keepInside: true);
        if (result.Count < 3) return result;
        result = ClipHalfSpace(result, -Vector3.UnitX, -(cell.X + 0.5), keepInside: true);
        if (result.Count < 3) return result;
        result = ClipHalfSpace(result, Vector3.UnitY, cell.Y - 0.5, keepInside: true);
        if (result.Count < 3) return result;
        result = ClipHalfSpace(result, -Vector3.UnitY, -(cell.Y + 0.5), keepInside: true);
        if (result.Count < 3) return result;
        result = ClipHalfSpace(result, Vector3.UnitZ, cell.Z - 0.5, keepInside: true);
        if (result.Count < 3) return result;
        result = ClipHalfSpace(result, -Vector3.UnitZ, -(cell.Z + 0.5), keepInside: true);
        return result;
    }

    private static Vector3 Centroid(IReadOnlyList<Vector3> points)
    {
        var sum = Vector3.Zero;
        foreach (var point in points) sum += point;
        return sum / points.Count;
    }

    /// <summary>Canonical plane identity: a normal sign convention plus a quantized offset.</summary>
    private static PlaneKey PlaneKeyOf(Vector3 normal, double offset)
    {
        var n = normal;
        var d = offset;
        if (IsNegative(n))
        {
            n = -n;
            d = -d;
        }

        return new PlaneKey(
            (int)Math.Round(n.X / PlaneTolerance),
            (int)Math.Round(n.Y / PlaneTolerance),
            (int)Math.Round(n.Z / PlaneTolerance),
            (long)Math.Round(d / PlaneTolerance));
    }

    private static bool IsNegative(Vector3 normal) =>
        normal.X < -NormalZero ||
        (Math.Abs(normal.X) <= NormalZero &&
         (normal.Y < -NormalZero || (Math.Abs(normal.Y) <= NormalZero && normal.Z < -NormalZero)));

    private static (int X, int Y, int Z) NormalKey(Vector3 normal) => (
        (int)Math.Round(normal.X / PlaneTolerance),
        (int)Math.Round(normal.Y / PlaneTolerance),
        (int)Math.Round(normal.Z / PlaneTolerance));

    private static (double MinU, double MaxU, double MinV, double MaxV) BoundsOf(
        IReadOnlyList<Vector3> points, Vector3 u, Vector3 v)
    {
        double minU = double.MaxValue, maxU = double.MinValue, minV = double.MaxValue, maxV = double.MinValue;
        foreach (var point in points)
        {
            var pu = Vector3.Dot(point, u);
            var pv = Vector3.Dot(point, v);
            if (pu < minU) minU = pu;
            if (pu > maxU) maxU = pu;
            if (pv < minV) minV = pv;
            if (pv > maxV) maxV = pv;
        }

        return (minU, maxU, minV, maxV);
    }

    private readonly record struct PlaneKey(int NX, int NY, int NZ, long Offset);

    /// <summary>
    /// All faces on one canonical plane with one exact normal sign, indexed by a coarse 2D grid over
    /// the plane. A query face only tests faces whose in-plane bounding boxes share a grid cell, so a
    /// large coplanar set stays local instead of quadratic.
    /// </summary>
    private sealed class FaceGroup
    {
        private const double CellSize = 1d;
        private readonly Vector3 _u;
        private readonly Vector3 _v;
        private readonly List<FaceEntry> _faces = [];
        private readonly Dictionary<(int U, int V), List<int>> _grid = [];
        private int[] _stamp = [];
        private int _stampValue;

        public FaceGroup(Vector3 normal)
        {
            var reference = Math.Abs(normal.X) <= Math.Abs(normal.Y) && Math.Abs(normal.X) <= Math.Abs(normal.Z)
                ? Vector3.UnitX
                : Math.Abs(normal.Y) <= Math.Abs(normal.Z) ? Vector3.UnitY : Vector3.UnitZ;
            _u = Vector3.Normalize(Vector3.Cross(reference, normal));
            _v = Vector3.Cross(normal, _u);
        }

        public void Add(FaceEntry entry)
        {
            var index = _faces.Count;
            _faces.Add(entry);
            var bounds = BoundsOf(entry.Face.Points, _u, _v);
            var u0 = (int)Math.Floor(bounds.MinU / CellSize);
            var u1 = (int)Math.Floor(bounds.MaxU / CellSize);
            var v0 = (int)Math.Floor(bounds.MinV / CellSize);
            var v1 = (int)Math.Floor(bounds.MaxV / CellSize);
            for (var u = u0; u <= u1; u++)
            for (var v = v0; v <= v1; v++)
            {
                if (!_grid.TryGetValue((u, v), out var list))
                {
                    list = [];
                    _grid[(u, v)] = list;
                }

                list.Add(index);
            }
        }

        public FaceEntry[] CoverersOf(FaceEntry entry)
        {
            if (_stamp.Length != _faces.Count) _stamp = new int[_faces.Count];
            _stampValue++;
            var bounds = BoundsOf(entry.Face.Points, _u, _v);
            var candidates = new List<FaceEntry>();
            var u0 = (int)Math.Floor(bounds.MinU / CellSize);
            var u1 = (int)Math.Floor(bounds.MaxU / CellSize);
            var v0 = (int)Math.Floor(bounds.MinV / CellSize);
            var v1 = (int)Math.Floor(bounds.MaxV / CellSize);
            for (var u = u0; u <= u1; u++)
            for (var v = v0; v <= v1; v++)
            {
                if (!_grid.TryGetValue((u, v), out var list)) continue;
                foreach (var index in list)
                {
                    if (_stamp[index] == _stampValue) continue;
                    _stamp[index] = _stampValue;
                    var candidate = _faces[index];
                    if (candidate.PlacementIndex != entry.PlacementIndex) candidates.Add(candidate);
                }
            }

            candidates.Sort((left, right) => left.PlacementIndex != right.PlacementIndex
                ? left.PlacementIndex.CompareTo(right.PlacementIndex)
                : left.FaceIndex.CompareTo(right.FaceIndex));
            return candidates.ToArray();
        }
    }

    private sealed record FaceEntry(
        int PlacementIndex,
        int FaceIndex,
        BlockPlacement Placement,
        StructuralFace Face,
        (int X, int Y, int Z)[] Cells,
        PlaneKey Plane,
        (int X, int Y, int Z) Normal);
}
