using System.Text.Json;
using static SlopeFillTestSupport;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Geometry;
using FtdHullGenerator.Geometry.Smoothing;

internal static class VerticalSlopeFillTests
{
    public static GeneratedHull Run(HullGenerator generator, bool full)
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "vertical-fill-40x15x11.json")));
        BlockPlacement[] Read(string name) => fixture.RootElement.GetProperty(name).EnumerateArray()
            .Select(row => new BlockPlacement(Enum.Parse<BlockShape>(row[4].GetString()!), MaterialKind.Metal,
                row[0].GetInt32(), row[1].GetInt32(), row[2].GetInt32(), row[3].GetInt32())).ToArray();
        var referenceBase = Read("baseBlocks");
        var expected = Read("slopes");
        var parameters = HullParameters.Default with
        {
            Length = 40, Width = 15, Height = 11, BowFullness = 0.54,
            SternFullness = 0, CrossSectionCurve = 0.35, Beamify = true,
            Shape = null,
        };
        var baseline = generator.GenerateLegacyRegression(parameters);
        Require(Cells(baseline.Blocks).SetEquals(Cells(referenceBase)),
            "The small generated base no longer matches the independent handmade fixture.");
        var filled = generator.GenerateLegacyRegression(parameters with { Smoothing = SmoothingMethod.VerticalSlopeFill });
        VerifyAdditive(baseline, filled);
        Require(expected.Length == 86, "The reference must retain all 86 hand-placed slopes.");
        VerifyReference(filled, expected);

        // Pin the wedge equivalence used by comparison, rather than treating all
        // rotations as interchangeable. These six envelope vertices come from
        // the installed Wood down slope 1m.obj used by the Metal item as well.
        Require(Wedge(4).SetEquals(Wedge(12)) && Wedge(6).SetEquals(Wedge(14)),
            "The 1m tread/riser rotation equivalence changed.");
        Require(!Wedge(12).SetEquals(Wedge(14)), "Bow and stern wedges must remain distinct.");
        Require(!Key(new(BlockShape.Slope2, MaterialKind.Metal, 0, 0, 0, 4)).Equals(
            Key(new(BlockShape.Slope2, MaterialKind.Metal, 0, 0, 0, 12))),
            "Multi-cell treads and risers must not be canonicalized together.");

        // The rule must follow geometry, not fixture coordinates or a hardcoded
        // bow/stern station. Reflect Z, translate all axes, and reverse input order.
        BlockPlacement Transform(BlockPlacement block) => block with
        {
            X = block.X + 17, Y = block.Y + 9, Z = 31 - block.Z,
            Rotation = block.Rotation switch { 12 => 14, 14 => 12, 4 => 6, 6 => 4, _ => block.Rotation },
        };
        // Expand the beams before reflection: their anchor would otherwise need
        // moving to preserve their occupied cells with rotation 0.
        var transformedBase = baseline with
        {
            Blocks = Cells(referenceBase).Select(cell => Transform(new BlockPlacement(
                BlockShape.Cube, MaterialKind.Metal, cell.X, cell.Y, cell.Z, 0))).Reverse().ToArray(),
            MinX = 10, MaxX = 24, MinY = 9, MaxY = 19, MinZ = 12, MaxZ = 51,
        };
        var transformed = new VerticalSlopeFillPass().Apply(transformedBase);
        VerifyAdditive(transformedBase, transformed);
        VerifyReference(transformed, expected.Select(Transform).ToArray());
        Require(new VerticalSlopeFillPass().Apply(baseline with { Blocks = baseline.Blocks.Reverse().ToArray() })
            .Blocks.SequenceEqual(filled.Blocks), "Vertical fill depends on input enumeration order.");

        VerifyReportedShark(generator);

        if (full)
            VerifyVariants(generator, parameters, SmoothingMethod.VerticalSlopeFill);
        return filled;
    }

    private static void VerifyReportedShark(HullGenerator generator)
    {
        var parameters = HullParameters.Default with
        {
            Length = 70, Width = 49, Height = 20,
            BowFullness = -0.25, SternFullness = 0.10, CrossSectionCurve = 0.10,
            Beamify = true,
            Shape = null,
        };
        var baseline = generator.GenerateLegacyRegression(parameters);
        var filled = generator.GenerateLegacyRegression(parameters with { Smoothing = SmoothingMethod.VerticalSlopeFill });
        VerifyAdditive(baseline, filled);

        var expectedPortTreads = new[]
        {
            new BlockPlacement(BlockShape.Slope4, MaterialKind.Metal, -9, 7, -1, 12),
            new BlockPlacement(BlockShape.Slope3, MaterialKind.Metal, -18, 14, -3, 12),
            new BlockPlacement(BlockShape.Slope3, MaterialKind.Metal, -17, 14, -1, 12),
            new BlockPlacement(BlockShape.Slope3, MaterialKind.Metal, -23, 18, -4, 12),
            new BlockPlacement(BlockShape.Slope4, MaterialKind.Metal, -19, 15, -14, 14),
        };
        var slopes = filled.Blocks.Where(block => block.Origin == BlockOrigin.Smoothing).ToArray();
        var actualKeys = slopes.Select(Key).ToHashSet();
        Require(expectedPortTreads.All(block => actualKeys.Contains(Key(block))),
            "Vertical fill still omits one of the four bow-facing or one stern-facing Shark treads.");
        Require(slopes.Length == 580,
            $"The reported Shark must contain 580 mirrored smoothing slopes, not {slopes.Length}.");
    }

    private static void VerifyReference(GeneratedHull hull, BlockPlacement[] expected)
    {
        var slopes = hull.Blocks.Where(block => block.Origin == BlockOrigin.Smoothing).ToArray();
        var actualKeys = slopes.Select(Key).ToHashSet();
        var expectedKeys = expected.Select(Key).ToHashSet();
        Require(slopes.Length == expected.Length && actualKeys.SetEquals(expectedKeys),
            $"Handmade contour mismatch. Missing: {string.Join("; ", expectedKeys.Except(actualKeys))}. " +
            $"Extra: {string.Join("; ", actualKeys.Except(expectedKeys))}.");
    }

    private static (BlockShape Shape, int X, int Y, int Z, int Rotation) Key(BlockPlacement block) =>
        (block.Shape, block.X, block.Y, block.Z, block.Shape == BlockShape.Slope1
            ? block.Rotation switch { 4 => 12, 6 => 14, _ => block.Rotation } : block.Rotation);

    private static HashSet<(int X, int Y, int Z)> Wedge(int rotation)
    {
        var axes = BlockRotations.GetRotationAxes(rotation);
        return (from x in new[] { -1, 1 }
                from yz in new[] { (Y: -1, Z: -1), (Y: -1, Z: 1), (Y: 1, Z: -1) }
                select (x * axes.Right.X + yz.Y * axes.Up.X + yz.Z * axes.Forward.X,
                        x * axes.Right.Y + yz.Y * axes.Up.Y + yz.Z * axes.Forward.Y,
                        x * axes.Right.Z + yz.Y * axes.Up.Z + yz.Z * axes.Forward.Z)).ToHashSet();
    }

}
