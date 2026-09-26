using System.Diagnostics;
using System.Text.Json;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Composition;
using FtdHullGenerator.Geometry;
using FtdHullGenerator.Geometry.Smoothing;
using static SlopeFillTestSupport;

internal static class HorizontalSlopeFillTests
{
    public static GeneratedHull Run(HullGenerator generator, bool full)
    {
        VerifyShapeV2CavityGuard(generator);
        using var fixture = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "horizontal-fill-40x15x11.json")));
        var expected = fixture.RootElement.GetProperty("slopes").EnumerateArray()
            .Select(row => new BlockPlacement(Enum.Parse<BlockShape>(row[4].GetString()!), MaterialKind.Metal,
                row[0].GetInt32(), row[1].GetInt32(), row[2].GetInt32(), row[3].GetInt32())).ToArray();
        var parameters = HullParameters.Default with
        {
            Length = 40, Width = 15, Height = 11, BowFullness = 0.54,
            SternFullness = 0, CrossSectionCurve = 0.35, Beamify = true,
            Shape = null,
        };
        var baseline = generator.GenerateLegacyRegression(parameters);
        var filled = generator.GenerateLegacyRegression(parameters with { Smoothing = SmoothingMethod.HorizontalSlopeFill });
        VerifyAdditive(baseline, filled);
        Require(expected.Length == 108, "Retain all 108 independent horizontal reference slopes.");
        VerifyReference(filled, expected);
        Require(filled.BlockCount == baseline.BlockCount + 108,
            "The small horizontal reference must add exactly 108 slopes to the current base packing.");

        foreach (var rotation in new[] { 16, 17, 18, 19 })
        {
            var direction = rotation is 16 or 18 ? 1 : -1;
            var block = new BlockPlacement(BlockShape.Slope4, MaterialKind.Metal, 5, 6, 7, rotation);
            Require(block.OccupiedCells.SequenceEqual(Enumerable.Range(0, 4).Select(step => (5, 6, 7 + direction * step))),
                $"Rotation {rotation} expands in the wrong direction.");
            Require(HullGeometryValidator.MirrorRotation(rotation) == (rotation < 18 ? rotation + 2 : rotation - 2),
                $"Rotation {rotation} has the wrong X mirror.");
        }

        // Reflect the independent expected geometry fore/aft and translate all
        // axes; this exercises trailing pieces without a hardcoded station split.
        BlockPlacement Transform(BlockPlacement block) => block with
        {
            X = block.X + 17, Y = block.Y + 9, Z = 31 - block.Z,
            Rotation = block.Rotation switch { 16 => 17, 17 => 16, 18 => 19, 19 => 18, _ => block.Rotation },
        };
        var transformedBase = baseline with
        {
            Blocks = Cells(baseline.Blocks).Select(cell => Transform(new BlockPlacement(
                BlockShape.Cube, MaterialKind.Metal, cell.X, cell.Y, cell.Z, 0))).Reverse().ToArray(),
            MinX = 10, MaxX = 24, MinY = 9, MaxY = 19, MinZ = 12, MaxZ = 51,
        };
        var transformed = new HorizontalSlopeFillPass().Apply(transformedBase);
        VerifyAdditive(transformedBase, transformed);
        VerifyReference(transformed, expected.Select(Transform).ToArray());
        Require(new HorizontalSlopeFillPass().Apply(baseline with { Blocks = baseline.Blocks.Reverse().ToArray() })
            .Blocks.SequenceEqual(filled.Blocks), "Horizontal fill depends on input order.");
        // Beam partitioning cannot change the contour or its resulting slopes.
        VerifyReference(generator.GenerateLegacyRegression(parameters with { Beamify = false, Smoothing = SmoothingMethod.HorizontalSlopeFill }), expected);

        // Small independent strips pin policy at the 1m and long-gap boundaries.
        GeneratedHull Strip(int sideEnd) => new(parameters,
            (from x in new[] { -1, 0, 1 }
             from z in Enumerable.Range(0, x == 0 ? 8 : sideEnd + 1)
             select new BlockPlacement(BlockShape.Cube, MaterialKind.Metal, x, 0, z, 0)).ToArray(),
            -1, 1, 0, 0, 0, 7);
        var shortGap = Strip(6);
        var shortFilled = new HorizontalSlopeFillPass().Apply(shortGap);
        VerifyAdditive(shortGap, shortFilled);
        VerifyReference(shortFilled,
        [
            new(BlockShape.Slope1, MaterialKind.Metal, -1, 0, 7, 18),
            new(BlockShape.Slope1, MaterialKind.Metal, 1, 0, 7, 16),
        ]);
        var longGap = Strip(0);
        var longFilled = new HorizontalSlopeFillPass().Apply(longGap);
        VerifyAdditive(longGap, longFilled);
        VerifyReference(longFilled,
        [
            new(BlockShape.Slope4, MaterialKind.Metal, -1, 0, 1, 18),
            new(BlockShape.Slope4, MaterialKind.Metal, 1, 0, 1, 16),
        ]);
        var unsupported = longGap with { Blocks = longGap.Blocks.Where(block => block.Position != (0, 0, 3)).ToArray() };
        Require(ReferenceEquals(new HorizontalSlopeFillPass().Apply(unsupported), unsupported),
            "A gap in inboard support must reject the whole slope pair.");

        if (full)
        {
            VerifyVariants(generator, parameters, SmoothingMethod.HorizontalSlopeFill);
            var largeParameters = parameters with { Length = 300, Width = 60, Height = 30 };
            var largeBase = generator.GenerateLegacyRegression(largeParameters);
            var timer = Stopwatch.StartNew();
            var large = generator.GenerateLegacyRegression(largeParameters with { Smoothing = SmoothingMethod.HorizontalSlopeFill });
            timer.Stop();
            VerifyAdditive(largeBase, large);
            Require(timer.Elapsed < TimeSpan.FromSeconds(2), $"300m horizontal fill took {timer.Elapsed.TotalMilliseconds:N0} ms.");
            Console.WriteLine($"Horizontal fill: all 108 reference slopes match exact shapes, anchors and rotations; 300m case {timer.Elapsed.TotalMilliseconds:N0} ms.");
        }
        else
        {
            Console.WriteLine("Horizontal fill: all 108 reference slopes match exact shapes, anchors and rotations.");
        }
        return filled;
    }

    /// <summary>
    /// HF-01: a Shape V2 hull's hollow interior is a longitudinal gap with the same inboard/lower
    /// support as a genuine surface notch, so the row contour alone cannot tell them apart. Every
    /// native Horizontal internal return must occupy genuine exterior space under the original
    /// analytic classifier, and a real exterior return must survive.
    /// </summary>
    private static void VerifyShapeV2CavityGuard(HullGenerator generator)
    {
        var parameters = HullParameters.Default with
        {
            Length = 90, Width = 17, Height = 17,
            Shape = HullShapeSettings.Default with { Profile = new HullProfileSettings(4, 2, 3, 1) },
        };
        var none = generator.Generate(parameters with { Smoothing = SmoothingMethod.None });
        var horizontal = generator.Generate(parameters with { Smoothing = SmoothingMethod.HorizontalSlopeFill });

        // Independent of the production classifier: the public hull-context query over the same
        // parameters classifies each original analytic cell. This is not derived from the final
        // placement list.
        var context = HullGenerator.CreateContext(parameters);
        var role = (Func<(int X, int Y, int Z), HullCellRole>)(cell => context.RoleAt(cell.X, cell.Y, cell.Z));

        // 1. No native Horizontal smoothing placement may sit in cavity, structural armor or
        //    reserved armor air. Before the guard this hull emitted 92 such mirrored placements.
        var nonExterior = horizontal.Blocks
            .Where(block => block.Origin == BlockOrigin.Smoothing)
            .Where(block => block.OccupiedCells.Any(cell => role(cell) != HullCellRole.Outside))
            .ToArray();
        Require(nonExterior.Length == 0,
            $"Native Horizontal smoothing left {nonExterior.Length} placement(s) outside exterior space" +
            (nonExterior.Length > 0 ? $", e.g. {Describe(nonExterior[0])}." : "."));

        // 2. A raw internal-return candidate that faces the cavity is rejected. The pre-fix pass
        //    emitted a 3 m forward return anchored at (-4, 12, -43) whose every cell is cavity.
        Require(context.RoleAt(-4, 12, -43) == HullCellRole.Cavity,
            "The reproduced cavity cell (-4,12,-43) is no longer classified as cavity.");
        var raw = HorizontalSlopeFillPass.CollectCandidates(none);
        Require(!raw.Any(block => block.Position == (-4, 12, -43) && block.Rotation == 0),
            "The cavity-facing raw internal return at (-4,12,-43) survived candidate discovery.");
        Require(!horizontal.Blocks.Any(block => block.Origin == BlockOrigin.Smoothing &&
                block.Position == (-4, 12, -43) && block.Rotation == 0),
            "The cavity-facing internal return at (-4,12,-43) reached the final placement list.");

        // 3. A legitimate exterior internal return survives, unchanged. The pass still caps the
        //    genuine surface notch at (-7, 17, -30) and its two mirror-side partners.
        var exteriorReturn = raw.Single(block => block.Position == (-7, 17, -30) && block.Rotation == 0);
        Require(exteriorReturn.OccupiedCells.All(cell => role(cell) == HullCellRole.Outside),
            "The retained internal return at (-7,17,-30) is not exterior.");
        Require(horizontal.Blocks.Any(block => block.Origin == BlockOrigin.Smoothing &&
                block.Position == (-7, 17, -30) && block.Rotation == 0),
            "The guard removed the legitimate exterior internal return at (-7,17,-30).");

        // 4. Not count-only: every raw internal return that survives is exterior, and the
        //    reproduced hull still has some, so the guard did not simply delete the path.
        var internalRaw = raw.Where(block => block.Rotation is 0 or 2).ToArray();
        Require(internalRaw.Length > 0 && internalRaw.All(block =>
                block.OccupiedCells.All(cell => role(cell) == HullCellRole.Outside)),
            "A non-exterior internal return survived raw candidate discovery.");

        // 5. Repeated generation is deterministic.
        var repeated = generator.Generate(parameters with { Smoothing = SmoothingMethod.HorizontalSlopeFill });
        Require(horizontal.Blocks.SequenceEqual(repeated.Blocks) &&
                HashNativeSmoothing(horizontal.Blocks) == HashNativeSmoothing(repeated.Blocks),
            "Repeated Shape V2 horizontal generation is not deterministic.");

        // 6. The legacy pre-Shape-V2 contour path never needs the classifier and is unchanged;
        //    its exact 108-slope reference is still verified by the rest of this test.
        Require(none.RoleClassifier is not null && horizontal.RoleClassifier is not null,
            "A generated Shape V2 hull did not carry its original analytic role classifier.");
    }

    private static string Describe(BlockPlacement block)
    {
        var cells = string.Join(",", block.OccupiedCells.Select(cell => $"({cell.X},{cell.Y},{cell.Z})"));
        return $"shape={block.Shape} anchor=({block.X},{block.Y},{block.Z}) rotation={block.Rotation} " +
               $"material={block.Material} cells=[{cells}]";
    }

    private static string HashNativeSmoothing(IReadOnlyList<BlockPlacement> blocks)
    {
        var lines = blocks.Where(block => block.Origin == BlockOrigin.Smoothing).Select(block =>
        {
            var info = BlockShapeMetadata.Get(block.Shape);
            return $"{block.X},{block.Y},{block.Z}|{block.Material}|{block.Shape}|{info.Family}|" +
                   $"{info.Length}|{info.Mirrored}|{info.ShortLength}|{block.Rotation}|{block.Origin}";
        }).Order(StringComparer.Ordinal);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(string.Join("\n", lines)))).ToLowerInvariant();
    }

    private static void VerifyReference(GeneratedHull hull, BlockPlacement[] expected)
    {
        static (BlockShape, int, int, int, int) Key(BlockPlacement block) =>
            (block.Shape, block.X, block.Y, block.Z, block.Rotation);
        var slopes = hull.Blocks.Where(block => block.Origin == BlockOrigin.Smoothing).ToArray();
        var actualKeys = slopes.Select(Key).ToHashSet();
        var expectedKeys = expected.Select(Key).ToHashSet();
        Require(slopes.Length == expected.Length && actualKeys.SetEquals(expectedKeys),
            $"Horizontal reference mismatch. Missing: {string.Join("; ", expectedKeys.Except(actualKeys))}. " +
            $"Extra: {string.Join("; ", actualKeys.Except(expectedKeys))}.");
    }
}
