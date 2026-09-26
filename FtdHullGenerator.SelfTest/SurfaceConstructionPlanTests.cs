using FtdHullGenerator.Domain;
using FtdHullGenerator.Geometry;
using FtdHullGenerator.Geometry.Smoothing;
using static SlopeFillTestSupport;

internal static class SurfaceConstructionPlanTests
{
    public static void Run()
    {
        // Independently sampled compound plane: x = 4 + y - floor(z/3).
        // It has both a vertical and a gradual horizontal gradient, without using
        // any library footprint to construct the input or expected contour.
        var grid = new HullSurfaceGrid(-12, 12, 0, 8, 0, 12,
            (y, z) => new(-(4 + y - z / 3), 4 + y - z / 3), _ => 0, _ => 8);
        var parameters = HullParameters.Default with
        {
            Length = 13, Width = 25, Height = 9,
            Beamify = false, Smoothing = SmoothingMethod.InvertedTriangleFill,
        };
        var plan = InvertedTriangleConstruction.Plan(grid, parameters);
        ConstructionSkinTests.VerifySamplingFixture();
        ConstructionSkinTests.VerifyOriginalSkin(grid, plan.Regions.SelectMany(region => region.Placements));
        Require(plan.Regions.Count > 0 && plan.SpanEdits.Count > 0,
            "The gradual compound-plane fixture produced no construction or regularization.");
        var repeated = InvertedTriangleConstruction.Plan(grid, parameters);
        Require(plan.Regions.SelectMany(region => region.Placements)
                    .SequenceEqual(repeated.Regions.SelectMany(region => region.Placements)) &&
                plan.SpanEdits.SequenceEqual(repeated.SpanEdits),
            "The construction search is not deterministic.");
        foreach (var edit in plan.SpanEdits)
            Require(!grid.IsProtected(edit.Y, edit.Z) &&
                    Math.Abs(edit.Original.MinX - edit.Revised.MinX) <= 1 &&
                    Math.Abs(edit.Original.MaxX - edit.Revised.MaxX) <= 1 &&
                    grid.SpanAt(edit.Y, edit.Z) == edit.Original,
                "Regularization changed a protected row, exceeded its allowance, or mutated the original grid.");

        var rebuiltSolid = Cubes(plan.ModifiedGrid, parameters);
        var fitted = plan.TryFit(rebuiltSolid);
        Require(fitted.Succeeded && fitted.Hull.Blocks.Any(block => block.Origin == BlockOrigin.Smoothing),
            "Complete construction could not reconcile with eligible rebuilt outer cells.");
        Require(HullGeometryValidator.Validate(fitted.Hull).Count == 0,
            "The complete synthetic construction overlaps cells, loses symmetry, or disconnects its backing.");

        var target = plan.Regions[0];
        var targetCell = target.Placements[0].OccupiedCells.First();
        foreach (var invalidCell in new Func<BlockPlacement, BlockPlacement>[]
        {
            block => block with { ArmorDepth = 1 },
            block => block with { UsePoles = true },
            block => block with { Material = MaterialKind.Wood },
            block => block with { Shape = BlockShape.CornerLeft },
        })
        {
            var invalidSolid = rebuiltSolid with
            {
                Blocks = rebuiltSolid.Blocks.Select(block => block.Position == targetCell ? invalidCell(block) : block).ToArray(),
            };
            VerifyRejectedAtomically(plan, target, invalidSolid);
        }
        var missingBacking = target.RequiredBacking[0];
        VerifyRejectedAtomically(plan, target, rebuiltSolid with
        {
            Blocks = rebuiltSolid.Blocks.Where(block => block.Position != missingBacking).ToArray(),
        });
        // An absent outer footprint may represent reserved air. The fitter must not
        // fill it opportunistically merely because neighboring cube backing exists.
        VerifyRejectedAtomically(plan, target, rebuiltSolid with
        {
            Blocks = rebuiltSolid.Blocks.Where(block => block.Position != targetCell).ToArray(),
        });

        var restored = plan.WithoutRegions(plan.Regions.Select(region => region.Id));
        Require(restored.Regions.Count == 0 && restored.SpanEdits.Count == 0,
            "Removing all failed regions left fitted parts or contour edits behind.");
        for (var z = grid.MinZ; z <= grid.MaxZ; z++)
        for (var y = grid.MinY; y <= grid.MaxY; y++)
            Require(restored.ModifiedGrid.SpanAt(y, z) == grid.SpanAt(y, z),
                "Rollback did not restore the immutable original contour.");
        var untouched = Cubes(grid, parameters);
        Require(restored.TryFit(untouched).Hull.Blocks.ToHashSet().SetEquals(untouched.Blocks),
            "An empty plan ran an obsolete refit as fallback.");

        var flat = new HullSurfaceGrid(-12, 12, 0, 8, 0, 12,
            (_, _) => new(-12, 12), _ => 0, _ => 8);
        Require(InvertedTriangleConstruction.Plan(flat, parameters).Regions.Count == 0,
            "The planner introduced diagonal dents into a flat wall.");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        RequireCancelled(() => InvertedTriangleConstruction.Plan(grid, parameters, cancelled.Token));
        RequireCancelled(() => plan.TryFit(rebuiltSolid, cancelled.Token));
        Console.WriteLine($"Surface construction: compound plane, {plan.Regions.Count} mirrored assemblies, atomic rejection, pins, rollback, and cancellation passed.");
    }

    private static void VerifyRejectedAtomically(SurfaceConstructionPlan plan,
        SurfaceConstructionRegion target, GeneratedHull invalidSolid)
    {
        var actual = plan.TryFit(invalidSolid);
        Require(!actual.Succeeded && actual.FailedRegionIds.Contains(target.Id),
            "Construction consumed reserved air, internal armor, a pole, wrong material, or incomplete backing.");
        var targetFootprint = target.Placements.SelectMany(block => block.OccupiedCells).ToHashSet();
        Require(!actual.Hull.Blocks.Any(block => block.Origin == BlockOrigin.Smoothing &&
                    block.OccupiedCells.Any(targetFootprint.Contains)),
            "A rejected mirrored assembly left partially fitted pieces behind.");
    }

    private static GeneratedHull Cubes(HullSurfaceGrid grid, HullParameters parameters)
    {
        var blocks = new List<BlockPlacement>();
        for (var z = grid.MinZ; z <= grid.MaxZ; z++)
        for (var y = grid.MinY; y <= grid.MaxY; y++)
        {
            var span = grid.SpanAt(y, z);
            for (var x = span.MinX; x <= span.MaxX; x++)
                blocks.Add(new(BlockShape.Cube, MaterialKind.Metal, x, y, z, 0));
        }
        return new(parameters, blocks, grid.MinX, grid.MaxX, grid.MinY, grid.MaxY, grid.MinZ, grid.MaxZ);
    }

    private static void RequireCancelled(Action action)
    {
        var observed = false;
        try { action(); }
        catch (OperationCanceledException) { observed = true; }
        Require(observed, "Surface planning/fitting ignored cancellation.");
    }
}
