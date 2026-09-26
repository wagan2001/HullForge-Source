using System.Diagnostics;
using FtdHullGenerator;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Composition;
using FtdHullGenerator.Geometry;
using FtdHullGenerator.Geometry.Smoothing;
using static SlopeFillTestSupport;

internal static class CombinedSlopeFillTests
{
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
            Shape = null,
        };
        var baseline = generator.GenerateLegacyRegression(parameters);
        (int Priority, SmoothingMethod Method, IReadOnlyList<BlockPlacement> Candidates)[] sources =
        [
            (0, SmoothingMethod.VerticalSlopeFill, VerticalSlopeFillPass.CollectCandidates(baseline)),
            (1, SmoothingMethod.HorizontalSlopeFill, HorizontalSlopeFillPass.CollectCandidates(baseline)),
            (2, SmoothingMethod.CrossSectionSlopeFill, CrossSectionSlopeFillPass.CollectCandidates(baseline)),
        ];
        var methods = sources.Select(source => source.Method).ToArray();
        var rawCandidateKeys = sources.SelectMany(source => source.Candidates).Select(Key).ToHashSet();
        var expectedTaggedCandidates = sources
            .SelectMany(source => source.Candidates.Select(placement =>
                (source.Priority, Placement: Key(placement))))
            .ToHashSet();
        var actualTaggedCandidates = CombinedSlopeFillPass.CollectCandidates(baseline)
            .Select(candidate => (candidate.Priority, Placement: Key(candidate.Placement)))
            .ToHashSet();
        Require(actualTaggedCandidates.SetEquals(expectedTaggedCandidates),
            "Combined fill did not collect the complete raw candidate sets.");
        var independent = methods.Select(method => generator.GenerateLegacyRegression(parameters with { Smoothing = method })).ToArray();
        var mirrorSum = baseline.MinX + baseline.MaxX;
        var independentKeys = independent
            .SelectMany(hull => hull.Blocks.Where(block =>
                block.Origin == BlockOrigin.Smoothing && 2 * block.X < mirrorSum))
            .Select(Key)
            .ToHashSet();
        Require(independentKeys.All(rawCandidateKeys.Contains),
            "An independent pass accepted a placement absent from its raw candidate set.");
        var reserveCandidateCount = rawCandidateKeys.Count(key => !independentKeys.Contains(key));
        Require(reserveCandidateCount > 0,
            "The regression fixture no longer exercises candidates pruned by independent passes.");

        var combined = generator.GenerateLegacyRegression(parameters with { Smoothing = SmoothingMethod.CombinedSlopeFill });
        VerifyAdditive(baseline, combined);
        var combinedSlopes = combined.Blocks.Where(block => block.Origin == BlockOrigin.Smoothing).ToArray();
        Require(combinedSlopes.Length == 190,
            $"Combined fill added {combinedSlopes.Length} slopes instead of the pinned 190.");
        var combinedKeys = combinedSlopes.Where(block => 2 * block.X < mirrorSum).Select(Key).ToHashSet();
        Require(combinedKeys.All(rawCandidateKeys.Contains),
            "Combined fill invented a placement absent from its raw source candidates.");
        foreach (var source in sources)
        {
            var sourceKeys = source.Candidates.Select(Key).ToHashSet();
            var otherSourceKeys = sources.Where(other => other.Method != source.Method)
                .SelectMany(other => other.Candidates)
                .Select(Key)
                .ToHashSet();
            var sourceExclusiveKeys = sourceKeys.Where(key => !otherSourceKeys.Contains(key)).ToHashSet();
            Require(combinedKeys.Any(sourceExclusiveKeys.Contains),
                $"Combined fill retained no source-exclusive candidate from {source.Method}.");
        }

        Require(new CombinedSlopeFillPass().Apply(baseline with
                { Blocks = baseline.Blocks.Reverse().ToArray() }).Blocks.SequenceEqual(combined.Blocks),
            "Combined fill depends on input enumeration order.");
        Require(MainWindow.CreateBlueprintName(parameters with
                { Smoothing = SmoothingMethod.CombinedSlopeFill }) == MainWindow.CreateBlueprintName(parameters),
            "Combined fill leaked into the generated blueprint name.");

        VerifyPriorityAndWholePairResolution();
        VerifyRawRunnerUpSurvivesGlobalArbitration();
        VerifyShapeV2CavityGuardInheritance(generator);
        if (full)
        {
            VerifyVariants(generator, parameters, SmoothingMethod.CombinedSlopeFill);

            var largeParameters = parameters with { Length = 300, Width = 60, Height = 30 };
            var largeBase = generator.GenerateLegacyRegression(largeParameters);
            var timer = Stopwatch.StartNew();
            var large = generator.GenerateLegacyRegression(largeParameters with { Smoothing = SmoothingMethod.CombinedSlopeFill });
            timer.Stop();
            VerifyAdditive(largeBase, large);
            Require(timer.Elapsed < TimeSpan.FromSeconds(2),
                $"300m combined fill took {timer.Elapsed.TotalMilliseconds:N0} ms.");
            Console.WriteLine($"Combined fill: {combinedSlopes.Length:N0} conflict-resolved slopes, " +
                              $"{reserveCandidateCount:N0} raw reserve candidates available; " +
                              $"300m case {timer.Elapsed.TotalMilliseconds:N0} ms.");
        }
        else
        {
            Console.WriteLine($"Combined fill: {combinedSlopes.Length:N0} conflict-resolved slopes, " +
                              $"{reserveCandidateCount:N0} raw reserve candidates available.");
        }
        return combined;
    }

    /// <summary>
    /// HF-01 inheritance: Combined consumes <see cref="HorizontalSlopeFillPass.CollectCandidates" />
    /// directly, so its priority-1 source is the corrected Shape V2 candidate set and no second
    /// divergent guard exists.
    /// </summary>
    private static void VerifyShapeV2CavityGuardInheritance(HullGenerator generator)
    {
        var parameters = HullParameters.Default with
        {
            Length = 90, Width = 17, Height = 17,
            Shape = HullShapeSettings.Default with { Profile = new HullProfileSettings(4, 2, 3, 1) },
        };
        var none = generator.Generate(parameters with { Smoothing = SmoothingMethod.None });
        var combined = generator.Generate(parameters with { Smoothing = SmoothingMethod.CombinedSlopeFill });
        var context = HullGenerator.CreateContext(parameters);

        var nonExterior = combined.Blocks
            .Where(block => block.Origin == BlockOrigin.Smoothing)
            .Where(block => block.OccupiedCells.Any(cell =>
                context.RoleAt(cell.X, cell.Y, cell.Z) != HullCellRole.Outside))
            .ToArray();
        Require(nonExterior.Length == 0,
            $"Combined inherited {nonExterior.Length} cavity/armor-facing smoothing placement(s) " +
            "from the uncorrected Horizontal candidate set.");

        // The priority-1 (horizontal) source is exactly the corrected Horizontal candidate set.
        var horizontalSource = CombinedSlopeFillPass.CollectCandidates(none)
            .Where(candidate => candidate.Priority == 1)
            .Select(candidate => Key(candidate.Placement))
            .ToHashSet();
        var correctedHorizontal = HorizontalSlopeFillPass.CollectCandidates(none)
            .Select(Key)
            .ToHashSet();
        Require(horizontalSource.SetEquals(correctedHorizontal),
            "Combined's horizontal source diverged from the corrected Horizontal candidate set.");
        Require(correctedHorizontal.Count > 0 &&
                HorizontalSlopeFillPass.CollectCandidates(none).All(candidate =>
                    candidate.OccupiedCells.All(cell =>
                        context.RoleAt(cell.X, cell.Y, cell.Z) == HullCellRole.Outside)),
            "The corrected Horizontal candidate set still contains a cavity-facing internal return.");
    }

    private static void VerifyPriorityAndWholePairResolution()
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
        var higherPriority = new BlockPlacement(BlockShape.Slope2, MaterialKind.Metal, -1, 0, 1, 12)
            { Origin = BlockOrigin.Smoothing };
        var longerLowerPriority = new BlockPlacement(BlockShape.Slope4, MaterialKind.Metal, -1, 0, 1, 18)
            { Origin = BlockOrigin.Smoothing };
        (int Priority, BlockPlacement Placement)[] lengthCandidates =
            [(1, longerLowerPriority), (0, higherPriority)];

        var lengthResolved = CombinedSlopeFillPass.ResolveCandidates(baseline, lengthCandidates);
        VerifyAdditive(baseline, lengthResolved);
        var lengthAdditions = lengthResolved.Blocks
            .Where(block => block.Origin == BlockOrigin.Smoothing)
            .ToArray();
        Require(lengthAdditions.Length == 2 &&
                lengthAdditions.Any(block => Key(block) == Key(longerLowerPriority)) &&
                lengthAdditions.Any(block => block.Shape == BlockShape.Slope4 &&
                                              block.X == 1 && block.Rotation == 16),
            "Combined arbitration did not prefer the longer complete mirrored pair.");
        Require(CombinedSlopeFillPass.ResolveCandidates(baseline, lengthCandidates.Reverse()).Blocks
                .SequenceEqual(lengthResolved.Blocks),
            "Combined length arbitration depends on candidate enumeration order.");

        var equalLengthLowerPriority = new BlockPlacement(
            BlockShape.Slope2, MaterialKind.Metal, -1, 0, 1, 18)
            { Origin = BlockOrigin.Smoothing };
        (int Priority, BlockPlacement Placement)[] priorityCandidates =
            [(1, equalLengthLowerPriority), (0, higherPriority)];
        var priorityResolved = CombinedSlopeFillPass.ResolveCandidates(baseline, priorityCandidates);
        VerifyAdditive(baseline, priorityResolved);
        var priorityAdditions = priorityResolved.Blocks
            .Where(block => block.Origin == BlockOrigin.Smoothing)
            .ToArray();
        Require(priorityAdditions.Length == 2 &&
                priorityAdditions.Any(block => Key(block) == Key(higherPriority)) &&
                priorityAdditions.Any(block => block.Shape == BlockShape.Slope2 &&
                                                block.X == 1 && block.Rotation == 12),
            "Combined arbitration did not apply evidence priority to equal-length pairs.");
        Require(CombinedSlopeFillPass.ResolveCandidates(baseline, priorityCandidates.Reverse()).Blocks
                .SequenceEqual(priorityResolved.Blocks),
            "Combined priority arbitration depends on candidate enumeration order.");
    }

    private static void VerifyRawRunnerUpSurvivesGlobalArbitration()
    {
        var blocks = Enumerable.Range(0, 7)
            .Select(z => new BlockPlacement(BlockShape.Cube, MaterialKind.Metal, 0, 0, z, 0))
            .Concat(
            [
                new BlockPlacement(BlockShape.Cube, MaterialKind.Metal, -1, 0, 0, 0),
                new BlockPlacement(BlockShape.Cube, MaterialKind.Metal, 1, 0, 0, 0),
            ])
            .OrderBy(block => block.Z).ThenBy(block => block.Y).ThenBy(block => block.X)
            .ToArray();
        var baseline = new GeneratedHull(HullParameters.Default with { Shape = null }, blocks, -1, 1, 0, 0, 0, 6);

        // Within the horizontal source, A wins over B because it is longer. A then
        // loses an equal-length global conflict to higher-priority C. Only the raw
        // combined candidate set keeps B available to claim its now-free cells.
        var sourceWinnerA = new BlockPlacement(BlockShape.Slope4, MaterialKind.Metal, -1, 0, 3, 18)
            { Origin = BlockOrigin.Smoothing };
        var sourceRunnerUpB = new BlockPlacement(BlockShape.Slope2, MaterialKind.Metal, -1, 0, 5, 18)
            { Origin = BlockOrigin.Smoothing };
        var higherPriorityC = new BlockPlacement(BlockShape.Slope4, MaterialKind.Metal, -1, 0, 1, 12)
            { Origin = BlockOrigin.Smoothing };

        (int Priority, BlockPlacement Placement)[] localSourceCandidates =
            [(1, sourceWinnerA), (1, sourceRunnerUpB)];
        var locallyPruned = CombinedSlopeFillPass.ResolveCandidates(baseline, localSourceCandidates);
        var locallyPrunedKeys = locallyPruned.Blocks
            .Where(block => block.Origin == BlockOrigin.Smoothing && block.X < 0)
            .Select(Key)
            .ToHashSet();
        Require(locallyPrunedKeys.Contains(Key(sourceWinnerA)) &&
                !locallyPrunedKeys.Contains(Key(sourceRunnerUpB)),
            "Synthetic source arbitration no longer prunes its runner-up.");

        var usingPrunedSource = CombinedSlopeFillPass.ResolveCandidates(baseline,
            [(1, sourceWinnerA), (0, higherPriorityC)]);
        var usingRawSources = CombinedSlopeFillPass.ResolveCandidates(baseline,
            [(1, sourceWinnerA), (1, sourceRunnerUpB), (0, higherPriorityC)]);
        VerifyAdditive(baseline, usingRawSources);
        var rawKeys = usingRawSources.Blocks
            .Where(block => block.Origin == BlockOrigin.Smoothing && block.X < 0)
            .Select(Key)
            .ToHashSet();
        Require(usingPrunedSource.Blocks.Count(block => block.Origin == BlockOrigin.Smoothing) == 2 &&
                usingRawSources.Blocks.Count(block => block.Origin == BlockOrigin.Smoothing) == 4 &&
                rawKeys.Contains(Key(higherPriorityC)) && rawKeys.Contains(Key(sourceRunnerUpB)),
            "Global arbitration did not recover the source-local runner-up after its blocker lost.");
    }

    private static (BlockShape Shape, int X, int Y, int Z, int Rotation) Key(BlockPlacement block) =>
        (block.Shape, block.X, block.Y, block.Z, block.Rotation);
}
