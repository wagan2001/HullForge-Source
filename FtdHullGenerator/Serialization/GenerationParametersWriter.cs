using System.Text.Json;
using System.Text.Json.Serialization;
using FtdHullGenerator.Domain;

namespace FtdHullGenerator.Serialization;

/// <summary>
/// Writes the optional human-readable record that accompanies a debug export.
/// Keeping this outside the native blueprint preserves the game's file contract while
/// retaining enough information to reconstruct the exact generation request by hand.
/// </summary>
internal static class GenerationParametersWriter
{
    public const int CurrentFormatVersion = 1;
    public const string FileSuffix = ".generation.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Write(GeneratedHull hull, string blueprintPath, string gameVersion)
    {
        ArgumentNullException.ThrowIfNull(hull);
        ArgumentException.ThrowIfNullOrWhiteSpace(blueprintPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameVersion);

        var parametersPath = GetParametersPath(blueprintPath);
        var parameters = hull.Parameters;
        var document = new GenerationParametersDocument
        {
            Format = "HullForge.GenerationParameters",
            FormatVersion = CurrentFormatVersion,
            GeneratedUtc = DateTimeOffset.UtcNow,
            BlueprintFile = Path.GetFileName(blueprintPath),
            GameVersion = gameVersion,
            Parameters = GenerationParametersModel.From(parameters),
        };

        var temporaryPath = parametersPath + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(document, JsonOptions));
            File.Move(temporaryPath, parametersPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }

        return parametersPath;
    }

    public static string GetParametersPath(string blueprintPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blueprintPath);

        return Path.Combine(
            Path.GetDirectoryName(blueprintPath) ?? string.Empty,
            Path.GetFileNameWithoutExtension(blueprintPath) + FileSuffix);
    }

    private sealed class GenerationParametersDocument
    {
        public required string Format { get; init; }
        public int FormatVersion { get; init; }
        public DateTimeOffset GeneratedUtc { get; init; }
        public required string BlueprintFile { get; init; }
        public required string GameVersion { get; init; }
        public required GenerationParametersModel Parameters { get; init; }
    }

    private sealed class GenerationParametersModel
    {
        public int Length { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public double BowFullness { get; init; }
        public double SternFullness { get; init; }
        public double CrossSectionCurve { get; init; }
        public required GenerationArmorLayer[] HullArmor { get; init; }
        public GenerationArmorLayer[]? DeckArmor { get; init; }
        public bool Beamify { get; init; }
        public SmoothingMethod Smoothing { get; init; }
        public int HybridFillOffset { get; init; }
        public BowStyle BowStyle { get; init; }
        public SternStyle SternStyle { get; init; }
        public bool HasBulb { get; init; }
        public BulbSettings? Bulb { get; init; }
        public GenerationShape? Shape { get; init; }
        public GenerationArmorLayer[]? BottomArmor { get; init; }
        public SuperstructureSettings? Superstructure { get; init; }

        // These are the values the generator consumes after HullParameters applies its
        // compatibility fallbacks. They make older or hand-authored records immediately
        // reproducible without requiring the reader to know which nullable fields fall back.
        public required GenerationShape EffectiveShape { get; init; }
        public BulbSettings EffectiveBulb { get; init; }
        public required GenerationArmorLayer[] EffectiveBottomArmor { get; init; }
        public required SuperstructureSettings EffectiveSuperstructure { get; init; }

        public static GenerationParametersModel From(HullParameters parameters)
        {
            return new GenerationParametersModel
            {
                Length = parameters.Length,
                Width = parameters.Width,
                Height = parameters.Height,
                BowFullness = parameters.BowFullness,
                SternFullness = parameters.SternFullness,
                CrossSectionCurve = parameters.CrossSectionCurve,
                HullArmor = ToLayers(parameters.HullArmor),
                DeckArmor = ToOptionalLayers(parameters.DeckArmor),
                Beamify = parameters.Beamify,
                Smoothing = parameters.Smoothing,
                HybridFillOffset = parameters.HybridFillOffset,
                BowStyle = parameters.BowStyle,
                SternStyle = parameters.SternStyle,
                HasBulb = parameters.HasBulb,
                Bulb = parameters.Bulb,
                Shape = parameters.Shape is null ? null : ToShape(parameters.Shape),
                BottomArmor = ToOptionalLayers(parameters.BottomArmor),
                Superstructure = parameters.Superstructure,
                EffectiveShape = ToShape(parameters.EffectiveShape),
                EffectiveBulb = parameters.EffectiveBulb,
                EffectiveBottomArmor = ToLayers(parameters.EffectiveBottomArmor),
                EffectiveSuperstructure = parameters.EffectiveSuperstructure,
            };
        }

        private static GenerationArmorLayer[]? ToOptionalLayers(ArmorLayout? layout) =>
            layout?.Layers.Select(layer => new GenerationArmorLayer(layer.Material, layer.Construction)).ToArray();

        private static GenerationArmorLayer[] ToLayers(ArmorLayout layout) =>
            layout.Layers.Select(layer => new GenerationArmorLayer(layer.Material, layer.Construction)).ToArray();

        private static GenerationShape ToShape(HullShapeSettings shape) => new()
        {
            Bow = new GenerationBowShape
            {
                Fullness = shape.Bow.Fullness,
                Flare = shape.Bow.Flare,
                EntranceLengthPercent = shape.Bow.EntranceLengthPercent,
            },
            Body = new GenerationBodyShape
            {
                Style = shape.Body.Style,
                Fullness = shape.Body.Fullness,
                SideShape = shape.Body.SideShape,
                Chine = shape.Body.Chine,
                FlatBottom = shape.Body.FlatBottom,
            },
            Stern = new GenerationSternShape
            {
                Fullness = shape.Stern.Fullness,
                SideShape = shape.Stern.SideShape,
                RunLengthPercent = shape.Stern.RunLengthPercent,
            },
            Profile = new GenerationProfile
            {
                BowDeckRise = shape.Profile.BowDeckRise,
                SternDeckRise = shape.Profile.SternDeckRise,
                BowKeelRise = shape.Profile.BowKeelRise,
                SternKeelRise = shape.Profile.SternKeelRise,
            },
        };
    }

    private sealed record GenerationArmorLayer(MaterialKind? Material, ArmorConstruction Construction);

    private sealed class GenerationShape
    {
        public required GenerationBowShape Bow { get; init; }
        public required GenerationBodyShape Body { get; init; }
        public required GenerationSternShape Stern { get; init; }
        public required GenerationProfile Profile { get; init; }
    }

    private sealed class GenerationBowShape
    {
        public double Fullness { get; init; }
        public double Flare { get; init; }
        public int EntranceLengthPercent { get; init; }
    }

    private sealed class GenerationBodyShape
    {
        public BodyStyle Style { get; init; }
        public double Fullness { get; init; }
        public double SideShape { get; init; }
        public double Chine { get; init; }
        public double FlatBottom { get; init; }
    }

    private sealed class GenerationSternShape
    {
        public double Fullness { get; init; }
        public double SideShape { get; init; }
        public int RunLengthPercent { get; init; }
    }

    private sealed class GenerationProfile
    {
        public int BowDeckRise { get; init; }
        public int SternDeckRise { get; init; }
        public int BowKeelRise { get; init; }
        public int SternKeelRise { get; init; }
    }
}
