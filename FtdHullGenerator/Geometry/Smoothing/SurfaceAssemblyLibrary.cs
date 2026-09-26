using System.Numerics;
using FtdHullGenerator.Domain;

namespace FtdHullGenerator.Geometry.Smoothing;

/// <summary>
/// Finite native assemblies. Every transverse/longitudinal boundary is either
/// covered by another piece or reserved as a full-cube cap; an incomplete pair
/// is never made into a candidate. Decorative geometry is deliberately absent.
/// </summary>
/// <remarks>
/// Completeness is the contract: a pattern is admitted only when every exposed plane is
/// matched and every boundary is covered or capped. Occupied-cell adjacency alone does not
/// prove a smooth physical join between non-cubic pieces, which is why patterns are declared
/// here rather than discovered by neighbour tests.
/// The triangle/inverted pair and hybrid panels were measured against native geometry.
/// </remarks>
public static class SurfaceAssemblyLibrary
{
    public static IReadOnlyList<SurfaceAssemblyPattern> Patterns { get; } = Build();

    private static IReadOnlyList<SurfaceAssemblyPattern> Build()
    {
        var result = new List<SurfaceAssemblyPattern>();
        foreach (var rotation in new[] { 16, 17, 5, 9 })
        for (var length = 1; length <= 4; length++)
        {
            Add($"horizontal/vertical slope {length}m r{rotation}",
                [Part(SlopeFillPlacement.ShapeForLength(length), rotation)]);
            foreach (var hand in new[] { "Left", "Right" })
            {
                var triangle = Part(Enum.Parse<BlockShape>($"Corner{hand}{(length == 1 ? "" : length)}"), rotation);
                var inverse = Part(Enum.Parse<BlockShape>($"InverseCorner{hand}{(length == 1 ? "" : length)}"), rotation);
                var axes = BlockRotations.GetRotationAxes(rotation);
                // Imported left cuts N*x+N*y+z=-.5. Moving its inverse
                // -localX (right uses +localX) makes the two cut planes identical.
                var shift = hand == "Left" ? axes.Right.Negate() : axes.Right;
                inverse = inverse with { X = shift.X, Y = shift.Y, Z = shift.Z };
                Add($"coplanar triangle/inverted {hand} {length}m r{rotation}", [triangle, inverse]);
            }

            // Serchio's end-to-end inverse/triangle pair has the same exposed
            // plane. Consecutive pairs move one metre inward and one native
            // length forward; adjacent rows shift one length along the run.
            foreach (var hand in new[] { "Left", "Right" })
            for (var steps = 1; steps <= 3; steps++)
            for (var rows = 1; rows <= 2; rows++)
            {
                var axes = BlockRotations.GetRotationAxes(rotation);
                var transverse = hand == "Left" ? axes.Right.Negate() : axes.Right;
                var forward = axes.Forward;
                var inward = axes.Up.Negate();
                var triangleShape = Enum.Parse<BlockShape>($"Corner{hand}{(length == 1 ? "" : length)}");
                var inverseShape = Enum.Parse<BlockShape>($"InverseCorner{hand}{(length == 1 ? "" : length)}");
                var parts = new List<BlockPlacement>();
                for (var step = 0; step < steps; step++)
                {
                    for (var row = 0; row < rows; row++)
                    {
                        var anchor = Sum(inward, step, transverse, row, forward, (step + row) * length);
                        parts.Add(At(inverseShape, rotation, anchor));
                        parts.Add(At(triangleShape, rotation, AddAxis(anchor, forward, length)));
                    }
                    // These two ordinary-slope faces exactly match the lower
                    // inverse and upper triangle boundaries, respectively.
                    var lower = Sum(inward, step, transverse, -1, forward, step * length);
                    var upper = Sum(inward, step, transverse, rows, forward, (step + rows) * length);
                    parts.Add(At(SlopeFillPlacement.ShapeForLength(length), rotation, lower));
                    parts.Add(At(SlopeFillPlacement.ShapeForLength(length), rotation, upper));
                }
                Add($"Serchio hybrid panel {hand} {length}m {steps}x{rows} r{rotation}", parts.ToArray(), true);
            }
        }

        foreach (var shape in Enum.GetValues<BlockShape>())
        {
            var family = BlockShapeMetadata.Get(shape).Family;
            if (family is not (StructuralFamily.BeamSlope or StructuralFamily.SquareCorner or
                StructuralFamily.SquareBackedCorner or StructuralFamily.SlopeTransition or
                StructuralFamily.InverseTransition))
                continue;
            foreach (var rotation in new[] { 0, 2, 12, 14, 16, 17, 5, 9 })
            {
                Add($"native {shape} r{rotation}", [Part(shape, rotation)]);
                if (family == StructuralFamily.BeamSlope && BlockShapeMetadata.Get(shape).Length == 4)
                foreach (var count in new[] { 2, 4, 8 })
                {
                    var forward = BlockRotations.GetRotationAxes(rotation).Forward;
                    Add($"continuous beam band {count * 4}m {shape} r{rotation}", Enumerable.Range(0, count)
                        .Select(index => Part(shape, rotation) with
                        {
                            X = forward.X * index * 4, Y = forward.Y * index * 4, Z = forward.Z * index * 4,
                        }).ToArray());
                }
            }
        }
        foreach (var rotation in new[] { 16, 17, 5, 9 })
        foreach (var hand in new[] { "Left", "Right" })
        foreach (var (firstLength, secondLength) in new[] { (4, 3), (3, 2), (2, 1), (3, 4), (2, 3), (1, 2) })
        {
            // The Serchio 4m -> 3m end sequence changes length at the shared
            // triangular end face. The ordinary slopes below/above use the
            // inverse/triangle's own length, so every shared boundary matches.
            var axes = BlockRotations.GetRotationAxes(rotation);
            var transverse = hand == "Left" ? axes.Right.Negate() : axes.Right;
            var inward = axes.Up.Negate();
            var forward = axes.Forward;
            var pieces = new List<BlockPlacement>();
            for (var step = 0; step < 2; step++)
            {
                var inverseLength = step == 0 ? firstLength : secondLength;
                var triangleLength = secondLength;
                var start = step == 0 ? 0 : firstLength;
                var anchor = Sum(inward, step, transverse, 0, forward, start);
                var end = AddAxis(anchor, forward, inverseLength);
                pieces.Add(At(Enum.Parse<BlockShape>($"InverseCorner{hand}{(inverseLength == 1 ? "" : inverseLength)}"), rotation, anchor));
                pieces.Add(At(Enum.Parse<BlockShape>($"Corner{hand}{(triangleLength == 1 ? "" : triangleLength)}"), rotation, end));
                pieces.Add(At(SlopeFillPlacement.ShapeForLength(inverseLength), rotation, AddAxis(anchor, transverse, -1)));
                pieces.Add(At(SlopeFillPlacement.ShapeForLength(triangleLength), rotation, AddAxis(end, transverse, 1)));
            }
            Add($"Serchio length change {hand} {firstLength}to{secondLength}m r{rotation}", pieces.ToArray(), true);
        }
        // Several native beam rotations and handed aliases describe exactly the
        // same placed envelope. Keep one deterministic representative rather than
        // spending the search budget on indistinguishable alternatives.
        return result.GroupBy(pattern => string.Join("|", pattern.Placements
                .SelectMany(StructuralShapeGeometry.WorldFaces).Select(face => string.Join(";", face.Points
                    .Select(point => $"{point.X:R},{point.Y:R},{point.Z:R}").OrderBy(value => value, StringComparer.Ordinal)))
                .OrderBy(value => value, StringComparer.Ordinal)), StringComparer.Ordinal)
            .Select(group => group.First()).ToArray();

        void Add(string name, BlockPlacement[] placements, bool intendedGradientChange = false)
        {
            var pattern = SurfaceAssemblyPattern.Create(name, placements, intendedGradientChange);
            if (pattern is not null) result.Add(pattern);
        }
    }

    private static BlockPlacement Part(BlockShape shape, int rotation) =>
        new(shape, MaterialKind.Metal, 0, 0, 0, rotation) { Origin = BlockOrigin.Smoothing };
    private static (int X,int Y,int Z) Sum(AxisDirection a, int aa, AxisDirection b, int bb, AxisDirection c, int cc) =>
        (a.X*aa+b.X*bb+c.X*cc, a.Y*aa+b.Y*bb+c.Y*cc, a.Z*aa+b.Z*bb+c.Z*cc);
    private static (int X,int Y,int Z) AddAxis((int X,int Y,int Z) p, AxisDirection a, int amount) =>
        (p.X+a.X*amount,p.Y+a.Y*amount,p.Z+a.Z*amount);
    private static BlockPlacement At(BlockShape shape,int rotation,(int X,int Y,int Z) p) =>
        Part(shape,rotation) with { X=p.X,Y=p.Y,Z=p.Z };
}

public sealed record SurfaceAssemblyPattern(
    string Name,
    IReadOnlyList<BlockPlacement> Placements,
    IReadOnlyList<(int X, int Y, int Z)> Footprint,
    IReadOnlyList<(int X, int Y, int Z)> RequiredBacking,
    IReadOnlyList<Vector3> SurfaceNormals,
    double SurfaceArea,
    double Volume)
{
    internal bool IsContinuousBeamBand { get; } = Placements.Count > 1 &&
        Placements.All(part => BlockShapeMetadata.Get(part.Shape).Family == StructuralFamily.BeamSlope);
    internal IReadOnlySet<(int X, int Y, int Z)> FootprintSet { get; } = Footprint.ToHashSet();
    internal IReadOnlyList<(int X, int Y, int Z)> GuardFootprint { get; } = BuildGuard(Footprint);

    private static IReadOnlyList<(int X, int Y, int Z)> BuildGuard(IReadOnlyList<(int X, int Y, int Z)> footprint)
    {
        var occupied = footprint.ToHashSet();
        return footprint.SelectMany(cell => BlockRotations.AxisDirections.Select(axis =>
                (X: cell.X + axis.X, Y: cell.Y + axis.Y, Z: cell.Z + axis.Z)))
            .Where(cell => !occupied.Contains(cell)).Distinct().ToArray();
    }
    public PreparedSurfacePatch SurfaceMetric { get; } = SurfacePatchMetrics.Prepare(Placements);
    public IReadOnlyDictionary<(int X, int Y, int Z), double> InwardCoverage { get; } =
        MeasureInwardCoverage(Placements, Footprint);
    public IReadOnlyDictionary<((int X, int Y, int Z) Cell, AxisDirection Direction), double> BoundaryCoverage { get; } =
        MeasureBoundaryCoverage(Placements, Footprint);
    private IReadOnlyDictionary<(int X, int Y, int Z), byte>? _fullFaceMasks;
    internal IReadOnlyDictionary<(int X, int Y, int Z), byte> FullFaceMasks => _fullFaceMasks ??=
        Footprint.ToDictionary(cell => cell, cell =>
        {
            byte mask = 0;
            for (var index = 0; index < BlockRotations.AxisDirections.Length; index++)
                if (BoundaryCoverage[(cell, BlockRotations.AxisDirections[index])] >= 1 - 1e-4)
                    mask |= (byte)(1 << index);
            return mask;
        });

    private static IReadOnlyDictionary<((int X, int Y, int Z) Cell, AxisDirection Direction), double> MeasureBoundaryCoverage(
        IReadOnlyList<BlockPlacement> parts, IReadOnlyList<(int X, int Y, int Z)> footprint)
    {
        var faces = parts.SelectMany(StructuralShapeGeometry.WorldFaces).Where(face => AxisAligned(face.Normal)).ToArray();
        var result = new Dictionary<((int X, int Y, int Z) Cell, AxisDirection Direction), double>();
        foreach (var cell in footprint)
        foreach (var direction in BlockRotations.AxisDirections)
        {
            var normal = new Vector3(direction.X, direction.Y, direction.Z);
            var cube = new BlockPlacement(BlockShape.Cube, MaterialKind.Metal,
                cell.X + direction.X, cell.Y + direction.Y, cell.Z + direction.Z, 0);
            var opposing = StructuralShapeGeometry.WorldFaces(cube).Single(face => Vector3.Dot(face.Normal, normal) < -.9999f);
            result[(cell, direction)] = faces.Sum(face => StructuralShapeGeometry.ContactArea(face, opposing));
        }
        return result;
    }

    private static IReadOnlyDictionary<(int X, int Y, int Z), double> MeasureInwardCoverage(
        IReadOnlyList<BlockPlacement> parts, IReadOnlyList<(int X, int Y, int Z)> footprint)
    {
        var faces = parts.SelectMany(StructuralShapeGeometry.WorldFaces).Where(face => face.Normal.X < -.9999f).ToArray();
        return footprint.ToDictionary(cell => cell, cell =>
        {
            var cube = new BlockPlacement(BlockShape.Cube, MaterialKind.Metal, cell.X - 1, cell.Y, cell.Z, 0);
            var cubeFace = StructuralShapeGeometry.WorldFaces(cube).Single(face => face.Normal.X > .9999f);
            return faces.Sum(face => StructuralShapeGeometry.ContactArea(face, cubeFace));
        });
    }
    internal static SurfaceAssemblyPattern? Create(string name, IReadOnlyList<BlockPlacement> parts,
        bool intendedGradientChange = false)
    {
        const double tolerance = 1e-4;
        var cells = parts.SelectMany(part => part.OccupiedCells).ToArray();
        // A diagonal panel may expose two neighbouring footprint cells on the
        // same row. Each is later checked against the original surface allowance.
        if (cells.Distinct().Count() != cells.Length)
            return null;
        var faces = parts.SelectMany(StructuralShapeGeometry.WorldFaces).ToArray();
        var surface = faces.Where(face => !AxisAligned(face.Normal)).ToArray();
        if (surface.Length == 0 || surface.Any(face => face.Normal.X <= .01f)) return null;
        if (parts.Count > 1)
        {
            if (StructuralShapeGeometry.TouchingArea(parts[0], parts[1]) <= tolerance) return null;
            var joined = new HashSet<int> { 0 };
            bool advanced;
            do
            {
                advanced = false;
                for (var i = 0; i < parts.Count; i++)
                    if (!joined.Contains(i) && joined.Any(j => StructuralShapeGeometry.TouchingArea(parts[i], parts[j]) > tolerance))
                        advanced |= joined.Add(i);
            } while (advanced);
            if (joined.Count != parts.Count) return null;
            for (var firstIndex = 0; firstIndex < parts.Count; firstIndex++)
            for (var secondIndex = firstIndex + 1; secondIndex < parts.Count; secondIndex++)
            {
                var firstFaces = StructuralShapeGeometry.WorldFaces(parts[firstIndex]);
                var secondFaces = StructuralShapeGeometry.WorldFaces(parts[secondIndex]);
                var firstBoxes = parts[firstIndex].OccupiedCells.SelectMany(cell => StructuralShapeGeometry.WorldFaces(
                    new(BlockShape.Cube, MaterialKind.Metal, cell.X, cell.Y, cell.Z, 0))).ToArray();
                var secondBoxes = parts[secondIndex].OccupiedCells.SelectMany(cell => StructuralShapeGeometry.WorldFaces(
                    new(BlockShape.Cube, MaterialKind.Metal, cell.X, cell.Y, cell.Z, 0))).ToArray();
                var contact = firstFaces.Sum(face => secondFaces.Sum(other => StructuralShapeGeometry.ContactArea(face, other)));
                var firstBoundary = firstFaces.Sum(face => secondBoxes.Sum(other => StructuralShapeGeometry.ContactArea(face, other)));
                var secondBoundary = secondFaces.Sum(face => firstBoxes.Sum(other => StructuralShapeGeometry.ContactArea(face, other)));
                if (Math.Abs(firstBoundary - contact) > tolerance || Math.Abs(secondBoundary - contact) > tolerance)
                    return null;
            }
            // The observed 3m/1m end contact is not admitted as a constant-plane
            // band; multi-piece bands here require matching exposed planes.
            var first = surface[0];
            if (!intendedGradientChange && surface.Any(face => Vector3.Dot(first.Normal, face.Normal) < 1 - tolerance ||
                                   Math.Abs(Vector3.Dot(first.Normal, face.Points[0] - first.Points[0])) > tolerance))
                return null;
        }

        var occupied = cells.ToHashSet();
        var backing = new HashSet<(int X, int Y, int Z)>();
        foreach (var face in faces)
        {
            if (!AxisAligned(face.Normal) || Math.Abs(face.Normal.X) > .99f) continue;
            var covered = faces.Sum(other => StructuralShapeGeometry.ContactArea(face, other));
            if (covered >= face.Area - tolerance) continue;
            var direction = ((int)Math.Round(face.Normal.X), (int)Math.Round(face.Normal.Y),
                (int)Math.Round(face.Normal.Z));
            foreach (var cell in cells)
            {
                var neighbour = (cell.X + direction.Item1, cell.Y + direction.Item2, cell.Z + direction.Item3);
                if (occupied.Contains(neighbour)) continue;
                var cube = new BlockPlacement(BlockShape.Cube, MaterialKind.Metal,
                    neighbour.Item1, neighbour.Item2, neighbour.Item3, 0);
                var area = StructuralShapeGeometry.WorldFaces(cube).Sum(other => StructuralShapeGeometry.ContactArea(face, other));
                if (area <= tolerance) continue;
                covered += area;
                backing.Add(neighbour);
            }
            if (Math.Abs(covered - face.Area) > tolerance) return null;
        }
        if (backing.Count == 0) return null;
        var visible = faces.Where(face => face.Normal.X > .001f).Sum(face => face.Area);
        // Divergence theorem; native convex solids and complete disjoint unions.
        var volume = faces.Sum(face => face.Area * Vector3.Dot(face.Normal, face.Points[0])) / 3;
        return new(name, parts.ToArray(), cells, backing.OrderBy(cell => cell.Z).ThenBy(cell => cell.Y).ToArray(),
            surface.Select(face => face.Normal).Distinct().ToArray(), visible, Math.Abs(volume));
    }

    internal static bool AxisAligned(Vector3 normal) =>
        Math.Abs(normal.X) > .99999f || Math.Abs(normal.Y) > .99999f || Math.Abs(normal.Z) > .99999f;
}
