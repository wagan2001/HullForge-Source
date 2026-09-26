using System.Diagnostics;
using FtdHullGenerator;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Composition;
using FtdHullGenerator.Geometry;
using FtdHullGenerator.Geometry.Smoothing;
using static SlopeFillTestSupport;

internal static class HybridSlopeFillTests
{
    private static readonly HashSet<int> HorizontalRotations = [16, 17, 18, 19, 22, 23];
    private static readonly HashSet<int> VerticalRotations = [0, 2, 4, 6, 8, 10, 12, 14];

    public static GeneratedHull Run(HullGenerator generator, bool full)
    {
        var parameters = HullParameters.Default with
        {
            Length = 40,
            Width = 15,
            Height = 11,
            BowFullness = 0.54,
            SternFullness = 0,
            CrossSectionCurve = 0.35,
            Beamify = true,
            HybridFillOffset = 1,
            Shape = null,
        };
        var baseline = generator.GenerateLegacyRegression(parameters);
        var offsetOne = generator.GenerateLegacyRegression(parameters with { Smoothing = SmoothingMethod.HybridSlopeFill });
        VerifyPartition(baseline, offsetOne, offset: 1);

        var offsetThree = generator.GenerateLegacyRegression(parameters with
        {
            Smoothing = SmoothingMethod.HybridSlopeFill,
            HybridFillOffset = 3,
        });
        VerifyPartition(baseline, offsetThree, offset: 3);
        var offsetThreeTransition = offsetThree.MaxY - 2;
        Require(offsetThree.Blocks.Where(IsHorizontal).SelectMany(block => block.OccupiedCells)
                .Any(cell => cell.Y == offsetThreeTransition),
            "Increasing the hybrid offset did not move horizontal fill down to the new transition row.");

        Require(new HybridSlopeFillPass().Apply(baseline with
                {
                    Parameters = parameters,
                    Blocks = baseline.Blocks.Reverse().ToArray(),
                }).Blocks.SequenceEqual(offsetOne.Blocks),
            "Hybrid fill depends on input enumeration order.");
        Require(MainWindow.CreateBlueprintName(parameters with { Smoothing = SmoothingMethod.HybridSlopeFill }) ==
                MainWindow.CreateBlueprintName(parameters),
            "Hybrid fill leaked into the generated blueprint name.");

        Require((parameters with { HybridFillOffset = 0 }).Validate()
                .Any(error => error.Contains("Hybrid fill offset", StringComparison.Ordinal)),
            "A zero hybrid offset was accepted.");
        var oversizedOffset = generator.GenerateLegacyRegression(parameters with
            { Smoothing = SmoothingMethod.HybridSlopeFill, HybridFillOffset = 1000 });
        Require(oversizedOffset.Parameters.HybridFillOffset == oversizedOffset.OccupiedHeight - 1,
            "A stale hybrid offset was not normalized to the generated voxel height.");
        Require((parameters with { HybridFillOffset = 1 }).Validate().Count == 0 &&
                (parameters with { HybridFillOffset = parameters.Height - 1 }).Validate().Count == 0,
            "A valid hybrid offset boundary was rejected.");

        VerifyHorizontalPriority();
        VerifyShapeV2CavityGuardInheritance(generator);
        if (full)
        {
            VerifyVariants(generator, parameters, SmoothingMethod.HybridSlopeFill);

            var largeParameters = parameters with { Length = 300, Width = 60, Height = 30 };
            var largeBase = generator.GenerateLegacyRegression(largeParameters);
            var timer = Stopwatch.StartNew();
            var large = generator.GenerateLegacyRegression(largeParameters with { Smoothing = SmoothingMethod.HybridSlopeFill });
            timer.Stop();
            VerifyAdditive(largeBase, large);
            Require(timer.Elapsed < TimeSpan.FromSeconds(2),
                $"300m hybrid fill took {timer.Elapsed.TotalMilliseconds:N0} ms.");
            Console.WriteLine($"HybridSlopeFill: offsets 1/3 and horizontal-first arbitration passed; " +
                              $"300m case {timer.Elapsed.TotalMilliseconds:N0} ms.");
        }
        else
        {
            Console.WriteLine("HybridSlopeFill: offsets 1/3 and horizontal-first arbitration passed.");
        }
        return offsetOne;
    }

    private static void VerifyPartition(GeneratedHull baseline, GeneratedHull filled, int offset)
    {
        VerifyAdditive(baseline, filled);
        var transitionY = filled.MaxY - offset + 1;
        var slopes = filled.Blocks.Where(block => block.Origin == BlockOrigin.Smoothing).ToArray();
        var horizontal = slopes.Where(IsHorizontal).ToArray();
        var vertical = slopes.Where(IsVertical).ToArray();
        Require(horizontal.Length > 0 && vertical.Length > 0 && horizontal.Length + vertical.Length == slopes.Length,
            $"Hybrid offset {offset} did not retain both expected fill families.");
        Require(horizontal.SelectMany(block => block.OccupiedCells).All(cell => cell.Y >= transitionY),
            $"Hybrid offset {offset} placed horizontal fill below its transition.");
        Require(vertical.SelectMany(block => block.OccupiedCells).All(cell => cell.Y < transitionY),
            $"Hybrid offset {offset} placed or clipped a vertical slope across its transition.");

        var mirrorSum = baseline.MinX + baseline.MaxX;
        var horizontalRaw = HorizontalSlopeFillPass.CollectCandidates(baseline)
            .Where(candidate => candidate.OccupiedCells.All(cell => cell.Y >= transitionY))
            .Select(Key).ToHashSet();
        var verticalRaw = VerticalSlopeFillPass.CollectCandidates(baseline)
            .Where(candidate => candidate.OccupiedCells.All(cell => cell.Y < transitionY))
            .Select(Key).ToHashSet();
        Require(horizontal.Where(block => 2 * block.X < mirrorSum).Select(Key).All(horizontalRaw.Contains) &&
                vertical.Where(block => 2 * block.X < mirrorSum).Select(Key).All(verticalRaw.Contains),
            "Hybrid fill invented a placement outside its partitioned raw candidate sets.");

        var crossingVertical = VerticalSlopeFillPass.CollectCandidates(baseline)
            .Where(candidate => candidate.OccupiedCells.Any(cell => cell.Y < transitionY) &&
                                candidate.OccupiedCells.Any(cell => cell.Y >= transitionY))
            .Select(Key).ToHashSet();
        Require(crossingVertical.Count > 0 &&
                vertical.Where(block => 2 * block.X < mirrorSum).Select(Key).All(key => !crossingVertical.Contains(key)),
            "Hybrid fill did not reject complete vertical slopes crossing the transition.");
    }

    /// <summary>
    /// HF-01 inheritance: Hybrid draws its horizontal candidates directly from
    /// <see cref="HorizontalSlopeFillPass.CollectCandidates" />, so the Shape V2 cavity guard is
    /// inherited automatically and no second divergent guard exists.
    /// </summary>
    private static void VerifyShapeV2CavityGuardInheritance(HullGenerator generator)
    {
        var parameters = HullParameters.Default with
        {
            Length = 90, Width = 17, Height = 17,
            Shape = HullShapeSettings.Default with { Profile = new HullProfileSettings(4, 2, 3, 1) },
            HybridFillOffset = 3,
        };
        var none = generator.Generate(parameters with { Smoothing = SmoothingMethod.None });
        var hybrid = generator.Generate(parameters with { Smoothing = SmoothingMethod.HybridSlopeFill });
        var context = HullGenerator.CreateContext(parameters);

        var nonExterior = hybrid.Blocks
            .Where(block => block.Origin == BlockOrigin.Smoothing)
            .Where(block => block.OccupiedCells.Any(cell =>
                context.RoleAt(cell.X, cell.Y, cell.Z) != HullCellRole.Outside))
            .ToArray();
        Require(nonExterior.Length == 0,
            $"Hybrid inherited {nonExterior.Length} cavity/armor-facing horizontal placement(s) " +
            "from the uncorrected Horizontal candidate set.");

        // The horizontal source is exactly the corrected Horizontal candidate set, filtered only
        // by the hybrid transition band; no cavity-facing candidate is reintroduced.
        var transitionY = hybrid.MaxY - Math.Clamp(parameters.HybridFillOffset, 1, hybrid.OccupiedHeight - 1) + 1;
        var horizontalRaw = HorizontalSlopeFillPass.CollectCandidates(none)
            .Where(candidate => candidate.OccupiedCells.All(cell => cell.Y >= transitionY))
            .ToArray();
        Require(horizontalRaw.All(candidate => candidate.OccupiedCells.All(cell =>
                context.RoleAt(cell.X, cell.Y, cell.Z) == HullCellRole.Outside)),
            "Hybrid's horizontal raw candidates still contain a cavity-facing internal return.");
        var mirrorSum = hybrid.MinX + hybrid.MaxX;
        var horizontalRawKeys = horizontalRaw.Select(Key).ToHashSet();
        Require(hybrid.Blocks.Where(block => IsHorizontal(block) && 2 * block.X < mirrorSum)
                .All(block => horizontalRawKeys.Contains(Key(block))),
            "Hybrid emitted a horizontal placement absent from the corrected Horizontal candidate set.");
    }

    private static void VerifyHorizontalPriority()
    {
        var blocks = Enumerable.Range(0, 5)
            .Select(z => new BlockPlacement(BlockShape.Cube, MaterialKind.Metal, 0, 0, z, 0))
            .Concat(
            [
                new BlockPlacement(BlockShape.Cube, MaterialKind.Metal, -1, 0, 0, 0),
                new BlockPlacement(BlockShape.Cube, MaterialKind.Metal, 1, 0, 0, 0),
            ])
            .OrderBy(block => block.Z).ThenBy(block => block.Y).ThenBy(block => block.X)
            .ToArray();
        var baseline = new GeneratedHull(HullParameters.Default with { Shape = null }, blocks, -1, 1, 0, 0, 0, 4);
        var horizontal = new BlockPlacement(BlockShape.Slope2, MaterialKind.Metal, -1, 0, 1, 18)
            { Origin = BlockOrigin.Smoothing };
        var longerVertical = new BlockPlacement(BlockShape.Slope4, MaterialKind.Metal, -1, 0, 1, 12)
            { Origin = BlockOrigin.Smoothing };
        var resolved = HybridSlopeFillPass.ResolveCandidates(baseline, [horizontal], [longerVertical]);
        VerifyAdditive(baseline, resolved);
        var additions = resolved.Blocks.Where(block => block.Origin == BlockOrigin.Smoothing).ToArray();
        Require(additions.Length == 2 && additions.Any(block => Key(block) == Key(horizontal)) &&
                additions.All(block => block.CellLength == 2),
            "Hybrid arbitration did not give a shorter horizontal pair precedence over vertical fill.");
    }

    private static bool IsHorizontal(BlockPlacement block) =>
        block.Origin == BlockOrigin.Smoothing && HorizontalRotations.Contains(block.Rotation);

    private static bool IsVertical(BlockPlacement block) =>
        block.Origin == BlockOrigin.Smoothing && VerticalRotations.Contains(block.Rotation);

    private static (BlockShape Shape, int X, int Y, int Z, int Rotation) Key(BlockPlacement block) =>
        (block.Shape, block.X, block.Y, block.Z, block.Rotation);
}
