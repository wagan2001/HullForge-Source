using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using FtdHullGenerator.Domain;

namespace FtdHullGenerator.Infrastructure;

public sealed record CatalogBlock(
    Guid Guid,
    string Name,
    MaterialKind Material,
    BlockShape Shape,
    double MaterialCost,
    bool IsFallback = false);

/// <summary>
/// The parts the player's <em>installed</em> game can actually provide, resolved at run time by
/// scanning Core_Structural/Items and Core_Structural/ItemDup. This is the third and narrowest
/// of the three part concepts, and it is not interchangeable with either of the others.
/// </summary>
/// <remarks>
/// Operational contract:
/// <list type="bullet">
/// <item>Keyed by (MaterialKind, BlockShape) — it can only ever hold shapes
/// <see cref="BlockShape" /> already names, never the wider game catalogue.</item>
/// <item><see cref="Resolve" /> never throws and never returns null: an unavailable part comes
/// back as that material's cube with <c>IsFallback</c> set. Callers decide what that means —
/// the exporter substitutes cubes for most shapes but refuses to write a coordinated inverted
/// construction whose fitted parts are missing.</item>
/// <item>Classification reads DisplayName, because that is the item's current identity; the GUID
/// it yields is what every downstream consumer uses. Names are used to tell parts apart, never
/// to infer orientation, occupancy or handedness.</item>
/// <item>SizeInfo is deliberately not read. Footprints come from
/// <see cref="BlockShapeMetadata" />, which is why admitting a part that extends along a local
/// negative axis would need the general rule first (see that type's remarks).</item>
/// <item>The fallback costs below are a development-time approximation used only when no install
/// is found; a real install overrides them.</item>
/// </list>
/// </remarks>
public sealed class FtdBlockCatalog
{
    public static readonly Guid ConstructableVehicleGuid = Guid.Parse("e63040c9-0027-4fd3-be30-67fe3e950140");

    private readonly Dictionary<(MaterialKind Material, BlockShape Shape), CatalogBlock> _blocks;

    private FtdBlockCatalog(Dictionary<(MaterialKind, BlockShape), CatalogBlock> blocks, string gameVersion)
    {
        _blocks = blocks;
        GameVersion = gameVersion;
    }

    public string GameVersion { get; }

    public IReadOnlyCollection<CatalogBlock> Blocks => _blocks.Values;

    public static FtdBlockCatalog Load(string gameDirectory)
    {
        var blocks = CreateFallbacks();
        var priorities = new Dictionary<(MaterialKind, BlockShape), int>();
        var streamingAssets = FtdInstallationLocator.GetStreamingAssets(gameDirectory);
        var coreStructural = Path.Combine(streamingAssets, "Mods", "Core_Structural");

        foreach (var folder in new[] { Path.Combine(coreStructural, "Items"), Path.Combine(coreStructural, "ItemDup") })
        {
            if (!Directory.Exists(folder))
                continue;

            foreach (var file in Directory.EnumerateFiles(folder, "*.item*", SearchOption.TopDirectoryOnly)
                         .Order(StringComparer.Ordinal))
                TryMergeDefinition(file, blocks, priorities);
        }

        return new FtdBlockCatalog(blocks, ReadGameVersion(streamingAssets));
    }

    public CatalogBlock Resolve(MaterialKind material, BlockShape shape)
    {
        if (_blocks.TryGetValue((material, shape), out var exact))
            return exact;
        return _blocks[(material, BlockShape.Cube)] with { IsFallback = true };
    }

    private static Dictionary<(MaterialKind, BlockShape), CatalogBlock> CreateFallbacks()
    {
        return new Dictionary<(MaterialKind, BlockShape), CatalogBlock>
        {
            [(MaterialKind.Wood, BlockShape.Cube)] = new(Guid.Parse("9a0ae372-beb4-4009-b14e-36ed0715af73"), "Wood block", MaterialKind.Wood, BlockShape.Cube, 1),
            [(MaterialKind.Metal, BlockShape.Cube)] = new(Guid.Parse("ab699540-efc8-4592-bc97-204f6a874b3a"), "Metal block", MaterialKind.Metal, BlockShape.Cube, 5),
            [(MaterialKind.LightweightAlloy, BlockShape.Cube)] = new(Guid.Parse("3cc75979-18ac-46c4-9a5b-25b327d99410"), "Light-weight alloy block", MaterialKind.LightweightAlloy, BlockShape.Cube, 3),
            [(MaterialKind.HeavyArmor, BlockShape.Cube)] = new(Guid.Parse("0c03433e-8947-4e7d-9dec-793526fe06d1"), "Heavy armour", MaterialKind.HeavyArmor, BlockShape.Cube, 20),
            [(MaterialKind.Stone, BlockShape.Cube)] = new(Guid.Parse("710ee212-563b-42f8-acd1-57515479524d"), "Stone block", MaterialKind.Stone, BlockShape.Cube, 1),
            [(MaterialKind.Lead, BlockShape.Cube)] = new(Guid.Parse("e71e6f97-fbe8-4bf5-9645-d15179ba0c17"), "Lead block", MaterialKind.Lead, BlockShape.Cube, 6),
            [(MaterialKind.Rubber, BlockShape.Cube)] = new(Guid.Parse("6c0bab88-aa88-4825-9cf5-55df36aa12b8"), "Rubber block", MaterialKind.Rubber, BlockShape.Cube, 4),
            [(MaterialKind.Glass, BlockShape.Cube)] = new(Guid.Parse("2d519ca8-1f12-4a8e-9340-aa6648b5e799"), "Glass block", MaterialKind.Glass, BlockShape.Cube, 2),
        };
    }

    private static void TryMergeDefinition(string file, Dictionary<(MaterialKind, BlockShape), CatalogBlock> blocks,
        Dictionary<(MaterialKind, BlockShape), int> priorities)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            var root = document.RootElement;
            var displayName = ExtractDisplayName(root, file);
            // DisplayName is the current item identity. Some source filenames and mesh
            // references still say "diagonal cut" for today's ordinary beam slopes.
            // Never concatenate those obsolete labels back into the classification.
            if (!TryGetMaterial(displayName, out var material) || !TryGetShape(displayName, out var shape))
                return;
            if (!TryGetGuid(root, out var guid))
                return;

            // The catalog retains hidden historical duplicates (for example the old
            // right 3m inverse). Prefer the current inventory part deterministically.
            var priority = root.TryGetProperty("DisplayOnInventory", out var visible) &&
                           visible.ValueKind == JsonValueKind.False ? 0 : 1;
            var key = (material, shape);
            if (priorities.TryGetValue(key, out var existingPriority) && existingPriority >= priority)
                return;

            var fallback = blocks[(material, BlockShape.Cube)];
            var scale = GetNumber(root, "CostWeightHealthScaling") ?? 1d;
            var explicitCost = TryGetNestedNumber(root, "Cost", "Material");
            var cost = explicitCost ?? fallback.MaterialCost * scale;
            blocks[(material, shape)] = new CatalogBlock(guid, displayName, material, shape, cost);
            priorities[key] = priority;
        }
        catch (IOException)
        {
            // A game update can transiently lock a catalog file. Existing fallbacks remain usable.
        }
        catch (JsonException)
        {
            // Ignore unrelated or malformed mod files instead of making export fail.
        }
    }

    private static string ExtractDisplayName(JsonElement root, string path)
    {
        if (root.TryGetProperty("DisplayName", out var display) && display.ValueKind == JsonValueKind.String)
        {
            var value = display.GetString()!;
            var marker = value.LastIndexOf("#?!", StringComparison.Ordinal);
            return marker >= 0 ? value[(marker + 3)..] : value;
        }
        return Path.GetFileNameWithoutExtension(path);
    }

    private static bool TryGetGuid(JsonElement root, out Guid guid)
    {
        guid = Guid.Empty;
        if (!root.TryGetProperty("ComponentId", out var component) || !component.TryGetProperty("Guid", out var value) || value.ValueKind != JsonValueKind.String)
            return false;
        return Guid.TryParse(value.GetString(), out guid) && guid != Guid.Empty;
    }

    private static bool TryGetMaterial(string name, out MaterialKind material)
    {
        var text = name.ToLowerInvariant();
        if (text.Contains("light-weight alloy") || text.StartsWith("alloy ")) { material = MaterialKind.LightweightAlloy; return true; }
        if (text.Contains("heavy armour") || text.Contains("heavy armor")) { material = MaterialKind.HeavyArmor; return true; }
        if (text.StartsWith("wood")) { material = MaterialKind.Wood; return true; }
        if (text.StartsWith("metal")) { material = MaterialKind.Metal; return true; }
        if (text.StartsWith("stone")) { material = MaterialKind.Stone; return true; }
        if (text.StartsWith("lead")) { material = MaterialKind.Lead; return true; }
        if (text.StartsWith("rubber")) { material = MaterialKind.Rubber; return true; }
        if (text.StartsWith("glass")) { material = MaterialKind.Glass; return true; }
        material = default;
        return false;
    }

    private static bool TryGetShape(string name, out BlockShape shape)
    {
        var text = name.ToLowerInvariant();
        shape = default;
        var transition = Regex.Match(text, @"([1-4])m to ([1-4])m (slope|inverse) transition (left|right)");
        if (transition.Success)
        {
            var shortLength = int.Parse(transition.Groups[1].Value, CultureInfo.InvariantCulture);
            var length = int.Parse(transition.Groups[2].Value, CultureInfo.InvariantCulture);
            if (shortLength >= length) return false;
            var family = transition.Groups[3].Value == "inverse" ? "InverseTransition" : "SlopeTransition";
            var hand = transition.Groups[4].Value == "left" ? "Left" : "Right";
            return Enum.TryParse($"{family}{hand}{shortLength}{length}", out shape);
        }

        var metres = Regex.Match(text, @"([1-4])\s*m\b");
        var size = metres.Success ? int.Parse(metres.Groups[1].Value, CultureInfo.InvariantCulture) : 1;
        var left = text.Contains("left") || text.Contains("(l ");
        var right = text.Contains("right") || text.Contains("(r ");
        var handed = left || right;
        var suffix = size == 1 ? "" : size.ToString(CultureInfo.InvariantCulture);
        var handName = right ? "Right" : "Left";
        if (text.Contains("square backed corner") && handed && size > 1)
            return Enum.TryParse($"SquareBackedCorner{handName}{size}", out shape);
        if (text.Contains("square corner") && handed)
            return Enum.TryParse($"SquareCorner{handName}{suffix}", out shape);
        if (text.Contains("beam slope") && size > 1)
        {
            // Current native beam slopes have diagonal cross-sections. Old filenames
            // call the same family "beam slope diagonal cut"; no extra family exists.
            var mirror = text.Contains("mirror") ? "Mirrored" : "";
            return Enum.TryParse($"BeamSlope{mirror}{size}", out shape);
        }
        if (text.Contains("inverted triangle corner") || text.Contains("inverted ("))
        {
            if (handed) return Enum.TryParse($"InverseCorner{handName}{suffix}", out shape);
            if (size == 1) { shape = BlockShape.InverseCorner; return true; }
            return false;
        }
        if (text.Contains("triangle corner"))
        {
            if (handed) return Enum.TryParse($"Corner{handName}{suffix}", out shape);
            if (size == 1) { shape = BlockShape.Corner; return true; }
            return false;
        }
        if (text.Contains("transition") || text.Contains("corner") || text.Contains("wedge") || text.Contains("offset"))
            return false;
        if (text.Contains("pole") && metres.Success)
            return Enum.TryParse($"Pole{size}", out shape);
        if ((text.Contains("down slope") || Regex.IsMatch(text, @"\bslope\s*\(")) && metres.Success)
            return Enum.TryParse($"Slope{size}", out shape);
        if (text.Contains("beam") && !text.Contains("slope") && size > 1)
            return Enum.TryParse($"Beam{size}", out shape);
        // Variant blocks can have a full one-cell footprint but a different native
        // identity (Wood Block Variant is shown in game as Mast Top). Never let one
        // replace the ordinary material cube in the export dictionary.
        if (text is "wood block" or "metal block" or "light-weight alloy block" or
            "heavy armour" or "heavy armor" or "stone block" or "lead block" or
            "rubber block" or "glass block")
        {
            shape = BlockShape.Cube;
            return true;
        }
        return false;
    }

    private static double? GetNumber(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : null;

    private static double? TryGetNestedNumber(JsonElement root, string parent, string property) =>
        root.TryGetProperty(parent, out var parentValue) && parentValue.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : null;

    private static string ReadGameVersion(string streamingAssets)
    {
        var logPath = Path.Combine(streamingAssets, "BuildLog.json");
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(logPath));
            var final = document.RootElement.EnumerateArray().LastOrDefault();
            if (final.ValueKind == JsonValueKind.Object && final.TryGetProperty("Version", out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString()!.Replace(',', '.');
        }
        catch (IOException) { }
        catch (JsonException) { }
        return "4.3.4.0";
    }
}
