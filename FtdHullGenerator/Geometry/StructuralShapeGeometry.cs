using System.Numerics;
using FtdHullGenerator.Domain;

namespace FtdHullGenerator.Geometry;

/// <summary>A convex face wound counter-clockwise when seen from outside its solid.</summary>
public sealed record StructuralFace(IReadOnlyList<Vector3> Points, Vector3 Normal)
{
    public double Area => StructuralShapeGeometry.PolygonArea(Points);
}

/// <summary>
/// Material-independent native structural envelopes, in game-imported coordinates.
/// Anchor cells are centred at zero and multi-cell parts extend along local +Z.
/// These analytic envelopes omit mesh bevels and incidental sub-millimetre seams.
/// </summary>
/// <remarks>
/// Verified against Core_Structural item SizeInfo and imported mesh extrema. The game's
/// OBJ reader reflects X; objbytes already includes that reflection. In particular a
/// left triangle's solid corner is (-.5,-.5,-.5), not (+.5,-.5,-.5).
/// Transition envelopes are convex hulls of their native boundary landmarks, not a
/// single interpolated plane: their two exposed triangles have different normals.
/// Handed corners and import reflection were checked against native mesh geometry.
/// These envelopes answer "what solid is here", never "which cells are reserved" —
/// occupancy comes from <see cref="Domain.BlockPlacement.OccupiedCells" /> alone.
/// </remarks>
public static class StructuralShapeGeometry
{
    private const float Epsilon = 1e-5f;
    private static readonly StructuralFace[][] LocalFaces = Enum.GetValues<BlockShape>()
        .Select(BuildFaces).ToArray();

    public static IReadOnlyList<StructuralFace> Faces(BlockShape shape) => LocalFaces[(int)shape];

    public static IReadOnlyList<StructuralFace> WorldFaces(BlockPlacement placement)
    {
        var axes = BlockRotations.GetRotationAxes(placement.Rotation);
        var right = Vector(axes.Right);
        var up = Vector(axes.Up);
        var forward = Vector(axes.Forward);
        var anchor = new Vector3(placement.X, placement.Y, placement.Z);
        Vector3 Rotate(Vector3 p) => right * p.X + up * p.Y + forward * p.Z;
        return Faces(placement.Shape).Select(face => new StructuralFace(
            face.Points.Select(p => Rotate(p) + anchor).ToArray(), Rotate(face.Normal))).ToArray();
    }

    /// <summary>Positive-area geometric attachment; this does not certify a smooth continuation.</summary>
    public static double TouchingArea(BlockPlacement first, BlockPlacement second)
    {
        var a = WorldFaces(first);
        var b = WorldFaces(second);
        return a.Sum(left => b.Sum(right => ContactArea(left, right)));
    }

    /// <summary>Area shared by two opposing coplanar convex faces. Edge/point contact is zero.</summary>
    public static double ContactArea(StructuralFace first, StructuralFace second)
    {
        if (Vector3.Dot(first.Normal, second.Normal) > -1 + Epsilon ||
            Math.Abs(Vector3.Dot(first.Normal, second.Points[0] - first.Points[0])) > Epsilon)
            return 0;
        var polygon = first.Points.ToList();
        for (var i = 0; i < second.Points.Count && polygon.Count >= 3; i++)
        {
            var a = second.Points[i];
            var b = second.Points[(i + 1) % second.Points.Count];
            var outward = Vector3.Normalize(Vector3.Cross(b - a, second.Normal));
            polygon = Clip(polygon, outward, Vector3.Dot(outward, a));
        }
        return polygon.Count < 3 ? 0 : PolygonArea(polygon);
    }

    public static double PolygonArea(IReadOnlyList<Vector3> polygon)
    {
        if (polygon.Count < 3) return 0;
        // Subtract the first point before cross products to avoid loss of precision
        // on parts placed hundreds of metres from the blueprint origin.
        var sum = Vector3.Zero;
        for (var i = 1; i + 1 < polygon.Count; i++)
            sum += Vector3.Cross(polygon[i] - polygon[0], polygon[i + 1] - polygon[0]);
        return sum.Length() / 2d;
    }

    private static Vector3 Vector(AxisDirection axis) => new(axis.X, axis.Y, axis.Z);

    private static List<Vector3> Clip(IReadOnlyList<Vector3> polygon, Vector3 normal, float offset)
    {
        var result = new List<Vector3>();
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            var da = Vector3.Dot(normal, a) - offset;
            var db = Vector3.Dot(normal, b) - offset;
            if (da <= Epsilon) result.Add(a);
            if ((da > Epsilon) != (db > Epsilon))
                result.Add(a + (b - a) * (da / (da - db)));
        }
        return result;
    }

    private static StructuralFace[] BuildFaces(BlockShape shape)
    {
        var info = BlockShapeMetadata.Get(shape);
        if (info.Family == StructuralFamily.Pole) return PoleFaces(info.Length);
        var vertices = Vertices(info);
        var planes = new List<(Vector3 Normal, float Offset)>();
        for (var i = 0; i < vertices.Length; i++)
        for (var j = i + 1; j < vertices.Length; j++)
        for (var k = j + 1; k < vertices.Length; k++)
        {
            var normal = Vector3.Cross(vertices[j] - vertices[i], vertices[k] - vertices[i]);
            if (normal.LengthSquared() < Epsilon * Epsilon) continue;
            normal = Vector3.Normalize(normal);
            var offset = Vector3.Dot(normal, vertices[i]);
            var distances = vertices.Select(v => Vector3.Dot(normal, v) - offset).ToArray();
            if (distances.Any(d => d > Epsilon) && distances.Any(d => d < -Epsilon)) continue;
            if (distances.Any(d => d > Epsilon)) { normal = -normal; offset = -offset; }
            if (!planes.Any(p => Vector3.DistanceSquared(p.Normal, normal) < Epsilon * Epsilon &&
                                 Math.Abs(p.Offset - offset) < Epsilon))
                planes.Add((normal, offset));
        }
        return planes.Select(plane =>
        {
            var points = vertices.Where(v => Math.Abs(Vector3.Dot(plane.Normal, v) - plane.Offset) < Epsilon).ToArray();
            return Face(points, plane.Normal);
        }).ToArray();
    }

    private static StructuralFace Face(IReadOnlyList<Vector3> vertices, Vector3 normal)
    {
        var centre = vertices.Aggregate(Vector3.Zero, (a, b) => a + b) / vertices.Count;
        var u = Vector3.Normalize(vertices[0] - centre);
        var v = Vector3.Cross(normal, u);
        var ordered = vertices.OrderBy(p => Math.Atan2(Vector3.Dot(p - centre, v), Vector3.Dot(p - centre, u))).ToArray();
        return new StructuralFace(ordered, normal);
    }

    private static StructuralFace[] PoleFaces(int length)
    {
        const int sides = 16;
        var radius = .5 / Math.Cos(Math.PI / sides);
        var rear = Enumerable.Range(0, sides).Select(i =>
        {
            var angle = (i + .5) * Math.PI * 2 / sides;
            return new Vector3((float)(radius * Math.Cos(angle)), (float)(radius * Math.Sin(angle)), -.5f);
        }).ToArray();
        var front = rear.Select(p => p + new Vector3(0, 0, length)).ToArray();
        var faces = new List<StructuralFace> { new(rear.Reverse().ToArray(), -Vector3.UnitZ), new(front, Vector3.UnitZ) };
        for (var i = 0; i < sides; i++)
        {
            var next = (i + 1) % sides;
            var points = new[] { rear[i], rear[next], front[next], front[i] };
            faces.Add(new StructuralFace(points, Vector3.Normalize(Vector3.Cross(points[1] - points[0], points[2] - points[0]))));
        }
        return faces.ToArray();
    }

    private static Vector3[] Vertices(StructuralShapeInfo info)
    {
        var end = info.Length - .5f;
        // Names are geometric landmarks, after the game's OBJ import X reflection.
        var a = new Vector3(-.5f, -.5f, -.5f);
        var b = new Vector3(.5f, -.5f, -.5f);
        var c = new Vector3(-.5f, .5f, -.5f);
        var d = new Vector3(.5f, .5f, -.5f);
        var e = new Vector3(-.5f, -.5f, end);
        var f = new Vector3(.5f, -.5f, end);
        var g = new Vector3(-.5f, .5f, end);
        var h = new Vector3(.5f, .5f, end);
        Vector3[] vertices = info.Family switch
        {
            StructuralFamily.Box => [a, b, c, d, e, f, g, h],
            StructuralFamily.Slope => [a, b, c, d, e, f],
            StructuralFamily.Triangle => [a, b, c, e],
            StructuralFamily.InverseTriangle => [a, b, c, d, e, f, g],
            // Beam slope and its mirrored part are constant diagonal cross-sections.
            StructuralFamily.BeamSlope => [a, b, d, e, f, h],
            StructuralFamily.SquareCorner => [a, b, c, e, f],
            StructuralFamily.SquareBackedCorner => [a, b, c, d, e],
            StructuralFamily.SlopeTransition => [a, b, c, d, e, new(.5f, -.5f, info.ShortLength - .5f)],
            StructuralFamily.InverseTransition => [a, b, c, d, e, f, new(.5f, .5f, info.Length - info.ShortLength - .5f)],
            _ => throw new ArgumentOutOfRangeException(nameof(info)),
        };
        return info.Mirrored ? vertices.Select(p => new Vector3(-p.X, p.Y, p.Z)).ToArray() : vertices;
    }
}
