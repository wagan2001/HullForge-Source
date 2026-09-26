using System.Text.Json;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Design;
using FtdHullGenerator.Domain.Projects;

namespace FtdHullGenerator.Serialization.Projects.Migrations;

/// <summary>
/// Migrates the version-1 generation-parameter sidecar that <see cref="GenerationParametersWriter"/>
/// actually writes into a fresh <see cref="ShipDocument"/>. The sidecar records the authored
/// parameter values plus the effective fallbacks; this migration prefers the authored values and
/// falls back to the effective ones, so both a full record and a hand-trimmed one reconstruct the
/// same generation request.
/// </summary>
/// <remarks>
/// This is a one-way import. It creates a new project; it never rewrites the sidecar. A native
/// blueprint is handled by <see cref="ProjectMigrationRegistry"/> and is explicitly not a project
/// input.
/// </remarks>
internal sealed class GenerationParametersV1Migration : IProjectMigration
{
    public string Name => "generation-parameters-v1-to-ship-document-v1";

    public int SourceFormatVersion => 1;

    public bool CanMigrate(string format, int formatVersion) =>
        string.Equals(format, ProjectMigrationRegistry.GenerationParametersFormat, StringComparison.Ordinal) &&
        formatVersion == SourceFormatVersion;

    public ProjectDocumentResult Migrate(JsonElement root)
    {
        var context = new ProjectConversionContext();

        if (!root.TryGetProperty("Parameters", out var parameters) ||
            parameters.ValueKind != JsonValueKind.Object)
        {
            context.Error(ProjectDiagnosticCodes.MigrationUnsupported,
                "The generation-parameter sidecar has no Parameters object.");
            return new ProjectDocumentResult(null, context.Diagnostics);
        }

        var hull = BuildParameters(parameters, context);
        if (hull is null || context.HasErrors)
            return new ProjectDocumentResult(null, context.Diagnostics);

        var name = ReadDocumentName(root);
        var document = ShipDocument.FromLegacyParameters(hull, name);
        return new ProjectDocumentResult(document, context.Diagnostics,
            MigratedFrom: $"{ProjectMigrationRegistry.GenerationParametersFormat} v{SourceFormatVersion}");
    }

    private static HullParameters? BuildParameters(JsonElement parameters, ProjectConversionContext context)
    {
        var length = ReadInt(parameters, "Length");
        var width = ReadInt(parameters, "Width");
        var height = ReadInt(parameters, "Height");
        var bowFullness = ReadDouble(parameters, "BowFullness", context, nameof(HullParameters.BowFullness));
        var sternFullness = ReadDouble(parameters, "SternFullness", context, nameof(HullParameters.SternFullness));
        var crossSection = ReadDouble(parameters, "CrossSectionCurve", context, nameof(HullParameters.CrossSectionCurve));
        var hullArmor = ReadArmorLayout(parameters, "HullArmor", context, required: true);
        var deckArmor = ReadArmorLayout(parameters, "DeckArmor", context, required: false);
        var bottomArmor = ReadArmorLayout(parameters, "BottomArmor", context, required: false);
        var beamify = ReadBool(parameters, "Beamify");
        var smoothing = ReadEnum(parameters, "Smoothing", SmoothingMethod.None, context, nameof(HullParameters.Smoothing));
        var hybridOffset = ReadInt(parameters, "HybridFillOffset") ?? 1;
        var bowStyle = ReadEnum(parameters, "BowStyle", BowStyle.Pointed, context, nameof(HullParameters.BowStyle));
        var sternStyle = ReadEnum(parameters, "SternStyle", SternStyle.Transom, context, nameof(HullParameters.SternStyle));
        var hasBulb = ReadBool(parameters, "HasBulb");
        var bulb = ReadBulb(parameters, "Bulb") ?? ReadBulb(parameters, "EffectiveBulb");
        var shape = ReadShape(parameters, "Shape", context) ?? ReadShape(parameters, "EffectiveShape", context);
        var superstructure = ReadSuperstructure(parameters, "Superstructure", context) ??
                             ReadSuperstructure(parameters, "EffectiveSuperstructure", context);

        if (length is null || width is null || height is null || bowFullness is null || sternFullness is null ||
            crossSection is null || hullArmor is null || smoothing is null || bowStyle is null || sternStyle is null)
        {
            context.Error(ProjectDiagnosticCodes.MigrationUnsupported,
                "The generation-parameter sidecar is missing a required parameter.");
            return null;
        }

        return new HullParameters(
            length.Value,
            width.Value,
            height.Value,
            bowFullness.Value,
            sternFullness.Value,
            crossSection.Value,
            hullArmor,
            deckArmor,
            beamify,
            smoothing.Value,
            hybridOffset,
            bowStyle.Value,
            sternStyle.Value,
            hasBulb,
            bulb,
            shape,
            bottomArmor,
            superstructure);
    }

    private static string ReadDocumentName(JsonElement root)
    {
        var blueprintFile = root.TryGetProperty("BlueprintFile", out var element) &&
                            element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
        var name = string.IsNullOrWhiteSpace(blueprintFile)
            ? "Imported hull"
            : Path.GetFileNameWithoutExtension(blueprintFile);
        if (string.IsNullOrWhiteSpace(name))
            name = "Imported hull";
        return name.Length > DesignLimits.MaxDocumentNameLength
            ? name[..DesignLimits.MaxDocumentNameLength]
            : name;
    }

    private static ArmorLayout? ReadArmorLayout(
        JsonElement parent,
        string name,
        ProjectConversionContext context,
        bool required)
    {
        if (!parent.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            if (required)
                context.Error(ProjectDiagnosticCodes.MigrationUnsupported,
                    $"The sidecar is missing '{name}'.", field: name);
            return null;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            context.Error(ProjectDiagnosticCodes.InvalidValue, $"'{name}' is not an armor-layer array.", field: name);
            return null;
        }

        var layers = new List<ArmorLayer>();
        foreach (var layer in element.EnumerateArray())
        {
            if (layer.ValueKind != JsonValueKind.Object)
            {
                context.Error(ProjectDiagnosticCodes.InvalidValue, $"'{name}' contains a non-object layer.", field: name);
                continue;
            }

            MaterialKind? material = null;
            if (layer.TryGetProperty("Material", out var materialElement) &&
                materialElement.ValueKind != JsonValueKind.Null)
            {
                var text = materialElement.ValueKind == JsonValueKind.String ? materialElement.GetString() : null;
                if (text is null || !Enum.IsDefined(typeof(MaterialKind), text))
                {
                    context.Error(ProjectDiagnosticCodes.InvalidValue,
                        $"'{name}' contains an unsupported material.", field: name);
                    continue;
                }

                material = Enum.Parse<MaterialKind>(text);
            }

            var construction = ArmorConstruction.Solid;
            if (layer.TryGetProperty("Construction", out var constructionElement) &&
                constructionElement.ValueKind == JsonValueKind.String)
            {
                var text = constructionElement.GetString();
                if (text is null || !Enum.IsDefined(typeof(ArmorConstruction), text))
                {
                    context.Error(ProjectDiagnosticCodes.InvalidValue,
                        $"'{name}' contains an unsupported construction.", field: name);
                    continue;
                }

                construction = Enum.Parse<ArmorConstruction>(text);
            }

            try
            {
                layers.Add(new ArmorLayer(material, construction));
            }
            catch (ArgumentException exception)
            {
                context.Error(ProjectDiagnosticCodes.InvalidValue,
                    $"'{name}' contains an invalid layer: {exception.Message}", field: name);
            }
        }

        if (layers.Count == 0)
        {
            context.Error(ProjectDiagnosticCodes.InvalidValue, $"'{name}' has no usable layers.", field: name);
            return null;
        }

        try
        {
            return new ArmorLayout(layers);
        }
        catch (ArgumentException exception)
        {
            context.Error(ProjectDiagnosticCodes.InvalidValue,
                $"'{name}' is invalid: {exception.Message}", field: name);
            return null;
        }
    }

    private static HullShapeSettings? ReadShape(JsonElement parent, string name, ProjectConversionContext context)
    {
        if (!parent.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Object)
            return null;

        if (!element.TryGetProperty("Bow", out var bow) || bow.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("Body", out var body) || body.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("Stern", out var stern) || stern.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("Profile", out var profile) || profile.ValueKind != JsonValueKind.Object)
        {
            context.Error(ProjectDiagnosticCodes.InvalidValue, $"'{name}' is incomplete.", field: name);
            return null;
        }

        var bowFullness = ReadDouble(bow, "Fullness", context, name);
        var bowFlare = ReadDouble(bow, "Flare", context, name);
        var bodyFullness = ReadDouble(body, "Fullness", context, name);
        var sideShape = ReadDouble(body, "SideShape", context, name);
        var chine = ReadDouble(body, "Chine", context, name);
        var flatBottom = ReadDouble(body, "FlatBottom", context, name) ?? 0;
        var sternFullness = ReadDouble(stern, "Fullness", context, name);
        var sternSide = ReadDouble(stern, "SideShape", context, name);
        var bodyStyle = ReadEnum(body, "Style", BodyStyle.Custom, context, name);

        if (bowFullness is null || bowFlare is null || bodyFullness is null || sideShape is null ||
            chine is null || sternFullness is null || sternSide is null || bodyStyle is null)
            return null;

        return new HullShapeSettings(
            new BowShapeSettings(bowFullness.Value, bowFlare.Value, ReadInt(bow, "EntranceLengthPercent") ?? 45),
            new BodyShapeSettings(bodyStyle.Value, bodyFullness.Value, sideShape.Value, chine.Value, flatBottom),
            new SternShapeSettings(sternFullness.Value, sternSide.Value, ReadInt(stern, "RunLengthPercent") ?? 25),
            new HullProfileSettings(
                ReadInt(profile, "BowDeckRise") ?? 0,
                ReadInt(profile, "SternDeckRise") ?? 0,
                ReadInt(profile, "BowKeelRise") ?? 0,
                ReadInt(profile, "SternKeelRise") ?? 0));
    }

    private static BulbSettings? ReadBulb(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Object)
            return null;
        return new BulbSettings(
            ReadInt(element, "LengthPercent") ?? BulbSettings.Default.LengthPercent,
            ReadInt(element, "WidthPercent") ?? BulbSettings.Default.WidthPercent,
            ReadInt(element, "ForeAftPercent") ?? BulbSettings.Default.ForeAftPercent,
            ReadInt(element, "RisePercent") ?? BulbSettings.Default.RisePercent);
    }

    private static SuperstructureSettings? ReadSuperstructure(
        JsonElement parent,
        string name,
        ProjectConversionContext context)
    {
        if (!parent.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Object)
            return null;

        var style = ReadEnum(element, "Style", SuperstructureStyle.CenterIsland, context, name);
        var material = ReadEnum(element, "Material", MaterialKind.Metal, context, name);
        var smoothing = ReadEnum(element, "Smoothing", SuperstructureSmoothingMethod.None, context, name);
        if (style is null || material is null || smoothing is null)
            return null;

        return new SuperstructureSettings(
            ReadBool(element, "Enabled"),
            style.Value,
            ReadInt(element, "Levels") ?? SuperstructureSettings.Default.Levels,
            material.Value,
            smoothing.Value,
            ReadInt(element, "ForeAftPercent") ?? SuperstructureSettings.DefaultForeAftPercent);
    }

    private static int? ReadInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var parsed)
            ? parsed
            : null;

    private static double? ReadDouble(
        JsonElement element,
        string name,
        ProjectConversionContext context,
        string field)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
            return null;
        if (!value.TryGetDouble(out var parsed))
        {
            context.Error(ProjectDiagnosticCodes.InvalidValue, $"'{field}' is not a finite number.", field: field);
            return null;
        }

        return context.Finite(parsed, field);
    }

    private static bool ReadBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False &&
        value.GetBoolean();

    private static TEnum? ReadEnum<TEnum>(
        JsonElement element,
        string name,
        TEnum fallback,
        ProjectConversionContext context,
        string field)
        where TEnum : struct, Enum
    {
        if (!element.TryGetProperty(name, out var value))
            return fallback;
        if (value.ValueKind != JsonValueKind.String)
        {
            context.Error(ProjectDiagnosticCodes.InvalidValue,
                $"'{field}' has a '{name}' value that is not an enum name.", field: field);
            return null;
        }

        var text = value.GetString();
        if (text is null || !Enum.IsDefined(typeof(TEnum), text))
        {
            context.Error(ProjectDiagnosticCodes.InvalidValue,
                $"'{field}' declares an unsupported '{name}' value.", field: field);
            return null;
        }

        return Enum.Parse<TEnum>(text);
    }
}
