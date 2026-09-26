using System.Collections.Immutable;
using FtdHullGenerator.Domain.Design;
using FtdHullGenerator.Domain.Layout;

namespace FtdHullGenerator.Geometry.Layout;

/// <summary>
/// Applies an explicit resize policy to the arrangement's ruler values.
/// </summary>
/// <remarks>
/// The default policy keeps physical component sizes and absolute gap values and simply
/// re-evaluates the anchor, which is what a hull-length change must do by default. Proportional
/// scaling is only applied when the caller explicitly asks for it, and even then it scales the
/// margins and the *literal* gap segments only: a named gap is a named construction rule (the 5 m
/// gun-pair gap), and node footprints come from the realized raster, so neither is silently
/// rescaled here. Armor is never touched by this type.
/// </remarks>
/// <remarks>
/// Rounding is away from zero on an exact half, so a scaled value never drifts below the requested
/// proportion; the result is still reported as a realized value.
/// </remarks>
public static class ArrangementResize
{
    public static Arrangement Apply(
        Arrangement source,
        DesignMeasure oldSupportedLength,
        DesignMeasure newSupportedLength,
        ArrangementResizePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (oldSupportedLength <= DesignMeasure.Zero)
            throw new ArgumentOutOfRangeException(nameof(oldSupportedLength),
                "The previous supported length must be positive.");

        if (policy == ArrangementResizePolicy.PreserveAbsolute)
            return source;

        DesignMeasure Scale(DesignMeasure value) => DesignMeasure.FromTwiceMetres((int)Math.Round(
            value.TwiceMetres * ((double)newSupportedLength.TwiceMetres / oldSupportedLength.TwiceMetres),
            MidpointRounding.AwayFromZero));

        return source with
        {
            BowMargin = Scale(source.BowMargin),
            SternMargin = Scale(source.SternMargin),
            Gaps = source.Gaps
                .Select(gap => gap.NamedGapId is null ? gap with { Value = Scale(gap.Value) } : gap)
                .ToImmutableArray(),
        };
    }
}
