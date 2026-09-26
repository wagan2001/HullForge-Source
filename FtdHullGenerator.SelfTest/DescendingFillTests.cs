using FtdHullGenerator.Domain;
using FtdHullGenerator.Geometry;
using FtdHullGenerator.Geometry.Smoothing;
using FtdHullGenerator.Infrastructure;
using static SlopeFillTestSupport;

internal static class DescendingFillTests
{
    public static void Run(HullGenerator generator, FtdBlockCatalog catalog, bool full)
    {
        // Reflect an independent stair fixture in Y. Every accepted wedge must
        // reflect as an entire envelope, including its multi-cell anchor axis.
        var edges = new[] { 2, 2, 4, 4, 5, 5, 7, 7 };
        var blocks = new List<BlockPlacement>();
        for (var x = -1; x <= 1; x++)
        for (var y = 0; y < edges.Length; y++)
        for (var z = 0; z <= (x == 0 ? 8 : edges[y]); z++)
            blocks.Add(new(BlockShape.Cube, MaterialKind.Metal, x, y, z, 0));
        var basis = new GeneratedHull(HullParameters.Default with { Height = 8, Width = 3, Length = 9 },
            blocks, -1, 1, 0, 7, 0, 8);
        var reflected = basis with { Blocks = blocks.Select(b => b with { Y = 7 - b.Y }).ToArray() };
        var upward = new VerticalSlopeFillPass().Apply(basis);
        var downward = new VerticalSlopeFillPass().Apply(reflected);
        var expected = upward.Blocks.Where(b => b.Origin == BlockOrigin.Smoothing)
            .Select(b => b with { Y = 7 - b.Y, Rotation = b.Rotation switch
                { 12 => 0, 14 => 2, 4 => 8, 6 => 10, _ => throw new Exception("Unexpected fixture orientation.") } }).ToHashSet();
        var actual = downward.Blocks.Where(b => b.Origin == BlockOrigin.Smoothing).ToHashSet();
        Require(expected.Count > 0 && expected.SetEquals(actual),
            "Descending fill is not the exact vertical reflection of the ascending stair fixture.");
        Require(actual.Any(b => b.Rotation == 0) && actual.Any(b => b.Rotation == 8),
            "Descending fixture did not exercise both longitudinal and vertical slopes.");
        VerifyAdditive(reflected, downward);
        var sternBasis = reflected with { Blocks = reflected.Blocks.Select(b => b with { Z = 8 - b.Z }).ToArray() };
        var sternFill = new VerticalSlopeFillPass().Apply(sternBasis);
        var sternExpected = actual.Select(b => b with { Z = 8 - b.Z, Rotation = b.Rotation == 0 ? 2 : 10 }).ToHashSet();
        Require(sternExpected.SetEquals(sternFill.Blocks.Where(b => b.Origin == BlockOrigin.Smoothing)),
            "Descending stern wedges are not the longitudinal reflection of the bow wedges.");
        VerifyAdditive(sternBasis, sternFill);

        var parameters = HullParameters.Default with
        {
            BowStyle = BowStyle.Raked,
            Shape = HullShapeSettings.Default with { Bow = HullShapeSettings.Default.Bow with { Flare = -0.9 } },
        };
        var baseHull = generator.Generate(parameters);
        var filled = generator.Generate(parameters with { Smoothing = SmoothingMethod.VerticalSlopeFill });
        Require(filled.Blocks.Any(b => b.Origin == BlockOrigin.Smoothing && b.Rotation is 0 or 8),
            "Negative-flare raked bow has no descending wedges.");
        VerifyAdditive(baseHull, filled);
        var rows = Cells(baseHull.Blocks).GroupBy(c => c.X).ToDictionary(g => g.Key,
            g => g.GroupBy(c => c.Y).ToDictionary(r => r.Key, r => r.Max(c => c.Z)));
        foreach (var b in filled.Blocks.Where(b => b.Origin == BlockOrigin.Smoothing && b.Rotation == 0))
            Require(rows[b.X].TryGetValue(b.Y - 1, out var below) && below > rows[b.X][b.Y],
                "A downward bow tread was emitted on an ascending contour.");
        foreach (var method in new[] { SmoothingMethod.VerticalSlopeFill, SmoothingMethod.CombinedSlopeFill, SmoothingMethod.HybridSlopeFill })
        foreach (var beamify in new[] { false, true })
        foreach (var width in new[] { 21, 22 })
            VerifyAdditive(generator.Generate(parameters with { Beamify = beamify, Width = width }),
                generator.Generate(parameters with { Smoothing = method, Beamify = beamify, Width = width }));

        var large = HullEditorSettings.Default with { Length = 240, Width = 105, Height = 54 };
        if (full)
        {
            var random = new Random(870);
            for (var index = 0; index < 4; index++)
            {
                var choice = HullEditorSettings.RandomizeShape(large, random);
                var hull = generator.Generate(choice);
                Require(choice.Length == 240 && choice.Width == 105 && choice.Height == 54 &&
                        hull.OccupiedLength >= choice.Length,
                    "Randomizer rejected or resized an unlocked hull.");
            }
        }
        var directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "HullForge-Descending-" + Guid.NewGuid().ToString("N")));
        try { VerifyExport(filled, catalog, directory, filled.Blocks.Count(b => b.Origin == BlockOrigin.Smoothing)); }
        finally
        {
            var prefix = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "HullForge-Descending-");
            if (directory.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        Console.WriteLine(full
            ? "Descending fill: reflected stair envelopes, negative-flare raked bow, beam/smoothing variants, export rotations, and unlocked randomizer passed."
            : "Descending fill: reflected stair envelopes, negative-flare raked bow, representative smoothing, and export rotations passed.");
    }
}
