using System.Numerics;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Geometry;
using FtdHullGenerator.Geometry.Smoothing;
using static SlopeFillTestSupport;

internal static class ConstructionSkinTests
{
    private static readonly float[] SampleOffsets = [-.375f, -.125f, .125f, .375f];

    public static void VerifySamplingFixture()
    {
        // Native r16 left triangle at the original +X edge has a half-square
        // inward face. Its complementary inverse retains the complete inward
        // square. Lattice connectivity and even positive-area contact miss this.
        var triangle = new BlockPlacement(BlockShape.CornerLeft, MaterialKind.Metal, 0, 0, 0, 16);
        var inverse = triangle with { Shape = BlockShape.InverseCornerLeft };
        Require(CoveredSamples(StructuralShapeGeometry.WorldFaces(triangle), (0, 0, 0), new(-1, 0, 0)) == 10,
            "The independent quarter-metre probes no longer detect a triangle's missing inward half-face.");
        Require(CoveredSamples(StructuralShapeGeometry.WorldFaces(inverse), (0, 0, 0), new(-1, 0, 0)) == 16,
            "The complementary inverse does not preserve the original complete inward face.");

        // Actual Orca leak found by independent sub-cell review: this wedge's +X
        // inward face is complete, but its +Y face opens into the cavity above.
        // Checking only the lateral back face would falsely accept it.
        var orcaSlope = new BlockPlacement(BlockShape.Slope1, MaterialKind.Metal, -3, 2, -40, 19);
        var orcaFaces = StructuralShapeGeometry.WorldFaces(orcaSlope);
        Require(CoveredSamples(orcaFaces, orcaSlope.Position, new(1, 0, 0)) == 16 &&
                CoveredSamples(orcaFaces, orcaSlope.Position, new(0, 1, 0)) == 10,
            "The Orca regression no longer distinguishes a full inward face from its leaking upper face.");
        var cube = StructuralShapeGeometry.WorldFaces(orcaSlope with { Shape = BlockShape.Cube });
        Require(BlockRotations.AxisDirections.All(axis => CoveredSamples(cube, orcaSlope.Position, axis) == 16),
            "The six-direction skin probes do not recognize a complete original armor cube.");
    }

    public static void VerifyOriginalSkin(HullSurfaceGrid original, IEnumerable<BlockPlacement> placements) =>
        VerifyOriginalSkin(original.SpanAt, placements);

    public static void VerifyOriginalSkin(GeneratedHull original, GeneratedHull constructed)
    {
        var originalCells = original.Blocks.SelectMany(block => block.OccupiedCells).ToHashSet();
        var revisedCells = constructed.Blocks.SelectMany(block => block.OccupiedCells).ToHashSet();
        var rows = originalCells.GroupBy(cell => (cell.Y, cell.Z)).ToDictionary(row => row.Key,
                row => new HullSurfaceSpan(row.Min(cell => cell.X), row.Max(cell => cell.X)));
        var fitted = constructed.Blocks.Where(block => block.Origin == BlockOrigin.Smoothing).ToArray();
        VerifyOriginalSkin((y, z) => rows.GetValueOrDefault((y, z), HullSurfaceSpan.Empty),
            fitted);

        foreach (var part in fitted)
        {
            var faces = StructuralShapeGeometry.WorldFaces(part);
            foreach (var cell in part.OccupiedCells.Where(originalCells.Contains))
            foreach (var axis in BlockRotations.AxisDirections)
            {
                var neighbor = (X: cell.X + axis.X, Y: cell.Y + axis.Y, Z: cell.Z + axis.Z);
                // The original material list independently identifies cavity air:
                // a neighbor lies inside the sampled row but contains no armor.
                // A new material cell there belongs to a complete fitted assembly
                // or rebuilt backing; its contacts are checked separately.
                var span = rows.GetValueOrDefault((neighbor.Y, neighbor.Z), HullSurfaceSpan.Empty);
                if (!span.Contains(neighbor.X) || originalCells.Contains(neighbor) || revisedCells.Contains(neighbor)) continue;
                Require(CoveredSamples(faces, cell, axis) == 16,
                    $"Fitted {part.Shape} at {part.Position} opens an original cavity face at {cell} toward {axis}.");
            }
        }
    }

    private static void VerifyOriginalSkin(Func<int, int, HullSurfaceSpan> originalRow,
        IEnumerable<BlockPlacement> placements)
    {
        foreach (var part in placements)
        {
            var faces = StructuralShapeGeometry.WorldFaces(part);
            foreach (var cell in part.OccupiedCells)
            {
                var span = originalRow(cell.Y, cell.Z);
                if (span.IsEmpty) continue;
                foreach (var side in new[] { -1, 1 })
                {
                    if (cell.X != (side < 0 ? span.MinX : span.MaxX)) continue;
                    Require(CoveredSamples(faces, cell, new(-side, 0, 0)) == 16,
                        $"Fitted {part.Shape} at {part.Position} opens the original inward skin at {cell} on side {side}.");
                }
            }
        }
    }

    // Independent point-in-native-solid probes, not the planner's cached contact
    // areas. Every point on an original cavity face must remain in the fitted
    // envelope. Outward-added cells have no old skin face and are tested elsewhere.
    private static int CoveredSamples(IReadOnlyList<StructuralFace> faces,
        (int X, int Y, int Z) cell, AxisDirection axis)
    {
        var covered = 0;
        var normal = new Vector3(axis.X, axis.Y, axis.Z);
        var firstTangent = axis.X == 0 ? Vector3.UnitX : Vector3.UnitY;
        var secondTangent = Vector3.Cross(normal, firstTangent);
        var center = new Vector3(cell.X, cell.Y, cell.Z) + normal * .5f;
        foreach (var first in SampleOffsets)
        foreach (var second in SampleOffsets)
        {
            var point = center + firstTangent * first + secondTangent * second;
            if (faces.All(face => Vector3.Dot(face.Normal, point - face.Points[0]) <= 1e-5f)) covered++;
        }
        return covered;
    }
}
