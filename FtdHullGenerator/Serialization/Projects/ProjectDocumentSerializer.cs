using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Components;
using FtdHullGenerator.Domain.Design;
using FtdHullGenerator.Domain.Layout;
using FtdHullGenerator.Domain.Projects;
using FtdHullGenerator.Domain.Superstructures;
using FtdHullGenerator.Infrastructure.Projects;
using FtdHullGenerator.Serialization.Projects.Migrations;

namespace FtdHullGenerator.Serialization.Projects;

/// <summary>
/// Turns a <see cref="ShipDocument"/> into the versioned JSON envelope persisted as a
/// <c>.hfship</c> document, and back. The wire format stores identifiers and values only; it never
/// stores .NET type names and never emits a runtime object graph.
/// </summary>
/// <remarks>
/// The explicit DTOs and adapters are not decoration. <see cref="HullParameters"/>,
/// <see cref="ArmorLayout"/> and <see cref="ArmorLayer"/> do not survive a default
/// System.Text.Json round trip: <c>ArmorLayout</c> throws <see cref="NotSupportedException"/> and
/// <c>ArmorLayer</c> silently deserializes to air. <see cref="DesignMeasure"/> and
/// <see cref="LayoutDatum"/> already carry the annotations they need.
/// </remarks>
public static class ProjectDocumentSerializer
{
    /// <summary>The envelope identifier. A reader that does not know it must refuse the file.</summary>
    public const string EnvelopeFormat = "HullForge.ShipDocument";

    /// <summary>The newest envelope major version this build writes and understands.</summary>
    public const int CurrentEnvelopeVersion = 1;

    /// <summary>Hard byte budget for a project file. A ship document carries no geometry, so this is generous.</summary>
    public const int MaxDocumentBytes = 4 * 1024 * 1024;

    public const int MaxNestingDepth = 64;
    public const int MaxElementCount = 100_000;
    public const int MaxArrayLength = 16_384;
    public const int MaxStringChars = DesignLimits.MaxDocumentExtensionBytes + 1024;
    public const int MaxArmorLayers = 512;

    private const int MaxDocumentChars = MaxDocumentBytes;
    private const int MaxPropertyNameChars = 256;

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = MaxNestingDepth,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        MaxDepth = MaxNestingDepth,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        MaxDepth = MaxNestingDepth,
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
    };

    /// <summary>
    /// Serializes a document after the existing design validation passes. An invalid document is
    /// never written: the caller keeps the previous manual save.
    /// </summary>
    public static ProjectSerializeResult Serialize(ShipDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var diagnostics = new List<DesignDiagnostic>();
        AddNameSafetyDiagnostics(document, diagnostics);
        diagnostics.AddRange(document.Validate());
        if (diagnostics.HasErrors())
            return new ProjectSerializeResult(null, diagnostics);

        try
        {
            var dto = new ShipDocumentEnvelopeDto
            {
                Format = EnvelopeFormat,
                FormatVersion = CurrentEnvelopeVersion,
                Document = ToDto(document),
            };
            return new ProjectSerializeResult(JsonSerializer.Serialize(dto, WriteOptions), diagnostics);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException)
        {
            diagnostics.Add(new DesignDiagnostic(ProjectDiagnosticCodes.InvalidValue, DesignSeverity.Error,
                $"The document could not be serialized: {exception.Message}"));
            return new ProjectSerializeResult(null, diagnostics);
        }
    }

    /// <summary>
    /// Reads a payload into design intent. The envelope, a supported legacy sidecar and a native
    /// blueprint are distinguished here; a native blueprint is explicitly not a reversible project
    /// input and produces <see cref="ProjectDiagnosticCodes.NotAProjectInput"/>.
    /// </summary>
    public static ProjectDocumentResult Deserialize(string json)
    {
        if (json is null)
            return ProjectDocumentResult.Failure(InvalidJson("The payload was null."));
        if (json.Length == 0)
            return ProjectDocumentResult.Failure(InvalidJson("The payload was empty."));
        if (json.Length > MaxDocumentChars)
            return ProjectDocumentResult.Failure(TooLarge(json.Length));

        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(json, DocumentOptions);
        }
        catch (JsonException exception)
        {
            return ProjectDocumentResult.Failure(InvalidJson($"The payload is not valid JSON: {exception.Message}"));
        }

        using (parsed)
        {
            var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return ProjectDocumentResult.Failure(InvalidJson("The payload root is not a JSON object."));

            var budget = InspectBudget(root);
            if (budget.Diagnostic is not null)
                return ProjectDocumentResult.Failure(budget.Diagnostic);

            var format = ReadString(root, "format");
            if (string.Equals(format, EnvelopeFormat, StringComparison.Ordinal))
                return DeserializeEnvelope(root);

            var migrated = ProjectMigrationRegistry.Migrate(root);
            return migrated.Document is null
                ? migrated
                : FinalizeDocument(migrated.Document, migrated.Diagnostics, migrated.IsReadOnlyFallback,
                    migrated.MigratedFrom);
        }
    }

    /// <summary>
    /// Runs the post-conversion gates that apply to every successful read, including a migrated
    /// sidecar: name safety and the existing <see cref="ShipDocument.Validate"/> design checks.
    /// </summary>
    private static ProjectDocumentResult FinalizeDocument(
        ShipDocument document,
        IReadOnlyList<DesignDiagnostic> priorDiagnostics,
        bool isReadOnlyFallback,
        string? migratedFrom)
    {
        var diagnostics = new List<DesignDiagnostic>(priorDiagnostics);
        AddNameSafetyDiagnostics(document, diagnostics);
        diagnostics.AddRange(document.Validate());
        return new ProjectDocumentResult(document, diagnostics, isReadOnlyFallback, migratedFrom);
    }

    /// <summary>Reads a UTF-8 payload into design intent.</summary>
    public static ProjectDocumentResult Deserialize(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length == 0)
            return ProjectDocumentResult.Failure(InvalidJson("The payload was empty."));
        if (utf8.Length > MaxDocumentBytes)
            return ProjectDocumentResult.Failure(TooLarge(utf8.Length));

        try
        {
            var json = System.Text.Encoding.UTF8.GetString(utf8);
            return Deserialize(json);
        }
        catch (DecoderFallbackException exception)
        {
            return ProjectDocumentResult.Failure(InvalidJson($"The payload is not valid UTF-8: {exception.Message}"));
        }
    }

    private static ProjectDocumentResult DeserializeEnvelope(JsonElement root)
    {
        var formatVersion = ReadInt(root, "formatVersion");
        if (formatVersion is null || formatVersion < 1)
            return ProjectDocumentResult.Failure(InvalidValue("The envelope format version is missing or invalid."));
        if (formatVersion > CurrentEnvelopeVersion)
            return FutureVersion($"envelope format version {formatVersion}", CurrentEnvelopeVersion);

        if (!root.TryGetProperty("document", out var documentElement) ||
            documentElement.ValueKind != JsonValueKind.Object)
            return ProjectDocumentResult.Failure(InvalidJson("The envelope has no document object."));

        var schemaVersion = ReadInt(documentElement, "schemaVersion");
        if (schemaVersion is null || schemaVersion < 1)
            return ProjectDocumentResult.Failure(InvalidValue("The document schema version is missing or invalid."));
        if (schemaVersion > ShipDocument.CurrentSchemaVersion)
            return FutureVersion($"document schema version {schemaVersion}", ShipDocument.CurrentSchemaVersion);

        ShipDocumentEnvelopeDto? envelope;
        try
        {
            envelope = root.Deserialize<ShipDocumentEnvelopeDto>(ReadOptions);
        }
        catch (JsonException exception)
        {
            return ProjectDocumentResult.Failure(InvalidJson($"The envelope could not be read: {exception.Message}"));
        }
        catch (Exception exception) when (exception is NotSupportedException or ArgumentException)
        {
            return ProjectDocumentResult.Failure(InvalidValue($"The envelope could not be read: {exception.Message}"));
        }

        if (envelope?.Document is null)
            return ProjectDocumentResult.Failure(InvalidJson("The envelope has no document."));

        var context = new ProjectConversionContext();
        var document = ToDomain(envelope.Document, context);
        if (document is null || context.HasErrors)
            return new ProjectDocumentResult(null, context.Diagnostics);

        return FinalizeDocument(document, context.Diagnostics, isReadOnlyFallback: false, migratedFrom: null);
    }

    private static ProjectDocumentResult FutureVersion(string what, int newest)
    {
        var diagnostic = new DesignDiagnostic(ProjectDiagnosticCodes.FutureVersion, DesignSeverity.Error,
            $"The file declares {what}, but this build understands at most {newest}. It is opened as an " +
            "explicit read-only fallback, never as default settings.",
            Field: "formatVersion",
            SuggestedCorrection: "Update Hull Forge to read this document, or keep the file unchanged.");
        return new ProjectDocumentResult(null, [diagnostic], IsReadOnlyFallback: true);
    }

    private static DesignDiagnostic InvalidJson(string message) =>
        new(ProjectDiagnosticCodes.InvalidJson, DesignSeverity.Error, message,
            SuggestedCorrection: "Restore the file from its backup, or choose another project.");

    private static DesignDiagnostic InvalidValue(string message, string? field = null) =>
        new(ProjectDiagnosticCodes.InvalidValue, DesignSeverity.Error, message, Field: field);

    private static DesignDiagnostic TooLarge(long bytes) =>
        new(ProjectDiagnosticCodes.FileTooLarge, DesignSeverity.Error,
            $"The payload is {bytes} bytes; the persistence budget is {MaxDocumentBytes} bytes.",
            Field: "length",
            SuggestedCorrection: "Open a Hull Forge project; a native blueprint is not a project input.");

    private static (bool Ok, DesignDiagnostic? Diagnostic) InspectBudget(JsonElement root)
    {
        var count = 0;
        var stack = new Stack<JsonElement>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (++count > MaxElementCount)
                return (false, BudgetDiagnostic($"The payload holds more than {MaxElementCount} JSON elements."));

            switch (current.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in current.EnumerateObject())
                    {
                        if (property.Name.Length > MaxPropertyNameChars)
                            return (false, BudgetDiagnostic($"A JSON property name exceeds {MaxPropertyNameChars} characters."));
                        stack.Push(property.Value);
                    }

                    break;
                case JsonValueKind.Array:
                    if (current.GetArrayLength() > MaxArrayLength)
                        return (false, BudgetDiagnostic($"A JSON array holds more than {MaxArrayLength} entries."));
                    foreach (var item in current.EnumerateArray())
                        stack.Push(item);
                    break;
                case JsonValueKind.String:
                    if ((current.GetString()?.Length ?? 0) > MaxStringChars)
                        return (false, BudgetDiagnostic($"A JSON string exceeds {MaxStringChars} characters."));
                    break;
            }
        }

        return (true, null);
    }

    private static DesignDiagnostic BudgetDiagnostic(string message) =>
        new(ProjectDiagnosticCodes.NestingLimit, DesignSeverity.Error, message,
            SuggestedCorrection: "This is not a normal Hull Forge project; do not open it.");

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var parsed)
            ? parsed
            : null;

    // -----------------------------------------------------------------------------------------
    // Name safety. A display name may contain spaces; it may not smuggle a path or device name.
    // -----------------------------------------------------------------------------------------

    private static void AddNameSafetyDiagnostics(ShipDocument document, List<DesignDiagnostic> diagnostics)
    {
        Check(document.DocumentId, "document identifier", nameof(document.DocumentId));
        Check(document.Name, "document name", nameof(document.Name));
        Check(document.GenerationVersion, "generation version", nameof(document.GenerationVersion));
        Check(document.Source.AssetId, "historical asset identifier", nameof(document.Source));
        Check(document.Source.AssetVersion, "historical asset version", nameof(document.Source));
        Check(document.Source.AssetHash, "historical asset hash", nameof(document.Source));
        Check(document.AppliedStyle?.StyleId, "style identifier", nameof(document.AppliedStyle));
        Check(document.AppliedStyle?.StyleName, "style name", nameof(document.AppliedStyle));

        foreach (var node in document.Arrangement.Nodes)
        {
            Check(node.Id, "arrangement node identifier", nameof(document.Arrangement));
            Check(node.ComponentId, "arrangement component identifier", nameof(document.Arrangement));
            foreach (var handle in node.Handles)
            {
                Check(handle.Id, "arrangement handle identifier", nameof(document.Arrangement));
                Check(handle.TargetId, "arrangement handle target", nameof(document.Arrangement));
            }
        }

        foreach (var gap in document.Arrangement.Gaps)
            Check(gap.Id, "arrangement gap identifier", nameof(document.Arrangement));
        foreach (var named in document.Arrangement.NamedGaps)
            Check(named.Id, "named gap identifier", nameof(document.Arrangement));

        foreach (var barbette in document.Barbettes)
        {
            Check(barbette.Id, "barbette identifier", nameof(document.Barbettes));
            Check(barbette.NodeId, "barbette node reference", nameof(document.Barbettes));
        }

        foreach (var layer in document.Superstructure.Layers)
        {
            Check(layer.Id, "superstructure layer identifier", nameof(document.Superstructure));
            foreach (var module in layer.Modules)
                Check(module.Id, "superstructure module identifier", nameof(document.Superstructure));
        }

        foreach (var root in document.Superstructure.TowerRoots)
        {
            Check(root.Id, "tower root identifier", nameof(document.Superstructure));
            Check(root.SpanNodeId, "tower root span reference", nameof(document.Superstructure));
        }

        return;

        void Check(string? value, string what, string field)
        {
            if (value is null)
                return;
            var reason = ProjectPathSafety.FindUnsafeName(value);
            if (reason is not null)
                diagnostics.Add(new DesignDiagnostic(ProjectDiagnosticCodes.UnsafeName, DesignSeverity.Error,
                    $"The {what} is unsafe: {reason}.", Field: field,
                    SuggestedCorrection: "Use a plain name without path separators or reserved device names."));
        }
    }

    // -----------------------------------------------------------------------------------------
    // Domain -> DTO
    // -----------------------------------------------------------------------------------------

    private static ShipDocumentDto ToDto(ShipDocument document) => new()
    {
        SchemaVersion = document.SchemaVersion,
        DocumentId = document.DocumentId,
        Name = document.Name,
        GenerationVersion = document.GenerationVersion,
        Source = new HullSourceDto
        {
            Kind = document.Source.Kind,
            AssetId = document.Source.AssetId,
            AssetVersion = document.Source.AssetVersion,
            AssetHash = document.Source.AssetHash,
        },
        Hull = ToDto(document.Hull),
        Arrangement = ToDto(document.Arrangement),
        Datum = document.Datum is null ? null : new LayoutDatumDto
        {
            LayoutBowZ = document.Datum.LayoutBowZ.TwiceMetres,
            CenterPlaneX = document.Datum.CenterPlaneX.TwiceMetres,
        },
        Internals = ToDto(document.Internals),
        Barbettes = document.Barbettes.Select(ToDto).ToList(),
        Superstructure = ToDto(document.Superstructure),
        Smoothing = ToDto(document.Smoothing),
        AppliedStyle = document.AppliedStyle is null ? null : ToDto(document.AppliedStyle),
        Extensions = document.Extensions is null ? null : ToDto(document.Extensions),
    };

    private static HullParametersDto ToDto(HullParameters parameters) => new()
    {
        Length = parameters.Length,
        Width = parameters.Width,
        Height = parameters.Height,
        BowFullness = parameters.BowFullness,
        SternFullness = parameters.SternFullness,
        CrossSectionCurve = parameters.CrossSectionCurve,
        HullArmor = ToDto(parameters.HullArmor),
        DeckArmor = parameters.DeckArmor is null ? null : ToDto(parameters.DeckArmor),
        Beamify = parameters.Beamify,
        Smoothing = parameters.Smoothing,
        HybridFillOffset = parameters.HybridFillOffset,
        BowStyle = parameters.BowStyle,
        SternStyle = parameters.SternStyle,
        HasBulb = parameters.HasBulb,
        Bulb = parameters.Bulb is { } bulb ? ToDto(bulb) : null,
        Shape = parameters.Shape is null ? null : ToDto(parameters.Shape),
        BottomArmor = parameters.BottomArmor is null ? null : ToDto(parameters.BottomArmor),
        Superstructure = parameters.Superstructure is null ? null : ToDto(parameters.Superstructure),
    };

    private static ArmorLayoutDto ToDto(ArmorLayout layout) => new()
    {
        Layers = layout.Layers.Select(layer => new ArmorLayerDto
        {
            Material = layer.Material,
            Construction = layer.Construction,
        }).ToList(),
    };

    private static HullShapeDto ToDto(HullShapeSettings shape) => new()
    {
        Bow = new BowShapeDto
        {
            Fullness = shape.Bow.Fullness,
            Flare = shape.Bow.Flare,
            EntranceLengthPercent = shape.Bow.EntranceLengthPercent,
        },
        Body = new BodyShapeDto
        {
            Style = shape.Body.Style,
            Fullness = shape.Body.Fullness,
            SideShape = shape.Body.SideShape,
            Chine = shape.Body.Chine,
            FlatBottom = shape.Body.FlatBottom,
        },
        Stern = new SternShapeDto
        {
            Fullness = shape.Stern.Fullness,
            SideShape = shape.Stern.SideShape,
            RunLengthPercent = shape.Stern.RunLengthPercent,
        },
        Profile = new HullProfileDto
        {
            BowDeckRise = shape.Profile.BowDeckRise,
            SternDeckRise = shape.Profile.SternDeckRise,
            BowKeelRise = shape.Profile.BowKeelRise,
            SternKeelRise = shape.Profile.SternKeelRise,
        },
    };

    private static BulbSettingsDto ToDto(BulbSettings bulb) => new()
    {
        LengthPercent = bulb.LengthPercent,
        WidthPercent = bulb.WidthPercent,
        ForeAftPercent = bulb.ForeAftPercent,
        RisePercent = bulb.RisePercent,
    };

    private static SuperstructureSettingsDto ToDto(SuperstructureSettings settings) => new()
    {
        Enabled = settings.Enabled,
        Style = settings.Style,
        Levels = settings.Levels,
        Material = settings.Material,
        Smoothing = settings.Smoothing,
        ForeAftPercent = settings.ForeAftPercent,
    };

    private static ArrangementDto ToDto(Arrangement arrangement) => new()
    {
        Nodes = arrangement.Nodes.Select(node => new ArrangementNodeDto
        {
            Id = node.Id,
            Kind = node.Kind,
            ComponentId = node.ComponentId,
            OuterHalfExtent = node.OuterHalfExtent.TwiceMetres,
            RequestedCenter = node.RequestedCenter?.TwiceMetres,
            Handles = node.Handles.Select(handle => new ArrangementHandleDto
            {
                Id = handle.Id,
                Name = handle.Name,
                OffsetFromNodeCenter = handle.OffsetFromNodeCenter.TwiceMetres,
                TargetId = handle.TargetId,
            }).ToList(),
        }).ToList(),
        Gaps = arrangement.Gaps.Select(gap => new ArrangementGapDto
        {
            Id = gap.Id,
            Measure = gap.Measure,
            Value = gap.Value.TwiceMetres,
            NamedGapId = gap.NamedGapId,
        }).ToList(),
        NamedGaps = arrangement.NamedGaps.Select(named => new NamedArrangementGapDto
        {
            Id = named.Id,
            Name = named.Name,
            Measure = named.Measure,
            Value = named.Value.TwiceMetres,
        }).ToList(),
        BowMargin = arrangement.BowMargin.TwiceMetres,
        SternMargin = arrangement.SternMargin.TwiceMetres,
        Anchor = arrangement.Anchor,
        ResizePolicy = arrangement.ResizePolicy,
        FlexibleGapId = arrangement.FlexibleGapId,
    };

    private static InternalStructureDto ToDto(InternalStructure internals) => new()
    {
        JunctionPriority = internals.JunctionPriority.ToList(),
        Families = internals.Families.Select(family => new InternalStructureFamilyDto
        {
            Family = family.Family,
            Enabled = family.Enabled,
            Thickness = family.Thickness,
            Material = family.Material,
            Spacing = family.Spacing.TwiceMetres,
            Count = family.Count,
            Offset = family.Offset.TwiceMetres,
            MirrorSymmetry = family.MirrorSymmetry,
            SpacingKind = family.SpacingKind,
            Datum = family.Datum,
            Direction = family.Direction,
            CountMode = family.CountMode,
            IncludeCentralPlane = family.IncludeCentralPlane,
            RepetitionExtent = family.RepetitionExtent is { } extent
                ? new DesignSpanDto
                {
                    Start = extent.Start.TwiceMetres,
                    End = extent.End.TwiceMetres,
                }
                : null,
        }).ToList(),
    };

    private static BarbetteDefinitionDto ToDto(BarbetteDefinition barbette) => new()
    {
        Id = barbette.Id,
        NodeId = barbette.NodeId,
        ClearDiameter = barbette.ClearDiameter.TwiceMetres,
        ClearDepthMetres = barbette.ClearDepthMetres,
        TopOffsetMetres = barbette.TopOffsetMetres,
        SideArmor = barbette.SideArmor is null ? null : ToDto(barbette.SideArmor),
        RoofArmor = barbette.RoofArmor is null ? null : ToDto(barbette.RoofArmor),
        BottomArmor = barbette.BottomArmor is null ? null : ToDto(barbette.BottomArmor),
        NeckArmor = barbette.NeckArmor is null ? null : ToDto(barbette.NeckArmor),
        NeckClearSizeMetres = barbette.NeckClearSizeMetres,
    };

    private static SuperstructureLayoutDto ToDto(SuperstructureLayout layout) => new()
    {
        Enabled = layout.Enabled,
        Layers = layout.Layers.Select(layer => new SuperstructureLayerDto
        {
            Id = layer.Id,
            Level = layer.Level,
            Modules = layer.Modules.Select(module => new SuperstructureBoxModuleDto
            {
                Id = module.Id,
                OffsetAlong = module.OffsetAlong.TwiceMetres,
                OffsetAthwartships = module.OffsetAthwartships.TwiceMetres,
                Length = module.Length.TwiceMetres,
                Width = module.Width.TwiceMetres,
                ClearHeightMetres = module.ClearHeightMetres,
                WallThicknessMetres = module.WallThicknessMetres,
                RoofThicknessMetres = module.RoofThicknessMetres,
                Material = module.Material,
            }).ToList(),
        }).ToList(),
        TowerRoots = layout.TowerRoots.Select(root => new SuperstructureTowerRootDto
        {
            Id = root.Id,
            SpanNodeId = root.SpanNodeId,
            OffsetFromSpanCenter = root.OffsetFromSpanCenter.TwiceMetres,
            AllowableOffsetRange = root.AllowableOffsetRange?.TwiceMetres,
        }).ToList(),
        Legacy = layout.Legacy is null ? null : new LegacySuperstructureSettingsDto
        {
            UseLegacyGenerator = layout.Legacy.UseLegacyGenerator,
            Settings = ToDto(layout.Legacy.Settings),
        },
    };

    private static ExplicitSlopeRefinementDto? ToDto(ExplicitSlopeRefinement? intent) => intent is null ? null : new()
    {
        RecipeId = intent.RecipeId, RecipeVersion = intent.RecipeVersion,
        Requests = intent.Requests.Select(r => new SlopeExtensionRequestDto {
            Shape = r.Anchor.Shape, Material = r.Anchor.Material, X = r.Anchor.X, Y = r.Anchor.Y, Z = r.Anchor.Z,
            Rotation = r.Anchor.Rotation, Origin = r.Anchor.Origin, ArmorDepth = r.Anchor.ArmorDepth,
            UsePoles = r.Anchor.UsePoles, Construction = r.Anchor.Construction, ArmorRegion = r.Anchor.ArmorRegion,
            VisualLengthMetres = r.VisualLengthMetres }).ToArray(),
    };

    private static ExplicitSlopeRefinement? ToDomainExplicit(ExplicitSlopeRefinementDto? dto) => dto is null ? null : new(
        dto.RecipeId, dto.RecipeVersion, dto.Requests is null ? default : dto.Requests.Select(r => r is null ? null! :
            new FtdHullGenerator.Domain.Decorations.SlopeExtensionRequest(
                new BlockPlacement(r.Shape, r.Material, r.X, r.Y, r.Z, r.Rotation) {
                    Origin = r.Origin, ArmorDepth = r.ArmorDepth, UsePoles = r.UsePoles,
                    Construction = r.Construction, ArmorRegion = r.ArmorRegion }, r.VisualLengthMetres)).ToImmutableArray());

    private static SmoothingSettingsDto ToDto(SmoothingSettings smoothing) => new()
    {
        NativeMethod = smoothing.NativeMethod,
        AlgorithmVersion = smoothing.AlgorithmVersion,
        Refinement = smoothing.Refinement,
        RefinementMaxRunMetres = smoothing.RefinementMaxRunMetres,
        ExplicitRefinement = ToDto(smoothing.ExplicitRefinement),
    };

    private static AppliedStyleProvenanceDto ToDto(AppliedStyleProvenance provenance) => new()
    {
        StyleId = provenance.StyleId,
        StyleVersion = provenance.StyleVersion,
        CopiedFields = new CopiedStyleFieldsDto
        {
            HullShape = provenance.CopiedFields.HullShape,
            Armor = provenance.CopiedFields.Armor,
            Internals = provenance.CopiedFields.Internals,
            ArrangementRules = provenance.CopiedFields.ArrangementRules,
            Superstructure = provenance.CopiedFields.Superstructure,
            Smoothing = provenance.CopiedFields.Smoothing,
            Dimensions = provenance.CopiedFields.Dimensions,
        },
        StyleName = provenance.StyleName,
    };

    private static DocumentExtensionsDto ToDto(DocumentExtensions extensions) => new()
    {
        Values = extensions.Values
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new ExtensionValueDto { Key = pair.Key, Value = pair.Value })
            .ToList(),
    };

    // -----------------------------------------------------------------------------------------
    // DTO -> Domain
    // -----------------------------------------------------------------------------------------

    private static ShipDocument? ToDomain(ShipDocumentDto dto, ProjectConversionContext context)
    {
        var source = ToDomainSource(dto.Source, context);
        var hull = ToDomainHull(dto.Hull, context);
        var arrangement = ToDomainArrangement(dto.Arrangement, context);
        var datum = dto.Datum is null ? null : ToDomainDatum(dto.Datum, context);
        var internals = ToDomainInternals(dto.Internals, context);
        var barbettes = (dto.Barbettes ?? []).Select(barbette => ToDomainBarbette(barbette, context))
            .OfType<BarbetteDefinition>().ToImmutableArray();
        var superstructure = ToDomainSuperstructure(dto.Superstructure, context);
        var smoothing = ToDomainSmoothing(dto.Smoothing, context);
        var appliedStyle = dto.AppliedStyle is null ? null : ToDomainAppliedStyle(dto.AppliedStyle, context);
        var extensions = dto.Extensions is null ? null : ToDomainExtensions(dto.Extensions, context);

        if (context.HasErrors || source is null || hull is null || arrangement is null ||
            internals is null || superstructure is null || smoothing is null)
            return null;

        return new ShipDocument(
            dto.SchemaVersion,
            dto.DocumentId,
            dto.Name,
            dto.GenerationVersion,
            source,
            hull,
            arrangement,
            datum,
            internals,
            barbettes,
            superstructure,
            smoothing,
            appliedStyle,
            extensions);
    }

    private static HullSource? ToDomainSource(HullSourceDto? dto, ProjectConversionContext context)
    {
        if (dto is null)
        {
            context.Error(ProjectDiagnosticCodes.InvalidValue, "The document has no hull source.", field: "source");
            return null;
        }

        if (!Enum.IsDefined(dto.Kind))
        {
            context.Error(ProjectDiagnosticCodes.InvalidValue, "The document declares an unsupported hull source kind.",
                field: nameof(HullSource.Kind));
            return null;
        }

        return new HullSource(dto.Kind, dto.AssetId, dto.AssetVersion, dto.AssetHash);
    }

    private static HullParameters? ToDomainHull(HullParametersDto? dto, ProjectConversionContext context)
    {
        if (dto is null)
        {
            context.Error(ProjectDiagnosticCodes.InvalidValue, "The document has no hull parameters.", field: "hull");
            return null;
        }

        var bowFullness = context.Finite(dto.BowFullness, nameof(HullParameters.BowFullness));
        var sternFullness = context.Finite(dto.SternFullness, nameof(HullParameters.SternFullness));
        var crossSection = context.Finite(dto.CrossSectionCurve, nameof(HullParameters.CrossSectionCurve));
        var hullArmor = ToDomainArmorLayout(dto.HullArmor, nameof(HullParameters.HullArmor), context);
        var deckArmor = dto.DeckArmor is null
            ? null
            : ToDomainArmorLayout(dto.DeckArmor, nameof(HullParameters.DeckArmor), context);
        var bottomArmor = dto.BottomArmor is null
            ? null
            : ToDomainArmorLayout(dto.BottomArmor, nameof(HullParameters.BottomArmor), context);
        var shape = dto.Shape is null ? null : ToDomainShape(dto.Shape, context);
        BulbSettings? bulb = dto.Bulb is null ? null : ToDomainBulb(dto.Bulb);
        var superstructure = dto.Superstructure is null ? null : ToDomainSuperstructureSettings(dto.Superstructure, context);

        context.RequireDefined(dto.Smoothing, nameof(HullParameters.Smoothing));
        context.RequireDefined(dto.BowStyle, nameof(HullParameters.BowStyle));
        context.RequireDefined(dto.SternStyle, nameof(HullParameters.SternStyle));

        if (context.HasErrors || bowFullness is null || sternFullness is null || crossSection is null ||
            hullArmor is null)
            return null;

        return new HullParameters(
            dto.Length,
            dto.Width,
            dto.Height,
            bowFullness.Value,
            sternFullness.Value,
            crossSection.Value,
            hullArmor,
            deckArmor,
            dto.Beamify,
            dto.Smoothing,
            dto.HybridFillOffset,
            dto.BowStyle,
            dto.SternStyle,
            dto.HasBulb,
            bulb,
            shape,
            bottomArmor,
            superstructure);
    }

    private static ArmorLayout? ToDomainArmorLayout(ArmorLayoutDto? dto, string field, ProjectConversionContext context)
    {
        if (dto is null)
        {
            context.Error(ProjectDiagnosticCodes.InvalidValue, $"The {field} armor layout is missing.", field: field);
            return null;
        }

        var layers = dto.Layers ?? [];
        if (layers.Count == 0)
        {
            context.Error(ProjectDiagnosticCodes.InvalidValue, $"The {field} armor layout has no layers.", field: field);
            return null;
        }

        if (layers.Count > MaxArmorLayers)
        {
            context.Error(ProjectDiagnosticCodes.CountLimit,
                $"The {field} armor layout has {layers.Count} layers; the cap is {MaxArmorLayers}.", field: field);
            return null;
        }

        var built = new List<ArmorLayer>(layers.Count);
        foreach (var layer in layers)
        {
            if (layer is null)
            {
                context.Error(ProjectDiagnosticCodes.InvalidValue, $"The {field} armor layout has a null layer.",
                    field: field);
                continue;
            }

            if (layer.Material is { } material && !Enum.IsDefined(material))
            {
                context.Error(ProjectDiagnosticCodes.InvalidValue,
                    $"The {field} armor layout declares an unsupported material.", field: field);
                continue;
            }

            if (!Enum.IsDefined(layer.Construction))
            {
                context.Error(ProjectDiagnosticCodes.InvalidValue,
                    $"The {field} armor layout declares an unsupported construction.", field: field);
                continue;
            }

            try
            {
                built.Add(new ArmorLayer(layer.Material, layer.Construction));
            }
            catch (ArgumentException exception)
            {
                context.Error(ProjectDiagnosticCodes.InvalidValue,
                    $"The {field} armor layer is invalid: {exception.Message}", field: field);
            }
        }

        if (built.Count == 0)
            return null;

        try
        {
            return new ArmorLayout(built);
        }
        catch (ArgumentException exception)
        {
            context.Error(ProjectDiagnosticCodes.InvalidValue,
                $"The {field} armor layout is invalid: {exception.Message}", field: field);
            return null;
        }
    }

    private static HullShapeSettings? ToDomainShape(HullShapeDto? dto, ProjectConversionContext context)
    {
        if (dto?.Bow is null || dto.Body is null || dto.Stern is null || dto.Profile is null)
        {
            context.Error(ProjectDiagnosticCodes.InvalidValue, "The shape settings are incomplete.",
                field: nameof(HullParameters.Shape));
            return null;
        }

        var bowFullness = context.Finite(dto.Bow.Fullness, nameof(HullShapeSettings.Bow));
        var bowFlare = context.Finite(dto.Bow.Flare, nameof(HullShapeSettings.Bow));
        var bodyFullness = context.Finite(dto.Body.Fullness, nameof(HullShapeSettings.Body));
        var sideShape = context.Finite(dto.Body.SideShape, nameof(HullShapeSettings.Body));
        var chine = context.Finite(dto.Body.Chine, nameof(HullShapeSettings.Body));
        var flatBottom = context.Finite(dto.Body.FlatBottom, nameof(HullShapeSettings.Body));
        var sternFullness = context.Finite(dto.Stern.Fullness, nameof(HullShapeSettings.Stern));
        var sternSide = context.Finite(dto.Stern.SideShape, nameof(HullShapeSettings.Stern));
        context.RequireDefined(dto.Body.Style, nameof(HullShapeSettings.Body));

        if (context.HasErrors || bowFullness is null || bowFlare is null || bodyFullness is null ||
            sideShape is null || chine is null || flatBottom is null || sternFullness is null || sternSide is null)
            return null;

        return new HullShapeSettings(
            new BowShapeSettings(bowFullness.Value, bowFlare.Value, dto.Bow.EntranceLengthPercent),
            new BodyShapeSettings(dto.Body.Style, bodyFullness.Value, sideShape.Value, chine.Value, flatBottom.Value),
            new SternShapeSettings(sternFullness.Value, sternSide.Value, dto.Stern.RunLengthPercent),
            new HullProfileSettings(dto.Profile.BowDeckRise, dto.Profile.SternDeckRise,
                dto.Profile.BowKeelRise, dto.Profile.SternKeelRise));
    }

    private static BulbSettings ToDomainBulb(BulbSettingsDto dto) => new(
        dto.LengthPercent, dto.WidthPercent, dto.ForeAftPercent, dto.RisePercent);

    private static SuperstructureSettings? ToDomainSuperstructureSettings(
        SuperstructureSettingsDto dto,
        ProjectConversionContext context)
    {
        context.RequireDefined(dto.Style, nameof(SuperstructureSettings.Style));
        context.RequireDefined(dto.Material, nameof(SuperstructureSettings.Material));
        context.RequireDefined(dto.Smoothing, nameof(SuperstructureSettings.Smoothing));
        if (context.HasErrors)
            return null;

        return new SuperstructureSettings(dto.Enabled, dto.Style, dto.Levels, dto.Material, dto.Smoothing,
            dto.ForeAftPercent);
    }

    private static LayoutDatum? ToDomainDatum(LayoutDatumDto dto, ProjectConversionContext context)
    {
        var bow = context.Measure(dto.LayoutBowZ, nameof(LayoutDatum.LayoutBowZ));
        var center = context.Measure(dto.CenterPlaneX, nameof(LayoutDatum.CenterPlaneX));
        if (bow is null || center is null)
            return null;
        return new LayoutDatum(bow.Value, center.Value);
    }

    private static Arrangement? ToDomainArrangement(ArrangementDto? dto, ProjectConversionContext context)
    {
        if (dto is null)
        {
            context.Error(ProjectDiagnosticCodes.InvalidValue, "The document has no arrangement.",
                field: nameof(ShipDocument.Arrangement));
            return null;
        }

        var nodes = new List<ArrangementNode>();
        foreach (var node in dto.Nodes ?? [])
        {
            if (node is null)
            {
                context.Error(ProjectDiagnosticCodes.InvalidValue, "The arrangement has a null node.",
                    field: nameof(Arrangement.Nodes));
                continue;
            }

            context.RequireDefined(node.Kind, nameof(ArrangementNode.Kind));
            var extent = context.Measure(node.OuterHalfExtent, nameof(ArrangementNode.OuterHalfExtent));
            var handles = new List<ArrangementHandle>();
            foreach (var handle in node.Handles ?? [])
            {
                if (handle is null)
                {
                    context.Error(ProjectDiagnosticCodes.InvalidValue, "The arrangement has a null handle.",
                        field: nameof(ArrangementNode.Handles));
                    continue;
                }

                var offset = context.Measure(handle.OffsetFromNodeCenter,
                    nameof(ArrangementHandle.OffsetFromNodeCenter));
                if (offset is not null)
                    handles.Add(new ArrangementHandle(handle.Id, handle.Name, offset.Value, handle.TargetId));
            }

            if (extent is not null)
            {
                DesignMeasure? requestedCenter = null;
                if (node.RequestedCenter is { } requestedTwiceMetres)
                    requestedCenter = context.Measure(requestedTwiceMetres, nameof(ArrangementNode.RequestedCenter));
                nodes.Add(new ArrangementNode(node.Id, node.Kind, node.ComponentId, extent.Value,
                    [.. handles], requestedCenter));
            }
        }

        var gaps = new List<ArrangementGap>();
        foreach (var gap in dto.Gaps ?? [])
        {
            if (gap is null)
            {
                context.Error(ProjectDiagnosticCodes.InvalidValue, "The arrangement has a null gap.",
                    field: nameof(Arrangement.Gaps));
                continue;
            }

            context.RequireDefined(gap.Measure, nameof(ArrangementGap.Measure));
            var value = context.Measure(gap.Value, nameof(ArrangementGap.Value));
            if (value is not null)
                gaps.Add(new ArrangementGap(gap.Id, gap.Measure, value.Value, gap.NamedGapId));
        }

        var namedGaps = new List<NamedArrangementGap>();
        foreach (var named in dto.NamedGaps ?? [])
        {
            if (named is null)
            {
                context.Error(ProjectDiagnosticCodes.InvalidValue, "The arrangement has a null named gap.",
                    field: nameof(Arrangement.NamedGaps));
                continue;
            }

            context.RequireDefined(named.Measure, nameof(NamedArrangementGap.Measure));
            var value = context.Measure(named.Value, nameof(NamedArrangementGap.Value));
            if (value is not null)
                namedGaps.Add(new NamedArrangementGap(named.Id, named.Name, named.Measure, value.Value));
        }

        var bowMargin = context.Measure(dto.BowMargin, nameof(Arrangement.BowMargin));
        var sternMargin = context.Measure(dto.SternMargin, nameof(Arrangement.SternMargin));
        context.RequireDefined(dto.Anchor, nameof(Arrangement.Anchor));
        context.RequireDefined(dto.ResizePolicy, nameof(Arrangement.ResizePolicy));

        if (context.HasErrors || bowMargin is null || sternMargin is null)
            return null;

        return new Arrangement([.. nodes], [.. gaps], [.. namedGaps], bowMargin.Value, sternMargin.Value,
            dto.Anchor, dto.ResizePolicy, dto.FlexibleGapId);
    }

    private static InternalStructure? ToDomainInternals(InternalStructureDto? dto, ProjectConversionContext context)
    {
        if (dto is null)
        {
            context.Error(ProjectDiagnosticCodes.InvalidValue, "The document has no internal structure.",
                field: nameof(ShipDocument.Internals));
            return null;
        }

        var families = new List<InternalStructureFamily>();
        foreach (var family in dto.Families ?? [])
        {
            if (family is null)
            {
                context.Error(ProjectDiagnosticCodes.InvalidValue, "The internal structure has a null family.",
                    field: nameof(InternalStructure.Families));
                continue;
            }

            context.RequireDefined(family.Family, nameof(InternalStructureFamily.Family));
            context.RequireDefined(family.Material, nameof(InternalStructureFamily.Material));
            if (family.SpacingKind is { } spacingKind)
                context.RequireDefined(spacingKind, nameof(InternalStructureFamily.SpacingKind));
            if (family.Datum is { } datum)
                context.RequireDefined(datum, nameof(InternalStructureFamily.Datum));
            if (family.Direction is { } direction)
                context.RequireDefined(direction, nameof(InternalStructureFamily.Direction));
            if (family.CountMode is { } countMode)
                context.RequireDefined(countMode, nameof(InternalStructureFamily.CountMode));
            var spacing = context.Measure(family.Spacing, nameof(InternalStructureFamily.Spacing));
            var offset = context.Measure(family.Offset, nameof(InternalStructureFamily.Offset));
            var extentStart = family.RepetitionExtent is null
                ? null
                : context.Measure(family.RepetitionExtent.Start, nameof(InternalStructureFamily.RepetitionExtent));
            var extentEnd = family.RepetitionExtent is null
                ? null
                : context.Measure(family.RepetitionExtent.End, nameof(InternalStructureFamily.RepetitionExtent));
            DesignSpan? repetitionExtent = null;
            if (extentStart is not null && extentEnd is not null)
            {
                if (extentEnd.Value < extentStart.Value)
                    context.Error(ProjectDiagnosticCodes.InvalidValue,
                        "An internal repetition extent cannot end before it starts.",
                        field: nameof(InternalStructureFamily.RepetitionExtent));
                else
                    repetitionExtent = new DesignSpan(extentStart.Value, extentEnd.Value);
            }
            var isLegacyFamily = family.SpacingKind is null && family.Datum is null &&
                                 family.Direction is null && family.CountMode is null &&
                                 family.IncludeCentralPlane is null && family.RepetitionExtent is null;
            if (spacing is not null && offset is not null &&
                (family.RepetitionExtent is null || repetitionExtent is not null))
            {
                families.Add(new InternalStructureFamily(family.Family, family.Enabled, family.Thickness,
                    family.Material, spacing.Value, family.Count, offset.Value,
                    isLegacyFamily && family.Family == InternalPlaneFamily.LongitudinalBulkhead
                        ? true
                        : family.MirrorSymmetry)
                {
                    SpacingKind = family.SpacingKind ?? InternalSpacingKind.CenterPitch,
                    Datum = family.Datum,
                    Direction = family.Direction,
                    CountMode = family.CountMode ?? InternalPlaneCountMode.FixedCount,
                    IncludeCentralPlane = family.IncludeCentralPlane ??
                        (family.Family == InternalPlaneFamily.LongitudinalBulkhead && (family.Count & 1) != 0),
                    RepetitionExtent = repetitionExtent,
                });
            }
        }

        var junctionPriority = InternalStructure.DefaultJunctionPriority;
        if (dto.JunctionPriority is not null)
        {
            foreach (var family in dto.JunctionPriority)
                context.RequireDefined(family, nameof(InternalStructure.JunctionPriority));
            junctionPriority = [.. dto.JunctionPriority];
        }

        if (context.HasErrors)
            return null;

        return new InternalStructure([.. families]) { JunctionPriority = junctionPriority };
    }

    private static BarbetteDefinition? ToDomainBarbette(BarbetteDefinitionDto? dto, ProjectConversionContext context)
    {
        if (dto is null)
        {
            context.Error(ProjectDiagnosticCodes.InvalidValue, "The document has a null barbette.",
                field: nameof(ShipDocument.Barbettes));
            return null;
        }

        if (dto.ClearDiameter is null || dto.ClearDepthMetres is null || dto.NeckClearSizeMetres is null ||
            dto.SideArmor is null || dto.RoofArmor is null || dto.BottomArmor is null || dto.NeckArmor is null)
        {
            var hasLegacy = dto.OuterDiameter is not null || dto.ClearBoreDiameter is not null ||
                            dto.WallThicknessMetres is not null || dto.HeightMetres is not null ||
                            dto.WellDepthMetres is not null || dto.Material is not null;
            context.Error(ProjectDiagnosticCodes.BarbetteMigrationUnsupported,
                hasLegacy
                    ? $"Barbette '{dto.Id}' was saved with the superseded outside-diameter/single-material " +
                      "model. It cannot map losslessly to the frozen clear-volume model, so no armor was invented."
                    : $"Barbette '{dto.Id}' is missing one or more frozen clear-volume fields.",
                field: nameof(ShipDocument.Barbettes));
            return null;
        }

        var clear = context.Measure(dto.ClearDiameter.Value, nameof(BarbetteDefinition.ClearDiameter));
        var side = ToDomainArmorLayout(dto.SideArmor, $"{nameof(BarbetteDefinition.SideArmor)}[{dto.Id}]", context);
        var roof = ToDomainArmorLayout(dto.RoofArmor, $"{nameof(BarbetteDefinition.RoofArmor)}[{dto.Id}]", context);
        var bottom = ToDomainArmorLayout(dto.BottomArmor, $"{nameof(BarbetteDefinition.BottomArmor)}[{dto.Id}]", context);
        var neck = ToDomainArmorLayout(dto.NeckArmor, $"{nameof(BarbetteDefinition.NeckArmor)}[{dto.Id}]", context);
        if (clear is null || side is null || roof is null || bottom is null || neck is null)
            return null;

        return new BarbetteDefinition(dto.Id, dto.NodeId, clear.Value, dto.ClearDepthMetres.Value,
            dto.TopOffsetMetres ?? 0, side, roof, bottom, neck, dto.NeckClearSizeMetres.Value);
    }

    private static SuperstructureLayout? ToDomainSuperstructure(
        SuperstructureLayoutDto? dto,
        ProjectConversionContext context)
    {
        if (dto is null)
        {
            context.Error(ProjectDiagnosticCodes.InvalidValue, "The document has no superstructure.",
                field: nameof(ShipDocument.Superstructure));
            return null;
        }

        var layers = new List<SuperstructureLayer>();
        foreach (var layer in dto.Layers ?? [])
        {
            if (layer is null)
            {
                context.Error(ProjectDiagnosticCodes.InvalidValue, "The superstructure has a null layer.",
                    field: nameof(SuperstructureLayout.Layers));
                continue;
            }

            var modules = new List<SuperstructureBoxModule>();
            foreach (var module in layer.Modules ?? [])
            {
                if (module is null)
                {
                    context.Error(ProjectDiagnosticCodes.InvalidValue, "A superstructure layer has a null module.",
                        field: nameof(SuperstructureLayer.Modules));
                    continue;
                }

                var along = context.Measure(module.OffsetAlong, nameof(SuperstructureBoxModule.OffsetAlong));
                var athwart = context.Measure(module.OffsetAthwartships,
                    nameof(SuperstructureBoxModule.OffsetAthwartships));
                var length = context.Measure(module.Length, nameof(SuperstructureBoxModule.Length));
                var width = context.Measure(module.Width, nameof(SuperstructureBoxModule.Width));
                context.RequireDefined(module.Material, nameof(SuperstructureBoxModule.Material));
                if (along is null || athwart is null || length is null || width is null)
                    continue;

                modules.Add(new SuperstructureBoxModule(module.Id, along.Value, athwart.Value, length.Value,
                    width.Value, module.ClearHeightMetres, module.WallThicknessMetres,
                    module.RoofThicknessMetres, module.Material));
            }

            layers.Add(new SuperstructureLayer(layer.Id, layer.Level, [.. modules]));
        }

        var roots = new List<SuperstructureTowerRoot>();
        foreach (var root in dto.TowerRoots ?? [])
        {
            if (root is null)
            {
                context.Error(ProjectDiagnosticCodes.InvalidValue, "The superstructure has a null tower root.",
                    field: nameof(SuperstructureLayout.TowerRoots));
                continue;
            }

            var offset = context.Measure(root.OffsetFromSpanCenter, nameof(SuperstructureTowerRoot.OffsetFromSpanCenter));
            DesignMeasure? allowance = null;
            if (root.AllowableOffsetRange is { } allowanceValue)
                allowance = context.Measure(allowanceValue, nameof(SuperstructureTowerRoot.AllowableOffsetRange));
            if (offset is not null)
                roots.Add(new SuperstructureTowerRoot(root.Id, root.SpanNodeId, offset.Value, allowance));
        }

        LegacySuperstructureSettings? legacy = null;
        if (dto.Legacy is not null)
        {
            if (dto.Legacy.Settings is null)
            {
                context.Error(ProjectDiagnosticCodes.InvalidValue, "The legacy superstructure settings are missing.",
                    field: nameof(SuperstructureLayout.Legacy));
            }
            else if (ToDomainSuperstructureSettings(dto.Legacy.Settings, context) is { } settings)
            {
                legacy = new LegacySuperstructureSettings(dto.Legacy.UseLegacyGenerator, settings);
            }
        }

        if (context.HasErrors)
            return null;

        return new SuperstructureLayout(dto.Enabled, [.. layers], [.. roots], legacy);
    }

    private static SmoothingSettings? ToDomainSmoothing(SmoothingSettingsDto? dto, ProjectConversionContext context)
    {
        if (dto is null)
        {
            context.Error(ProjectDiagnosticCodes.InvalidValue, "The document has no smoothing settings.",
                field: nameof(ShipDocument.Smoothing));
            return null;
        }

        context.RequireDefined(dto.NativeMethod, nameof(SmoothingSettings.NativeMethod));
        context.RequireDefined(dto.Refinement, nameof(SmoothingSettings.Refinement));
        if (context.HasErrors)
            return null;

        return new SmoothingSettings(dto.NativeMethod, dto.AlgorithmVersion, dto.Refinement,
            dto.RefinementMaxRunMetres) { ExplicitRefinement = ToDomainExplicit(dto.ExplicitRefinement) };
    }

    private static AppliedStyleProvenance? ToDomainAppliedStyle(
        AppliedStyleProvenanceDto dto,
        ProjectConversionContext context)
    {
        if (dto.CopiedFields is null)
        {
            context.Error(ProjectDiagnosticCodes.InvalidValue, "The applied style has no copied-field mask.",
                field: nameof(AppliedStyleProvenance.CopiedFields));
            return null;
        }

        return new AppliedStyleProvenance(dto.StyleId, dto.StyleVersion, new CopiedStyleFields(
            dto.CopiedFields.HullShape,
            dto.CopiedFields.Armor,
            dto.CopiedFields.Internals,
            dto.CopiedFields.ArrangementRules,
            dto.CopiedFields.Superstructure,
            dto.CopiedFields.Smoothing,
            dto.CopiedFields.Dimensions), dto.StyleName);
    }

    private static DocumentExtensions ToDomainExtensions(DocumentExtensionsDto dto, ProjectConversionContext context)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var pair in dto.Values ?? [])
        {
            if (pair is null)
                continue;
            if (!builder.TryAdd(pair.Key, pair.Value))
                context.Error(ProjectDiagnosticCodes.InvalidValue,
                    $"The extension key '{pair.Key}' appears more than once.", field: nameof(DocumentExtensions.Values));
        }

        return new DocumentExtensions(builder.ToImmutable());
    }
}
