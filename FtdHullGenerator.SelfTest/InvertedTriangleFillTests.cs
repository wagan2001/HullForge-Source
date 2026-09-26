using System.Diagnostics;
using System.Text.Json;
using FtdHullGenerator;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Geometry;
using FtdHullGenerator.Infrastructure;
using FtdHullGenerator.Serialization;
using static SlopeFillTestSupport;

// The rejected corner/cube refit is not an output oracle. These checks exercise the
// complete construction contract; native geometry expectations are independently pinned.
internal static class InvertedTriangleFillTests
{
    public static GeneratedHull Run(HullGenerator generator)
    {
        NativeConstructionGeometryTests.Run();
        SurfacePatchMetricTests.Run();
        SurfaceConstructionPlanTests.Run();
        var parameters = HullParameters.Default with
        {
            Length = 96, Width = 23, Height = 14,
            Beamify = false, Smoothing = SmoothingMethod.InvertedTriangleFill,
        };
        var generated = generator.Generate(parameters);
        VerifyContour(generator.Generate(parameters with { Smoothing = SmoothingMethod.None }), generated);
        Require(generated.Blocks.Any(block => block.Origin == BlockOrigin.Smoothing && IsInverse(block.Shape)),
            "The representative hull produced no inverted construction; unchanged output does not establish coverage.");
        Require(generated.Blocks.Any(block => block.Origin == BlockOrigin.Smoothing &&
                block.Shape is BlockShape.Slope1 or BlockShape.Slope2 or BlockShape.Slope3 or BlockShape.Slope4),
            "The representative hull produced no ordinary horizontal runs alongside inverted construction.");
        VerifyFittedSupport(generated);
        Require(generator.Generate(parameters).Blocks.SequenceEqual(generated.Blocks),
            "Inverted construction changed placement ordering or geometry between identical requests.");
        var beamified = generator.Generate(parameters with { Beamify = true });
        Require(Cells(generated.Blocks).SetEquals(Cells(beamified.Blocks)),
            "Beamification changed inverted construction's occupied cells.");
        Require(generated.Blocks.Where(block => block.Origin == BlockOrigin.Smoothing).ToHashSet()
                .SetEquals(beamified.Blocks.Where(block => block.Origin == BlockOrigin.Smoothing)),
            "Beamification changed a fitted assembly, including one-metre parts.");
        Require(MainWindow.CreateBlueprintName(parameters) ==
                MainWindow.CreateBlueprintName(parameters with { Smoothing = SmoothingMethod.None }),
            "Inverted construction leaked into the blueprint name.");

        var checkedCases = 0;
        foreach (var style in Enum.GetValues<BodyStyle>().Where(style => style != BodyStyle.Custom))
        foreach (var width in new[] { 20, 21 })
        {
            var variant = parameters with
            {
                Length = 64, Width = width, Height = 13,
                Shape = HullShapeSettings.Default with { Body = BodyShapeSettings.ForStyle(style) },
            };
            VerifyContour(generator.Generate(variant with { Smoothing = SmoothingMethod.None }), generator.Generate(variant));
            checkedCases++;
        }

        foreach (var variant in ConstructionCases(parameters))
        {
            var basis = generator.Generate(variant with { Smoothing = SmoothingMethod.None });
            var actual = generator.Generate(variant);
            VerifyContour(basis, actual);
            VerifyFittedSupport(actual);
            Require(actual.Blocks.Where(block => block.Origin == BlockOrigin.Smoothing)
                    .All(block => block.ArmorDepth == 0 && !block.UsePoles),
                "A fitted assembly replaced internal armor or a pole-based surface.");
            var depthMaterials = basis.Blocks.Where(block => block.ArmorDepth > 0)
                .Select(block => (block.ArmorDepth, block.Material, block.UsePoles)).ToHashSet();
            Require(actual.Blocks.Where(block => block.ArmorDepth > 0)
                    .Select(block => (block.ArmorDepth, block.Material, block.UsePoles)).ToHashSet().SetEquals(depthMaterials),
                "Construction changed or lost an internal armor depth/material/pole layer.");
            checkedCases++;
        }

        var poles = parameters with
        {
            HullArmor = new ArmorLayout([new ArmorLayer(MaterialKind.Metal, usePoles: true)]),
            DeckArmor = null, Beamify = true,
        };
        var poleHull = generator.Generate(poles);
        Require(!poleHull.Blocks.Any(block => block.Origin == BlockOrigin.Smoothing),
            "A pole-based outer surface was rewritten as inverted construction.");
        VerifyContour(generator.Generate(poles with { Smoothing = SmoothingMethod.None }), poleHull);
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            var observed = false;
            try { generator.Generate(parameters, cancellation.Token); }
            catch (OperationCanceledException) { observed = true; }
            Require(observed, "Inverted construction ignored cancellation.");
        }
        var timer = Stopwatch.StartNew();
        var large = generator.Generate(parameters with { Length = 300, Width = 60, Height = 30, Beamify = true });
        timer.Stop();
        Require(HullGeometryValidator.Validate(large).Count == 0,
            "The 300 m inverted construction failed geometry validation.");
        Require(timer.Elapsed < TimeSpan.FromSeconds(2),
            $"300 m inverted construction took {timer.Elapsed.TotalMilliseconds:N0} ms.");
        Console.WriteLine($"Inverted construction: {checkedCases} shape/armor cases; " +
                          $"{generated.Blocks.Count(block => block.Origin == BlockOrigin.Smoothing):N0} fitted pieces; " +
                          $"300 m {timer.Elapsed.TotalMilliseconds:N0} ms.");
        return generated;
    }

    public static void VerifyContour(GeneratedHull baseline, GeneratedHull actual)
    {
        var errors = HullGeometryValidator.Validate(actual);
        Check(errors.Count == 0, string.Join("; ", errors));
        try { ConstructionSkinTests.VerifyOriginalSkin(baseline, actual); }
        catch (InvalidOperationException exception)
        { throw new InvalidOperationException($"{exception.Message} {CaseDescription()}", exception); }
        Check((baseline.MinX, baseline.MaxX, baseline.MinY, baseline.MaxY, baseline.MinZ, baseline.MaxZ) ==
                (actual.MinX, actual.MaxX, actual.MinY, actual.MaxY, actual.MinZ, actual.MaxZ),
            $"Inverted construction changed the pinned overall extents: " +
            $"before=({baseline.MinX},{baseline.MaxX},{baseline.MinY},{baseline.MaxY},{baseline.MinZ},{baseline.MaxZ}); " +
            $"after=({actual.MinX},{actual.MaxX},{actual.MinY},{actual.MaxY},{actual.MinZ},{actual.MaxZ}).");
        var originalRows = Rows(baseline);
        var revisedRows = Rows(actual);
        if (!originalRows.Keys.ToHashSet().SetEquals(revisedRows.Keys))
        {
            var added = revisedRows.Keys.Except(originalRows.Keys).OrderBy(row => row.Z).ThenBy(row => row.Y).ToArray();
            var removed = originalRows.Keys.Except(revisedRows.Keys).OrderBy(row => row.Z).ThenBy(row => row.Y).ToArray();
            Check(false, "Inverted construction created or removed a sampled height/station row. " +
                $"Added {added.Length}: {string.Join("; ", added.Take(16).Select(row => $"{row} x={revisedRows[row]}"))}. " +
                $"Removed {removed.Length}: {string.Join("; ", removed.Take(16).Select(row => $"{row} x={originalRows[row]}"))}.");
        }
        foreach (var (key, before) in originalRows)
        {
            var after = revisedRows[key];
            Check(Math.Abs(before.Min - after.Min) <= 1 && Math.Abs(before.Max - after.Max) <= 1,
                $"Construction moved row {key} by more than its single one-metre allowance: {before} -> {after}.");
            if (before.Min == baseline.MinX || before.Max == baseline.MaxX)
                Check(before == after, $"Construction moved a maximum-width landmark at {key}: {before} -> {after}.");
        }
        foreach (var station in originalRows.Keys.GroupBy(key => key.Z))
        {
            var floor = station.Min(key => key.Y);
            var deck = station.Max(key => key.Y);
            Check(originalRows[(floor, station.Key)] == revisedRows[(floor, station.Key)] &&
                    originalRows[(deck, station.Key)] == revisedRows[(deck, station.Key)],
                $"Construction changed flat-bottom limits or the deck rim at station {station.Key}: " +
                $"floor y={floor}, {originalRows[(floor, station.Key)]} -> {revisedRows[(floor, station.Key)]}; " +
                $"deck y={deck}, {originalRows[(deck, station.Key)]} -> {revisedRows[(deck, station.Key)]}.");
        }

        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException($"{message} {CaseDescription()}");
        }
        string CaseDescription()
        {
            var p = actual.Parameters;
            return $"Case: {p.BowStyle}/{p.SternStyle}; {p.Length}x{p.Width}x{p.Height}; " +
                   $"bulb={p.HasBulb} {p.EffectiveBulb}; deck={p.HasDeck}; beamify={p.Beamify}; " +
                   $"shape={p.EffectiveShape}; sideArmor={p.HullArmor}; bottomArmor={p.EffectiveBottomArmor}; " +
                   $"deckArmor={p.DeckArmor}; notes={string.Join("; ", actual.ConstructionNotes)}.";
        }
    }

    public static void VerifyExport(GeneratedHull hull, FtdBlockCatalog catalog, string directory)
    {
        foreach (var material in Enum.GetValues<MaterialKind>())
        foreach (var shape in Enum.GetValues<BlockShape>().Where(shape => shape is not
                     (BlockShape.Cube or BlockShape.Beam2 or BlockShape.Beam3 or BlockShape.Beam4 or
                      BlockShape.Pole1 or BlockShape.Pole2 or BlockShape.Pole3 or BlockShape.Pole4 or
                      BlockShape.Corner or BlockShape.InverseCorner)))
            Require(!catalog.Resolve(material, shape).IsFallback,
                $"The installed native construction vocabulary omitted {material} {shape}.");
        foreach (var block in hull.Blocks.Where(block => block.Origin == BlockOrigin.Smoothing))
            Require(!catalog.Resolve(block.Material, block.Shape).IsFallback,
                $"The installed catalog did not resolve fitted {block.Material} {block.Shape}.");
        var export = new BlueprintExporter().Export(hull, catalog, directory, "Inverted construction reference");
        Require(export.ShapeFallbackCount == 0, "Inverted construction exported a part as fallback cubes.");
        using var document = JsonDocument.Parse(File.ReadAllText(export.FilePath));
        var root = document.RootElement;
        var craft = root.GetProperty("Blueprint");
        var items = root.GetProperty("ItemDictionary").EnumerateObject()
            .ToDictionary(entry => int.Parse(entry.Name), entry => Guid.Parse(entry.Value.GetString()!));
        var definitions = catalog.Blocks.ToDictionary(block => block.Guid);
        var placements = new List<BlockPlacement>();
        for (var index = 0; index < craft.GetProperty("BLP").GetArrayLength(); index++)
        {
            var xyz = craft.GetProperty("BLP")[index].GetString()!.Split(',').Select(int.Parse).ToArray();
            var definition = definitions[items[craft.GetProperty("BlockIds")[index].GetInt32()]];
            placements.Add(new BlockPlacement(definition.Shape, definition.Material, xyz[0], xyz[1], xyz[2],
                craft.GetProperty("BLR")[index].GetInt32()));
        }
        var expanded = placements.SelectMany(block => block.OccupiedCells).ToArray();
        Require(expanded.Length == expanded.Distinct().Count() && expanded.ToHashSet().SetEquals(Cells(hull.Blocks)),
            "Inverted construction export lost, added, or overlapped occupied cells.");
        static (BlockShape, MaterialKind, int, int, int, int) Key(BlockPlacement block) =>
            (block.Shape, block.Material, block.X, block.Y, block.Z, block.Rotation);
        Require(placements.Select(Key).ToHashSet().SetEquals(hull.Blocks.Select(Key)),
            "Inverted construction export changed a shape, material, anchor, or rotation.");
        Require(craft.GetProperty("MinCords").GetString() == $"{hull.MinX},{hull.MinY},{hull.MinZ}" &&
                craft.GetProperty("MaxCords").GetString() == $"{hull.MaxX},{hull.MaxY},{hull.MaxZ}",
            "Inverted construction export bounds disagree with actual occupied cells.");
        var emptyCatalogRoot = Path.Combine(directory, "empty-game-catalog");
        Directory.CreateDirectory(Path.Combine(emptyCatalogRoot, "From_The_Depths_Data", "StreamingAssets"));
        var missingCatalog = FtdBlockCatalog.Load(emptyCatalogRoot);
        var failedOutput = Path.Combine(directory, "missing-fitted-parts-output");
        var rejected = false;
        try { new BlueprintExporter().Export(hull, missingCatalog, failedOutput, "Must not be written"); }
        catch (InvalidOperationException exception) when (exception.Message.Contains("catalog is missing", StringComparison.OrdinalIgnoreCase))
        { rejected = true; }
        Require(rejected && !Directory.Exists(failedOutput),
            "Missing fitted parts did not fail export before creating the output directory.");
    }

    private static Dictionary<(int Y, int Z), (int Min, int Max)> Rows(GeneratedHull hull) =>
        Cells(hull.Blocks).GroupBy(cell => (cell.Y, cell.Z))
            .ToDictionary(row => row.Key, row => (row.Min(cell => cell.X), row.Max(cell => cell.X)));

    private static void VerifyFittedSupport(GeneratedHull hull)
    {
        var owners = hull.Blocks.SelectMany(block => block.OccupiedCells.Select(cell => (cell, block)))
            .ToDictionary(pair => pair.cell, pair => pair.block);
        foreach (var block in hull.Blocks.Where(block => block.Origin == BlockOrigin.Smoothing))
        {
            var neighbors = block.OccupiedCells.SelectMany(cell => BlockRotations.AxisDirections.Select(direction =>
                    (cell.X + direction.X, cell.Y + direction.Y, cell.Z + direction.Z)))
                .Where(owners.ContainsKey).Select(cell => owners[cell]).Where(other => other != block).Distinct();
            Require(neighbors.Any(other => StructuralShapeGeometry.TouchingArea(block, other) > 1e-6),
                $"Fitted {block.Shape} at {block.Position} has only empty, edge, or point contact.");
        }
    }

    private static IEnumerable<HullParameters> ConstructionCases(HullParameters basis)
    {
        yield return basis with { Length = 12, Width = 9, Height = 7 };
        yield return basis with { DeckArmor = null };
        yield return basis with
        {
            HullArmor = new ArmorLayout([MaterialKind.Metal, MaterialKind.HeavyArmor, MaterialKind.Rubber]),
            BottomArmor = new ArmorLayout([MaterialKind.Metal, MaterialKind.Lead]),
            DeckArmor = new ArmorLayout([MaterialKind.Wood, MaterialKind.LightweightAlloy]),
        };
        yield return basis with
        {
            HullArmor = new ArmorLayout([new ArmorLayer(MaterialKind.Metal), ArmorLayer.Air,
                new ArmorLayer(MaterialKind.HeavyArmor, usePoles: true)]),
            DeckArmor = null, Beamify = true,
        };
        yield return basis with
        {
            Shape = basis.EffectiveShape with { Profile = new HullProfileSettings(3, 2, 4, 3) },
            DeckArmor = new ArmorLayout([MaterialKind.Metal, MaterialKind.Wood]),
        };
        yield return HullShapePreset.FindByName("Orca")!.Apply(basis);
        yield return HullShapePreset.FindByName("Narwhal")!.Apply(basis);
    }

    private static bool IsInverse(BlockShape shape) => shape is
        BlockShape.InverseCornerLeft or BlockShape.InverseCornerLeft2 or
        BlockShape.InverseCornerLeft3 or BlockShape.InverseCornerLeft4 or
        BlockShape.InverseCornerRight or BlockShape.InverseCornerRight2 or
        BlockShape.InverseCornerRight3 or BlockShape.InverseCornerRight4;
}
