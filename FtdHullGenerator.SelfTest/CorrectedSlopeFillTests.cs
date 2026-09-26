using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Geometry;
using static SlopeFillTestSupport;

internal static class CorrectedSlopeFillTests
{
    public static IReadOnlyList<GeneratedHull> Run(HullGenerator generator)
    {
        var correctedHulls = new List<GeneratedHull>();
        using var fixture = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "corrected-fill-medium.json")));
        foreach (var testCase in fixture.RootElement.GetProperty("Cases").EnumerateArray())
        {
            var values = testCase.GetProperty("Parameters");
            var method = Enum.Parse<SmoothingMethod>(testCase.GetProperty("Method").GetString()!);
            var material = Enum.Parse<MaterialKind>(values.GetProperty("Material").GetString()!);
            var parameters = new HullParameters(
                values.GetProperty("Length").GetInt32(),
                values.GetProperty("Width").GetInt32(),
                values.GetProperty("Height").GetInt32(),
                values.GetProperty("BowFullness").GetDouble(),
                values.GetProperty("SternFullness").GetDouble(),
                values.GetProperty("CrossSectionCurve").GetDouble(),
                ArmorLayout.Single(material),
                values.GetProperty("HasDeck").GetBoolean() ? ArmorLayout.Single(material) : null,
                values.GetProperty("Beamify").GetBoolean());

            var baseline = generator.GenerateLegacyRegression(parameters);
            Require(baseline.BlockCount <= testCase.GetProperty("BaseBlockCount").GetInt32(),
                $"{Name(testCase)} beam packing increased the base block count.");
            Require(baseline.OccupiedCellCount == testCase.GetProperty("BaseOccupiedCellCount").GetInt32(),
                $"{Name(testCase)} base occupied-cell count changed.");
            Require(CellHash(baseline) == testCase.GetProperty("BaseCellSha256").GetString(),
                $"{Name(testCase)} no longer uses the base lattice reviewed by the user.");

            var filled = generator.GenerateLegacyRegression(parameters with { Smoothing = method });
            VerifyAdditive(baseline, filled);
            var expectedBounds = testCase.GetProperty("Bounds").EnumerateArray()
                .SelectMany(point => point.EnumerateArray().Select(value => value.GetInt32())).ToArray();
            Require(expectedBounds.SequenceEqual(
                [filled.MinX, filled.MinY, filled.MinZ, filled.MaxX, filled.MaxY, filled.MaxZ]),
                $"{Name(testCase)} bounds changed.");
            var slopes = filled.Blocks.Where(block => block.Origin == BlockOrigin.Smoothing).ToArray();
            Require(slopes.Length == testCase.GetProperty("ExpectedSlopeCount").GetInt32() &&
                    filled.BlockCount == baseline.BlockCount + slopes.Length,
                $"{Name(testCase)} slope count or additive placement identity changed.");
            Require(PlacementHash(slopes) == testCase.GetProperty("ExpectedSlopePlacementSha256").GetString(),
                $"{Name(testCase)} slope shapes, anchors, or rotations differ from the hand-corrected blueprint.");
            correctedHulls.Add(filled);
        }

        Console.WriteLine("Vertical and horizontal fill match all four hand-corrected medium references exactly.");
        return correctedHulls;
    }

    private static string Name(JsonElement testCase) => testCase.GetProperty("Name").GetString()!;

    private static string CellHash(GeneratedHull hull)
    {
        var text = new StringBuilder();
        foreach (var cell in hull.Blocks.SelectMany(block => block.OccupiedCells).Distinct()
                     .OrderBy(cell => cell.Z).ThenBy(cell => cell.Y).ThenBy(cell => cell.X))
            text.Append(cell.X.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(cell.Y.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(cell.Z.ToString(CultureInfo.InvariantCulture)).Append('\n');
        return Hash(text);
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
        return Hash(text);
    }

    private static string Hash(StringBuilder value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToString())));
}
