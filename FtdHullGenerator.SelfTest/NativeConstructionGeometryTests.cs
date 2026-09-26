using System.Numerics;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Geometry;
using static SlopeFillTestSupport;

/// <summary>
/// Analytic fixtures transcribed from imported native mesh coordinates, independently
/// of the planner's pattern declarations, including the measured Z=11.5
/// interface. Raw game assets stay untracked.
/// </summary>
internal static class NativeConstructionGeometryTests
{
    private static readonly BlockShape[] LeftTriangles =
        [BlockShape.CornerLeft, BlockShape.CornerLeft2, BlockShape.CornerLeft3, BlockShape.CornerLeft4];
    private static readonly BlockShape[] RightTriangles =
        [BlockShape.CornerRight, BlockShape.CornerRight2, BlockShape.CornerRight3, BlockShape.CornerRight4];
    private static readonly BlockShape[] LeftInverses =
        [BlockShape.InverseCornerLeft, BlockShape.InverseCornerLeft2,
         BlockShape.InverseCornerLeft3, BlockShape.InverseCornerLeft4];
    private static readonly BlockShape[] RightInverses =
        [BlockShape.InverseCornerRight, BlockShape.InverseCornerRight2,
         BlockShape.InverseCornerRight3, BlockShape.InverseCornerRight4];

    public static void Run()
    {
        for (var length = 1; length <= 4; length++)
        {
            // Native OBJ import reflects X once. The left tetrahedron's solid corner
            // is (-.5,-.5,-.5), not (+.5,-.5,-.5). Pure volume/mirror tests miss this bug.
            Vector3[] leftVertices =
            [new(-.5f, -.5f, -.5f), new(.5f, -.5f, -.5f),
             new(-.5f, .5f, -.5f), new(-.5f, -.5f, length - .5f)];
            CheckVertices(LeftTriangles[length - 1], leftVertices);
            CheckVertices(RightTriangles[length - 1], leftVertices.Select(MirrorX).ToArray());
            var inverseVertices = (from x in new[] { -.5f, .5f }
                                   from y in new[] { -.5f, .5f }
                                   from z in new[] { -.5f, length - .5f }
                                   where !(x == .5f && y == .5f && z == length - .5f)
                                   select new Vector3(x, y, z)).ToArray();
            CheckVertices(LeftInverses[length - 1], inverseVertices);
            CheckVertices(RightInverses[length - 1], inverseVertices.Select(MirrorX).ToArray());

            // All saved rotations must preserve the exact measured local geometry and
            // produce the appropriate mirror, not merely mirrored occupied cells.
            foreach (var rotation in Enumerable.Range(0, 24))
            foreach (var (left, right) in new[]
                     { (LeftTriangles[length - 1], RightTriangles[length - 1]),
                       (LeftInverses[length - 1], RightInverses[length - 1]) })
            {
                var original = new BlockPlacement(left, MaterialKind.Metal, 3, 4, 5, rotation);
                var mirrored = new BlockPlacement(right, MaterialKind.Metal, -3, 4, 5,
                    HullGeometryValidator.MirrorRotation(rotation));
                var first = StructuralShapeGeometry.WorldFaces(original).SelectMany(face => face.Points).Select(MirrorX);
                var second = StructuralShapeGeometry.WorldFaces(mirrored).SelectMany(face => face.Points);
                Require(Quantize(first).SetEquals(Quantize(second)),
                    $"{left}/{right} length {length} rotation {rotation} did not mirror imported mesh geometry.");
            }

            // A constant-gradient two-row skin uses one triangle and one inverse;
            // their exposed cuts are coplanar and their shared face has nonzero area.
            var lower = new BlockPlacement(LeftTriangles[length - 1], MaterialKind.Metal, 0, 0, 0, 16);
            var upper = new BlockPlacement(LeftInverses[length - 1], MaterialKind.Metal, 0, 1, 0, 16);
            Require(StructuralShapeGeometry.TouchingArea(lower, upper) > 1e-6,
                $"The {length} m native stacked pair lost its positive-area attachment.");
            var lowerCut = CutFace(lower);
            var upperCut = CutFace(upper);
            Require(Vector3.Cross(lowerCut.Normal, upperCut.Normal).Length() < 1e-5 &&
                    Math.Abs(Vector3.Dot(lowerCut.Normal, lowerCut.Points[0] - upperCut.Points[0])) < 1e-5,
                $"The {length} m native stacked pair has a stepped exposed plane.");
        }

        VerifyAddedVocabulary();
        VerifySerchioBand();
        VerifyObservedLengthChange();
        Console.WriteLine("Native construction geometry: 1–4 m handed meshes, all rotations, Serchio's lateral band, stacked planes, and hand 3→1 contact passed.");
    }

    private static void VerifySerchioBand()
    {
        // Six actual main-construct placements in Serchio Class BC2, source SHA-256
        // 1fcc395dc5d3a142841fd1113f5a769f5f2387c56c025bc5c54e01777cf1bf60.
        // This band crosses lateral cells. It cannot be represented by separately
        // cube-capping every same-X triangle/inverse pair.
        foreach (var side in new[] { 1, -1 })
        {
            BlockPlacement Part(bool inverse, int x, int z) => new(
                (side, inverse) switch
                {
                    (1, true) => BlockShape.InverseCornerLeft4,
                    (1, false) => BlockShape.CornerLeft4,
                    (-1, true) => BlockShape.InverseCornerRight4,
                    _ => BlockShape.CornerRight4,
                }, MaterialKind.Metal, side * x, 3, z, side == 1 ? 16 : 18);
            BlockPlacement[] band =
            [Part(true, 7, -12), Part(false, 7, -8), Part(true, 6, -8),
             Part(false, 6, -4), Part(true, 5, -4), Part(false, 5, 0)];
            var cells = band.SelectMany(block => block.OccupiedCells).ToArray();
            Require(cells.Length == 24 && cells.Distinct().Count() == 24,
                "The observed Serchio band overlaps native four-metre footprints.");
            var normal = new Vector3(side * 4, -4, 1);
            foreach (var part in band)
                Require(CutFace(part).Points.All(point => Math.Abs(Vector3.Dot(normal, point) - 7.5) < 1e-5),
                    "Serchio's observed lower-side band no longer forms its independently measured single plane.");
            for (var index = 0; index + 1 < band.Length; index++)
                Require(Math.Abs(StructuralShapeGeometry.TouchingArea(band[index], band[index + 1]) -
                                 (index % 2 == 0 ? .5 : 2)) < 1e-5,
                    "Serchio's end/side interlocking band lost its measured contact area.");
        }
    }

    private static void VerifyAddedVocabulary()
    {
        // Explicit family pairs guard against catalog/metadata mirroring the wrong
        // historical filename. All six native length-change pairs are intentional.
        var pairs = new List<(string Left, string Right, int Length)>();
        for (var length = 2; length <= 4; length++)
        {
            pairs.Add(($"BeamSlope{length}", $"BeamSlopeMirrored{length}", length));
            pairs.Add(($"SquareBackedCornerLeft{length}", $"SquareBackedCornerRight{length}", length));
        }
        for (var length = 1; length <= 4; length++)
            pairs.Add(($"SquareCornerLeft{(length == 1 ? "" : length)}",
                $"SquareCornerRight{(length == 1 ? "" : length)}", length));
        foreach (var code in new[] { "12", "13", "14", "23", "24", "34" })
        {
            pairs.Add(($"SlopeTransitionLeft{code}", $"SlopeTransitionRight{code}", code[1] - '0'));
            pairs.Add(($"InverseTransitionLeft{code}", $"InverseTransitionRight{code}", code[1] - '0'));
        }
        foreach (var (leftName, rightName, length) in pairs)
        {
            var left = Enum.Parse<BlockShape>(leftName);
            var right = Enum.Parse<BlockShape>(rightName);
            Require(HullGeometryValidator.MirrorShape(left) == right && HullGeometryValidator.MirrorShape(right) == left,
                $"Native {leftName}/{rightName} did not retain its exact family mirror.");
            foreach (var shape in new[] { left, right })
            {
                var placement = new BlockPlacement(shape, MaterialKind.Metal, 0, 0, 0, 16);
                Require(placement.CellLength == length && placement.OccupiedCells.SequenceEqual(
                        Enumerable.Range(0, length).Select(z => (0, 0, z))),
                    $"{shape} did not occupy its independently specified positive-forward {length} m footprint.");
            }
            var mirroredPoints = StructuralShapeGeometry.Faces(left).SelectMany(face => face.Points).Select(MirrorX);
            Require(Quantize(mirroredPoints).SetEquals(Quantize(StructuralShapeGeometry.Faces(right).SelectMany(face => face.Points))),
                $"Native {leftName}/{rightName} envelopes did not mirror each other.");
        }
    }

    private static void VerifyObservedLengthChange()
    {
        foreach (var side in new[] { 1, -1 })
        {
            var inverse = new BlockPlacement(side == 1 ? BlockShape.InverseCornerLeft3 : BlockShape.InverseCornerRight3,
                MaterialKind.Metal, side, 4, 9, side == 1 ? 16 : 18);
            var triangle = new BlockPlacement(side == 1 ? BlockShape.CornerLeft : BlockShape.CornerRight,
                MaterialKind.Metal, side, 4, 12, side == 1 ? 16 : 18);
            Vector3[] expected = [new(side * .5f, 3.5f, 11.5f), new(side * .5f, 4.5f, 11.5f),
                new(side * 1.5f, 4.5f, 11.5f)];
            foreach (var block in new[] { inverse, triangle })
            {
                var boundary = StructuralShapeGeometry.WorldFaces(block)
                    .Where(face => face.Points.All(point => Math.Abs(point.Z - 11.5) < 1e-6))
                    .SelectMany(face => face.Points);
                Require(Quantize(boundary).SetEquals(Quantize(expected)),
                    $"The hand fixture's {block.Shape} interface does not match its independently measured three vertices.");
            }
            Require(Math.Abs(StructuralShapeGeometry.TouchingArea(inverse, triangle) - .5) < 1e-5,
                "The observed 3 m inverse/1 m triangle interface is not the measured half-square contact.");
            Require(Vector3.Cross(CutFace(inverse).Normal, CutFace(triangle).Normal).Length() > .1,
                "A matching 3 m/1 m attachment was incorrectly treated as unchanged surface angle.");
            Require(StructuralShapeGeometry.TouchingArea(inverse, triangle with { Y = 5 }) < 1e-6,
                "Edge-only contact was incorrectly accepted as a positive-area attachment.");
            Require(StructuralShapeGeometry.TouchingArea(inverse, triangle with { Z = 13 }) < 1e-6,
                "A one-metre gap was incorrectly accepted as a positive-area attachment.");
        }
    }

    private static void CheckVertices(BlockShape shape, IReadOnlyList<Vector3> expected)
    {
        var actual = StructuralShapeGeometry.Faces(shape).SelectMany(face => face.Points);
        Require(Quantize(actual).SetEquals(Quantize(expected)),
            $"{shape} does not match the independently measured imported native envelope.");
    }

    private static StructuralFace CutFace(BlockPlacement block) =>
        StructuralShapeGeometry.WorldFaces(block).Single(face =>
            Math.Abs(face.Normal.X) > 1e-6 && Math.Abs(face.Normal.Y) > 1e-6 && Math.Abs(face.Normal.Z) > 1e-6);

    private static Vector3 MirrorX(Vector3 point) => new(-point.X, point.Y, point.Z);
    private static HashSet<(int X, int Y, int Z)> Quantize(IEnumerable<Vector3> points) =>
        points.Select(point => ((int)Math.Round(point.X * 100000),
            (int)Math.Round(point.Y * 100000), (int)Math.Round(point.Z * 100000))).ToHashSet();
}
