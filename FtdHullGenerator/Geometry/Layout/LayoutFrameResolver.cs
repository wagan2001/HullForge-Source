using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Design;
using FtdHullGenerator.Domain.Layout;
using FtdHullGenerator.Geometry.Composition;

namespace FtdHullGenerator.Geometry.Layout;

/// <summary>
/// The outcome of resolving a <see cref="ResolvedLayoutFrame"/>. An unsupported or invalid hull
/// returns <see cref="Frame"/> as <c>null</c> with diagnostics; it never returns a zeroed frame.
/// </summary>
public sealed record LayoutFrameResolution(
    ResolvedLayoutFrame? Frame,
    IReadOnlyList<DesignDiagnostic> Diagnostics)
{
    public bool IsResolved => Frame is not null;

    internal static LayoutFrameResolution Unavailable(IEnumerable<DesignDiagnostic> diagnostics) =>
        new(null, diagnostics as IReadOnlyList<DesignDiagnostic> ?? diagnostics.ToArray());

    internal static LayoutFrameResolution Unavailable(DesignDiagnostic diagnostic) =>
        new(null, [diagnostic]);
}

/// <summary>
/// Evaluates the derived arrangement frame of one hull revision. It creates the hull context once
/// and delegates the structural-deck-interval math to
/// <see cref="SupportedDeckRulerAuthority"/>; it does not re-derive the ruler anywhere else.
/// </summary>
/// <remarks>
/// The frame is derived state: resolving it never commits an editor transaction, never mutates a
/// document and never creates an undo/history item.
/// </remarks>
public static class LayoutFrameResolver
{
    /// <summary>
    /// Resolves the frame for <paramref name="parameters"/> bound to the given document identity,
    /// source revision, preparation job and (nullable) catalog identity. Returns no frame, with a
    /// diagnostic, when the hull is invalid or has no contiguous supported structural-deck interval.
    /// </summary>
    public static LayoutFrameResolution Resolve(
        HullParameters parameters,
        string documentId,
        long sourceRevision,
        long jobId,
        string? catalogIdentity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        cancellationToken.ThrowIfCancellationRequested();

        HullBuildContext context;
        try
        {
            context = HullGenerator.CreateContext(parameters, cancellationToken: cancellationToken);
        }
        catch (HullGenerationException error)
        {
            return LayoutFrameResolution.Unavailable(error.Errors.Select(message =>
                DesignDiagnostic.Error(HullContextDiagnosticCodes.TopologyInvalid, message,
                    documentId, nameof(HullParameters))));
        }

        var diagnostics = context.Diagnostics.ToList();
        if (diagnostics.HasErrors())
            return LayoutFrameResolution.Unavailable(diagnostics);

        var measurement = SupportedDeckRulerAuthority.Measure(context);
        if (measurement is null)
        {
            diagnostics.Add(new DesignDiagnostic(LayoutFrameDiagnosticCodes.FrameUnavailable,
                DesignSeverity.Error,
                "The hull has no structural deck interval to define an arrangement frame.",
                documentId, nameof(ResolvedLayoutFrame.BowDatum),
                SuggestedCorrection: "Add structural deck armor before placing deck-supported components."));
            return LayoutFrameResolution.Unavailable(diagnostics);
        }

        if (measurement.GapStation is { } gapStation)
        {
            diagnostics.Add(new DesignDiagnostic(LayoutFrameDiagnosticCodes.FrameUnavailable,
                DesignSeverity.Error,
                $"The structural deck support interval contains a gap at world Z={gapStation} m.",
                documentId, nameof(ResolvedLayoutFrame.BowDatum),
                SuggestedCorrection: "Use one continuous supported deck interval before placing components."));
            return LayoutFrameResolution.Unavailable(diagnostics);
        }

        var frame = new ResolvedLayoutFrame(
            measurement.BowDatum,
            measurement.SupportedEnd,
            context.CenterPlaneX,
            documentId,
            sourceRevision,
            jobId,
            catalogIdentity);
        var frameDiagnostics = frame.Validate().ToList();
        if (frameDiagnostics.HasErrors())
        {
            diagnostics.AddRange(frameDiagnostics);
            return LayoutFrameResolution.Unavailable(diagnostics);
        }

        return new LayoutFrameResolution(frame, diagnostics);
    }
}
