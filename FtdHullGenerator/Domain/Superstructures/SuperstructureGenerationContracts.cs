using System.Collections.Immutable;
using FtdHullGenerator.Domain.Composition;
using FtdHullGenerator.Domain.Design;

namespace FtdHullGenerator.Domain.Superstructures;

/// <summary>
/// Selects the generator which owns a superstructure. The legacy route is deliberately distinct:
/// modular generation never interprets old style settings as an editable box graph.
/// </summary>
public enum SuperstructureGenerationRoute
{
    ModularLayersV2 = 0,
    LegacyVersion1 = 1,
}

/// <summary>
/// The arrangement-owned root used to place one modular graph in world space. Both identifiers are
/// stable joins; an arrangement display name or list index is never accepted as a substitute.
/// </summary>
public sealed record SuperstructureRootPlacement(
    string Id,
    string SpanNodeId,
    DesignMeasure WorldX,
    DesignMeasure WorldZ);

/// <summary>One realized superstructure cell before packing or catalog resolution.</summary>
public sealed record SuperstructureCellIntent(
    HullCell Cell,
    MaterialKind Material,
    ImmutableArray<string> ModuleIds,
    SuperstructureCellRole Role);

public enum SuperstructureCellRole
{
    Wall = 0,
    Roof = 1,
    WallAndRoof = 2,
}

/// <summary>
/// Exact outer edges of the union in root-local coordinates. The longitudinal half extent is what
/// L01 consumes for its arrangement node; the centre offset keeps asymmetric graphs exact.
/// </summary>
public sealed record SuperstructureMeasuredFootprint(
    DesignSpan Along,
    DesignSpan Athwartships,
    DesignMeasure CenterOffsetAlong,
    DesignMeasure CenterOffsetAthwartships,
    DesignMeasure LongitudinalHalfExtent,
    DesignMeasure AthwartshipsHalfExtent)
{
    public DesignMeasure RealizedLength => Along.Length;
    public DesignMeasure RealizedWidth => Athwartships.Length;
}

public sealed record SuperstructureFootprintMeasurement(
    SuperstructureMeasuredFootprint? Footprint,
    ImmutableArray<DesignDiagnostic> Diagnostics)
{
    public bool IsValid => Footprint is not null && !Diagnostics.Any(diagnostic => diagnostic.IsError);
}

/// <summary>Inspectable realization metadata for one layer.</summary>
public sealed record SuperstructureLayerRealization(
    string LayerId,
    int Level,
    int BaseY,
    int TopY,
    int VolumeCellCount,
    int ShellCellCount,
    DesignBounds? RealizedBounds);

/// <summary>
/// Pure S01 output. Invalid requests never expose partial cell intent; measurements and diagnostics
/// remain available so an editor can retain and repair the requested design.
/// </summary>
public sealed record SuperstructureGenerationResult(
    ImmutableArray<SuperstructureCellIntent> Cells,
    ImmutableArray<SuperstructureLayerRealization> Layers,
    SuperstructureMeasuredFootprint? Footprint,
    ImmutableArray<DesignDiagnostic> Diagnostics,
    long EstimatedVolumeCells)
{
    public bool IsValid => !Diagnostics.Any(diagnostic => diagnostic.IsError);
}

/// <summary>Bounded first-pass generation policy. These are Hull Forge limits, not game limits.</summary>
public sealed record SuperstructureGenerationOptions
{
    public static SuperstructureGenerationOptions Default { get; } = new();

    public int MaxModules { get; init; } = DesignLimits.MaxSuperstructureBoxesPerDocument;
    public int MaxVolumeCells { get; init; } = 2_000_000;
    public int MaxRequiredVoidCells { get; init; } = 2_000_000;
}

/// <summary>Stable codes owned by the modular superstructure engine.</summary>
public static class SuperstructureDiagnosticCodes
{
    public const string LatticeUnrepresentable = "SUP101";
    public const string LevelSequenceInvalid = "SUP102";
    public const string MaterialConflict = "SUP103";
    public const string DisconnectedUnion = "SUP104";
    public const string UnsupportedFootprint = "SUP105";
    public const string HullCavityCollision = "SUP106";
    public const string HullArmorCollision = "SUP107";
    public const string RequiredVoidCollision = "SUP108";
    public const string RequiredVoidInvalid = "SUP109";
    public const string CandidateBudgetExceeded = "SUP110";
    public const string ArrangementRootInvalid = "SUP111";
    public const string LegacyRouteRequired = "SUP112";
    public const string ModularLegacyConflict = "SUP113";
    public const string UnsupportedRoute = "SUP114";
    public const string HollowInteriorUnavailable = "SUP115";
    public const string NativeSupportUnverified = "SUP116";
    public const string InteriorDisconnected = "SUP117";
}
