using System.Numerics;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Geometry;
using static SlopeFillTestSupport;

// Acceptance fixtures for the export-effective surface coverage engine. Expected values are derived
// by hand or from the independent StructuralShapeGeometry envelopes; none is copied from the engine
// under test.
internal static class SurfaceCoverageTests
{
    public static void Run()
    {
        CubeNextToCube();
        CubePartiallyCoveredByTriangle();
        DiagonalContactAndSubtractionPrimitive();
        MultiMetreSlopeDistributesAcrossCells();
        BeamMatchesEquivalentCubes();
        CoplanarPairAndDistinctSlopeAreas();
        ExportFlatteningChangesFittedCandidates();
        TransformsAndOrderAreEquivalent();
        InteriorArmorIsExcludedFromHullSkin();
        MergedBeamCavityFaceIsInternalArmor();
        TolerancesAndNotesAreDeterministic();
        Console.WriteLine(
            "Surface coverage: 11 export-effective geometry, subtraction, tessellation, scope, cavity, and tolerance cases passed.");
    }

    /// <summary>Fixture A: a shared full square is removed from both cubes; 10 m² exposed, 8 m² step.</summary>
    private static void CubeNextToCube()
    {
        var coverage = new SurfaceCoverage(MakeHull(Cube(0, 0, 0), Cube(1, 0, 0)));
        Close(coverage.Totals.Exposed, 10, "Two X-adjacent cubes exposed area.");
        Close(coverage.Totals.Step, 8, "Two X-adjacent cubes step area.");
        Close(coverage.CellAreas[(0, 0, 0)].Exposed, 5, "Left cube exposed area.");
        Close(coverage.CellAreas[(0, 0, 0)].Step, 4, "Left cube step area.");
        Close(coverage.CellAreas[(1, 0, 0)].Exposed, 5, "Right cube exposed area.");
        Close(coverage.CellAreas[(1, 0, 0)].Step, 4, "Right cube step area.");
        // 12 faces total; the two on the shared interface contribute no fragment at all.
        Require(coverage.Fragments.Count == 10,
            $"The shared interface left {coverage.Fragments.Count} fragments instead of 10.");
        Require(coverage.Fragments.All(fragment => !(Math.Abs(fragment.Normal.X) > 0.9 &&
                                                     Math.Abs(fragment.Normal.Y) < 1e-5 &&
                                                     Math.Abs(fragment.Normal.Z) < 1e-5 &&
                                                     Math.Abs(fragment.Cell.X - 0.5) < 1e-5)),
            "The shared X interface produced a non-zero fragment.");
    }

    /// <summary>
    /// Fixture B: a fitted left triangle at (1,0,0) covers half of the cube's +X face. The fitted
    /// shape keeps its envelope, so the contact is the triangle's -X face (area 0.5). Independent
    /// corner faces are three 0.5 right triangles plus one sqrt(3)/2 equilateral face.
    /// </summary>
    private static void CubePartiallyCoveredByTriangle()
    {
        var cube = Cube(0, 0, 0);
        var corner = new BlockPlacement(BlockShape.CornerLeft, MaterialKind.Metal, 1, 0, 0, 0)
        {
            Origin = BlockOrigin.Smoothing,
        };
        Require(corner.KeepsFittedShape, "The fitted triangle must keep its native envelope for fixture B.");

        var coverage = new SurfaceCoverage(MakeHull(cube, corner));
        var independent = FaceAreaSum(cube) + FaceAreaSum(corner);
        var cubeFace = StructuralShapeGeometry.WorldFaces(cube).Single(face => face.Normal.X > 0.9);
        var cornerFace = StructuralShapeGeometry.WorldFaces(corner).Single(face => face.Normal.X < -0.9);
        var contact = StructuralShapeGeometry.ContactArea(cubeFace, cornerFace);
        Close(contact, 0.5, "The independent triangle/cube contact area.");

        var expected = 6 + 1.5 + Math.Sqrt(3) / 2 - 2 * 0.5;
        Close(coverage.Totals.Exposed, expected, "Cube plus partially covering fitted triangle exposed area.");
        Close(coverage.Totals.Exposed, independent - 2 * contact,
            "Coverage exposed area did not match independent faces minus twice the contact.");

        var residual = coverage.Fragments.Single(fragment =>
            fragment.Cell == (0, 0, 0) && Math.Abs(fragment.Normal.X - 1) < 1e-5);
        Close(residual.Area, 0.5, "The retained half-square residual on the cube's +X face.");
        Require(!residual.IsStep, "The +X residual was misclassified as a step face.");
        Require(residual.Scope == SurfaceScope.HullSkin, "The shell cube residual was not hull skin.");
        Require(residual.Polygon.Count >= 3, "The retained residual did not carry its polygon.");
    }

    /// <summary>
    /// Fixture C: the game's diagonal faces are lattice-aligned. The sweep is exactly the enumerated
    /// matrix below: one representative shape for every structural family with diagonal faces
    /// (Slope, Triangle, InverseTriangle, BeamSlope, SquareCorner, SquareBackedCorner,
    /// SlopeTransition, InverseTransition), all 24 native rotations, and 20 neighbouring offsets
    /// (both signs of every axis plus diagonal offsets). No rotation reduction was needed. It finds no
    /// disjoint-cell pair with positive diagonal contact above the area tolerance: coplanar opposing
    /// diagonal faces tile edge-to-edge, leaving only sub-tolerance numerical slivers. The subtraction
    /// primitive is therefore exercised directly on two overlapping coplanar opposing convex polygons,
    /// exactly as the acceptance note allows. No zero-contact weakening is used, and the claim is
    /// scoped to this matrix rather than to all shapes and offsets.
    /// </summary>
    private static void DiagonalContactAndSubtractionPrimitive()
    {
        var maxContact = MaxDiagonalContact();
        Require(maxContact <= SurfaceCoverage.AreaTolerance,
            $"A disjoint-cell diagonal contact of {maxContact:G6} m^2 exists above the area tolerance in the " +
            "enumerated matrix; fixture C should assert the coverage formula on it instead of the primitive.");

        // Two unit squares in the z=0 plane. b is wound counter-clockwise about -Z and covers
        // x in [0.5, 1.5]; a covers x in [0, 1], so the residual is the half square x in [0, 0.5].
        var a = new List<Vector3> { new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(0, 1, 0) };
        var b = new List<Vector3> { new(0.5f, 1, 0), new(1.5f, 1, 0), new(1.5f, 0, 0), new(0.5f, 0, 0) };
        var residual = SurfaceCoverage.SubtractConvex(a, b, -Vector3.UnitZ);
        Close(residual.Sum(StructuralShapeGeometry.PolygonArea), 0.5,
            "The primitive subtraction of two half-overlapping opposing squares.");
        Require(residual.All(piece => piece.All(point => point.X <= 0.5 + 1e-6)),
            "A residual piece reached into the coverer's half of the plane.");

        // A coverer that contains the whole polygon removes everything.
        var covering = new List<Vector3> { new(-0.5f, 1, 0), new(1.5f, 1, 0), new(1.5f, 0, 0), new(-0.5f, 0, 0) };
        Require(SurfaceCoverage.SubtractConvex(a, covering, -Vector3.UnitZ).Count == 0,
            "A fully covering polygon did not remove the whole face.");

        // A disjoint coverer leaves the original area.
        var disjoint = new List<Vector3> { new(2, 1, 0), new(3, 1, 0), new(3, 0, 0), new(2, 0, 0) };
        Close(SurfaceCoverage.SubtractConvex(a, disjoint, -Vector3.UnitZ)
                .Sum(StructuralShapeGeometry.PolygonArea), 1,
            "A disjoint coverer changed the residual area.");

        // Folding two overlapping coverers must not subtract the overlap twice: their union covers
        // the whole square, so the folded residual is empty rather than negative.
        var left = new List<Vector3> { new(0, 1, 0), new(0.6f, 1, 0), new(0.6f, 0, 0), new(0, 0, 0) };
        var right = new List<Vector3> { new(0.4f, 1, 0), new(1, 1, 0), new(1, 0, 0), new(0.4f, 0, 0) };
        var once = SurfaceCoverage.SubtractConvex(a, left, -Vector3.UnitZ);
        var twice = once.SelectMany(piece => SurfaceCoverage.SubtractConvex(piece, right, -Vector3.UnitZ)).ToList();
        Close(twice.Sum(StructuralShapeGeometry.PolygonArea), 0,
            "Overlapping coverers were subtracted twice and left a non-empty residual.");
        Require(twice.All(piece => StructuralShapeGeometry.PolygonArea(piece) >= -1e-9),
            "Overlapping coverers produced a negative-area residual piece.");
    }

    /// <summary>Fixture D: a lone 4 m slope's independent area is distributed exactly across its four cells.</summary>
    private static void MultiMetreSlopeDistributesAcrossCells()
    {
        var slope = new BlockPlacement(BlockShape.Slope4, MaterialKind.Metal, 0, 0, 0, 0);
        var coverage = new SurfaceCoverage(MakeHull(slope));
        var independent = FaceAreaSum(slope);
        Close(independent, 4 + 1 + Math.Sqrt(17) + 2 + 2, "Independent 4 m wedge face area.");
        Close(coverage.Totals.Exposed, independent, "Lone 4 m slope exposed area.");
        Close(coverage.Totals.Step, 4 + 1, "Lone 4 m slope step area (bottom and back squares).");
        Require(coverage.CellAreas.Count == 4, "The 4 m slope did not report four occupied cells.");
        Require(coverage.CellAreas.Values.All(area => area.Exposed > 0),
            "A 4 m slope cell received no exposed area.");
        var perCell = coverage.CellAreas.Values.Sum(area => area.Exposed);
        Close(perCell, coverage.Totals.Exposed, "Per-cell slope area did not sum to the total.");
        Close(perCell, independent, "Per-cell slope area did not sum to the independent face area.");
    }

    /// <summary>Fixture E: one Beam4 and four cubes cover the same solid and must measure identically.</summary>
    private static void BeamMatchesEquivalentCubes()
    {
        var beamCoverage = new SurfaceCoverage(MakeHull(new BlockPlacement(BlockShape.Beam4, MaterialKind.Metal, 0, 0, 0, 0)));
        var cubeCoverage = new SurfaceCoverage(MakeHull(Cube(0, 0, 0), Cube(0, 0, 1), Cube(0, 0, 2), Cube(0, 0, 3)));
        Close(beamCoverage.Totals.Exposed, 18, "A 1x1x4 box exposed area.");
        Close(beamCoverage.Totals.Step, 10, "A 1x1x4 box step area.");
        Close(cubeCoverage.Totals.Exposed, beamCoverage.Totals.Exposed, "Beam and cubes exposed area.");
        Close(cubeCoverage.Totals.Step, beamCoverage.Totals.Step, "Beam and cubes step area.");
        for (var z = 0; z < 4; z++)
        {
            var cell = (0, 0, z);
            Close(beamCoverage.CellAreas[cell].Exposed, cubeCoverage.CellAreas[cell].Exposed,
                $"Beam and cubes exposed area at cell {cell}.");
            Close(beamCoverage.CellAreas[cell].Step, cubeCoverage.CellAreas[cell].Step,
                $"Beam and cubes step area at cell {cell}.");
        }
    }

    /// <summary>
    /// Fixture F: a 2 m slope is a 2:1 plane while two 1 m slopes are each 1:1, so those two
    /// constructions are intentionally NOT equivalent (a 2:2 union is not a 2:1 plane); they are only
    /// each compared against their own independently computed area. The genuine equivalent-tessellation
    /// case in this vocabulary is a 1x1x4 box expressed as one Beam4 versus two Beam2 parts, which must
    /// measure the same area per cell. The fixture also pins that coplanar same-normal faces are not
    /// mistaken for coverers and that a shared opposing face is removed once.
    /// </summary>
    private static void CoplanarPairAndDistinctSlopeAreas()
    {
        // Independent 2 m slope, not equivalent to two 1 m slopes (2:1 versus 2:2 planes).
        var slope2 = new BlockPlacement(BlockShape.Slope2, MaterialKind.Metal, 0, 0, 0, 0);
        var slope2Coverage = new SurfaceCoverage(MakeHull(slope2));
        Close(slope2Coverage.Totals.Exposed, FaceAreaSum(slope2), "A lone 2 m slope exposed area.");
        Close(slope2Coverage.Totals.Exposed, 5 + Math.Sqrt(5), "Independent 2:1 wedge face area.");

        // Two 1 m slopes side by side along X are coplanar with each other. They share their
        // triangular X-facing sides, so the union loses that triangle twice.
        var left = new BlockPlacement(BlockShape.Slope1, MaterialKind.Metal, 0, 0, 0, 0)
        {
            Origin = BlockOrigin.Smoothing,
        };
        var right = new BlockPlacement(BlockShape.Slope1, MaterialKind.Metal, 1, 0, 0, 0)
        {
            Origin = BlockOrigin.Smoothing,
        };
        var pairCoverage = new SurfaceCoverage(MakeHull(left, right));
        var leftFace = StructuralShapeGeometry.WorldFaces(left).Single(face => face.Normal.X > 0.9);
        var rightFace = StructuralShapeGeometry.WorldFaces(right).Single(face => face.Normal.X < -0.9);
        var sideContact = StructuralShapeGeometry.ContactArea(leftFace, rightFace);
        Close(sideContact, 0.5, "The coplanar pair's shared side triangle area.");
        var independentPair = FaceAreaSum(left) + FaceAreaSum(right) - 2 * sideContact;
        Close(pairCoverage.Totals.Exposed, independentPair, "The coplanar pair's union exposed area.");
        Require(pairCoverage.Fragments.Any(fragment => Math.Abs(fragment.Normal.X) > 0.9),
            "The coplanar pair lost its X-facing side fragments entirely.");
        Require(Math.Abs(pairCoverage.Totals.Exposed - slope2Coverage.Totals.Exposed) > 0.5,
            "The 2:1 and 2:2 surfaces are no longer geometrically distinct; fixture F must be re-derived.");

        // Genuine equivalent tessellation: one 1x1x4 box as a single Beam4 versus two Beam2 parts
        // meeting at z=1.5. The shared 1 m^2 Z face cancels once, so both constructions expose 18 m^2
        // and 10 m^2 of step (each Beam2 contributes 10 exposed and 6 step; the union loses 2 exposed
        // and 2 step at the shared face).
        var wholeBeam = new SurfaceCoverage(MakeHull(
            new BlockPlacement(BlockShape.Beam4, MaterialKind.Metal, 0, 0, 0, 0)));
        var splitBeam = new SurfaceCoverage(MakeHull(
            new BlockPlacement(BlockShape.Beam2, MaterialKind.Metal, 0, 0, 0, 0),
            new BlockPlacement(BlockShape.Beam2, MaterialKind.Metal, 0, 0, 2, 0)));
        Close(wholeBeam.Totals.Exposed, 18, "A lone Beam4 exposed area (1x1x4 box).");
        Close(wholeBeam.Totals.Step, 10, "A lone Beam4 step area (1x1x4 box).");
        Close(splitBeam.Totals.Exposed, wholeBeam.Totals.Exposed, "Beam4 and two Beam2 exposed area.");
        Close(splitBeam.Totals.Step, wholeBeam.Totals.Step, "Beam4 and two Beam2 step area.");
        for (var z = 0; z < 4; z++)
        {
            var cell = (0, 0, z);
            Close(splitBeam.CellAreas[cell].Exposed, wholeBeam.CellAreas[cell].Exposed,
                $"Beam4 and two Beam2 exposed area at cell {cell}.");
            Close(splitBeam.CellAreas[cell].Step, wholeBeam.CellAreas[cell].Step,
                $"Beam4 and two Beam2 step area at cell {cell}.");
        }
    }

    /// <summary>
    /// Fixture G: the exporter flattens a one-metre shell fitted candidate to a cube, but a
    /// smoothing placement keeps its native slope envelope.
    /// </summary>
    private static void ExportFlatteningChangesFittedCandidates()
    {
        var shellSlope = new BlockPlacement(BlockShape.Slope1, MaterialKind.Metal, 0, 0, 0, 0);
        Require(!shellSlope.KeepsFittedShape, "A one-metre shell slope must be a flattening candidate.");
        var shellCoverage = new SurfaceCoverage(MakeHull(shellSlope));
        Require(shellCoverage.EffectivePlacements[0].Shape == BlockShape.Cube,
            "A shell slope was not flattened to a cube.");
        Close(shellCoverage.Totals.Exposed, 6, "A flattened shell slope exposed area.");
        Close(shellCoverage.Totals.Step, 4, "A flattened shell slope step area.");

        var shellCorner = new BlockPlacement(BlockShape.CornerLeft, MaterialKind.Metal, 0, 0, 0, 0);
        Require(!shellCorner.KeepsFittedShape, "A one-metre shell corner must be a flattening candidate.");
        var cornerCoverage = new SurfaceCoverage(MakeHull(shellCorner));
        Close(cornerCoverage.Totals.Exposed, 6, "A flattened shell corner exposed area.");
        Close(cornerCoverage.Totals.Step, 4, "A flattened shell corner step area.");

        var smoothingSlope = shellSlope with { Origin = BlockOrigin.Smoothing };
        Require(smoothingSlope.KeepsFittedShape, "A smoothing slope must keep its fitted shape.");
        var smoothingCoverage = new SurfaceCoverage(MakeHull(smoothingSlope));
        Require(smoothingCoverage.EffectivePlacements[0].Shape == BlockShape.Slope1,
            "A smoothing slope lost its fitted shape.");
        Close(smoothingCoverage.Totals.Exposed, 3 + Math.Sqrt(2), "A retained smoothing slope exposed area.");
        Close(smoothingCoverage.Totals.Step, 2, "A retained smoothing slope step area.");
    }

    /// <summary>Fixture H: translation, whole-assembly rotation, X-mirror and shuffled order preserve the measurement.</summary>
    private static void TransformsAndOrderAreEquivalent()
    {
        BlockPlacement[] blocks =
        [
            Cube(0, 0, 0), Cube(0, 0, 1), Cube(0, 1, 0), Cube(0, 1, 1),
            new(BlockShape.Beam4, MaterialKind.Metal, 1, 0, 0, 0),
            new(BlockShape.Slope4, MaterialKind.Metal, 0, 0, 2, 0),
        ];
        var baseCoverage = new SurfaceCoverage(MakeHull(blocks));

        var shuffled = new SurfaceCoverage(MakeHull(blocks.Reverse()));
        Close(shuffled.Totals.Exposed, baseCoverage.Totals.Exposed, "Shuffled exposed area.");
        Close(shuffled.Totals.Step, baseCoverage.Totals.Step, "Shuffled step area.");

        var offset = (X: 137, Y: 29, Z: -119);
        var translatedBlocks = blocks
            .Select(block => block with { X = block.X + offset.X, Y = block.Y + offset.Y, Z = block.Z + offset.Z })
            .ToArray();
        var translatedCoverage = new SurfaceCoverage(MakeHull(translatedBlocks));
        Close(translatedCoverage.Totals.Exposed, baseCoverage.Totals.Exposed, "Translated exposed area.");
        Close(translatedCoverage.Totals.Step, baseCoverage.Totals.Step, "Translated step area.");
        foreach (var (cell, area) in baseCoverage.CellAreas)
        {
            var moved = (cell.X + offset.X, cell.Y + offset.Y, cell.Z + offset.Z);
            Close(translatedCoverage.CellAreas[moved].Exposed, area.Exposed, $"Translated exposed area at {moved}.");
            Close(translatedCoverage.CellAreas[moved].Step, area.Step, $"Translated step area at {moved}.");
        }

        // Whole-assembly half turn about Y: (x, y, z) -> (-x, y, -z). This preserves which faces are
        // X-facing (non-step) versus Y/Z-facing (step), unlike a quarter turn, and the fixture uses
        // only X-symmetric box and slope envelopes so the shape is unchanged and the rotation index
        // is remapped from the rotated axes.
        (int, int, int) HalfTurn((int X, int Y, int Z) point) => (-point.X, point.Y, -point.Z);
        AxisDirection HalfAxis(AxisDirection axis) => new(-axis.X, axis.Y, -axis.Z);
        var rotated = new SurfaceCoverage(MakeHull(blocks.Select(block => Transform(block, HalfTurn, HalfAxis))));
        Close(rotated.Totals.Exposed, baseCoverage.Totals.Exposed, "Half-turned exposed area.");
        Close(rotated.Totals.Step, baseCoverage.Totals.Step, "Half-turned step area.");
        foreach (var (cell, area) in baseCoverage.CellAreas)
        {
            var moved = HalfTurn(cell);
            Close(rotated.CellAreas[moved].Exposed, area.Exposed, $"Half-turned exposed area at {moved}.");
            Close(rotated.CellAreas[moved].Step, area.Step, $"Half-turned step area at {moved}.");
        }

        // X-mirror: (x, y, z) -> (-x, y, z). The fixture's box and slope envelopes are X-symmetric,
        // so only the anchor and the world axes reflect.
        (int, int, int) Mirror((int X, int Y, int Z) point) => (-point.X, point.Y, point.Z);
        AxisDirection MirrorAxis(AxisDirection axis) => new(-axis.X, axis.Y, axis.Z);
        var mirrored = new SurfaceCoverage(MakeHull(blocks.Select(block => Transform(block, Mirror, MirrorAxis))));
        Close(mirrored.Totals.Exposed, baseCoverage.Totals.Exposed, "X-mirrored exposed area.");
        Close(mirrored.Totals.Step, baseCoverage.Totals.Step, "X-mirrored step area.");
        foreach (var (cell, area) in baseCoverage.CellAreas)
        {
            var moved = Mirror(cell);
            Close(mirrored.CellAreas[moved].Exposed, area.Exposed, $"X-mirrored exposed area at {moved}.");
            Close(mirrored.CellAreas[moved].Step, area.Step, $"X-mirrored step area at {moved}.");
        }
    }

    /// <summary>Fixture I: an air-gapped inner armor layer is measured but excluded from the hull skin.</summary>
    private static void InteriorArmorIsExcludedFromHullSkin()
    {
        var outer = new BlockPlacement(BlockShape.Cube, MaterialKind.Metal, 0, 0, 0, 0);
        var inner = new BlockPlacement(BlockShape.Cube, MaterialKind.Metal, 2, 0, 0, 0) { ArmorDepth = 1 };
        var coverage = new SurfaceCoverage(MakeHull(outer, inner));

        Close(coverage.Totals.Exposed, 12, "Outer plus air-gapped inner exposed area.");
        Close(coverage.HullSkinTotals.Exposed, 6, "Hull-skin exposed area must exclude the inner layer.");
        Require(coverage.HullSkinTotals.Exposed < coverage.Totals.Exposed,
            "The inner layer was not excluded from the hull skin.");
        Require(coverage.Fragments.Where(fragment => fragment.PlacementIndex == 1)
                .All(fragment => fragment.Scope == SurfaceScope.InternalArmor),
            "An inner-armor fragment was not classified as internal armor.");
        Require(coverage.Fragments.Any(fragment => fragment.PlacementIndex == 1 &&
                                                   Math.Abs(fragment.Normal.X + 1) < 1e-5 && fragment.Area > 0),
            "The inner layer's gap-facing face was not measured as an internal boundary.");
        Require(coverage.Fragments.Where(fragment => fragment.Scope == SurfaceScope.HullSkin)
                .All(fragment => fragment.PlacementIndex == 0),
            "A hull-skin fragment came from the inner armor placement.");

        // D1: the per-cell dictionaries separate exterior hull skin from the all-boundary measure.
        Close(coverage.HullSkinCellAreas[(0, 0, 0)].Exposed, 6, "Outer cell hull-skin exposed area.");
        Close(coverage.HullSkinCellAreas[(2, 0, 0)].Exposed, 0, "Inner armor cell hull-skin exposed area.");
        Close(coverage.BoundaryCellAreas[(2, 0, 0)].Exposed, 6, "Inner armor cell all-boundary exposed area.");
        Require(coverage.CellAreas[(2, 0, 0)] == coverage.BoundaryCellAreas[(2, 0, 0)],
            "CellAreas must remain the all-boundary alias used by BoundaryCellAreas.");
        Close(coverage.HullSkinCellAreas.Values.Sum(area => area.Exposed), coverage.HullSkinTotals.Exposed,
            "Hull-skin per-cell areas did not sum to the hull-skin total.");
        Close(coverage.BoundaryCellAreas.Values.Sum(area => area.Exposed), coverage.Totals.Exposed,
            "All-boundary per-cell areas did not sum to the all-boundary total.");
    }

    /// <summary>
    /// Fixture K (D2 regression): a merged Beam4 at armor depth zero runs past a sealed one-cell air
    /// pocket. The pocket-facing fragment must be InternalArmor and leave the hull skin, while the
    /// beam's exterior faces stay HullSkin even though the whole placement reports armor depth zero.
    ///
    /// Hand derivation. Blocks: Beam4 cells (0,0,0..3); cubes at (2,0,1), (1,1,1), (1,-1,1),
    /// (1,0,0), (1,0,2) seal the pocket (1,0,1). The beam's +X face is a 4 m^2 rectangle split one
    /// square per cell: cells 0 and 2 are covered by the -X faces of the cubes at (1,0,0) and (1,0,2);
    /// cell 1 faces the pocket; cell 3 is open. All-boundary exposed = 18 (beam) + 5*6 (cubes) - 2*2
    /// (two cancelled contacts) = 44. Six pocket faces each lose 1 m^2 of hull skin (the beam's cell-1
    /// +X face plus the five cube faces facing the pocket), so hull-skin exposed = 44 - 6 = 38.
    /// All-boundary step = 10 (beam) + 5*4 (cubes) = 30; four of the pocket faces are step faces, so
    /// hull-skin step = 30 - 4 = 26.
    /// </summary>
    private static void MergedBeamCavityFaceIsInternalArmor()
    {
        var beam = new BlockPlacement(BlockShape.Beam4, MaterialKind.Metal, 0, 0, 0, 0);
        Require(beam.ArmorDepth == 0, "The merged regression beam must report armor depth zero.");
        var coverage = new SurfaceCoverage(MakeHull(
            beam,
            Cube(2, 0, 1), Cube(1, 1, 1), Cube(1, -1, 1), Cube(1, 0, 0), Cube(1, 0, 2)));

        Close(coverage.Totals.Exposed, 44, "All-boundary exposed area with the sealed pocket.");
        Close(coverage.HullSkinTotals.Exposed, 38, "Hull-skin exposed area excludes the pocket faces.");
        Close(coverage.Totals.Step, 30, "All-boundary step area with the sealed pocket.");
        Close(coverage.HullSkinTotals.Step, 26, "Hull-skin step area excludes the pocket step faces.");

        var pocketFace = coverage.Fragments.Single(fragment =>
            fragment.PlacementIndex == 0 && fragment.Cell == (0, 0, 1) &&
            Math.Abs(fragment.Normal.X - 1) < 1e-5);
        Require(pocketFace.Scope == SurfaceScope.InternalArmor,
            "The beam's pocket-facing fragment was not classified as internal armor.");
        Close(pocketFace.Area, 1, "The beam's pocket-facing fragment area.");

        var exteriorFace = coverage.Fragments.Single(fragment =>
            fragment.PlacementIndex == 0 && fragment.Cell == (0, 0, 1) &&
            Math.Abs(fragment.Normal.X + 1) < 1e-5);
        Require(exteriorFace.Scope == SurfaceScope.HullSkin,
            "The beam's exterior fragment was not kept as hull skin.");

        Require(coverage.Fragments.Where(fragment => fragment.PlacementIndex == 0 &&
                !(fragment.Cell == (0, 0, 1) && Math.Abs(fragment.Normal.X - 1) < 1e-5))
                .All(fragment => fragment.Scope == SurfaceScope.HullSkin),
            "A beam fragment other than the pocket-facing one was demoted to internal armor.");
        Close(coverage.HullSkinCellAreas[(0, 0, 1)].Exposed, 3,
            "The beam pocket cell's hull-skin exposed area (three of four side faces).");
        Close(coverage.BoundaryCellAreas[(0, 0, 1)].Exposed, 4,
            "The beam pocket cell's all-boundary exposed area (all four side faces).");
    }

    /// <summary>Fixture J: the notes channel exists, is deterministic, and stays empty on a clean fixture.</summary>
    private static void TolerancesAndNotesAreDeterministic()
    {
        var hull = MakeHull(Cube(0, 0, 0), Cube(1, 0, 0));
        var first = new SurfaceCoverage(hull);
        var second = new SurfaceCoverage(hull);
        var firstNotes = first.Notes;
        Require(firstNotes.Count == 0, "A clean axis-aligned fixture produced an attribution note.");
        Require(firstNotes.SequenceEqual(second.Notes), "Coverage notes were not deterministic.");
        Require(SurfaceCoverage.AreaTolerance > 0 && SurfaceCoverage.PlaneTolerance > 0,
            "Coverage tolerances must be explicit and positive.");
        Require(SurfaceCoverage.ReportScope.Contains("catalog-independent", StringComparison.Ordinal),
            "The coverage report scope must state that the catalog is not consulted.");
        Require(first.Fragments.Select(fragment => (fragment.PlacementIndex, fragment.Cell, fragment.Area))
                .SequenceEqual(second.Fragments.Select(fragment => (fragment.PlacementIndex, fragment.Cell, fragment.Area))),
            "Coverage fragments were not deterministic.");
    }

    /// <summary>
    /// Largest positive-area contact between two opposing diagonal faces on disjoint cells, over the
    /// enumerated matrix: one representative for every structural family with diagonal faces, all 24
    /// native rotations, and 20 neighbour offsets (both signs of every axis plus diagonal offsets).
    /// </summary>
    private static double MaxDiagonalContact()
    {
        BlockShape[] shapes =
        [
            BlockShape.Slope2,
            BlockShape.CornerLeft2,
            BlockShape.InverseCornerLeft2,
            BlockShape.BeamSlope2,
            BlockShape.SquareCornerLeft2,
            BlockShape.SquareBackedCornerLeft2,
            BlockShape.SlopeTransitionLeft12,
            BlockShape.InverseTransitionLeft12,
        ];
        (int, int, int)[] deltas =
        [
            (1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1),
            (1, 1, 0), (1, -1, 0), (-1, 1, 0), (-1, -1, 0),
            (1, 0, 1), (1, 0, -1), (-1, 0, 1), (-1, 0, -1),
            (0, 1, 1), (0, 1, -1), (0, -1, 1), (0, -1, -1),
            (1, 1, 1), (-1, -1, -1),
        ];

        var profiles = new Dictionary<(BlockShape, int), PlacementProfile>(8 * 24);
        foreach (var shape in shapes)
        for (var rotation = 0; rotation < 24; rotation++)
            profiles[(shape, rotation)] = CreateProfile(shape, rotation);

        var translatedOccupied = new Dictionary<(BlockShape Shape, int Rotation, int Dx, int Dy, int Dz), HashSet<(int X, int Y, int Z)>>(128);
        var translatedFaces = new Dictionary<(BlockShape Shape, int Rotation, int Dx, int Dy, int Dz), StructuralFace[]>(128);
        var maximum = 0d;
        foreach (var first in shapes)
        foreach (var second in shapes)
        for (var firstRotation = 0; firstRotation < 24; firstRotation++)
        for (var secondRotation = 0; secondRotation < 24; secondRotation++)
        foreach (var delta in deltas)
        {
            var firstProfile = profiles[(first, firstRotation)];
            var secondProfile = profiles[(second, secondRotation)];
            var translation = (second, secondRotation, delta.Item1, delta.Item2, delta.Item3);
            if (!translatedOccupied.TryGetValue(translation, out var translatedSecond))
            {
                translatedSecond = TranslateCells(secondProfile.Occupied, delta.Item1, delta.Item2, delta.Item3);
                translatedOccupied.Add(translation, translatedSecond);
            }

            if (firstProfile.Occupied.Overlaps(translatedSecond))
                continue;

            if (!translatedFaces.TryGetValue(translation, out var translatedSecondFaces))
            {
                translatedSecondFaces = Translate(secondProfile.DiagonalFaces,
                    delta.Item1, delta.Item2, delta.Item3);
                translatedFaces.Add(translation, translatedSecondFaces);
            }

            foreach (var faceA in firstProfile.DiagonalFaces)
            foreach (var faceB in translatedSecondFaces)
                maximum = Math.Max(maximum, StructuralShapeGeometry.ContactArea(faceA, faceB));
        }

        return maximum;
    }

    private static bool IsDiagonal(Vector3 normal)
    {
        var nonZero = 0;
        if (Math.Abs(normal.X) > 1e-4) nonZero++;
        if (Math.Abs(normal.Y) > 1e-4) nonZero++;
        if (Math.Abs(normal.Z) > 1e-4) nonZero++;
        return nonZero >= 2;
    }

    private static HashSet<(int X, int Y, int Z)> TranslateCells(
        HashSet<(int X, int Y, int Z)> cells,
        int dx,
        int dy,
        int dz) =>
        cells.Select(cell => (cell.X + dx, cell.Y + dy, cell.Z + dz)).ToHashSet();

    private static StructuralFace[] Translate(
        StructuralFace[] faces,
        int dx,
        int dy,
        int dz)
    {
        var translation = new Vector3(dx, dy, dz);
        return faces.Select(face =>
                new StructuralFace(face.Points.Select(point => point + translation).ToArray(), face.Normal))
            .ToArray();
    }

    private static PlacementProfile CreateProfile(BlockShape shape, int rotation) =>
        new(
            new BlockPlacement(shape, MaterialKind.Metal, 0, 0, 0, rotation).OccupiedCells.ToHashSet(),
            StructuralShapeGeometry.WorldFaces(new BlockPlacement(shape, MaterialKind.Metal, 0, 0, 0, rotation))
                .Where(face => IsDiagonal(face.Normal))
                .ToArray());

    private readonly record struct PlacementProfile(
        HashSet<(int X, int Y, int Z)> Occupied,
        StructuralFace[] DiagonalFaces);

    private static BlockPlacement Transform(
        BlockPlacement placement,
        Func<(int X, int Y, int Z), (int X, int Y, int Z)> point,
        Func<AxisDirection, AxisDirection> axis)
    {
        var axes = BlockRotations.GetRotationAxes(placement.Rotation);
        var forward = axis(axes.Forward);
        var up = axis(axes.Up);
        var anchor = point(placement.Position);
        var rotation = Enumerable.Range(0, 24).First(candidate =>
        {
            var axesCandidate = BlockRotations.GetRotationAxes(candidate);
            return axesCandidate.Forward == forward && axesCandidate.Up == up;
        });
        return placement with { X = anchor.X, Y = anchor.Y, Z = anchor.Z, Rotation = rotation };
    }

    private static double FaceAreaSum(BlockPlacement placement) =>
        StructuralShapeGeometry.WorldFaces(placement).Sum(face => face.Area);

    private static void Close(double actual, double expected, string message) =>
        Require(Math.Abs(actual - expected) < 1e-6, $"{message} Actual {actual:G12}; expected {expected:G12}.");

    private static GeneratedHull MakeHull(params BlockPlacement[] blocks) =>
        MakeHull((IEnumerable<BlockPlacement>)blocks);

    private static GeneratedHull MakeHull(IEnumerable<BlockPlacement> blocks)
    {
        var list = blocks.ToArray();
        var cells = list.SelectMany(block => block.OccupiedCells).ToArray();
        Require(cells.Length > 0, "A synthetic hull fixture must contain at least one cell.");
        return new GeneratedHull(
            HullParameters.Default,
            list,
            cells.Min(cell => cell.X), cells.Max(cell => cell.X),
            cells.Min(cell => cell.Y), cells.Max(cell => cell.Y),
            cells.Min(cell => cell.Z), cells.Max(cell => cell.Z));
    }

    private static BlockPlacement Cube(int x, int y, int z) =>
        new(BlockShape.Cube, MaterialKind.Metal, x, y, z, 0);
}
