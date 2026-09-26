using System.Collections.Immutable;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Decorations;
using FtdHullGenerator.Domain.Design;
using FtdHullGenerator.Domain.Projects;
using FtdHullGenerator.Geometry.Composition;
using FtdHullGenerator.Serialization.Decorations;

namespace FtdHullGenerator.Geometry.Refinement;

/// <summary>The shared visual/export authority, frozen after final native catalog resolution.</summary>
public sealed class ResolvedSlopeRefinement
{
    private readonly ShipDocument document;
    private readonly long revision;
    private readonly GeneratedHull hull;
    private readonly string catalogFingerprint;
    private readonly ImmutableArray<BlockPlacement> native;
    public ImmutableArray<NativeSlopeExtension> Extensions { get; }
    public DecorationModule Module { get; }
    public DecorationVisualBounds? VisualBounds { get; }

    private ResolvedSlopeRefinement(ShipGenerationSnapshot snapshot, ImmutableArray<NativeSlopeExtension> extensions)
    {
        document = snapshot.Document;
        hull = snapshot.Hull;
        revision = snapshot.Revision;
        catalogFingerprint = ShipCatalogFingerprint.Compute(snapshot.Catalog);
        native = snapshot.Hull.Blocks.ToImmutableArray();
        Extensions = extensions;
        Module = new DecorationModule(extensions.Select(e => e.Record));
        if (!extensions.IsEmpty)
            VisualBounds = new(extensions.Min(e => e.VisualBounds.MinX), extensions.Max(e => e.VisualBounds.MaxX),
                extensions.Min(e => e.VisualBounds.MinY), extensions.Max(e => e.VisualBounds.MaxY),
                extensions.Min(e => e.VisualBounds.MinZ), extensions.Max(e => e.VisualBounds.MaxZ));
        // Enforce combined D02 framing limits before a snapshot can reach preview or export.
        _ = DecorationModuleWriter.Write(Module);
    }

    public void ValidateFor(ShipGenerationSnapshot snapshot)
    {
        if (!ReferenceEquals(document, snapshot.Document) || !ReferenceEquals(hull, snapshot.Hull) || revision != snapshot.Revision ||
            snapshot.Hull.SourceRevision != revision || snapshot.Hull.SourceDocumentId != document.DocumentId ||
            catalogFingerprint != snapshot.Hull.ResolvedCatalogFingerprint ||
            catalogFingerprint != ShipCatalogFingerprint.Compute(snapshot.Catalog) ||
            !native.SequenceEqual(snapshot.Hull.Blocks))
            throw new InvalidOperationException("DEC105: Decorative snapshot no longer matches its native document/revision/catalog. Regenerate the current revision.");
    }

    public static ShipGenerationResult Attach(ShipGenerationSnapshot snapshot, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var intent = snapshot.Document.Smoothing.ExplicitRefinement;
        var automaticKind = SmoothingMethodMapping.DecorationKind(snapshot.Document.Smoothing.NativeMethod);
        if (intent is null && automaticKind is null)
            return new(snapshot with { Refinement = null }, snapshot.Diagnostics);
        var diagnostics = snapshot.Diagnostics.ToBuilder();
        ImmutableArray<SlopeExtensionRequest> selected;
        if (intent is not null)
        {
            diagnostics.AddRange(intent.Validate());
            if (diagnostics.Any(d => d.IsError)) return new(null, diagnostics.ToImmutable());
            selected = intent.Requests;
        }
        else
        {
            // Deco Vertical/Horizontal derive the explicit-length requests from the final resolved
            // native plan; the proven finite construction below is unchanged.
            selected = DecoSlopeRunSelector.Select(snapshot, automaticKind!.Value, diagnostics, token);
            if (diagnostics.Any(d => d.IsError)) return new(null, diagnostics.ToImmutable());
        }
        var vertical = new List<SlopeExtensionRequest>();
        var horizontal = new List<SlopeExtensionRequest>();
        var positions = new HashSet<(int, int, int)>();
        foreach (var request in selected)
        {
            token.ThrowIfCancellationRequested();
            if (!positions.Add(request.Anchor.Position))
                diagnostics.Add(DesignDiagnostic.Error(SlopeExtensionDiagnosticCodes.InvalidAnchor, "Each explicit native anchor may be selected only once."));
            if (request.Anchor.Rotation is 16 or 17 or 18 or 19) horizontal.Add(request);
            else vertical.Add(request);
        }
        if (diagnostics.Any(d => d.IsError)) return new(null, diagnostics.ToImmutable());
        var v = new VerticalSlopeExtensionPlanner().Build(snapshot, vertical, token);
        var h = new HorizontalSlopeExtensionPlanner().Build(snapshot, horizontal, token);
        diagnostics.AddRange(v.Diagnostics);
        diagnostics.AddRange(h.Diagnostics);
        if (diagnostics.Any(d => d.IsError)) return new(null, diagnostics.ToImmutable());
        var combined = ImmutableArray.CreateBuilder<NativeSlopeExtension>();
        foreach (var extension in v.Extensions.Concat(h.Extensions).OrderBy(e => e.Anchor.Placement.X)
                     .ThenBy(e => e.Anchor.Placement.Y).ThenBy(e => e.Anchor.Placement.Z))
        {
            token.ThrowIfCancellationRequested();
            combined.Add(new NativeSlopeExtension(extension.Anchor, extension.Kind, extension.RequestedLengthMetres,
                extension.Record with { DecorationId = combined.Count }));
        }
        try
        {
            var refined = new ResolvedSlopeRefinement(snapshot, combined.ToImmutable());
            token.ThrowIfCancellationRequested();
            var result = snapshot with { Refinement = refined, Diagnostics = diagnostics.ToImmutable() };
            return new(result, result.Diagnostics);
        }
        catch (InvalidDataException error)
        {
            diagnostics.Add(DesignDiagnostic.Error(SlopeExtensionDiagnosticCodes.PayloadLimit, error.Message));
            return new(null, diagnostics.ToImmutable());
        }
    }
}
