using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Design;
using FtdHullGenerator.Geometry.Composition;

namespace FtdHullGenerator.Geometry.Layout;

/// <summary>
/// The measured arrangement ruler of one evaluated hull: the forward structural-deck face that
/// defines ruler zero and the continuous supported deck interval that defines the stern end.
/// </summary>
/// <remarks>
/// <see cref="BowDatum"/> and <see cref="SupportedEnd"/> are both in ruler coordinates; the ruler
/// runs bow to stern with <c>z = BowDatum - s</c>. <see cref="GapStation"/> is the first world-Z
/// station missing structural deck armor inside the interval, or <c>null</c> when the interval is
/// continuous.
/// </remarks>
public sealed record DeckRulerInterval(
    DesignMeasure BowDatum,
    DesignMeasure SupportedEnd,
    int BowStation,
    int SternStation,
    int? GapStation);

/// <summary>
/// The one structural-deck-interval authority. Every ruler consumer (the composition service's
/// supported-length gate and the derived <see cref="Domain.Layout.ResolvedLayoutFrame"/>) measures
/// through this method, so the interval math exists exactly once.
/// </summary>
public static class SupportedDeckRulerAuthority
{
    /// <summary>
    /// Measures the supported deck interval from a captured hull evaluation, or returns <c>null</c>
    /// when the hull has no structural deck armor at all. A cell-centred supported row owns the
    /// half-cell faces on either side, so the ruler origin is the forward face of the most-forward
    /// supported deck station, not an arbitrary hull origin or an underwater/bulb extent.
    /// </summary>
    public static DeckRulerInterval? Measure(HullBuildContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var stations = context.EnumerateArmor()
            .Where(intent => intent.IsStructuralArmor && intent.Region == ArmorRegion.Deck)
            .Select(intent => intent.Cell.Z).Distinct().Order().ToArray();
        if (stations.Length == 0)
            return null;

        var sternStation = stations[0];
        var bowStation = stations[^1];
        var firstGap = Enumerable.Range(sternStation, checked(bowStation - sternStation + 1))
            .Where(z => Array.BinarySearch(stations, z) < 0)
            .Select(z => (int?)z).FirstOrDefault();
        return new DeckRulerInterval(
            DesignMeasure.FromTwiceMetres(checked(bowStation * 2 + 1)),
            DesignMeasure.FromMetres(checked(bowStation - sternStation + 1)),
            bowStation,
            sternStation,
            firstGap);
    }
}
