using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Components;
using FtdHullGenerator.Domain.Composition;
using FtdHullGenerator.Domain.Design;
using FtdHullGenerator.Domain.Layout;
using FtdHullGenerator.Domain.Projects;
using FtdHullGenerator.Domain.Historical;
using FtdHullGenerator.Domain.Superstructures;
using FtdHullGenerator.Geometry.Components;
using FtdHullGenerator.Geometry.Layout;
using FtdHullGenerator.Geometry.Superstructures;
using FtdHullGenerator.Geometry.HullSources;
using FtdHullGenerator.Infrastructure;

namespace FtdHullGenerator.Geometry.Composition;

/// <summary>Stable diagnostics owned by the bounded V2 physical-composition stage.</summary>
public static class ShipCompositionDiagnosticCodes
{
    public const string UnsupportedHullSource = "CMP101";
    public const string DatumMismatch = "CMP102";
    public const string ComponentNodeMismatch = "CMP103";
    public const string ArrangementUnsolved = "CMP104";
    public const string PhysicalConflict = "CMP105";
    public const string RequiredVoidOccupied = "CMP106";
    public const string PhysicalBudgetExceeded = "CMP107";
    public const string RootCardinalityUnsupported = "CMP108";
    public const string CatalogFallback = "CMP109";
    public const string LayoutDatumUnsupported = "CMP110";
    public const string RootHandleConstraintMismatch = "CMP111";
    public const string HistoricalSmoothingUnavailable = "CMP112";
}

/// <summary>Explicit first-pass resource limits and delegated component limits.</summary>
public sealed record ShipGenerationOptions
{
    public static ShipGenerationOptions Default { get; } = new();

    public int MaxPhysicalCells { get; init; } = 8_000_000;
    public BarbetteGenerationLimits BarbetteLimits { get; init; } = BarbetteGenerationLimits.Default;
    public BulkheadGenerationOptions BulkheadOptions { get; init; } = BulkheadGenerationOptions.Default;
    public SuperstructureGenerationOptions SuperstructureOptions { get; init; } =
        SuperstructureGenerationOptions.Default;
}

/// <summary>
/// The measured arrangement ruler of one evaluated hull: the forward structural-deck face that
/// defines ruler zero and the continuous supported deck interval that defines the stern end.
/// </summary>
public sealed record SupportedDeckRuler(DesignMeasure BowDatum, DesignMeasure SupportedEnd);

/// <summary>
/// One immutable resolved revision. Preview reads <see cref="Hull"/> and export consumes this same
/// object with its captured catalog; neither route is permitted to regenerate design intent.
/// </summary>
public sealed record ShipGenerationSnapshot(
    ShipDocument Document,
    long Revision,
    HullBuildContext HullContext,
    ArrangementSolution Arrangement,
    GeneratedHull Hull,
    ImmutableArray<BarbetteGenerationResult> Barbettes,
    InternalStructureGenerationResult Internals,
    ImmutableArray<SuperstructureGenerationResult> Superstructures,
    ImmutableArray<RequiredVoidExclusion> RequiredVoids,
    ImmutableArray<DesignDiagnostic> Diagnostics,
    FtdBlockCatalog Catalog)
{
    public FtdHullGenerator.Geometry.Refinement.ResolvedSlopeRefinement? Refinement { get; internal init; }

    public bool IsCurrent(long revision) => revision == Revision;
}

/// <summary>An atomic composition attempt: invalid intent returns diagnostics and no snapshot.</summary>
public sealed record ShipGenerationResult(
    ShipGenerationSnapshot? Snapshot,
    ImmutableArray<DesignDiagnostic> Diagnostics)
{
    public bool IsValid => Snapshot is not null && !Diagnostics.Any(diagnostic => diagnostic.IsError);
}

/// <summary>Stable identity of every export-relevant value in one captured catalog snapshot.</summary>
internal static class ShipCatalogFingerprint
{
    public static string Compute(FtdBlockCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var canonical = new StringBuilder();
        canonical.Append(catalog.GameVersion.Length.ToString(CultureInfo.InvariantCulture))
            .Append(':').Append(catalog.GameVersion).Append('\n');
        foreach (var block in catalog.Blocks.OrderBy(block => block.Material)
                     .ThenBy(block => block.Shape).ThenBy(block => block.Guid))
            canonical.Append((int)block.Material).Append('|').Append((int)block.Shape).Append('|')
                .Append(block.Guid.ToString("D")).Append('|')
                .Append(BitConverter.DoubleToInt64Bits(block.MaterialCost).ToString("X16",
                    CultureInfo.InvariantCulture)).Append('|')
                .Append(block.IsFallback ? '1' : '0').Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }
}

/// <summary>
/// Bounded V2 integration over one immutable document revision and one captured hull evaluation.
/// Component generators emit intent; this service alone owns cuts, cross-component ownership,
/// final packing, catalog fallback and the revision-stamped preview/export snapshot.
/// </summary>
public sealed class ShipGenerationService
{
    private sealed class MutablePhysicalCell
    {
        public required MaterialKind Material { get; init; }
        public List<PhysicalCellOwner> Owners { get; } = [];
    }

    public ShipGenerationResult Generate(
        ShipDocument document,
        long revision,
        FtdBlockCatalog catalog,
        ShipGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(catalog);
        options ??= ShipGenerationOptions.Default;
        cancellationToken.ThrowIfCancellationRequested();

        var diagnostics = document.Validate().ToList();
        ValidateOptions(options, diagnostics);
        if (document.Source.Kind != HullSourceKind.RegionalShapeV2)
            diagnostics.Add(new DesignDiagnostic(ShipCompositionDiagnosticCodes.UnsupportedHullSource,
                DesignSeverity.Error,
                "The bounded composition service can resolve only the regional Shape V2 hull source; " +
                "it will not substitute a regional hull for historical-envelope intent.",
                document.DocumentId, nameof(document.Source),
                SuggestedCorrection: "Install and select a supported historical hull evaluator before generating this source."));
        if (diagnostics.HasErrors())
            return Rejected(diagnostics);

        HullBuildContext context;
        try
        {
            // This is the one pre-materialization evaluation for the complete revision. T01 owns
            // authorization; no caller-supplied opening mask is trusted at this boundary.
            context = HullGenerator.CreateContext(document.Hull, cancellationToken: cancellationToken);
        }
        catch (HullGenerationException error)
        {
            diagnostics.AddRange(error.Errors.Select(message =>
                DesignDiagnostic.Error(HullContextDiagnosticCodes.TopologyInvalid, message,
                    document.DocumentId, nameof(document.Hull))));
            return Rejected(diagnostics);
        }
        return GenerateCaptured(document, revision, catalog, options, context, diagnostics,
            cancellationToken);
    }

    /// <summary>
    /// Explicit historical fallback entry. The caller must supply the exact validated package
    /// named by the document; no registry lookup or regional substitution occurs here.
    /// </summary>
    public ShipGenerationResult GenerateHistorical(
        ShipDocument document,
        long revision,
        FtdBlockCatalog catalog,
        LoadedHistoricalEnvelopeAsset loaded,
        ShipGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(loaded);
        options ??= ShipGenerationOptions.Default;
        cancellationToken.ThrowIfCancellationRequested();

        var diagnostics = document.Validate().ToList();
        ValidateOptions(options, diagnostics);
        var metadata = loaded.Asset.Metadata;
        if (document.Source.Kind != HullSourceKind.HistoricalEnvelope ||
            !string.Equals(document.Source.AssetId, metadata.AssetId, StringComparison.Ordinal) ||
            !string.Equals(document.Source.AssetVersion, metadata.AssetVersion, StringComparison.Ordinal) ||
            !string.Equals(document.Source.AssetHash, loaded.Asset.AssetContentHash,
                StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(new DesignDiagnostic(ShipCompositionDiagnosticCodes.UnsupportedHullSource,
                DesignSeverity.Error,
                "The validated historical package identity/hash does not match the document hull-source reference.",
                document.DocumentId, nameof(document.Source),
                SuggestedCorrection: "Load the exact asset id, version and package content hash recorded by this document."));
        }
        if (document.Hull.Smoothing != SmoothingMethod.None)
            diagnostics.Add(new DesignDiagnostic(
                ShipCompositionDiagnosticCodes.HistoricalSmoothingUnavailable,
                DesignSeverity.Error,
                "Sampled historical fallback does not support native smoothing and will not downgrade the request to None.",
                document.DocumentId, nameof(document.Hull.Smoothing), null, null,
                SuggestedCorrection: "Select None or use a future historical representation with reproduced native smoothing."));
        if (loaded.Asset.Fidelity.Representation != HistoricalHullRepresentation.SampledEnvelopeFallback ||
            loaded.Asset.Fidelity.FallbackValidation is null)
            diagnostics.Add(DesignDiagnostic.Error(
                ShipCompositionDiagnosticCodes.UnsupportedHullSource,
                "The historical package does not contain an exact sampled-fallback validation for these hull/armor parameters.",
                document.DocumentId, nameof(document.Hull)));
        if (diagnostics.HasErrors())
            return Rejected(diagnostics);

        HistoricalEnvelopeCompositionEvaluation evaluation;
        try
        {
            evaluation = HistoricalEnvelopeCompositionAdapter.Evaluate(
                loaded, document.Hull, options.MaxPhysicalCells, cancellationToken);
        }
        catch (HullGenerationException error)
        {
            diagnostics.AddRange(error.Errors.Select(message =>
                DesignDiagnostic.Error(HullContextDiagnosticCodes.TopologyInvalid, message,
                    document.DocumentId, nameof(document.Hull))));
            return Rejected(diagnostics);
        }
        catch (ArgumentException error)
        {
            diagnostics.Add(DesignDiagnostic.Error(
                ShipCompositionDiagnosticCodes.UnsupportedHullSource,
                $"The historical package cannot be reproduced safely: {error.Message}",
                document.DocumentId, nameof(document.Source)));
            return Rejected(diagnostics);
        }

        if (evaluation.VoxelQuantization != loaded.Asset.Fidelity.VoxelQuantization ||
            evaluation.Validation != loaded.Asset.Fidelity.FallbackValidation)
        {
            diagnostics.Add(DesignDiagnostic.Error(
                ShipCompositionDiagnosticCodes.UnsupportedHullSource,
                "The runtime historical voxel/context result does not reproduce the packaged fidelity evidence.",
                document.DocumentId, nameof(document.Source)));
            return Rejected(diagnostics);
        }

        return GenerateCaptured(document, revision, catalog, options, evaluation.Context, diagnostics,
            cancellationToken);
    }

    private ShipGenerationResult GenerateCaptured(
        ShipDocument document,
        long revision,
        FtdBlockCatalog catalog,
        ShipGenerationOptions options,
        HullBuildContext context,
        List<DesignDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {

        diagnostics.AddRange(context.Diagnostics);
        var hasV2Geometry = document.Barbettes.Length > 0 ||
                            document.Internals.Families.Any(family => family.Enabled) ||
                            document.Superstructure.Enabled;
        if (hasV2Geometry && document.EffectiveDatum.CenterPlaneX != context.CenterPlaneX)
            diagnostics.Add(new DesignDiagnostic(ShipCompositionDiagnosticCodes.DatumMismatch,
                DesignSeverity.Error,
                $"The document records centre plane X={document.EffectiveDatum.CenterPlaneX.Metres:0.##} m, " +
                $"but this hull revision evaluates to X={context.CenterPlaneX.Metres:0.##} m.",
                document.DocumentId, nameof(LayoutDatum.CenterPlaneX),
                document.EffectiveDatum.CenterPlaneX, context.CenterPlaneX,
                "Refresh the persisted layout datum from the current hull before composing components."));

        var measuredArrangement = MeasureArrangement(document, context, diagnostics, cancellationToken);
        if (diagnostics.HasErrors())
            return Rejected(diagnostics);

        DesignMeasure? supportedRulerEnd = null;
        if (measuredArrangement.Nodes.Length > 0)
            supportedRulerEnd = ResolveSupportedDeckRuler(document, context, diagnostics);
        if (diagnostics.HasErrors())
            return Rejected(diagnostics);

        var arrangement = ArrangementSolver.Solve(measuredArrangement,
            new ArrangementSolveOptions(document.EffectiveDatum.LayoutBowZ,
                supportedRulerEnd,
                ArrangementParityRequirement.None,
                SolveFlexibleRemainder: true,
                context.CenterPlaneX));
        diagnostics.AddRange(arrangement.Diagnostics);
        if (!arrangement.IsSolved)
        {
            diagnostics.Add(new DesignDiagnostic(ShipCompositionDiagnosticCodes.ArrangementUnsolved,
                DesignSeverity.Error,
                "The measured component arrangement is not solved; no physical component intent was materialized.",
                document.DocumentId, nameof(document.Arrangement),
                SuggestedCorrection: "Resolve the reported gaps, extents, parity or supported-length conflict."));
            return Rejected(diagnostics);
        }

        var barbetteOwnership = GenerateBarbettes(document, context, arrangement, options,
            diagnostics, cancellationToken);
        if (diagnostics.HasErrors())
            return Rejected(diagnostics);
        var barbetteResults = barbetteOwnership.Barbettes
            .Select(entry => entry.Generation)
            .ToImmutableArray();

        // Only a valid generation may contribute protected voids, deck cuts or reservations. A
        // rejected barbette retains its measurement/layout for diagnostics but must never expand
        // physical or reservation geometry.
        var requiredVoids = barbetteOwnership.Barbettes
            .Where(entry => entry.Generation.IsValid)
            .Select(entry => new RequiredVoidExclusion(entry.Id,
                entry.Generation.RequiredVoids.Select(intent => intent.Cell).Distinct().ToImmutableArray()))
            .Where(exclusion => exclusion.Cells.Length > 0)
            .OrderBy(exclusion => exclusion.Id, StringComparer.Ordinal)
            .ToImmutableArray();

        // Experimental internals route around the barbette's complete resolved footprint: protected
        // clear cavity and shaft, deliberate armor air, every solid side/roof/bottom/neck armor cell
        // and the authorized deck apertures. This reservation superset is internal-only; the
        // protected required-void set above stays the final-plan emptiness gate because the barbette
        // legitimately occupies its own armor.
        var internalReservations = BuildInternalReservations(barbetteOwnership);

        var internals = BulkheadGenerator.Generate(context, document.Internals, internalReservations,
            options.BulkheadOptions, cancellationToken);
        diagnostics.AddRange(internals.Diagnostics);

        var superstructures = GenerateSuperstructures(document, context, measuredArrangement,
            arrangement, requiredVoids, options, diagnostics, cancellationToken);
        if (diagnostics.HasErrors())
            return Rejected(diagnostics);

        var componentCells = BuildComponentCells(barbetteOwnership.Solids, internals, superstructures,
            diagnostics, cancellationToken);
        var voidCells = requiredVoids.SelectMany(exclusion => exclusion.Cells).ToHashSet();
        var deckCuts = barbetteResults.Where(result => result.IsValid)
            .SelectMany(result => result.AuthorizedDeckCuts)
            .Select(intent => intent.Cell).ToHashSet();
        var supersededArmor = barbetteOwnership.SupersededArmorCells.ToHashSet();
        if ((long)componentCells.Count + voidCells.Count + deckCuts.Count + supersededArmor.Count >
            options.MaxPhysicalCells)
        {
            diagnostics.Add(BudgetDiagnostic(options.MaxPhysicalCells,
                (long)componentCells.Count + voidCells.Count + deckCuts.Count + supersededArmor.Count));
            return Rejected(diagnostics);
        }

        cancellationToken.ThrowIfCancellationRequested();
        GeneratedHull featureFree;
        try
        {
            featureFree = FeatureFreeHullCompositionAdapter.Compose(context, cancellationToken);
        }
        catch (HullGenerationException error)
        {
            diagnostics.AddRange(error.Errors.Select(message =>
                DesignDiagnostic.Error(HullContextDiagnosticCodes.TopologyInvalid, message,
                    document.DocumentId, nameof(document.Hull))));
            return Rejected(diagnostics);
        }

        // A modular graph explicitly replaces only the legacy superstructure stage. The old route
        // otherwise remains byte-for-byte on the captured compatibility path.
        var basePlacements = document.Superstructure.Enabled
            ? featureFree.Blocks.Where(block => block.Origin is not BlockOrigin.Superstructure and
                not BlockOrigin.SuperstructureSmoothing).ToArray()
            : featureFree.Blocks.ToArray();

        // Native hull smoothing was computed from the hull surface alone. Components and wells are
        // hard blockers: discard a complete smoothing placement when any of its occupied cells is
        // now owned or reserved, never truncate a native slope into a different shape.
        var blockers = componentCells.Keys.Concat(voidCells).Concat(deckCuts).Concat(supersededArmor)
            .ToHashSet();
        basePlacements = basePlacements.Where(block =>
                block.Origin != BlockOrigin.Smoothing ||
                !block.OccupiedCells.Any(cell => blockers.Contains(new HullCell(cell.X, cell.Y, cell.Z))))
            .ToArray();

        var cutBase = ApplyAuthorizedBarbetteRemovals(basePlacements, deckCuts, supersededArmor, context,
            diagnostics, cancellationToken);
        if (diagnostics.HasErrors())
            return Rejected(diagnostics);
        if (cutBase.Sum(block => (long)block.CellLength) > options.MaxPhysicalCells)
        {
            diagnostics.Add(BudgetDiagnostic(options.MaxPhysicalCells,
                cutBase.Sum(block => (long)block.CellLength)));
            return Rejected(diagnostics);
        }

        var cutHull = WithPlacements(featureFree, cutBase);
        if (document.Hull.Beamify)
            cutHull = BeamOptimizer.Optimize(cutHull, mergeFlattenedShellCandidates: true);

        var provenance = new Dictionary<HullCell, MutablePhysicalCell>();
        var occupied = IndexBase(cutHull.Blocks, context, provenance, diagnostics, cancellationToken);
        ReconcileComponents(componentCells, occupied, provenance, context, diagnostics, cancellationToken);
        ValidateRequiredVoids(voidCells, occupied, componentCells, diagnostics);
        if (diagnostics.HasErrors())
            return Rejected(diagnostics);

        var componentPlacements = MaterializeComponents(componentCells, occupied, cancellationToken);
        var packedComponents = PackComponents(componentPlacements);
        var combined = cutHull.Blocks.Concat(packedComponents)
            .OrderBy(block => block.Z).ThenBy(block => block.Y).ThenBy(block => block.X)
            .ToArray();
        if (combined.Sum(block => (long)block.CellLength) > options.MaxPhysicalCells)
        {
            diagnostics.Add(BudgetDiagnostic(options.MaxPhysicalCells,
                combined.Sum(block => (long)block.CellLength)));
            return Rejected(diagnostics);
        }

        var composed = WithPlacements(cutHull, combined);

        // Repeat the blocker check at the last materialization boundary. This is deliberately
        // redundant with the pre-reconciliation filter: catalog substitution must never revive
        // or truncate a native hull-smoothing placement that a component or well displaced.
        var (resolved, fallbackCount) = ResolveCatalog(composed, catalog, cancellationToken, blockers);
        if (fallbackCount > 0)
            diagnostics.Add(new DesignDiagnostic(ShipCompositionDiagnosticCodes.CatalogFallback,
                DesignSeverity.Warning,
                $"The installed catalog resolved {fallbackCount} native placement(s) to full-volume cubes.",
                document.DocumentId, nameof(FtdBlockCatalog),
                SuggestedCorrection: "Refresh the installed catalog or keep the conservative cube substitutions."));

        var orderedProvenance = provenance.Select(pair => new PhysicalCellProvenance(
                pair.Key, pair.Value.Material,
                pair.Value.Owners.Distinct().OrderBy(owner => owner.Role).ThenBy(owner => owner.OwnerId,
                    StringComparer.Ordinal).ToImmutableArray()))
            .OrderBy(item => item.Cell.Z).ThenBy(item => item.Cell.Y).ThenBy(item => item.Cell.X)
            .ToArray();
        resolved = resolved with
        {
            SourceRevision = revision,
            SourceDocumentId = document.DocumentId,
            ResolvedCatalogVersion = catalog.GameVersion,
            ResolvedCatalogFingerprint = ShipCatalogFingerprint.Compute(catalog),
            CatalogFallbackCount = fallbackCount,
            CellProvenance = Array.AsReadOnly(orderedProvenance),
        };

        var authorizedRemovals = deckCuts.Concat(supersededArmor).ToHashSet();
        ValidateFinalPhysicalPlan(resolved, context, provenance, catalog, voidCells, authorizedRemovals,
            diagnostics, cancellationToken);
        if (diagnostics.HasErrors())
            return Rejected(diagnostics);

        var snapshot = new ShipGenerationSnapshot(document, revision, context, arrangement, resolved,
            barbetteResults, internals, superstructures, requiredVoids, diagnostics.ToImmutableArray(), catalog);
        return FtdHullGenerator.Geometry.Refinement.ResolvedSlopeRefinement.Attach(snapshot, cancellationToken);
    }

    private static Arrangement MeasureArrangement(
        ShipDocument document,
        HullBuildContext context,
        List<DesignDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var barbetteGroups = document.Barbettes.GroupBy(item => item.NodeId, StringComparer.Ordinal).ToArray();
        foreach (var group in barbetteGroups.Where(group => group.Count() > 1))
            diagnostics.Add(NodeMismatch(group.Key,
                $"Arrangement barbette node '{group.Key}' is claimed by multiple definitions: " +
                string.Join(", ", group.Select(item => item.Id).Order(StringComparer.Ordinal)) + "."));
        var barbetteByNode = barbetteGroups.ToDictionary(group => group.Key, group => group.First(),
            StringComparer.Ordinal);
        SuperstructureFootprintMeasurement? superstructure = null;
        if (document.Superstructure.Enabled)
        {
            superstructure = ModularSuperstructureGenerator.MeasureFootprint(
                document.Superstructure, cancellationToken);
            diagnostics.AddRange(superstructure.Diagnostics);
        }

        var nodes = new List<ArrangementNode>(document.Arrangement.Nodes.Length);
        foreach (var node in document.Arrangement.Nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (node.Kind)
            {
                case ArrangementNodeKind.Barbette:
                    if (!barbetteByNode.TryGetValue(node.Id, out var definition))
                    {
                        diagnostics.Add(NodeMismatch(node.Id,
                            $"Arrangement barbette node '{node.Id}' has no BarbetteDefinition."));
                        nodes.Add(node);
                        break;
                    }
                    if (!string.Equals(node.ComponentId, definition.Id, StringComparison.Ordinal))
                        diagnostics.Add(NodeMismatch(node.Id,
                            $"Arrangement node '{node.Id}' names component '{node.ComponentId}', not '{definition.Id}'."));
                    // The arrangement measurement uses the actual evaluated centre plane and a fixed
                    // legal whole-metre Z anchor. The clear diameter must never choose a temporary
                    // parity: an even or fractional diameter is a domain ClearDiameter error (BAR009),
                    // not a fabricated half-metre centre that would masquerade as a parity conflict.
                    var measured = BarbetteGenerator.Measure(definition, context.CenterPlaneX,
                        DesignMeasure.Zero, cancellationToken: cancellationToken);
                    diagnostics.AddRange(measured.Diagnostics);
                    nodes.Add(measured.Measurement is null
                        ? node
                        : node with { OuterHalfExtent = measured.Measurement.LongitudinalHalfExtent });
                    break;

                case ArrangementNodeKind.SuperstructureSpan:
                    var roots = document.Superstructure.TowerRoots.Where(root =>
                        string.Equals(root.SpanNodeId, node.Id, StringComparison.Ordinal)).ToArray();
                    if (document.Superstructure.Enabled && roots.Length == 0)
                        diagnostics.Add(NodeMismatch(node.Id,
                            $"Enabled superstructure span '{node.Id}' has no declared root."));
                    if (superstructure?.Footprint is { } footprint && roots.Length == 1)
                    {
                        // The arrangement interval represents the exact physical union. Its persisted
                        // child handle is a design constraint, not derived state that composition may
                        // rewrite. GenerateSuperstructures reconciles that handle and the declared S01
                        // root against this measured centre before any module intent is accepted.
                        nodes.Add(node with
                        {
                            OuterHalfExtent = footprint.LongitudinalHalfExtent,
                        });
                    }
                    else
                    {
                        nodes.Add(node);
                    }
                    break;

                default:
                    nodes.Add(node);
                    break;
            }
        }

        foreach (var definition in document.Barbettes)
            if (!document.Arrangement.Nodes.Any(node =>
                    string.Equals(node.Id, definition.NodeId, StringComparison.Ordinal) &&
                    node.Kind == ArrangementNodeKind.Barbette))
                diagnostics.Add(NodeMismatch(definition.NodeId,
                    $"Barbette '{definition.Id}' has no matching arrangement barbette node."));

        return document.Arrangement with { Nodes = nodes.ToImmutableArray() };
    }

    /// <summary>
    /// Measures the arrangement ruler of the current hull revision from the same captured evaluation
    /// the composition stage uses. The barbette ruler and any persisted <see cref="LayoutDatum" />
    /// must use exactly this bow datum and supported end; returns null when the hull has no
    /// contiguous structural deck interval to support an arrangement. This is the single authority
    /// for the ruler interval, so the editor never re-derives it.
    /// </summary>
    public static SupportedDeckRuler? ResolveDeckRuler(
        HullParameters parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        HullBuildContext context;
        try
        {
            context = HullGenerator.CreateContext(parameters, cancellationToken: cancellationToken);
        }
        catch (HullGenerationException)
        {
            return null;
        }

        var measurement = SupportedDeckRulerAuthority.Measure(context);
        return measurement is { GapStation: null }
            ? new SupportedDeckRuler(measurement.BowDatum, measurement.SupportedEnd)
            : null;
    }

    private static DesignMeasure? ResolveSupportedDeckRuler(
        ShipDocument document,
        HullBuildContext context,
        List<DesignDiagnostic> diagnostics)
    {
        var measurement = SupportedDeckRulerAuthority.Measure(context);
        if (measurement is null)
        {
            diagnostics.Add(new DesignDiagnostic(ShipCompositionDiagnosticCodes.LayoutDatumUnsupported,
                DesignSeverity.Error,
                "The arrangement has no contiguous structural deck interval to support its ruler.",
                document.DocumentId, nameof(LayoutDatum.LayoutBowZ),
                SuggestedCorrection: "Add structural deck armor or remove deck-supported arrangement components."));
            return null;
        }

        if (measurement.GapStation is { } unsupportedStation)
        {
            diagnostics.Add(new DesignDiagnostic(ShipCompositionDiagnosticCodes.LayoutDatumUnsupported,
                DesignSeverity.Error,
                $"The structural deck support interval contains a gap at world Z={unsupportedStation} m.",
                document.DocumentId, nameof(LayoutDatum.LayoutBowZ),
                SuggestedCorrection: "Use one continuous supported deck interval before solving the arrangement.",
                AffectedBounds: CellBounds(new HullCell(0, context.DeckYAt(unsupportedStation), unsupportedStation))));
            return null;
        }

        if (document.EffectiveDatum.LayoutBowZ != measurement.BowDatum)
        {
            diagnostics.Add(new DesignDiagnostic(ShipCompositionDiagnosticCodes.LayoutDatumUnsupported,
                DesignSeverity.Error,
                $"The persisted layout bow datum is {document.EffectiveDatum.LayoutBowZ.Metres:0.##} m, " +
                $"but the evaluated hull's forward supported deck face is {measurement.BowDatum.Metres:0.##} m.",
                document.DocumentId, nameof(LayoutDatum.LayoutBowZ),
                document.EffectiveDatum.LayoutBowZ, measurement.BowDatum,
                "Refresh the datum from the current hull before solving; do not use HullOrigin or a bulb tip.",
                new DesignBounds(context.CenterPlaneX, context.CenterPlaneX,
                    DesignMeasure.FromCellAnchor(context.DeckYAt(measurement.BowStation)),
                    DesignMeasure.FromCellAnchor(context.DeckYAt(measurement.BowStation)),
                    DesignMeasure.FromTwiceMetres(measurement.SternStation * 2 - 1), measurement.BowDatum)));
            return null;
        }

        return measurement.SupportedEnd;
    }

    private static BarbetteOwnershipResult GenerateBarbettes(
        ShipDocument document,
        HullBuildContext context,
        ArrangementSolution arrangement,
        ShipGenerationOptions options,
        List<DesignDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var placements = new List<BarbettePlacement>(document.Barbettes.Length);
        foreach (var definition in document.Barbettes.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var node = arrangement.FindNode(definition.NodeId);
            if (node is null)
            {
                diagnostics.Add(NodeMismatch(definition.NodeId,
                    $"Barbette '{definition.Id}' has no solved arrangement position."));
                continue;
            }

            placements.Add(new BarbettePlacement(definition, context.CenterPlaneX, node.WorldZCenter));
        }

        // The ship-wide longitudinal midpoint only ever breaks a shared-armor tie; it is derived
        // from the same solved supported deck interval the arrangement ruler uses.
        var midpointZ = document.EffectiveDatum.LayoutBowZ;
        if (arrangement.SupportedRulerEnd is { } supportedEnd)
            midpointZ -= supportedEnd.TryHalve() ?? DesignMeasure.FromTwiceMetres(supportedEnd.TwiceMetres / 2);

        var ownership = BarbetteOwnership.Reconcile(placements, midpointZ, context,
            options.BarbetteLimits, cancellationToken);
        diagnostics.AddRange(ownership.Diagnostics);
        return ownership;
    }

    /// <summary>
    /// The complete per-barbette footprint optional internal structures must not occupy: the
    /// protected clear cavity and neck shaft, deliberate armor air, every realized solid armor cell
    /// and the authorized deck apertures. Each entry keeps the barbette's stable id so an
    /// experimental plane's clipping count stays attributable; the sets are disjoint across
    /// barbettes because BAR02 already assigned one owner per armor cell.
    /// </summary>
    private static ImmutableArray<RequiredVoidExclusion> BuildInternalReservations(
        BarbetteOwnershipResult ownership)
    {
        var reservations = new List<RequiredVoidExclusion>(ownership.Barbettes.Count);
        foreach (var entry in ownership.Barbettes.Where(item => item.Generation.IsValid)
                     .OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            var cells = new HashSet<HullCell>();
            foreach (var intent in entry.Generation.RequiredVoids)
                cells.Add(intent.Cell);
            foreach (var intent in entry.Generation.Solids)
                cells.Add(intent.Cell);
            foreach (var cut in entry.Generation.AuthorizedDeckCuts)
                cells.Add(cut.Cell);
            if (cells.Count == 0)
                continue;
            reservations.Add(new RequiredVoidExclusion(entry.Id,
                cells.OrderBy(cell => cell.Z).ThenBy(cell => cell.Y).ThenBy(cell => cell.X)
                    .ToImmutableArray()));
        }
        return reservations.ToImmutableArray();
    }

    private static ImmutableArray<SuperstructureGenerationResult> GenerateSuperstructures(
        ShipDocument document,
        HullBuildContext context,
        Arrangement measuredArrangement,
        ArrangementSolution arrangement,
        ImmutableArray<RequiredVoidExclusion> requiredVoids,
        ShipGenerationOptions options,
        List<DesignDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        if (!document.Superstructure.Enabled)
            return [];
        if (document.Superstructure.TowerRoots.Length != 1)
        {
            diagnostics.Add(new DesignDiagnostic(ShipCompositionDiagnosticCodes.RootCardinalityUnsupported,
                DesignSeverity.Error,
                $"The bounded first-pass module graph needs exactly one owning root; the document declares " +
                $"{document.Superstructure.TowerRoots.Length}.",
                document.DocumentId, nameof(document.Superstructure.TowerRoots),
                SuggestedCorrection: "Keep one owning root until layers are explicitly associated with multiple roots."));
            return [];
        }

        var declared = document.Superstructure.TowerRoots[0];
        var span = arrangement.FindNode(declared.SpanNodeId);
        if (span is null)
        {
            diagnostics.Add(NodeMismatch(declared.SpanNodeId,
                $"Superstructure root '{declared.Id}' has no solved owning span."));
            return [];
        }
        var measurement = ModularSuperstructureGenerator.MeasureFootprint(
            document.Superstructure, cancellationToken);
        diagnostics.AddRange(measurement.Diagnostics.Where(diagnostic => !diagnostics.Contains(diagnostic)));
        if (measurement.Footprint is null || diagnostics.HasErrors())
            return [];

        var measuredSpan = measuredArrangement.FindNode(declared.SpanNodeId);
        if (measuredSpan is null)
        {
            diagnostics.Add(NodeMismatch(declared.SpanNodeId,
                $"Superstructure root '{declared.Id}' has no measured owning span."));
            return [];
        }

        // Arrangement offsets run along the bow-to-stern ruler, whereas S01 turns a positive
        // module OffsetAlong into negative world Z. The graph root must therefore sit at the
        // negative of the measured union-centre offset so that the physical union is centred in
        // the solved occupied interval. Both persisted root views must state that same constraint.
        var matchingHandles = measuredSpan.Handles.Where(handle => MatchesRoot(handle, declared.Id)).ToArray();
        var affectedBounds = SpanBounds(span, context.CenterPlaneX);
        if (matchingHandles.Length != 1)
        {
            var identities = matchingHandles.Length == 0
                ? "none"
                : string.Join(", ", matchingHandles.Select(handle => $"'{handle.Id}'").Order(StringComparer.Ordinal));
            diagnostics.Add(new DesignDiagnostic(ShipCompositionDiagnosticCodes.RootHandleConstraintMismatch,
                DesignSeverity.Error,
                $"Superstructure root '{declared.Id}' on span '{declared.SpanNodeId}' needs exactly one " +
                $"matching arrangement handle; found {matchingHandles.Length} ({identities}).",
                declared.SpanNodeId, nameof(ArrangementNode.Handles),
                SuggestedCorrection: "Keep one child handle whose target ID (or legacy handle ID) matches the root ID.",
                AffectedBounds: affectedBounds));
            return [];
        }

        var handle = matchingHandles[0];
        var requiredOffset = -measurement.Footprint.CenterOffsetAlong;
        if (handle.OffsetFromNodeCenter != declared.OffsetFromSpanCenter)
        {
            diagnostics.Add(new DesignDiagnostic(ShipCompositionDiagnosticCodes.RootHandleConstraintMismatch,
                DesignSeverity.Error,
                $"Arrangement handle '{handle.Id}' and superstructure root '{declared.Id}' declare different " +
                $"offsets from span '{declared.SpanNodeId}'.",
                declared.SpanNodeId, nameof(SuperstructureTowerRoot.OffsetFromSpanCenter),
                handle.OffsetFromNodeCenter, declared.OffsetFromSpanCenter,
                "Make the persisted handle and tower-root offsets identical; composition will not choose one silently.",
                affectedBounds));
        }
        if (declared.OffsetFromSpanCenter != requiredOffset)
        {
            diagnostics.Add(new DesignDiagnostic(ShipCompositionDiagnosticCodes.RootHandleConstraintMismatch,
                DesignSeverity.Error,
                $"Superstructure root '{declared.Id}' does not place the measured asymmetric union at the centre " +
                $"of span '{declared.SpanNodeId}'.",
                declared.SpanNodeId, nameof(SuperstructureTowerRoot.OffsetFromSpanCenter),
                declared.OffsetFromSpanCenter, requiredOffset,
                "Move the root/handle together to the reported realized offset, or move module offsets deliberately.",
                affectedBounds));
        }
        if (diagnostics.HasErrors())
            return [];

        var solvedHandles = span.Handles.Where(handle => MatchesRoot(handle, declared.Id)).ToArray();
        if (solvedHandles.Length != 1)
        {
            diagnostics.Add(new DesignDiagnostic(ShipCompositionDiagnosticCodes.RootHandleConstraintMismatch,
                DesignSeverity.Error,
                $"Solved span '{declared.SpanNodeId}' did not retain exactly one handle for root '{declared.Id}'.",
                declared.SpanNodeId, nameof(ArrangementNodeSolution.Handles),
                SuggestedCorrection: "Resolve the root/handle identity conflict before generating modules.",
                AffectedBounds: affectedBounds));
            return [];
        }

        var worldZ = solvedHandles[0].WorldZPosition;
        var placement = new SuperstructureRootPlacement(declared.Id, declared.SpanNodeId,
            context.CenterPlaneX, worldZ);
        var kinds = measuredArrangement.Nodes.ToDictionary(node => node.Id, node => node.Kind,
            StringComparer.Ordinal);
        var result = ModularSuperstructureGenerator.Generate(context, document.Superstructure, placement,
            SuperstructureGenerationRoute.ModularLayersV2, kinds, requiredVoids,
            options.SuperstructureOptions, cancellationToken);
        diagnostics.AddRange(result.Diagnostics);
        return [result];
    }

    private static bool MatchesRoot(ArrangementHandle handle, string rootId) =>
        string.Equals(handle.TargetId, rootId, StringComparison.Ordinal) ||
        handle.TargetId is null && string.Equals(handle.Id, rootId, StringComparison.Ordinal);

    private static bool MatchesRoot(ArrangementHandleSolution handle, string rootId) =>
        string.Equals(handle.TargetId, rootId, StringComparison.Ordinal) ||
        handle.TargetId is null && string.Equals(handle.HandleId, rootId, StringComparison.Ordinal);

    private static DesignBounds SpanBounds(ArrangementNodeSolution span, DesignMeasure centerPlaneX) => new(
        centerPlaneX, centerPlaneX,
        DesignMeasure.Zero, DesignMeasure.Zero,
        span.WorldZCenter - span.OuterHalfExtent,
        span.WorldZCenter + span.OuterHalfExtent);

    private static Dictionary<HullCell, MutablePhysicalCell> BuildComponentCells(
        IReadOnlyList<BarbetteSolidIntent> barbetteSolids,
        InternalStructureGenerationResult internals,
        ImmutableArray<SuperstructureGenerationResult> superstructures,
        List<DesignDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var cells = new Dictionary<HullCell, MutablePhysicalCell>();
        foreach (var intent in barbetteSolids)
            Add(intent.Cell, intent.Material,
                [new PhysicalCellOwner(intent.OwnerId, PhysicalCellRole.Barbette)]);
        foreach (var intent in internals.Cells)
            Add(intent.Cell, intent.Material, intent.Contributions.Select(contribution =>
                new PhysicalCellOwner(contribution.OwnerId, PhysicalCellRole.InternalStructure)));
        foreach (var intent in superstructures.SelectMany(result => result.Cells))
            Add(intent.Cell, intent.Material, intent.ModuleIds.Select(owner =>
                new PhysicalCellOwner(owner, PhysicalCellRole.ModularSuperstructure)));
        return cells;

        void Add(HullCell cell, MaterialKind material, IEnumerable<PhysicalCellOwner> owners)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!cells.TryGetValue(cell, out var existing))
                cells[cell] = existing = new MutablePhysicalCell { Material = material };
            else if (existing.Material != material)
            {
                diagnostics.Add(new DesignDiagnostic(ShipCompositionDiagnosticCodes.PhysicalConflict,
                    DesignSeverity.Error,
                    $"Component intents at ({cell.X}, {cell.Y}, {cell.Z}) request both " +
                    $"{existing.Material} and {material}.",
                    Field: nameof(GeneratedHull.Blocks),
                    SuggestedCorrection: "Move the components apart or make their shared physical material explicit.",
                    AffectedBounds: CellBounds(cell)));
                return;
            }
            existing.Owners.AddRange(owners);
        }
    }

    /// <summary>
    /// Removes every hull cell a barbette is authorized to take over: the concentric square neck
    /// aperture through deck armor, and ordinary interior armor the locally dominant barbette
    /// supersedes. Protected side/bottom skin and deliberate armor air are never removable, and a
    /// member that straddles a removal is re-materialized as its surviving full-volume cells so no
    /// compatible survivor is lost.
    /// </summary>
    private static IReadOnlyList<BlockPlacement> ApplyAuthorizedBarbetteRemovals(
        IReadOnlyList<BlockPlacement> placements,
        IReadOnlySet<HullCell> deckCuts,
        IReadOnlySet<HullCell> supersededArmor,
        HullBuildContext context,
        List<DesignDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var cuts = new HashSet<HullCell>(deckCuts);
        cuts.UnionWith(supersededArmor);
        if (cuts.Count == 0)
            return placements;
        foreach (var cut in cuts.OrderBy(cell => cell.Z).ThenBy(cell => cell.Y).ThenBy(cell => cell.X))
        {
            if (!context.TryGetArmor(cut.X, cut.Y, cut.Z, out var armor) ||
                !armor.IsStructuralArmor || context.IsProtectedShellCell(cut.X, cut.Y, cut.Z))
                diagnostics.Add(new DesignDiagnostic(ShipCompositionDiagnosticCodes.PhysicalConflict,
                    DesignSeverity.Error,
                    $"The requested barbette removal at ({cut.X}, {cut.Y}, {cut.Z}) is not removable " +
                    "hull armor.",
                    Field: nameof(cuts),
                    SuggestedCorrection: "Authorize only deck apertures or ordinary interior armor; side, bottom and rim skin stay protected.",
                    AffectedBounds: CellBounds(cut)));
            else if (deckCuts.Contains(cut) && armor.Region != ArmorRegion.Deck)
                diagnostics.Add(new DesignDiagnostic(ShipCompositionDiagnosticCodes.PhysicalConflict,
                    DesignSeverity.Error,
                    $"Authorized deck cut ({cut.X}, {cut.Y}, {cut.Z}) is not deck-owned armor.",
                    Field: nameof(cuts),
                    SuggestedCorrection: "Authorize only T01 deck-aperture intents; side, bottom and rim armor stay protected.",
                    AffectedBounds: CellBounds(cut)));
        }
        if (diagnostics.HasErrors())
            return [];

        var result = new List<BlockPlacement>(placements.Count);
        var removed = new HashSet<HullCell>();
        foreach (var placement in placements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var occupied = placement.OccupiedCells
                .Select(cell => new HullCell(cell.X, cell.Y, cell.Z)).ToArray();
            if (!occupied.Any(cuts.Contains))
            {
                result.Add(placement);
                continue;
            }
            foreach (var cell in occupied)
            {
                if (cuts.Contains(cell))
                {
                    removed.Add(cell);
                    continue;
                }
                result.Add(placement with
                {
                    Shape = BlockShape.Cube,
                    X = cell.X,
                    Y = cell.Y,
                    Z = cell.Z,
                    Rotation = 0,
                });
            }
        }
        foreach (var cut in cuts)
            if (!removed.Contains(cut))
                diagnostics.Add(new DesignDiagnostic(ShipCompositionDiagnosticCodes.PhysicalConflict,
                    DesignSeverity.Error,
                    $"Authorized barbette removal ({cut.X}, {cut.Y}, {cut.Z}) did not map to a materialized hull member.",
                    Field: nameof(cuts),
                    SuggestedCorrection: "Regenerate the removal from the same captured hull evaluation.",
                    AffectedBounds: CellBounds(cut)));
        return result;
    }

    private static Dictionary<HullCell, BlockPlacement> IndexBase(
        IReadOnlyList<BlockPlacement> placements,
        HullBuildContext context,
        Dictionary<HullCell, MutablePhysicalCell> provenance,
        List<DesignDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var occupied = new Dictionary<HullCell, BlockPlacement>();
        foreach (var placement in placements)
        foreach (var tuple in placement.OccupiedCells)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cell = new HullCell(tuple.X, tuple.Y, tuple.Z);
            if (!occupied.TryAdd(cell, placement))
            {
                diagnostics.Add(new DesignDiagnostic(ShipCompositionDiagnosticCodes.PhysicalConflict,
                    DesignSeverity.Error,
                    $"The base physical plan contains duplicate occupancy at ({cell.X}, {cell.Y}, {cell.Z}).",
                    Field: nameof(GeneratedHull.Blocks), AffectedBounds: CellBounds(cell)));
                continue;
            }
            var role = placement.Origin switch
            {
                BlockOrigin.Smoothing => PhysicalCellRole.HullSmoothing,
                BlockOrigin.Superstructure or BlockOrigin.SuperstructureSmoothing =>
                    PhysicalCellRole.LegacySuperstructure,
                _ when context.TryGetArmor(cell.X, cell.Y, cell.Z, out var armor) && armor.IsStructuralArmor =>
                    PhysicalCellRole.HullArmor,
                _ => PhysicalCellRole.HullExterior,
            };
            var ownerId = role switch
            {
                PhysicalCellRole.HullSmoothing => "hull:smoothing",
                PhysicalCellRole.LegacySuperstructure => "legacy-superstructure",
                _ => "hull",
            };
            provenance[cell] = new MutablePhysicalCell { Material = placement.Material };
            provenance[cell].Owners.Add(new PhysicalCellOwner(ownerId, role));
        }
        return occupied;
    }

    private static void ReconcileComponents(
        Dictionary<HullCell, MutablePhysicalCell> components,
        Dictionary<HullCell, BlockPlacement> occupied,
        Dictionary<HullCell, MutablePhysicalCell> provenance,
        HullBuildContext context,
        List<DesignDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        foreach (var (cell, component) in components.OrderBy(pair => pair.Key.Z)
                     .ThenBy(pair => pair.Key.Y).ThenBy(pair => pair.Key.X))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (context.IsProtectedShellCell(cell.X, cell.Y, cell.Z) ||
                context.IsReservedArmorAir(cell.X, cell.Y, cell.Z))
            {
                diagnostics.Add(new DesignDiagnostic(ShipCompositionDiagnosticCodes.PhysicalConflict,
                    DesignSeverity.Error,
                    $"Component intent reaches protected hull ownership at ({cell.X}, {cell.Y}, {cell.Z}).",
                    Field: nameof(GeneratedHull.Blocks),
                    SuggestedCorrection: "Move or clip the component without consuming shell armor or reserved armor air.",
                    AffectedBounds: CellBounds(cell)));
                continue;
            }
            if (!occupied.TryGetValue(cell, out var basePlacement))
            {
                provenance[cell] = component;
                continue;
            }
            if (basePlacement.Material != component.Material)
            {
                diagnostics.Add(new DesignDiagnostic(ShipCompositionDiagnosticCodes.PhysicalConflict,
                    DesignSeverity.Error,
                    $"Component {component.Material} conflicts with existing {basePlacement.Material} at " +
                    $"({cell.X}, {cell.Y}, {cell.Z}).",
                    Field: nameof(GeneratedHull.Blocks),
                    SuggestedCorrection: "Move the component or choose the existing physical material explicitly.",
                    AffectedBounds: CellBounds(cell)));
                continue;
            }
            provenance[cell].Owners.AddRange(component.Owners);
        }
    }

    private static void ValidateRequiredVoids(
        HashSet<HullCell> voids,
        IReadOnlyDictionary<HullCell, BlockPlacement> occupied,
        IReadOnlyDictionary<HullCell, MutablePhysicalCell> components,
        List<DesignDiagnostic> diagnostics)
    {
        var collision = voids.Where(cell => occupied.ContainsKey(cell) || components.ContainsKey(cell))
            .OrderBy(cell => cell.Z).ThenBy(cell => cell.Y).ThenBy(cell => cell.X).FirstOrDefault();
        if (collision != default || voids.Contains(default) &&
            (occupied.ContainsKey(default) || components.ContainsKey(default)))
            diagnostics.Add(new DesignDiagnostic(ShipCompositionDiagnosticCodes.RequiredVoidOccupied,
                DesignSeverity.Error,
                $"A required well is occupied at ({collision.X}, {collision.Y}, {collision.Z}).",
                Field: nameof(voids),
                SuggestedCorrection: "Keep every T01 required-void cell clear through packing and catalog resolution.",
                AffectedBounds: CellBounds(collision)));
    }

    private static IReadOnlyList<BlockPlacement> MaterializeComponents(
        Dictionary<HullCell, MutablePhysicalCell> components,
        IReadOnlyDictionary<HullCell, BlockPlacement> occupied,
        CancellationToken cancellationToken)
    {
        var placements = new List<BlockPlacement>();
        foreach (var (cell, intent) in components.OrderBy(pair => pair.Key.Z)
                     .ThenBy(pair => pair.Key.Y).ThenBy(pair => pair.Key.X))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (occupied.ContainsKey(cell))
                continue;
            var allSuperstructure = intent.Owners.Count > 0 && intent.Owners.All(owner =>
                owner.Role == PhysicalCellRole.ModularSuperstructure);
            placements.Add(new BlockPlacement(BlockShape.Cube, intent.Material,
                cell.X, cell.Y, cell.Z, 0)
            {
                Origin = allSuperstructure ? BlockOrigin.Superstructure : BlockOrigin.Shell,
                Construction = ArmorConstruction.Solid,
                ArmorRegion = ArmorRegion.Side,
            });
        }
        return placements;
    }

    private static IReadOnlyList<BlockPlacement> PackComponents(IReadOnlyList<BlockPlacement> placements)
    {
        if (placements.Count == 0)
            return placements;
        // Provenance remains cell-level. Packing is kept within material/origin groups so a native
        // member cannot silently acquire the preview semantics of another component family.
        return placements.GroupBy(block => (block.Material, block.Origin))
            .SelectMany(group => BeamOptimizer.Merge(group.ToArray(), mergeFlattenedShellCandidates: true))
            .OrderBy(block => block.Z).ThenBy(block => block.Y).ThenBy(block => block.X).ToArray();
    }

    internal static (GeneratedHull Hull, int FallbackCount) ResolveCatalog(
        GeneratedHull hull,
        FtdBlockCatalog catalog,
        CancellationToken cancellationToken,
        IReadOnlySet<HullCell>? smoothingBlockers = null)
    {
        var placements = new List<BlockPlacement>(hull.Blocks.Count);
        var fallbackCount = 0;
        foreach (var placement in hull.Blocks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (placement.Origin == BlockOrigin.Smoothing && smoothingBlockers is not null &&
                placement.OccupiedCells.Any(cell => smoothingBlockers.Contains(
                    new HullCell(cell.X, cell.Y, cell.Z))))
                continue;
            if (!placement.KeepsFittedShape)
            {
                placements.Add(placement.Shape == BlockShape.Cube
                    ? placement
                    : placement with { Shape = BlockShape.Cube, Rotation = 0 });
                continue;
            }
            if (!catalog.Resolve(placement.Material, placement.Shape).IsFallback)
            {
                placements.Add(placement);
                continue;
            }
            fallbackCount++;
            foreach (var cell in placement.OccupiedCells)
                placements.Add(placement with
                {
                    Shape = BlockShape.Cube,
                    X = cell.X,
                    Y = cell.Y,
                    Z = cell.Z,
                    Rotation = 0,
                    UsePoles = false,
                    Construction = ArmorConstruction.Solid,
                });
        }
        return (WithPlacements(hull, placements.OrderBy(block => block.Z).ThenBy(block => block.Y)
            .ThenBy(block => block.X).ToArray()), fallbackCount);
    }

    private static void ValidateFinalPhysicalPlan(
        GeneratedHull hull,
        HullBuildContext context,
        IReadOnlyDictionary<HullCell, MutablePhysicalCell> provenance,
        FtdBlockCatalog catalog,
        IReadOnlySet<HullCell> requiredVoids,
        IReadOnlySet<HullCell> authorizedCuts,
        List<DesignDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        if (hull.Blocks.Count == 0)
        {
            diagnostics.Add(DesignDiagnostic.Error(ShipCompositionDiagnosticCodes.PhysicalConflict,
                "The resolved physical ship contains no placements.", field: nameof(GeneratedHull.Blocks)));
            return;
        }

        var occupied = new Dictionary<HullCell, BlockPlacement>();
        var anchors = new HashSet<HullCell>();
        HullCell? badRotation = null;
        HullCell? badCatalog = null;
        HullCell? duplicateAnchor = null;
        HullCell? duplicateCell = null;
        foreach (var placement in hull.Blocks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var anchor = new HullCell(placement.X, placement.Y, placement.Z);
            if (!anchors.Add(anchor)) duplicateAnchor ??= anchor;
            if (!BlockRotations.IsValid(placement.Rotation))
            {
                badRotation ??= anchor;
                continue;
            }
            if (!Enum.IsDefined(placement.Material) ||
                catalog.Resolve(placement.Material, placement.Shape).IsFallback)
                badCatalog ??= anchor;
            foreach (var tuple in placement.OccupiedCells)
            {
                var cell = new HullCell(tuple.X, tuple.Y, tuple.Z);
                if (!occupied.TryAdd(cell, placement)) duplicateCell ??= cell;
            }
        }

        AddCellError(badRotation, "A resolved placement has an invalid native rotation.",
            "Regenerate the component revision using one of the canonical 24 BLR rotations.");
        AddCellError(badCatalog, "A resolved placement is not available in the captured installed catalog.",
            "Refresh the captured catalog or retain the conservative cube substitution.");
        AddCellError(duplicateAnchor, "Two resolved placements share one native anchor.",
            "Reconcile ownership before native packing so every anchor is unique.");
        AddCellError(duplicateCell, "Two resolved placements occupy the same physical metre.",
            "Resolve the component junction before final native packing.");
        if (diagnostics.HasErrors()) return;

        var bounds = occupied.Keys.ToArray();
        if (bounds.Min(cell => cell.X) != hull.MinX || bounds.Max(cell => cell.X) != hull.MaxX ||
            bounds.Min(cell => cell.Y) != hull.MinY || bounds.Max(cell => cell.Y) != hull.MaxY ||
            bounds.Min(cell => cell.Z) != hull.MinZ || bounds.Max(cell => cell.Z) != hull.MaxZ)
            diagnostics.Add(new DesignDiagnostic(ShipCompositionDiagnosticCodes.PhysicalConflict,
                DesignSeverity.Error,
                "Resolved placement bounds do not match the exported physical bounds.",
                Field: nameof(GeneratedHull.Blocks),
                SuggestedCorrection: "Recompute bounds from the final catalog-resolved placement list."));

        var occupiedCells = occupied.Keys.ToHashSet();
        // Required voids must stay empty. Authorized deck cuts are only permission to remove deck
        // material: the concentric neck armor legitimately refills the neck footprint, so a cut cell
        // may be occupied by the component that opened it.
        var forbidden = requiredVoids.Where(occupiedCells.Contains)
            .OrderBy(cell => cell.Z).ThenBy(cell => cell.Y).ThenBy(cell => cell.X)
            .Select(cell => (HullCell?)cell).FirstOrDefault();
        if (forbidden is { } forbiddenCell)
            diagnostics.Add(new DesignDiagnostic(ShipCompositionDiagnosticCodes.RequiredVoidOccupied,
                DesignSeverity.Error,
                $"A required well or authorized aperture is occupied at " +
                $"({forbiddenCell.X}, {forbiddenCell.Y}, {forbiddenCell.Z}).",
                Field: nameof(requiredVoids),
                SuggestedCorrection: "Keep every reserved void and aperture clear through final catalog materialization.",
                AffectedBounds: CellBounds(forbiddenCell)));

        var missingArmor = context.EnumerateArmor()
            .Where(intent => intent.IsStructuralArmor && !authorizedCuts.Contains(intent.Cell))
            .Where(intent => !occupied.TryGetValue(intent.Cell, out var placement) ||
                             placement.Material != intent.Material)
            .Select(intent => intent.Cell)
            .OrderBy(cell => cell.Z).ThenBy(cell => cell.Y).ThenBy(cell => cell.X)
            .Select(cell => (HullCell?)cell).FirstOrDefault();
        if (missingArmor is { } missingArmorCell)
            AddCellError(missingArmorCell, "Protected hull armor is missing or changed material in the resolved plan.",
                "Only explicitly authorized deck-interior cells may be removed; restore shell/deck ownership.");

        var filledReservedAir = context.EnumerateArmor().Where(intent => intent.IsReservedAir)
            .Select(intent => intent.Cell).Where(occupiedCells.Contains)
            .OrderBy(cell => cell.Z).ThenBy(cell => cell.Y).ThenBy(cell => cell.X)
            .Select(cell => (HullCell?)cell).FirstOrDefault();
        if (filledReservedAir is { } filledReservedAirCell)
            AddCellError(filledReservedAirCell, "A deliberate armor-air reservation was filled.",
                "Clip component intent without consuming reserved armor air.");

        var badProvenance = occupied.Where(pair =>
                !provenance.TryGetValue(pair.Key, out var value) ||
                value.Material != pair.Value.Material || value.Owners.Count == 0)
            .OrderBy(pair => pair.Key.Z).ThenBy(pair => pair.Key.Y).ThenBy(pair => pair.Key.X)
            .Select(pair => (HullCell?)pair.Key).FirstOrDefault();
        if (badProvenance is { } badProvenanceCell)
            AddCellError(badProvenanceCell, "An occupied physical metre lacks matching ownership provenance.",
                "Retain material and all contributing owner IDs through packing.");
        var extraProvenance = provenance.Keys.Where(cell => !occupiedCells.Contains(cell))
            .OrderBy(cell => cell.Z).ThenBy(cell => cell.Y).ThenBy(cell => cell.X)
            .Select(cell => (HullCell?)cell).FirstOrDefault();
        if (extraProvenance is { } extraProvenanceCell)
            AddCellError(extraProvenanceCell, "Ownership provenance names a cell absent from the resolved plan.",
                "Derive provenance from the final occupied-cell set.");

        var unsupported = HullGeometryValidator.FindUnsupportedNativeArmorContacts(hull)
            .Select(placement => (BlockPlacement?)placement).FirstOrDefault();
        if (unsupported is { } unsupportedPlacement)
            AddCellError(new HullCell(unsupportedPlacement.X, unsupportedPlacement.Y, unsupportedPlacement.Z),
                $"Native armor member {unsupportedPlacement.Shape} has no verified positive-area contact.",
                "Use a compatible full-volume member at the ownership seam.");

        var disconnected = FindDisconnectedCell(occupiedCells, cancellationToken);
        if (disconnected is { } isolated)
            AddCellError(isolated, "The resolved physical ship contains a face-disconnected placement group.",
                "Move the component onto verified positive-face support or add an explicit supported connection.");
        return;

        void AddCellError(HullCell? cell, string message, string correction)
        {
            if (cell is not { } value) return;
            diagnostics.Add(new DesignDiagnostic(ShipCompositionDiagnosticCodes.PhysicalConflict,
                DesignSeverity.Error, message, Field: nameof(GeneratedHull.Blocks),
                SuggestedCorrection: correction, AffectedBounds: CellBounds(value)));
        }
    }

    private static HullCell? FindDisconnectedCell(
        IReadOnlySet<HullCell> occupied,
        CancellationToken cancellationToken)
    {
        if (occupied.Count < 2) return null;
        var start = occupied.OrderBy(cell => cell.Z).ThenBy(cell => cell.Y).ThenBy(cell => cell.X).First();
        var visited = new HashSet<HullCell> { start };
        var queue = new Queue<HullCell>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cell = queue.Dequeue();
            Visit(new HullCell(cell.X - 1, cell.Y, cell.Z));
            Visit(new HullCell(cell.X + 1, cell.Y, cell.Z));
            Visit(new HullCell(cell.X, cell.Y - 1, cell.Z));
            Visit(new HullCell(cell.X, cell.Y + 1, cell.Z));
            Visit(new HullCell(cell.X, cell.Y, cell.Z - 1));
            Visit(new HullCell(cell.X, cell.Y, cell.Z + 1));
        }
        return visited.Count == occupied.Count
            ? null
            : occupied.Where(cell => !visited.Contains(cell)).OrderBy(cell => cell.Z)
                .ThenBy(cell => cell.Y).ThenBy(cell => cell.X).First();

        void Visit(HullCell candidate)
        {
            if (occupied.Contains(candidate) && visited.Add(candidate)) queue.Enqueue(candidate);
        }
    }

    private static GeneratedHull WithPlacements(GeneratedHull source, IReadOnlyList<BlockPlacement> placements)
    {
        if (placements.Count == 0)
            return new GeneratedHull(source.Parameters, placements, 0, 0, 0, 0, 0, 0)
            {
                ConstructionNotes = source.ConstructionNotes,
            };
        var cells = placements.SelectMany(block => block.OccupiedCells).ToArray();
        return new GeneratedHull(source.Parameters, placements,
            cells.Min(cell => cell.X), cells.Max(cell => cell.X),
            cells.Min(cell => cell.Y), cells.Max(cell => cell.Y),
            cells.Min(cell => cell.Z), cells.Max(cell => cell.Z))
        {
            ConstructionNotes = source.ConstructionNotes,
        };
    }

    private static void ValidateOptions(ShipGenerationOptions options, List<DesignDiagnostic> diagnostics)
    {
        if (options.MaxPhysicalCells < 1)
            diagnostics.Add(BudgetDiagnostic(options.MaxPhysicalCells, 0));
    }

    private static DesignDiagnostic BudgetDiagnostic(int limit, long requested) =>
        new(ShipCompositionDiagnosticCodes.PhysicalBudgetExceeded, DesignSeverity.Error,
            $"Composition requests {requested:N0} tracked physical cells against a {limit:N0}-cell budget.",
            Field: nameof(ShipGenerationOptions.MaxPhysicalCells),
            SuggestedCorrection: "Reduce hull/components or raise the explicit bounded composition budget.");

    private static DesignDiagnostic NodeMismatch(string nodeId, string message) =>
        new(ShipCompositionDiagnosticCodes.ComponentNodeMismatch, DesignSeverity.Error, message,
            nodeId, SuggestedCorrection: "Repair the stable component/node/root identifiers before generation.");

    private static DesignBounds CellBounds(HullCell cell) => new(
        DesignMeasure.FromCellAnchor(cell.X), DesignMeasure.FromCellAnchor(cell.X),
        DesignMeasure.FromCellAnchor(cell.Y), DesignMeasure.FromCellAnchor(cell.Y),
        DesignMeasure.FromCellAnchor(cell.Z), DesignMeasure.FromCellAnchor(cell.Z));

    private static ShipGenerationResult Rejected(IEnumerable<DesignDiagnostic> diagnostics) =>
        new(null, diagnostics.ToImmutableArray());
}
