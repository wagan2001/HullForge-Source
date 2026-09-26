using FtdHullGenerator.Domain;

namespace FtdHullGenerator.Geometry;

/// <summary>Exterior area affected by a replacement, including the unchanged collar.</summary>
public readonly record struct SurfacePatchArea(double SurfaceArea, double StepArea);

public readonly record struct SurfacePatchComparison(SurfacePatchArea Original, SurfacePatchArea Revised)
{
    public double SurfaceImprovement => Original.SurfaceArea - Revised.SurfaceArea;
    public double StepImprovement => Original.StepArea - Revised.StepArea;
}

/// <summary>
/// Measures actual exterior change when native pieces replace cells of a sampled solid.
/// A triangle touching half a cube's face leaves the other half exposed: that residual
/// belongs to the new area even though the supporting cube is outside the footprint.
/// </summary>
public static class SurfacePatchMetrics
{
    /// <summary>
    /// Precomputes geometric contacts once for a template. Occupancy supplied to Compare
    /// uses the same coordinates as the template; callers can translate their query.
    /// </summary>
    public static PreparedSurfacePatch Prepare(IReadOnlyList<BlockPlacement> placements) => new(placements);

    public static SurfacePatchComparison Compare(IReadOnlyList<BlockPlacement> placements,
        Func<(int X, int Y, int Z), bool> isOriginalSolid) => Prepare(placements).Compare(isOriginalSolid);
}

/// <summary>
/// A reusable metric for a finite assembly. Polygon contact calculations happen during
/// preparation; candidate comparison requires only integer-cell occupancy lookups.
/// The metric counts only faces that can change, so it excludes the collar's unchanged
/// outward faces from both totals. The difference equals the full solid's area change.
/// </summary>
public sealed class PreparedSurfacePatch
{
    private readonly (int X, int Y, int Z)[] _cells;
    private readonly Edge[] _edges;
    private readonly SurfacePatchArea _unsupportedAssembly;

    internal PreparedSurfacePatch(IReadOnlyList<BlockPlacement> placements)
    {
        ArgumentNullException.ThrowIfNull(placements);
        var owners = new Dictionary<(int X, int Y, int Z), int>();
        for (var i = 0; i < placements.Count; i++)
        foreach (var cell in placements[i].OccupiedCells)
            if (!owners.TryAdd(cell, i))
                throw new ArgumentException("Surface metric assemblies must have disjoint occupied footprints.", nameof(placements));
        _cells = owners.Keys.ToArray();
        var cellIndices = _cells.Select((cell, index) => (cell, index)).ToDictionary(pair => pair.cell, pair => pair.index);
        var faces = placements.Select(StructuralShapeGeometry.WorldFaces).ToArray();
        var surfaceArea = faces.Sum(part => part.Sum(face => face.Area));
        var stepArea = faces.Sum(part => part.Where(IsStepFace).Sum(face => face.Area));
        // Faces between fitted parts are internal twice, once on either owner.
        for (var first = 0; first < faces.Length; first++)
        for (var second = first + 1; second < faces.Length; second++)
        foreach (var a in faces[first])
        foreach (var b in faces[second])
        {
            var contact = StructuralShapeGeometry.ContactArea(a, b);
            surfaceArea -= 2 * contact;
            if (IsStepFace(a)) stepArea -= 2 * contact;
        }
        _unsupportedAssembly = new(surfaceArea, stepArea);

        var edges = new List<Edge>();
        foreach (var cell in _cells)
        foreach (var direction in BlockRotations.AxisDirections)
        {
            var adjacent = (X: cell.X + direction.X, Y: cell.Y + direction.Y, Z: cell.Z + direction.Z);
            var first = cellIndices[cell];
            if (cellIndices.TryGetValue(adjacent, out var second))
            {
                if (first < second) edges.Add(new(first, second, adjacent, direction.X == 0, 0));
                continue;
            }
            var cube = new BlockPlacement(BlockShape.Cube, MaterialKind.Metal, adjacent.X, adjacent.Y, adjacent.Z, 0);
            var cubeFaces = StructuralShapeGeometry.WorldFaces(cube);
            // One cube face can touch just this owner cell. ContactArea clips the
            // full native face to that square, including partial triangles/trapezoids.
            var coverage = faces[owners[cell]].Sum(face =>
                cubeFaces.Sum(other => StructuralShapeGeometry.ContactArea(face, other)));
            edges.Add(new(first, -1, adjacent, direction.X == 0, Math.Clamp(coverage, 0, 1)));
        }
        _edges = edges.ToArray();
    }

    public SurfacePatchComparison Compare(Func<(int X, int Y, int Z), bool> isOriginalSolid)
    {
        ArgumentNullException.ThrowIfNull(isOriginalSolid);
        var occupied = _cells.Select(isOriginalSolid).ToArray();
        double oldArea = 0, oldSteps = 0;
        var newArea = _unsupportedAssembly.SurfaceArea;
        var newSteps = _unsupportedAssembly.StepArea;
        foreach (var edge in _edges)
        {
            var neighborPresent = edge.Second >= 0 ? occupied[edge.Second] : isOriginalSolid(edge.Neighbor);
            // Count each boundary once, including an original collar face facing
            // a footprint cell that did not exist before an outward fill.
            if (occupied[edge.First] != neighborPresent)
            {
                oldArea++;
                if (edge.IsStep) oldSteps++;
            }
            if (edge.Second >= 0 || !neighborPresent) continue;
            // Remove the covered part face and add the residual collar square.
            var correction = 1 - 2 * edge.ContactArea;
            newArea += correction;
            if (edge.IsStep) newSteps += correction;
        }
        return new(new(oldArea, oldSteps), new(newArea, newSteps));
    }

    private static bool IsStepFace(StructuralFace face) => Math.Abs(face.Normal.X) < 1e-5 &&
        (Math.Abs(face.Normal.Y) > .99999 || Math.Abs(face.Normal.Z) > .99999);

    private readonly record struct Edge(int First, int Second, (int X, int Y, int Z) Neighbor,
        bool IsStep, double ContactArea);
}
