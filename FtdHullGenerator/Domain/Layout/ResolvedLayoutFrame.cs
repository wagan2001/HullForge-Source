using FtdHullGenerator.Domain.Design;

namespace FtdHullGenerator.Domain.Layout;

/// <summary>
/// Stable diagnostic codes owned by the derived layout frame. Codes are part of the contract:
/// they are added, never repurposed.
/// </summary>
public static class LayoutFrameDiagnosticCodes
{
    /// <summary>
    /// The evaluated hull has no contiguous supported structural-deck interval, or an identity/
    /// bounds check failed, so no frame can be published. Never replaced by a zeroed frame.
    /// </summary>
    public const string FrameUnavailable = "LAY010";
}

/// <summary>
/// An immutable, fully evaluated coordinate frame for one hull revision: the forward structural
/// deck face that defines ruler zero, the continuous supported deck interval that defines the
/// ruler's stern end, and the hull's true lateral centre plane.
/// </summary>
/// <remarks>
/// <para>
/// This is <b>derived state</b>. It is an evaluation of <see cref="HullParameters"/> against the
/// current installed geometry pipeline, not persisted design intent: it never enters
/// <see cref="Projects.ShipDocument"/> and is never written back by an editor transaction. The
/// persisted <see cref="LayoutDatum"/> remains the only saved datum, and it is only refreshed from
/// a measured frame; a frame is never used to reinterpret an old datum.
/// </para>
/// <para>
/// Contract C02 fixes the ruler algebra: the ruler coordinate <c>s</c> runs bow to stern and
/// <c>z = BowDatum - s</c>. <see cref="SourceDocumentId"/> and <see cref="SourceRevision"/> bind a
/// frame to the exact immutable revision it was evaluated from; <see cref="JobId"/> is the
/// monotonically increasing preparation identity that lets a superseded background job be rejected.
/// </para>
/// </remarks>
public sealed record ResolvedLayoutFrame(
    DesignMeasure BowDatum,
    DesignMeasure SupportedRulerEnd,
    DesignMeasure CenterPlaneX,
    string SourceDocumentId,
    long SourceRevision,
    long JobId,
    string? CatalogIdentity)
{
    /// <summary>
    /// True when this frame still describes the given document and revision. A frame evaluated from
    /// any other revision is stale and must not be used to place or measure components.
    /// </summary>
    public bool IsCurrent(string documentId, long revision) =>
        SourceRevision == revision &&
        string.Equals(SourceDocumentId, documentId, StringComparison.Ordinal);

    /// <summary>Converts a bow-to-stern ruler centre to world Z: <c>z = BowDatum - s</c>.</summary>
    public DesignMeasure RulerCenterToWorldZ(DesignMeasure rulerCenter) => BowDatum - rulerCenter;

    /// <summary>Converts a world Z coordinate to a bow-to-stern ruler centre: <c>s = BowDatum - z</c>.</summary>
    public DesignMeasure WorldZToRulerCenter(DesignMeasure worldZ) => BowDatum - worldZ;

    /// <summary>
    /// Checks the derived frame against the supported design bounds. An invalid frame must never be
    /// published; callers fail closed rather than substituting a zeroed origin.
    /// </summary>
    public IEnumerable<DesignDiagnostic> Validate()
    {
        if (string.IsNullOrWhiteSpace(SourceDocumentId))
            yield return DesignDiagnostic.Error(DesignDiagnosticCodes.IdentifierMissing,
                "The resolved layout frame has no source document identity.",
                field: nameof(SourceDocumentId));

        if (!BowDatum.IsWithinDesignBounds)
            yield return OutOfRange(nameof(BowDatum), BowDatum);
        if (!SupportedRulerEnd.IsWithinDesignBounds || SupportedRulerEnd < DesignMeasure.Zero)
            yield return OutOfRange(nameof(SupportedRulerEnd), SupportedRulerEnd);
        if (!CenterPlaneX.IsWithinDesignBounds)
            yield return OutOfRange(nameof(CenterPlaneX), CenterPlaneX);

        if (SourceRevision < 0)
            yield return DesignDiagnostic.Error(LayoutFrameDiagnosticCodes.FrameUnavailable,
                "The resolved layout frame carries a negative source revision.",
                field: nameof(SourceRevision));
        if (JobId < 0)
            yield return DesignDiagnostic.Error(LayoutFrameDiagnosticCodes.FrameUnavailable,
                "The resolved layout frame carries a negative preparation job identity.",
                field: nameof(JobId));
    }

    private static DesignDiagnostic OutOfRange(string field, DesignMeasure value) =>
        new(DesignDiagnosticCodes.MeasureOutOfRange, DesignSeverity.Error,
            $"The resolved layout frame's {field} lies outside the supported design range.",
            Field: field, Requested: value,
            Realized: DesignMeasure.FromTwiceMetres(DesignLimits.MaxDesignTwiceMetres),
            SuggestedCorrection: "Re-evaluate the hull before using its arrangement frame.");
}
