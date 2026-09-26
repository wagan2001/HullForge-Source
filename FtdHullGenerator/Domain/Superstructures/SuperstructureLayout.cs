using System.Collections.Immutable;
using System.Text.Json.Serialization;
using FtdHullGenerator.Domain.Design;
using FtdHullGenerator.Domain.Layout;

namespace FtdHullGenerator.Domain.Superstructures;

/// <summary>
/// One rectangular module on a layer. Modules of the same layer are unioned before the shell
/// is extracted, so overlapping boxes do not produce doubled invisible walls.
/// </summary>
public sealed record SuperstructureBoxModule(
    string Id,
    DesignMeasure OffsetAlong,
    DesignMeasure OffsetAthwartships,
    DesignMeasure Length,
    DesignMeasure Width,
    int ClearHeightMetres,
    int WallThicknessMetres = 1,
    int RoofThicknessMetres = 1,
    MaterialKind Material = MaterialKind.Metal)
{
    public IEnumerable<DesignDiagnostic> Validate()
    {
        foreach (var diagnostic in Arrangement.RequireIdentifier(Id, "superstructure module", Id))
            yield return diagnostic;

        if (Length <= DesignMeasure.Zero || Width <= DesignMeasure.Zero)
            yield return DesignDiagnostic.Error(DesignDiagnosticCodes.SuperstructureModuleTooSmall,
                $"Module '{Id}' needs a positive length and width.", Id, nameof(Length));
        if (ClearHeightMetres < 1)
            yield return DesignDiagnostic.Error(DesignDiagnosticCodes.SuperstructureModuleTooSmall,
                $"Module '{Id}' needs at least one metre of clear height.", Id, nameof(ClearHeightMetres));
        if (WallThicknessMetres < 1 || RoofThicknessMetres < 1)
            yield return DesignDiagnostic.Error(DesignDiagnosticCodes.SuperstructureModuleInvalidThickness,
                $"Module '{Id}' needs at least a one-cell wall and roof.", Id, nameof(WallThicknessMetres));
        if (!Length.IsWithinDesignBounds || !Width.IsWithinDesignBounds ||
            !OffsetAlong.IsWithinDesignBounds || !OffsetAthwartships.IsWithinDesignBounds)
            yield return new DesignDiagnostic(DesignDiagnosticCodes.MeasureOutOfRange, DesignSeverity.Error,
                $"Module '{Id}' exceeds the supported design-coordinate range.", Id,
                nameof(OffsetAlong), SuggestedCorrection: "Reduce the module dimensions or offsets.");
        if (!Enum.IsDefined(Material))
            yield return DesignDiagnostic.Error(DesignDiagnosticCodes.SuperstructureMaterialUnsupported,
                $"Module '{Id}' declares an unsupported material.", Id, nameof(Material));
    }
}

/// <summary>A stacked layer. Layers are ordered by <see cref="Level"/>, lowest first.</summary>
public sealed record SuperstructureLayer(string Id, int Level, ImmutableArray<SuperstructureBoxModule> Modules)
{
    public IEnumerable<DesignDiagnostic> Validate()
    {
        foreach (var diagnostic in Arrangement.RequireIdentifier(Id, "superstructure layer", Id))
            yield return diagnostic;
        if (Level is < SuperstructureSettings.MinimumLevels or > SuperstructureSettings.MaximumLevels)
            yield return DesignDiagnostic.Error(DesignDiagnosticCodes.SuperstructureModuleTooSmall,
                $"Superstructure layer '{Id}' needs a level between " +
                $"{SuperstructureSettings.MinimumLevels} and {SuperstructureSettings.MaximumLevels}.",
                Id, nameof(Level));
        foreach (var module in Modules)
            foreach (var diagnostic in module.Validate())
                yield return diagnostic;
    }
}

/// <summary>A tower or mast root attached to one superstructure span.</summary>
public sealed record SuperstructureTowerRoot(
    string Id,
    string SpanNodeId,
    DesignMeasure OffsetFromSpanCenter,
    DesignMeasure? AllowableOffsetRange = null);

/// <summary>
/// The legacy 1–8 level shell generator, retained so saved documents keep their original
/// meaning. Converting to modular boxes is an explicit, disclosed action; it never happens
/// silently on load.
/// </summary>
public sealed record LegacySuperstructureSettings(bool UseLegacyGenerator, SuperstructureSettings Settings)
{
    public static LegacySuperstructureSettings None { get; } =
        new(false, SuperstructureSettings.Default);

    public static LegacySuperstructureSettings FromParameters(SuperstructureSettings settings) => new(true, settings);
}

/// <summary>Modular superstructure definition plus any retained legacy generator intent.</summary>
public sealed record SuperstructureLayout(
    bool Enabled,
    ImmutableArray<SuperstructureLayer> Layers,
    ImmutableArray<SuperstructureTowerRoot> TowerRoots,
    LegacySuperstructureSettings? Legacy = null)
{
    public static SuperstructureLayout Disabled { get; } = new(false, [], []);

    [JsonIgnore]
    public int ModuleCount => Layers.Sum(layer => layer.Modules.Length);

    public IEnumerable<DesignDiagnostic> Validate()
    {
        var errors = new List<DesignDiagnostic>();
        if (ModuleCount > DesignLimits.MaxSuperstructureBoxesPerDocument)
            errors.Add(new DesignDiagnostic(DesignDiagnosticCodes.CountLimitExceeded, DesignSeverity.Error,
                $"The document declares {ModuleCount} superstructure modules; the first-pass cap is " +
                $"{DesignLimits.MaxSuperstructureBoxesPerDocument}.",
                Field: nameof(ModuleCount)));

        var layerIds = new HashSet<string>(StringComparer.Ordinal);
        var levels = new HashSet<int>();
        foreach (var layer in Layers)
        {
            if (!layerIds.Add(layer.Id))
                errors.Add(DesignDiagnostic.Error(DesignDiagnosticCodes.IdentifierDuplicate,
                    $"Two superstructure layers share the id '{layer.Id}'.", layer.Id));
            if (!levels.Add(layer.Level))
                errors.Add(DesignDiagnostic.Error(DesignDiagnosticCodes.IdentifierDuplicate,
                    $"Two superstructure layers occupy level {layer.Level}.", layer.Id));
            errors.AddRange(layer.Validate());
        }

        var moduleIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var module in Layers.SelectMany(layer => layer.Modules))
            if (!moduleIds.Add(module.Id))
                errors.Add(DesignDiagnostic.Error(DesignDiagnosticCodes.IdentifierDuplicate,
                    $"Two superstructure modules share the id '{module.Id}'.", module.Id));

        var rootIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in TowerRoots)
        {
            errors.AddRange(Arrangement.RequireIdentifier(root.Id, "tower root", root.Id));
            if (!rootIds.Add(root.Id))
                errors.Add(DesignDiagnostic.Error(DesignDiagnosticCodes.IdentifierDuplicate,
                    $"Two tower roots share the id '{root.Id}'.", root.Id));
            if (string.IsNullOrWhiteSpace(root.SpanNodeId))
                errors.Add(DesignDiagnostic.Error(DesignDiagnosticCodes.SuperstructureRootUnknownSpan,
                    $"Tower root '{root.Id}' has no owning span.", root.Id));
            if (root.AllowableOffsetRange is { } allowance && allowance < DesignMeasure.Zero)
                errors.Add(new DesignDiagnostic(DesignDiagnosticCodes.SuperstructureTowerOutsideRoot,
                    DesignSeverity.Error,
                    $"Tower root '{root.Id}' declares a negative allowable offset range.", root.Id,
                    nameof(root.AllowableOffsetRange)));
        }

        return errors;
    }
}
