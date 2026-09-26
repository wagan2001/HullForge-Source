using System.Collections.Immutable;
using System.Text.Json.Serialization;
using FtdHullGenerator.Domain.Components;
using FtdHullGenerator.Domain.Design;
using FtdHullGenerator.Domain.Layout;
using FtdHullGenerator.Domain.Superstructures;

namespace FtdHullGenerator.Domain.Projects;

/// <summary>
/// The versioned design intent of one ship. It wraps the existing <see cref="HullParameters"/>
/// rather than duplicating them, carries the new V2 sections, and is the single object save/load
/// and undo preserve.
/// </summary>
/// <remarks>
/// Contract C01: design intent is separate from physical construction and from presentation. This
/// type therefore contains no WPF types, no installed-catalog objects and no geometry output. A
/// document is shareable; machine-local paths, credentials and discovered installation folders
/// stay outside it.
/// </remarks>
public sealed record ShipDocument(
    int SchemaVersion,
    string DocumentId,
    string Name,
    string GenerationVersion,
    HullSource Source,
    HullParameters Hull,
    Arrangement Arrangement,
    LayoutDatum? Datum,
    InternalStructure Internals,
    ImmutableArray<BarbetteDefinition> Barbettes,
    SuperstructureLayout Superstructure,
    SmoothingSettings Smoothing,
    AppliedStyleProvenance? AppliedStyle = null,
    DocumentExtensions? Extensions = null)
{
    /// <summary>The schema this build writes and the newest schema it understands.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Identifies the geometry pipeline that produced a resolved plan for this document.</summary>
    public const string DefaultGenerationVersion = "hf-2.0-first-pass";

    public static bool IsSupportedSchemaVersion(int version) =>
        version >= 1 && version <= CurrentSchemaVersion;

    /// <summary>
    /// True when the document came from a newer schema than this build understands. Callers must
    /// open it read-only or refuse, never treat it as a default new hull.
    /// </summary>
    [JsonIgnore]
    public bool RequiresReadOnlyFallback => !IsSupportedSchemaVersion(SchemaVersion);

    [JsonIgnore]
    public DocumentExtensions EffectiveExtensions => Extensions ?? DocumentExtensions.Empty;

    /// <summary>The persisted ruler datum, or the default origin when none was recorded.</summary>
    [JsonIgnore]
    public LayoutDatum EffectiveDatum => Datum ?? LayoutDatum.Origin;

    public static string NewIdentifier() => Guid.NewGuid().ToString("n");

    /// <summary>A new project with every V2 feature disabled and every legacy setting preserved.</summary>
    public static ShipDocument CreateNew(
        string name,
        HullParameters parameters,
        string? documentId = null,
        string? generationVersion = null)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return new ShipDocument(
            CurrentSchemaVersion,
            documentId ?? NewIdentifier(),
            name,
            generationVersion ?? DefaultGenerationVersion,
            HullSource.Regional,
            parameters,
            Arrangement.Empty,
            LayoutDatum.Origin,
            InternalStructure.Disabled,
            ImmutableArray<BarbetteDefinition>.Empty,
            new SuperstructureLayout(false, [], [], LegacySuperstructureSettings.FromParameters(parameters.EffectiveSuperstructure)),
            SmoothingSettings.FromParameters(parameters));
    }

    /// <summary>
    /// Adapts an existing parameter set without changing any of its meanings. The smoothing
    /// method keeps its numeric identity (so Combined stays Combined and Hybrid keeps its top
    /// band), the legacy superstructure generator keeps its settings behind a compatibility
    /// discriminator, and every new V2 feature starts disabled.
    /// </summary>
    public static ShipDocument FromLegacyParameters(
        HullParameters parameters,
        string name,
        string? documentId = null,
        string? generationVersion = null) =>
        CreateNew(name, parameters, documentId, generationVersion);

    /// <summary>
    /// The single validation entry point used by load, style application and generation.
    /// </summary>
    public IReadOnlyList<DesignDiagnostic> Validate()
    {
        var diagnostics = new List<DesignDiagnostic>();
        AddIdentity(diagnostics);
        diagnostics.AddRange(Source.Validate());

        foreach (var message in Hull.Validate())
            diagnostics.Add(DesignDiagnostic.Error(DesignDiagnosticCodes.HullParametersInvalid, message,
                field: nameof(Hull)));

        diagnostics.AddRange(Arrangement.Validate());
        diagnostics.AddRange(EffectiveDatum.Validate());
        diagnostics.AddRange(Internals.Validate());
        diagnostics.AddRange(Superstructure.Validate());
        diagnostics.AddRange(Smoothing.Validate());
        diagnostics.AddRange(EffectiveExtensions.Validate());
        if (AppliedStyle is not null)
            diagnostics.AddRange(AppliedStyle.Validate());

        AddBarbettes(diagnostics);
        AddTowerRoots(diagnostics);

        // The wrapped parameters and the document's smoothing section are two views of one decision.
        // They are allowed to exist for compatibility, but they are not allowed to disagree silently.
        if (Hull.Smoothing != Smoothing.NativeMethod)
            diagnostics.Add(new DesignDiagnostic(DesignDiagnosticCodes.SmoothingIntentDiverges, DesignSeverity.Error,
                $"The wrapped hull parameters request {Hull.Smoothing} but the document's smoothing intent is " +
                $"{Smoothing.NativeMethod}; one of them is stale.",
                Field: nameof(Smoothing),
                SuggestedCorrection: "Re-derive the smoothing intent from the hull parameters, or change both."));

        return diagnostics;
    }

    private void AddIdentity(List<DesignDiagnostic> diagnostics)
    {
        if (!IsSupportedSchemaVersion(SchemaVersion))
            diagnostics.Add(DesignDiagnostic.Error(DesignDiagnosticCodes.SchemaVersionUnsupported,
                $"Schema version {SchemaVersion} is not supported by this build " +
                $"(newest known is {CurrentSchemaVersion}).", field: nameof(SchemaVersion)));
        if (string.IsNullOrWhiteSpace(DocumentId))
            diagnostics.Add(DesignDiagnostic.Error(DesignDiagnosticCodes.IdentifierMissing,
                "The document has no stable identifier.", field: nameof(DocumentId)));
        if (string.IsNullOrWhiteSpace(Name))
            diagnostics.Add(DesignDiagnostic.Error(DesignDiagnosticCodes.DocumentNameMissing,
                "The document needs a name.", field: nameof(Name)));
        else if (Name.Length > DesignLimits.MaxDocumentNameLength)
            diagnostics.Add(new DesignDiagnostic(DesignDiagnosticCodes.DocumentNameTooLong, DesignSeverity.Error,
                $"The document name is {Name.Length} characters; the limit is {DesignLimits.MaxDocumentNameLength}.",
                Field: nameof(Name),
                SuggestedCorrection: $"Shorten the name to {DesignLimits.MaxDocumentNameLength} characters or fewer."));
        if (string.IsNullOrWhiteSpace(GenerationVersion))
            diagnostics.Add(DesignDiagnostic.Error(DesignDiagnosticCodes.IdentifierMissing,
                "The document does not record the generation version that resolved it.",
                field: nameof(GenerationVersion)));
    }

    private void AddBarbettes(List<DesignDiagnostic> diagnostics)
    {
        if (Barbettes.Length > 0 && !Hull.HasSingleBlockCenterline)
        {
            diagnostics.Add(new DesignDiagnostic(
                DesignDiagnosticCodes.BarbetteOddHullWidthRequired,
                DesignSeverity.Error,
                "Centerline barbettes require an odd hull width.",
                DocumentId,
                nameof(HullParameters.Width),
                DesignMeasure.FromMetres(Hull.Width),
                null,
                "Return the hull to an odd width or explicitly remove every centerline barbette."));
        }

        if (Barbettes.Length > DesignLimits.MaxBarbettesPerDocument)
            diagnostics.Add(new DesignDiagnostic(DesignDiagnosticCodes.CountLimitExceeded, DesignSeverity.Error,
                $"The document declares {Barbettes.Length} barbettes; the first-pass cap is " +
                $"{DesignLimits.MaxBarbettesPerDocument}.",
                Field: nameof(Barbettes),
                SuggestedCorrection: $"Remove {Barbettes.Length - DesignLimits.MaxBarbettesPerDocument} barbette definition(s)."));

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var barbette in Barbettes)
        {
            diagnostics.AddRange(barbette.Validate());
            if (!ids.Add(barbette.Id))
                diagnostics.Add(DesignDiagnostic.Error(DesignDiagnosticCodes.IdentifierDuplicate,
                    $"Two barbettes share the id '{barbette.Id}'.", barbette.Id));

            var node = Arrangement.FindNode(barbette.NodeId);
            if (node is null)
                diagnostics.Add(DesignDiagnostic.Error(DesignDiagnosticCodes.BarbetteUnknownNode,
                    $"Barbette '{barbette.Id}' references the unknown arrangement node '{barbette.NodeId}'.",
                    barbette.Id, nameof(barbette.NodeId)));
            else if (node.Kind != ArrangementNodeKind.Barbette)
                diagnostics.Add(DesignDiagnostic.Error(DesignDiagnosticCodes.BarbetteUnknownNode,
                    $"Barbette '{barbette.Id}' is attached to the {node.Kind} node '{node.Id}'.",
                    barbette.Id, nameof(barbette.NodeId)));
        }
    }

    private void AddTowerRoots(List<DesignDiagnostic> diagnostics)
    {
        foreach (var root in Superstructure.TowerRoots)
        {
            var node = Arrangement.FindNode(root.SpanNodeId);
            if (node is null)
                diagnostics.Add(DesignDiagnostic.Error(DesignDiagnosticCodes.SuperstructureRootUnknownSpan,
                    $"Tower root '{root.Id}' references the unknown span '{root.SpanNodeId}'.", root.Id,
                    nameof(root.SpanNodeId)));
            else if (node.Kind != ArrangementNodeKind.SuperstructureSpan)
                diagnostics.Add(DesignDiagnostic.Error(DesignDiagnosticCodes.SuperstructureRootUnknownSpan,
                    $"Tower root '{root.Id}' is attached to the {node.Kind} node '{node.Id}'.", root.Id,
                    nameof(root.SpanNodeId)));
        }
    }
}
