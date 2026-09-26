using FtdHullGenerator.Domain.Design;

namespace FtdHullGenerator.Domain.Layout;

/// <summary>
/// Where the bow-first arrangement ruler sits in world space, and where the hull's true lateral
/// symmetry plane is.
/// </summary>
/// <remarks>
/// Contract C02 defines the ruler coordinate <c>s</c> running bow to stern with
/// <c>z = layoutBowZ - s</c>, and names the real mirror plane as <c>(MinX + MaxX) / 2</c>. Both are
/// design intent, so they are persisted with the document: a chain solved without a datum cannot be
/// re-opened and reproduced. <see cref="CenterPlaneX"/> is the value a
/// <see cref="CenterlineLattice"/> reports as its <c>CenterPlane</c> once the hull is evaluated.
/// </remarks>
public sealed record LayoutDatum(
    DesignMeasure LayoutBowZ,
    DesignMeasure CenterPlaneX)
{
    /// <summary>A datum on the default hull's centreline with its bow datum at the origin.</summary>
    public static LayoutDatum Origin { get; } = new(DesignMeasure.Zero, DesignMeasure.Zero);

    public IEnumerable<DesignDiagnostic> Validate()
    {
        if (!LayoutBowZ.IsWithinDesignBounds)
            yield return new DesignDiagnostic(DesignDiagnosticCodes.MeasureOutOfRange, DesignSeverity.Error,
                "The arrangement bow datum lies outside the supported design range.",
                Field: nameof(LayoutBowZ), Requested: LayoutBowZ,
                Realized: DesignMeasure.FromTwiceMetres(DesignLimits.MaxDesignTwiceMetres));
        if (!CenterPlaneX.IsWithinDesignBounds)
            yield return new DesignDiagnostic(DesignDiagnosticCodes.MeasureOutOfRange, DesignSeverity.Error,
                "The hull's centre plane lies outside the supported design range.",
                Field: nameof(CenterPlaneX), Requested: CenterPlaneX,
                Realized: DesignMeasure.FromTwiceMetres(DesignLimits.MaxDesignTwiceMetres));
    }
}
