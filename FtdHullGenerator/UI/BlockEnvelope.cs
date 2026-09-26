using System.Windows;
using System.Windows.Media.Media3D;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Geometry;

namespace FtdHullGenerator.UI;

/// <summary>
/// One unit square of a cell boundary, named by a cell's index within a placement's
/// footprint and by the local axis the square faces.
/// </summary>
internal readonly record struct CellFace(int CellIndex, AxisDirection Direction);

/// <summary>
/// One flat polygon of a block's envelope, in the block's local space: the anchor cell
/// is centred on the origin, local +Z is forward, +Y is up, and +X is right.
/// </summary>
/// <param name="Points">The polygon, wound counter-clockwise seen from outside the solid.</param>
/// <param name="TextureCoordinates">
/// Coordinates over the whole placement's footprint rather than over one cell, so the
/// material's seam border outlines the part instead of each metre it covers.
/// </param>
/// <param name="Boundary">The cell boundary this polygon lies in, or null if it is a cut face.</param>
/// <param name="FillsCellFace">Whether the polygon covers that entire boundary square.</param>
internal sealed record EnvelopeFace(
    IReadOnlyList<Point3D> Points,
    IReadOnlyList<Point> TextureCoordinates,
    CellFace? Boundary,
    bool FillsCellFace);

/// <summary>Preview adaptation of the shared native structural envelopes.</summary>
internal static class BlockEnvelope
{
    private const double Tolerance = 1e-9;

    private static readonly EnvelopeFace[][] ShapeFaces = BuildAllShapes();

    /// <summary>Gets the envelope polygons for a shape, in the block's local space.</summary>
    public static IReadOnlyList<EnvelopeFace> Faces(BlockShape shape)
    {
        var index = (int)shape;
        return index >= 0 && index < ShapeFaces.Length
            ? ShapeFaces[index]
            : ShapeFaces[(int)BlockShape.Cube];
    }

    /// <summary>A half-space, as the points satisfying <c>Normal · p &lt;= Offset</c>.</summary>
    private readonly record struct HalfSpace(Vector3D Normal, double Offset)
    {
        public double DistanceTo(Point3D point) =>
            Normal.X * point.X + Normal.Y * point.Y + Normal.Z * point.Z - Offset;
    }

    private static EnvelopeFace[][] BuildAllShapes()
    {
        var shapes = Enum.GetValues<BlockShape>();
        var faces = new EnvelopeFace[shapes.Length][];
        foreach (var shape in shapes)
            faces[(int)shape] = BuildShape(shape);
        return faces;
    }

    /// <summary>
    /// Gets how many cells a shape's footprint spans. This asks a placement rather than
    /// repeating the table, so the envelope can never disagree with the footprint the
    /// exporter and the geometry validator use.
    /// </summary>
    private static int FootprintLength(BlockShape shape) =>
        new BlockPlacement(shape, MaterialKind.Metal, 0, 0, 0, Rotation: 0).CellLength;

    private static EnvelopeFace[] BuildShape(BlockShape shape)
    {
        var length = FootprintLength(shape);
        var faces = new List<EnvelopeFace>();
        foreach (var face in StructuralShapeGeometry.Faces(shape))
        {
            var polygon = face.Points.Select(p => new Point3D(p.X, p.Y, p.Z)).ToList();
            var normal = new Vector3D(face.Normal.X, face.Normal.Y, face.Normal.Z);
            foreach (var piece in SplitAcrossCells(polygon, normal, length))
            {
                if (Area(piece) <= Tolerance) continue;
                var boundary = BoundaryOf(piece, normal, length);
                faces.Add(new EnvelopeFace(piece, TextureCoordinatesFor(piece, normal, length),
                    boundary, boundary is not null && Math.Abs(Area(piece) - 1) < 1e-6));
            }
        }
        return [.. faces];
    }

    /// <summary>Clips a convex polygon to a half-space, keeping the winding.</summary>
    private static List<Point3D> Clip(IReadOnlyList<Point3D> polygon, HalfSpace plane)
    {
        var clipped = new List<Point3D>(polygon.Count + 1);
        for (var index = 0; index < polygon.Count; index++)
        {
            var current = polygon[index];
            var next = polygon[(index + 1) % polygon.Count];
            var currentDistance = plane.DistanceTo(current);
            var nextDistance = plane.DistanceTo(next);

            if (currentDistance <= Tolerance)
                clipped.Add(current);
            if (currentDistance > Tolerance != nextDistance > Tolerance)
            {
                var fraction = currentDistance / (currentDistance - nextDistance);
                clipped.Add(current + (next - current) * fraction);
            }
        }
        return clipped;
    }

    /// <summary>
    /// Splits a face that lies in a side wall of a multi-cell footprint into one piece per
    /// cell, so each piece can be culled against the neighbour it actually abuts. Faces on
    /// the forward and back ends already belong to a single cell, and cut faces are never
    /// culled, so neither needs splitting.
    /// </summary>
    private static IEnumerable<IReadOnlyList<Point3D>> SplitAcrossCells(
        List<Point3D> polygon,
        Vector3D normal,
        int length)
    {
        if (length == 1 || Math.Abs(normal.Z) > Tolerance)
        {
            yield return polygon;
            yield break;
        }

        for (var cell = 0; cell < length; cell++)
        {
            var piece = Clip(polygon, new HalfSpace(new Vector3D(0, 0, 1), cell + 0.5));
            if (piece.Count >= 3)
                piece = Clip(piece, new HalfSpace(new Vector3D(0, 0, -1), 0.5 - cell));
            if (piece.Count >= 3)
                yield return piece;
        }
    }

    /// <summary>
    /// Names the cell boundary a polygon lies in, or returns null when the polygon is a cut
    /// face. Only axis-aligned polygons on a cell's own boundary plane can be culled against
    /// a neighbour.
    /// </summary>
    private static CellFace? BoundaryOf(IReadOnlyList<Point3D> polygon, Vector3D normal, int length)
    {
        var centroid = Centroid(polygon);
        if (Math.Abs(Math.Abs(normal.X) - 1) < 1e-9)
        {
            return Math.Abs(Math.Abs(centroid.X) - 0.5) < 1e-9
                ? CellAt(centroid.Z, length, new AxisDirection(Math.Sign(normal.X), 0, 0))
                : null;
        }
        if (Math.Abs(Math.Abs(normal.Y) - 1) < 1e-9)
        {
            return Math.Abs(Math.Abs(centroid.Y) - 0.5) < 1e-9
                ? CellAt(centroid.Z, length, new AxisDirection(0, Math.Sign(normal.Y), 0))
                : null;
        }
        if (Math.Abs(Math.Abs(normal.Z) - 1) < 1e-9)
        {
            // A forward face closes the cell behind it; a back face opens the cell ahead.
            var cell = centroid.Z - 0.5 * Math.Sign(normal.Z);
            return CellAt(cell, length, new AxisDirection(0, 0, Math.Sign(normal.Z)));
        }
        return null;
    }

    private static CellFace? CellAt(double cell, int length, AxisDirection direction)
    {
        var index = (int)Math.Round(cell, MidpointRounding.AwayFromZero);
        return Math.Abs(cell - index) < 1e-9 && index >= 0 && index < length
            ? new CellFace(index, direction)
            : null;
    }

    /// <summary>
    /// Maps a face onto the material brush across the whole footprint, so a beam or a long
    /// slope carries one seam outline rather than one per metre. The axis the face most
    /// faces is dropped and the other two are scaled by the footprint's extent.
    /// </summary>
    private static Point[] TextureCoordinatesFor(IReadOnlyList<Point3D> polygon, Vector3D normal, int length)
    {
        var magnitudes = new[] { Math.Abs(normal.X), Math.Abs(normal.Y), Math.Abs(normal.Z) };
        var dropped = magnitudes[0] >= magnitudes[1] && magnitudes[0] >= magnitudes[2]
            ? 0
            : magnitudes[1] >= magnitudes[2] ? 1 : 2;
        var vertical = dropped == 0 ? 1 : 0;
        var horizontal = dropped == 2 ? 1 : 2;

        return [.. polygon.Select(point => new Point(
            Fraction(Component(point, horizontal), horizontal, length),
            Fraction(Component(point, vertical), vertical, length)))];
    }

    private static double Component(Point3D point, int axis) => axis switch
    {
        0 => point.X,
        1 => point.Y,
        _ => point.Z,
    };

    private static double Fraction(double value, int axis, int length) =>
        axis == 2 ? (value + 0.5) / length : value + 0.5;

    private static Point3D Centroid(IReadOnlyList<Point3D> polygon)
    {
        double x = 0, y = 0, z = 0;
        foreach (var point in polygon)
        {
            x += point.X;
            y += point.Y;
            z += point.Z;
        }
        return new Point3D(x / polygon.Count, y / polygon.Count, z / polygon.Count);
    }

    /// <summary>Gets a planar polygon's area from its edge cross products.</summary>
    private static double Area(IReadOnlyList<Point3D> polygon)
    {
        var sum = new Vector3D();
        for (var index = 0; index < polygon.Count; index++)
        {
            var current = (Vector3D)polygon[index];
            var next = (Vector3D)polygon[(index + 1) % polygon.Count];
            sum += Vector3D.CrossProduct(current, next);
        }
        return sum.Length / 2;
    }
}
