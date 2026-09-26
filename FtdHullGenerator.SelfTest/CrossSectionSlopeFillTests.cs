using System.Diagnostics;
using FtdHullGenerator;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Geometry;
using FtdHullGenerator.Geometry.Smoothing;
using static SlopeFillTestSupport;

internal static class CrossSectionSlopeFillTests
{
    public static (GeneratedHull Hull, GeneratedHull BidirectionalFixture) Run(HullGenerator generator, bool full)
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
        var filled = generator.GenerateLegacyRegression(parameters with { Smoothing = SmoothingMethod.CrossSectionSlopeFill });
        VerifyAdditive(baseline, filled);

        var slopes = filled.Blocks.Where(block => block.Origin == BlockOrigin.Smoothing).ToArray();
        Require(slopes.Length == 230,
            $"Cross-section fill added {slopes.Length} slopes instead of the pinned 230.");
        Require(slopes.All(block => block.CellLength is >= 2 and <= 4),
            "The conservative cross-section pass emitted a singleton or overlong slope.");
        Require(slopes.All(block => block.Rotation is 5 or 7 or 9 or 11),
            "Cross-section fill used a rotation outside 5/7/9/11.");

        foreach (var (rotation, expectedMirror, direction) in new[]
                 {
                     (11, 9, 1),
                     (9, 11, 1),
                     (7, 5, -1),
                     (5, 7, -1),
                 })
        {
            var block = new BlockPlacement(BlockShape.Slope3, MaterialKind.Metal, 5, 6, 7, rotation);
            Require(block.OccupiedCells.SequenceEqual(
                    Enumerable.Range(0, 3).Select(step => (5, 6 + direction * step, 7))),
                $"Rotation {rotation} expands in the wrong vertical direction.");
            Require(HullGeometryValidator.MirrorRotation(rotation) == expectedMirror,
                $"Rotation {rotation} has the wrong X mirror.");
        }

        // A minimal independent cross-section has two-cell gaps above and below
        // each side. It pins anchors, directions, and both mirrored hands without
        // deriving the expected placements from the production detector.
        var strip = VerticalStrip(sideMinY: 2, sideMaxY: 5, centreMinY: 0, centreMaxY: 7);
        var stripFilled = new CrossSectionSlopeFillPass().Apply(strip);
        VerifyAdditive(strip, stripFilled);
        VerifyReference(stripFilled,
        [
            new(BlockShape.Slope2, MaterialKind.Metal, -1, 1, 0, 7),
            new(BlockShape.Slope2, MaterialKind.Metal, 1, 1, 0, 5),
            new(BlockShape.Slope2, MaterialKind.Metal, -1, 6, 0, 11),
            new(BlockShape.Slope2, MaterialKind.Metal, 1, 6, 0, 9),
        ]);

        // Gaps longer than the game's ordinary down-slope vocabulary are capped at
        // four metres. A missing inboard cell rejects both halves of the candidate.
        var longGap = VerticalStrip(sideMinY: 0, sideMaxY: 0, centreMinY: 0, centreMaxY: 7);
        var longFilled = new CrossSectionSlopeFillPass().Apply(longGap);
        VerifyAdditive(longGap, longFilled);
        VerifyReference(longFilled,
        [
            new(BlockShape.Slope4, MaterialKind.Metal, -1, 1, 0, 11),
            new(BlockShape.Slope4, MaterialKind.Metal, 1, 1, 0, 9),
        ]);
        var unsupported = longGap with
        {
            Blocks = longGap.Blocks.Where(block => block.Position != (0, 3, 0)).ToArray(),
        };
        Require(ReferenceEquals(new CrossSectionSlopeFillPass().Apply(unsupported), unsupported),
            "A gap in vertical inboard support must reject the whole mirrored slope pair.");

        Require(new CrossSectionSlopeFillPass().Apply(baseline with
                { Blocks = baseline.Blocks.Reverse().ToArray() }).Blocks.SequenceEqual(filled.Blocks),
            "Cross-section fill depends on input enumeration order.");
        Require(MainWindow.CreateBlueprintName(parameters with
                { Smoothing = SmoothingMethod.CrossSectionSlopeFill }) == MainWindow.CreateBlueprintName(parameters),
            "Cross-section fill leaked into the generated blueprint name.");

        if (full)
        {
            VerifyVariants(generator, parameters, SmoothingMethod.CrossSectionSlopeFill);
            var largeParameters = parameters with { Length = 300, Width = 60, Height = 30 };
            var largeBase = generator.GenerateLegacyRegression(largeParameters);
            var timer = Stopwatch.StartNew();
            var large = generator.GenerateLegacyRegression(largeParameters with { Smoothing = SmoothingMethod.CrossSectionSlopeFill });
            timer.Stop();
            VerifyAdditive(largeBase, large);
            Require(timer.Elapsed < TimeSpan.FromSeconds(2),
                $"300m cross-section fill took {timer.Elapsed.TotalMilliseconds:N0} ms.");
            Console.WriteLine($"Cross-section fill: {slopes.Length:N0} conservative slopes; " +
                              $"300m case {timer.Elapsed.TotalMilliseconds:N0} ms.");
        }
        else
        {
            Console.WriteLine($"Cross-section fill: {slopes.Length:N0} conservative slopes matched the frozen reference.");
        }
        return (filled, stripFilled);
    }

    private static GeneratedHull VerticalStrip(int sideMinY, int sideMaxY, int centreMinY, int centreMaxY)
    {
        var blocks = new List<BlockPlacement>();
        for (var y = centreMinY; y <= centreMaxY; y++)
            blocks.Add(new BlockPlacement(BlockShape.Cube, MaterialKind.Metal, 0, y, 0, 0));
        for (var y = sideMinY; y <= sideMaxY; y++)
        {
            blocks.Add(new BlockPlacement(BlockShape.Cube, MaterialKind.Metal, -1, y, 0, 0));
            blocks.Add(new BlockPlacement(BlockShape.Cube, MaterialKind.Metal, 1, y, 0, 0));
        }

        return new GeneratedHull(
            HullParameters.Default with { Shape = null },
            blocks.OrderBy(block => block.Z).ThenBy(block => block.Y).ThenBy(block => block.X).ToArray(),
            -1,
            1,
            Math.Min(sideMinY, centreMinY),
            Math.Max(sideMaxY, centreMaxY),
            0,
            0);
    }

    private static void VerifyReference(GeneratedHull hull, BlockPlacement[] expected)
    {
        static (BlockShape Shape, int X, int Y, int Z, int Rotation) Key(BlockPlacement block) =>
            (block.Shape, block.X, block.Y, block.Z, block.Rotation);
        var actual = hull.Blocks.Where(block => block.Origin == BlockOrigin.Smoothing).Select(Key).ToHashSet();
        Require(actual.Count == expected.Length && actual.SetEquals(expected.Select(Key)),
            $"Cross-section reference mismatch. Actual: {string.Join("; ", actual)}.");
    }
}
