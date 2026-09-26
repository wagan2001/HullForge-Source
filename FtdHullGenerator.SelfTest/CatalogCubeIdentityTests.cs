using System.Text.Json;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Geometry;
using FtdHullGenerator.Infrastructure;
using FtdHullGenerator.Serialization;

internal static class CatalogCubeIdentityTests
{
    private static readonly Guid WoodBlockGuid =
        Guid.Parse("9a0ae372-beb4-4009-b14e-36ed0715af73");
    private static readonly Guid MastTopGuid =
        Guid.Parse("f72b5a6e-081e-4889-b041-a90b2ca7ae4e");

    public static void Run(HullGenerator generator, FtdBlockCatalog installed)
    {
        Require(installed.Resolve(MaterialKind.Wood, BlockShape.Cube).Guid == WoodBlockGuid,
            "The installed catalog selected Mast Top or another variant as the Wood cube.");

        var root = Path.Combine(Path.GetTempPath(), $"HullForgeCubeCatalog-{Guid.NewGuid():N}");
        try
        {
            var items = Path.Combine(root, "From_The_Depths_Data", "StreamingAssets",
                "Mods", "Core_Structural", "Items");
            Directory.CreateDirectory(items);
            WriteItem("A-Wood Block.item", "Wood block", WoodBlockGuid);
            WriteItem("Z-Wood Block Variant.item", "Wood Block Variant", MastTopGuid);
            var synthetic = FtdBlockCatalog.Load(root);
            Require(synthetic.Resolve(MaterialKind.Wood, BlockShape.Cube).Guid == WoodBlockGuid,
                "A later visible Wood Block Variant replaced the ordinary cube.");

            var wood = ArmorLayout.Single(MaterialKind.Wood);
            var hull = generator.Generate(HullParameters.Default with
            {
                Length = 16, Width = 7, Height = 6,
                HullArmor = wood, BottomArmor = wood, DeckArmor = wood,
                Beamify = false, Smoothing = SmoothingMethod.None,
            });
            var export = new BlueprintExporter().Export(hull, synthetic, root, "Wood cube identity");
            using var json = JsonDocument.Parse(File.ReadAllText(export.FilePath));
            var itemGuids = json.RootElement.GetProperty("ItemDictionary")
                .EnumerateObject().Select(item => item.Value.GetString()).ToHashSet();
            Require(itemGuids.Contains(WoodBlockGuid.ToString()) &&
                    !itemGuids.Contains(MastTopGuid.ToString()),
                "Wood cube export referenced Mast Top rather than the ordinary Wood block.");

            void WriteItem(string filename, string name, Guid guid) =>
                File.WriteAllText(Path.Combine(items, filename), JsonSerializer.Serialize(new
                {
                    DisplayName = $"#?!{name}",
                    ComponentId = new { Guid = guid.ToString() },
                    DisplayOnInventory = true,
                }));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        Console.WriteLine("Catalog cube identity: installed Wood block, later Mast Top variant, and exported GUID passed.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
