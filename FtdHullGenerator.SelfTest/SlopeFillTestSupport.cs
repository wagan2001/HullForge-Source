using System.Text.Json;
using System.Collections.Concurrent;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Geometry;
using FtdHullGenerator.Infrastructure;
using FtdHullGenerator.Serialization;

internal static class SlopeFillTestSupport
{
    // Baselines are deterministic and reused across smoothing methods. Callers must treat
    // the cached GeneratedHull as read-only so later sweeps see an unmodified base.
    private static readonly ConcurrentDictionary<HullParameters, GeneratedHull> BaselineCache = new();

    public static void VerifyVariants(HullGenerator generator, HullParameters parameters, SmoothingMethod method)
    {
        var validatedVariants = 0;
        var decklessVariants = 0;
        var rejectedBaselines = 0;
        foreach (var width in new[] { 15, 18 })
        foreach (var deck in new[] { false, true })
        foreach (var beamify in new[] { false, true })
        foreach (var bow in new[] { -0.9, 0.9 })
        foreach (var stern in new[] { -0.9, 0.9 })
        foreach (var curve in new[] { -0.9, 0.35, 0.9 })
        {
            var variant = parameters with
            {
                Length = 64, Width = width, Height = 10,
                DeckArmor = deck ? ArmorLayout.Single(MaterialKind.Wood) : null, Beamify = beamify,
                BowFullness = bow, SternFullness = stern, CrossSectionCurve = curve,
                HullArmor = ArmorLayout.Single(MaterialKind.Wood),
                Shape = null,
            };
            var unsmoothed = variant with { Smoothing = SmoothingMethod.None };
            if (!BaselineCache.TryGetValue(unsmoothed, out var variantBase))
            {
                try
                {
                    variantBase = generator.GenerateLegacyRegression(unsmoothed);
                    BaselineCache.TryAdd(unsmoothed, variantBase);
                }
                catch (HullGenerationException exception) when (!deck && exception.Errors.SequenceEqual(
                           new[] { "The deckless hull did not produce a top opening." }))
                {
                    // Only skip this known failure of the unsmoothed base. Failures
                    // of the smoothing path below must always fail the test.
                    rejectedBaselines++;
                    continue;
                }
            }
            VerifyAdditive(variantBase, generator.GenerateLegacyRegression(variant with { Smoothing = method }));
            validatedVariants++;
            if (!deck) decklessVariants++;
        }
        Require(validatedVariants == 96 && decklessVariants == 48 && rejectedBaselines == 0,
            $"The contour sweep covered {validatedVariants} variants ({decklessVariants} deckless) " +
            $"with {rejectedBaselines} rejected bases instead of the pinned 96/48/0 matrix.");
        Console.WriteLine($"{method}: {validatedVariants} variants passed " +
                          $"({decklessVariants} deckless); {rejectedBaselines} unusable unsmoothed bases skipped.");
    }

    public static void VerifyExport(GeneratedHull hull, FtdBlockCatalog catalog, string directory, int expectedCount)
    {
        var export = new BlueprintExporter().Export(hull, catalog, directory, $"Small {hull.Parameters.Smoothing} regression");
        using var document = JsonDocument.Parse(File.ReadAllText(export.FilePath));
        var root = document.RootElement;
        var craft = root.GetProperty("Blueprint");
        var shapes = catalog.Blocks.ToDictionary(block => block.Guid, block => block.Shape);
        var items = root.GetProperty("ItemDictionary").EnumerateObject()
            .ToDictionary(entry => int.Parse(entry.Name), entry => Guid.Parse(entry.Value.GetString()!));
        var actual = new List<BlockPlacement>();
        var expanded = new List<(int X, int Y, int Z)>();
        for (var i = 0; i < craft.GetProperty("BLP").GetArrayLength(); i++)
        {
            var shape = shapes[items[craft.GetProperty("BlockIds")[i].GetInt32()]];
            var xyz = craft.GetProperty("BLP")[i].GetString()!.Split(',').Select(int.Parse).ToArray();
            var placement = new BlockPlacement(shape, hull.Parameters.SurfaceMaterial, xyz[0], xyz[1], xyz[2],
                craft.GetProperty("BLR")[i].GetInt32());
            expanded.AddRange(placement.OccupiedCells);
            if (shape is not (BlockShape.Slope1 or BlockShape.Slope2 or BlockShape.Slope3 or BlockShape.Slope4))
                continue;
            actual.Add(placement);
        }
        Require(expanded.Count == expanded.Distinct().Count() && expanded.ToHashSet().SetEquals(Cells(hull.Blocks)),
            "The exported contour hull lost, added, or overlapped occupied cells.");
        var expected = hull.Blocks.Where(block => block.Origin == BlockOrigin.Smoothing).ToArray();
        Require(export.SlopeCount == expectedCount && actual.Count == expected.Length &&
                actual.Select(block => (block.Shape, block.Position, block.Rotation)).ToHashSet()
                    .SetEquals(expected.Select(block => (block.Shape, block.Position, block.Rotation))),
            "Small contour export changed a slope's shape, anchor, or exact rotation.");
    }

    public static void VerifyAdditive(GeneratedHull baseline, GeneratedHull filled)
    {
        var errors = HullGeometryValidator.Validate(filled);
        Require(errors.Count == 0, string.Join("; ", errors));
        Require((baseline.MinX, baseline.MaxX, baseline.MinY, baseline.MaxY, baseline.MinZ, baseline.MaxZ) ==
                (filled.MinX, filled.MaxX, filled.MinY, filled.MaxY, filled.MinZ, filled.MaxZ), "Fill changed bounds.");
        Require(baseline.Blocks.ToHashSet().SetEquals(filled.Blocks.Where(block => block.Origin != BlockOrigin.Smoothing)),
            "Fill changed a base placement.");
        var baseCells = Cells(baseline.Blocks);
        var addedBlocks = filled.Blocks.Where(block => block.Origin == BlockOrigin.Smoothing).ToArray();
        var added = addedBlocks.SelectMany(block => block.OccupiedCells).ToArray();
        Require(added.All(cell => !baseCells.Contains(cell)), "Fill overlaps the base.");
        Require(addedBlocks.All(block => block.OccupiedCells.All(cell =>
                    baseCells.Contains((cell.X + (2 * cell.X < filled.MinX + filled.MaxX ? 1 : -1), cell.Y, cell.Z)) ||
                    block.Rotation is 12 or 14 && baseCells.Contains((cell.X, cell.Y + 1, cell.Z)) ||
                    block.Rotation is 0 or 2 && baseCells.Contains((cell.X, cell.Y - 1, cell.Z)) ||
                    block.Rotation is 4 or 8 && baseCells.Contains((cell.X, cell.Y, cell.Z - 1)) ||
                    block.Rotation is 6 or 10 && baseCells.Contains((cell.X, cell.Y, cell.Z + 1)))),
            "A contour cell has neither inboard nor verified transverse shell support.");
    }

    public static void VerifySmoothing(GeneratedHull baseline, GeneratedHull filled, SmoothingMethod method)
    {
        if (method != SmoothingMethod.InvertedTriangleFill)
        {
            VerifyAdditive(baseline, filled);
            return;
        }

        InvertedTriangleFillTests.VerifyContour(baseline, filled);
    }

    public static HashSet<(int X, int Y, int Z)> Cells(IEnumerable<BlockPlacement> blocks) =>
        blocks.SelectMany(block => block.OccupiedCells).ToHashSet();

    public static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
