using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Geometry;

internal static class ManualCorrectionRegressionTests
{
    public static void Run(HullGenerator generator)
    {
        var side = new ArmorLayout([
            new ArmorLayer(MaterialKind.Metal),
            new ArmorLayer(MaterialKind.Metal, ArmorConstruction.BeamSlopeSpike),
            new ArmorLayer(MaterialKind.Metal),
        ]);
        var parameters = HullParameters.Default with
        {
            Length = 90,
            Width = 17,
            Height = 17,
            BowStyle = BowStyle.Raked,
            SternStyle = SternStyle.Transom,
            HullArmor = side,
            BottomArmor = new ArmorLayout([
                new ArmorLayer(MaterialKind.Metal),
                new ArmorLayer(MaterialKind.LightweightAlloy),
            ]),
            DeckArmor = new ArmorLayout([
                new ArmorLayer(MaterialKind.Wood),
                new ArmorLayer(MaterialKind.LightweightAlloy),
            ]),
            Beamify = true,
            Smoothing = SmoothingMethod.HybridSlopeFill,
            HybridFillOffset = 8,
            Shape = new HullShapeSettings(
                new BowShapeSettings(.90, -.90, 49),
                new BodyShapeSettings(BodyStyle.Custom, .90, -.78, .27),
                new SternShapeSettings(.33, -.77, 29),
                new HullProfileSettings(2, 2, 2, 2)),
        };

        var hull = generator.Generate(parameters);
        var occupied = hull.Blocks.SelectMany(block => block.OccupiedCells).ToHashSet();

        // These are the short contour and layer runs restored in the independently
        // hand-corrected HF_Custom_90x17x17 blueprint supplied on 2026-09-11.
        var repairedRuns = new[]
        {
            Run(7, 14, -27, -27), Run(7, 14, 13, 14), Run(6, 15, -36, -35),
            Run(5, 4, 8, 9), Run(4, 5, -33, -31), Run(3, 6, -39, -37),
        }.SelectMany(run => run)
         .SelectMany(cell => cell.X == 0 ? [cell] : new[] { cell, (-cell.X, cell.Y, cell.Z) })
         .ToArray();
        Require(repairedRuns.All(occupied.Contains),
            "The corrected contour or longitudinal armor runs reopened.");

        // The former regional connector drew transverse ribs across both full-body
        // interfaces. Their path seeds must remain cavity, not depth-zero armor.
        var oldRibSeeds = new[]
        {
            (5, 6, -19), (5, 12, -19), (4, 4, -19), (4, 14, -19),
            (2, 3, -19), (2, 4, -19), (1, 3, -19), (0, 2, -19), (0, 3, -19),
            (5, 6, 0), (4, 4, 0), (4, 14, 0), (2, 3, 0),
            (2, 4, 0), (1, 3, 0), (0, 2, 0), (0, 3, 0),
        }.SelectMany(cell => cell.Item1 == 0 ? [cell] : new[] { cell, (-cell.Item1, cell.Item2, cell.Item3) });
        Require(!oldRibSeeds.Any(occupied.Contains),
            "The regional shell connector rebuilt a transverse internal rib.");

        var smoothing = hull.Blocks.Where(block => block.Origin == BlockOrigin.Smoothing).ToArray();
        Require(smoothing.Length == 315 && PlacementHash(smoothing) ==
                "2BCEAD50E94B2CC9ACCCEEF6F50498ED6523368E8117F82C0A2E19DE2116D77A",
            "Hybrid smoothing no longer matches the hand-corrected shapes, anchors, and rotations.");

        Require(hull.BlockCount <= 3547 && hull.OccupiedCellCount <= 9932,
            "The corrected hull regained more construction than the hand-reviewed reference.");
        Require(HullGeometryValidator.Validate(hull).Count == 0,
            "The corrected regression hull is no longer structurally valid.");

        Console.WriteLine("Manual correction regression: contour/armor seams, all 315 smoothing placements, and body-interface cavities passed.");
    }

    private static IEnumerable<(int X, int Y, int Z)> Run(int x, int y, int firstZ, int lastZ)
    {
        for (var z = firstZ; z <= lastZ; z++)
            yield return (x, y, z);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static string PlacementHash(IEnumerable<BlockPlacement> blocks)
    {
        var text = new StringBuilder();
        foreach (var block in blocks.OrderBy(block => block.Z).ThenBy(block => block.Y)
                     .ThenBy(block => block.X).ThenBy(block => block.Shape).ThenBy(block => block.Rotation))
            text.Append(block.Shape).Append('|')
                .Append(block.X.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(block.Y.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(block.Z.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(block.Rotation.ToString(CultureInfo.InvariantCulture)).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
