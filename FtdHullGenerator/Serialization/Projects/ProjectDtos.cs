using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Components;
using FtdHullGenerator.Domain.Layout;
using FtdHullGenerator.Domain.Projects;
using FtdHullGenerator.Domain.Superstructures;

namespace FtdHullGenerator.Serialization.Projects;

// Explicit serialization DTOs for the whole ShipDocument graph. They exist for two reasons:
//
//   1. HullParameters, ArmorLayout and ArmorLayer do not survive a default System.Text.Json round
//      trip (ArmorLayout throws NotSupportedException; ArmorLayer silently becomes Air). The DTO
//      adapter is the fix.
//   2. The wire format must persist identifiers and values, never .NET type names, and it must not
//      emit derived/computed properties (HullParameters.EffectiveShape, and so on). An explicit DTO
//      pins exactly the persisted surface.
//
// DTOs are internal: the store and serializer are the public entry points, and the SelfTest project
// sees these through InternalsVisibleTo.

internal sealed class ShipDocumentEnvelopeDto
{
    public string Format { get; init; } = string.Empty;
    public int FormatVersion { get; init; }
    public ShipDocumentDto? Document { get; init; }
}

internal sealed class ShipDocumentDto
{
    public int SchemaVersion { get; init; }
    public string DocumentId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string GenerationVersion { get; init; } = string.Empty;
    public HullSourceDto? Source { get; init; }
    public HullParametersDto? Hull { get; init; }
    public ArrangementDto? Arrangement { get; init; }
    public LayoutDatumDto? Datum { get; init; }
    public InternalStructureDto? Internals { get; init; }
    public List<BarbetteDefinitionDto> Barbettes { get; init; } = [];
    public SuperstructureLayoutDto? Superstructure { get; init; }
    public SmoothingSettingsDto? Smoothing { get; init; }
    public AppliedStyleProvenanceDto? AppliedStyle { get; init; }
    public DocumentExtensionsDto? Extensions { get; init; }
}

internal sealed class HullSourceDto
{
    public HullSourceKind Kind { get; init; }
    public string? AssetId { get; init; }
    public string? AssetVersion { get; init; }
    public string? AssetHash { get; init; }
}

internal sealed class HullParametersDto
{
    public int Length { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public double BowFullness { get; init; }
    public double SternFullness { get; init; }
    public double CrossSectionCurve { get; init; }
    public ArmorLayoutDto? HullArmor { get; init; }
    public ArmorLayoutDto? DeckArmor { get; init; }
    public bool Beamify { get; init; }
    public SmoothingMethod Smoothing { get; init; }
    public int HybridFillOffset { get; init; }
    public BowStyle BowStyle { get; init; }
    public SternStyle SternStyle { get; init; }
    public bool HasBulb { get; init; }
    public BulbSettingsDto? Bulb { get; init; }
    public HullShapeDto? Shape { get; init; }
    public ArmorLayoutDto? BottomArmor { get; init; }
    public SuperstructureSettingsDto? Superstructure { get; init; }
}

internal sealed class ArmorLayoutDto
{
    public List<ArmorLayerDto> Layers { get; init; } = [];
}

internal sealed class ArmorLayerDto
{
    /// <summary>Null is a deliberate one-metre air gap, not a missing value.</summary>
    public MaterialKind? Material { get; init; }

    public ArmorConstruction Construction { get; init; }
}

internal sealed class HullShapeDto
{
    public BowShapeDto? Bow { get; init; }
    public BodyShapeDto? Body { get; init; }
    public SternShapeDto? Stern { get; init; }
    public HullProfileDto? Profile { get; init; }
}

internal sealed class BowShapeDto
{
    public double Fullness { get; init; }
    public double Flare { get; init; }
    public int EntranceLengthPercent { get; init; }
}

internal sealed class BodyShapeDto
{
    public BodyStyle Style { get; init; }
    public double Fullness { get; init; }
    public double SideShape { get; init; }
    public double Chine { get; init; }
    public double FlatBottom { get; init; }
}

internal sealed class SternShapeDto
{
    public double Fullness { get; init; }
    public double SideShape { get; init; }
    public int RunLengthPercent { get; init; }
}

internal sealed class HullProfileDto
{
    public int BowDeckRise { get; init; }
    public int SternDeckRise { get; init; }
    public int BowKeelRise { get; init; }
    public int SternKeelRise { get; init; }
}

internal sealed class BulbSettingsDto
{
    public int LengthPercent { get; init; }
    public int WidthPercent { get; init; }
    public int ForeAftPercent { get; init; }
    public int RisePercent { get; init; }
}

internal sealed class SuperstructureSettingsDto
{
    public bool Enabled { get; init; }
    public SuperstructureStyle Style { get; init; }
    public int Levels { get; init; }
    public MaterialKind Material { get; init; }
    public SuperstructureSmoothingMethod Smoothing { get; init; }
    public int ForeAftPercent { get; init; }
}

internal sealed class LayoutDatumDto
{
    /// <summary>Twice-metre integer, so a half-cell centre survives exactly.</summary>
    public int LayoutBowZ { get; init; }

    public int CenterPlaneX { get; init; }
}

internal sealed class ArrangementDto
{
    public List<ArrangementNodeDto> Nodes { get; init; } = [];
    public List<ArrangementGapDto> Gaps { get; init; } = [];
    public List<NamedArrangementGapDto> NamedGaps { get; init; } = [];
    public int BowMargin { get; init; }
    public int SternMargin { get; init; }
    public ArrangementAnchorKind Anchor { get; init; }
    public ArrangementResizePolicy ResizePolicy { get; init; }
    public string? FlexibleGapId { get; init; }
}

internal sealed class ArrangementNodeDto
{
    public string Id { get; init; } = string.Empty;
    public ArrangementNodeKind Kind { get; init; }
    public string ComponentId { get; init; } = string.Empty;
    public int OuterHalfExtent { get; init; }
    public List<ArrangementHandleDto> Handles { get; init; } = [];

    /// <summary>Frozen 2.0 explicit ruler centre; null keeps the legacy linked-gap chain.</summary>
    public int? RequestedCenter { get; init; }
}

internal sealed class ArrangementHandleDto
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public int OffsetFromNodeCenter { get; init; }
    public string? TargetId { get; init; }
}

internal sealed class ArrangementGapDto
{
    public string Id { get; init; } = string.Empty;
    public ArrangementMeasure Measure { get; init; }
    public int Value { get; init; }
    public string? NamedGapId { get; init; }
}

internal sealed class NamedArrangementGapDto
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public ArrangementMeasure Measure { get; init; }
    public int Value { get; init; }
}

internal sealed class InternalStructureDto
{
    public List<InternalStructureFamilyDto> Families { get; init; } = [];
    public List<InternalPlaneFamily>? JunctionPriority { get; init; }
}

internal sealed class InternalStructureFamilyDto
{
    public InternalPlaneFamily Family { get; init; }
    public bool Enabled { get; init; }
    public int Thickness { get; init; }
    public MaterialKind Material { get; init; }
    public int Spacing { get; init; }
    public int Count { get; init; }
    public int Offset { get; init; }
    public bool MirrorSymmetry { get; init; }
    public InternalSpacingKind? SpacingKind { get; init; }
    public InternalPlaneDatum? Datum { get; init; }
    public InternalRepeatDirection? Direction { get; init; }
    public InternalPlaneCountMode? CountMode { get; init; }
    public bool? IncludeCentralPlane { get; init; }
    public DesignSpanDto? RepetitionExtent { get; init; }
}

internal sealed class DesignSpanDto
{
    public int Start { get; init; }
    public int End { get; init; }
}

internal sealed class BarbetteDefinitionDto
{
    public string Id { get; init; } = string.Empty;
    public string NodeId { get; init; } = string.Empty;

    // Frozen 2.0 clear-volume intent.
    public int? ClearDiameter { get; init; }
    public int? ClearDepthMetres { get; init; }
    public int? TopOffsetMetres { get; init; }
    public ArmorLayoutDto? SideArmor { get; init; }
    public ArmorLayoutDto? RoofArmor { get; init; }
    public ArmorLayoutDto? BottomArmor { get; init; }
    public ArmorLayoutDto? NeckArmor { get; init; }
    public int? NeckClearSizeMetres { get; init; }

    // Superseded pre-frozen fields. They are retained only so a legacy file can be recognized and
    // refused explicitly instead of being silently reinterpreted as frozen clear-volume intent.
    public int? OuterDiameter { get; init; }
    public int? ClearBoreDiameter { get; init; }
    public int? WallThicknessMetres { get; init; }
    public int? HeightMetres { get; init; }
    public int? WellDepthMetres { get; init; }
    public MaterialKind? Material { get; init; }
    public bool? RequiresFlatDeckSupport { get; init; }
}

internal sealed class SuperstructureLayoutDto
{
    public bool Enabled { get; init; }
    public List<SuperstructureLayerDto> Layers { get; init; } = [];
    public List<SuperstructureTowerRootDto> TowerRoots { get; init; } = [];
    public LegacySuperstructureSettingsDto? Legacy { get; init; }
}

internal sealed class SuperstructureLayerDto
{
    public string Id { get; init; } = string.Empty;
    public int Level { get; init; }
    public List<SuperstructureBoxModuleDto> Modules { get; init; } = [];
}

internal sealed class SuperstructureBoxModuleDto
{
    public string Id { get; init; } = string.Empty;
    public int OffsetAlong { get; init; }
    public int OffsetAthwartships { get; init; }
    public int Length { get; init; }
    public int Width { get; init; }
    public int ClearHeightMetres { get; init; }
    public int WallThicknessMetres { get; init; }
    public int RoofThicknessMetres { get; init; }
    public MaterialKind Material { get; init; }
}

internal sealed class SuperstructureTowerRootDto
{
    public string Id { get; init; } = string.Empty;
    public string SpanNodeId { get; init; } = string.Empty;
    public int OffsetFromSpanCenter { get; init; }
    public int? AllowableOffsetRange { get; init; }
}

internal sealed class LegacySuperstructureSettingsDto
{
    public bool UseLegacyGenerator { get; init; }
    public SuperstructureSettingsDto? Settings { get; init; }
}

internal sealed class SmoothingSettingsDto
{
    public ExplicitSlopeRefinementDto? ExplicitRefinement { get; init; }
    public SmoothingMethod NativeMethod { get; init; }
    public int AlgorithmVersion { get; init; }
    public DecorativeRefinementKind Refinement { get; init; }
    public int RefinementMaxRunMetres { get; init; }
}

internal sealed class AppliedStyleProvenanceDto
{
    public string StyleId { get; init; } = string.Empty;
    public int StyleVersion { get; init; }
    public CopiedStyleFieldsDto? CopiedFields { get; init; }
    public string? StyleName { get; init; }
}

internal sealed class CopiedStyleFieldsDto
{
    public bool HullShape { get; init; }
    public bool Armor { get; init; }
    public bool Internals { get; init; }
    public bool ArrangementRules { get; init; }
    public bool Superstructure { get; init; }
    public bool Smoothing { get; init; }
    public bool Dimensions { get; init; }
}

internal sealed class DocumentExtensionsDto
{
    /// <summary>Ordered by key so a canonical round trip is byte-for-byte deterministic.</summary>
    public List<ExtensionValueDto> Values { get; init; } = [];
}

internal sealed class ExtensionValueDto
{
    public string Key { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
}

internal sealed class ExplicitSlopeRefinementDto
{
    public string RecipeId { get; init; } = "";
    public int RecipeVersion { get; init; }
    public SlopeExtensionRequestDto[]? Requests { get; init; }
}
internal sealed class SlopeExtensionRequestDto
{
    public BlockShape Shape { get; init; }
    public MaterialKind Material { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public int Z { get; init; }
    public int Rotation { get; init; }
    public BlockOrigin Origin { get; init; }
    public int ArmorDepth { get; init; }
    public bool UsePoles { get; init; }
    public ArmorConstruction Construction { get; init; }
    public ArmorRegion ArmorRegion { get; init; }
    public int VisualLengthMetres { get; init; }
}
