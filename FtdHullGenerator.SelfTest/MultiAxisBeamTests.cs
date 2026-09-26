using System.Diagnostics;
using System.Text.Json;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Geometry;
using FtdHullGenerator.Infrastructure;
using FtdHullGenerator.Serialization;

internal static class MultiAxisBeamTests
{
    public static void Run(HullGenerator generator, FtdBlockCatalog catalog)
    {
        CrosswiseMirrorsAndCenterline();
        VerticalRunsAndMetadataBarriers();
        WholeHullRoundTrip(generator, catalog);
        Console.WriteLine("Multi-axis beams: mirrored X/Y anchors and rotations, metadata barriers, normalized hull cells and native export passed.");
    }

    private static void CrosswiseMirrorsAndCenterline()
    {
        var source = Enumerable.Range(1, 4).SelectMany(offset => new[]
        {
            Cube(-offset, 0, 0), Cube(offset, 0, 0),
        }).Append(Cube(0, 0, 0)).ToArray();
        var packed = BeamOptimizer.PackRemainingSolidCells(source, -4, 4);
        Require(packed.Count == 3, "A four-cell run on each side did not become one beam per side.");
        Require(packed.Any(block => block.Shape == BlockShape.Beam4 && block.X == -4 && block.Rotation == 1) &&
                packed.Any(block => block.Shape == BlockShape.Beam4 && block.X == 4 && block.Rotation == 3) &&
                packed.Any(block => block.Shape == BlockShape.Cube && block.X == 0),
            "Crosswise beam anchors, rotations, or centerline split are wrong.");
        Require(Cells(packed).SetEquals(Cells(source)), "Crosswise packing changed occupied cells.");
    }

    private static void VerticalRunsAndMetadataBarriers()
    {
        var source = Enumerable.Range(0, 4).SelectMany(y => new[]
        {
            Cube(-2, y, 0), Cube(2, y, 0),
        }).ToArray();
        var packed = BeamOptimizer.PackRemainingSolidCells(source, -2, 2);
        Require(packed.Count == 2 && packed.All(block =>
                block.Shape == BlockShape.Beam4 && block.Rotation == 8 && block.Y == 0),
            "Vertical four-cell runs did not become native 4 m beams.");
        Require(Cells(packed).SetEquals(Cells(source)), "Vertical packing changed occupied cells.");

        var barrier = new[]
        {
            Cube(-4, 0, 0), Cube(-3, 0, 0),
            Cube(-2, 0, 0) with { ArmorDepth = 1 },
            Cube(-1, 0, 0) with { ArmorRegion = ArmorRegion.Bottom },
            Cube(1, 0, 0) with { Material = MaterialKind.Wood },
            Cube(2, 0, 0) with { Origin = BlockOrigin.Superstructure },
            Cube(3, 0, 0) with { Construction = ArmorConstruction.Pole },
            Cube(4, 0, 0) with { Shape = BlockShape.Slope1, Origin = BlockOrigin.Smoothing },
        };
        var bounded = BeamOptimizer.PackRemainingSolidCells(barrier, -4, 4);
        Require(bounded.Count == barrier.Length - 1 &&
                bounded.Any(block => block.Shape == BlockShape.Beam2 && block.X == -4 && block.Rotation == 1),
            "Crosswise packing crossed material, ownership, depth, construction or fitted-piece barriers.");
    }

    private static void WholeHullRoundTrip(HullGenerator generator, FtdBlockCatalog catalog)
    {
        var parameters = HullParameters.Default with { Length = 18, Width = 9, Height = 8 };
        var source = generator.Generate(parameters with { Beamify = false });
        var longitudinal = BeamOptimizer.Merge(source.Blocks, mergeFlattenedShellCandidates: true);
        var packingTimer = Stopwatch.StartNew();
        var crossPacked = BeamOptimizer.PackRemainingSolidCells(longitudinal, source.MinX, source.MaxX);
        packingTimer.Stop();
        var packed = generator.Generate(parameters);
        Require(crossPacked.SequenceEqual(packed.Blocks),
            "The direct crosswise pass differs from the generated hull's native placements.");
        Require(packed.Blocks.Count <= longitudinal.Count,
            "Multi-axis packing increased the native placement count.");
        Require(Normalized(packed.Blocks).SetEquals(Normalized(longitudinal)),
            "Multi-axis packing changed material, structural family, origin or armor ownership in occupied cells.");
        Require(longitudinal.Where(block => block.Shape != BlockShape.Cube ||
                block.Origin != BlockOrigin.Shell || block.Construction != ArmorConstruction.Solid ||
                block.UsePoles).All(packed.Blocks.Contains),
            "Multi-axis packing changed a pre-existing native member or a non-solid placement.");
        Require(HullGeometryValidator.Validate(packed).Count == 0,
            "A multi-axis packed hull failed whole-hull validation.");
        var rotated = packed.Blocks.Where(block =>
            block.Shape is BlockShape.Beam2 or BlockShape.Beam3 or BlockShape.Beam4 &&
            block.Rotation is 1 or 3 or 8).ToArray();
        Require(rotated.Length > 0, "The whole-hull case did not exercise a rotated beam.");
        Console.WriteLine($"Multi-axis sample: {source.Blocks.Count} cubes/fitted placements -> " +
            $"{longitudinal.Count} longitudinal placements -> {packed.Blocks.Count} multi-axis placements; " +
            $"crosswise and vertical packing {packingTimer.Elapsed.TotalMilliseconds:F2} ms.");

        var destination = Path.Combine(Path.GetTempPath(), $"HullForgeMultiAxis-{Guid.NewGuid():N}");
        try
        {
            var export = new BlueprintExporter().Export(packed, catalog, destination, "Multi axis beams");
            using var json = JsonDocument.Parse(File.ReadAllText(export.FilePath));
            var craft = json.RootElement.GetProperty("Blueprint");
            var rotations = craft.GetProperty("BLR").EnumerateArray()
                .Select(value => value.GetInt32()).ToHashSet();
            Require(rotated.All(block => rotations.Contains(block.Rotation)),
                "Export did not retain the rotated native beam BLR values.");
        }
        finally
        {
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        }
    }

    private static BlockPlacement Cube(int x, int y, int z) =>
        new(BlockShape.Cube, MaterialKind.Metal, x, y, z, 0)
        {
            Origin = BlockOrigin.Shell,
            Construction = ArmorConstruction.Solid,
            ArmorRegion = ArmorRegion.Side,
        };

    private static HashSet<(int X, int Y, int Z)> Cells(IEnumerable<BlockPlacement> blocks) =>
        blocks.SelectMany(block => block.OccupiedCells).ToHashSet();

    private static HashSet<string> Normalized(IEnumerable<BlockPlacement> blocks) =>
        blocks.SelectMany(block => block.OccupiedCells.Select(cell =>
            $"{cell.X},{cell.Y},{cell.Z}|{block.Material}|{BlockShapeMetadata.Get(block.Shape).Family}|" +
            $"{block.Origin}|{block.ArmorDepth}|{block.Construction}|{block.ArmorRegion}")).ToHashSet();

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
