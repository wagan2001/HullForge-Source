using FtdHullGenerator.Domain;
using FtdHullGenerator.Geometry;

namespace FtdHullGenerator.UI.Sketchbook;

/// <summary>
/// One midship section measurement of a generated hull, taken at the station with the greatest
/// half-breadth. It is the honest product-side section evidence a lower-body-dependent identity
/// (a tumblehome or a very full section) needs: a top-only thumbnail would hide the inward
/// upper sides, so the browser renders a real bow-on station cut instead.
/// </summary>
/// <param name="StationZ">The lattice Z station the measurement and section are taken at.</param>
/// <param name="MaxHalfBreadth">The greatest half-breadth at that station, in metres.</param>
/// <param name="MaxHalfBreadthY">The lowest occupied Y that achieves <paramref name="MaxHalfBreadth" />.</param>
/// <param name="TopOccupiedY">The highest occupied Y at that station.</param>
/// <param name="TumblehomeDelta">The half-breadth lost from the widest row to the top row.</param>
/// <param name="ContourCount">How many stitched contours the analytic section produced.</param>
/// <param name="ClosedContourCount">How many of those contours are closed loops.</param>
/// <param name="HasClosedContour">Whether any contour closed.</param>
public sealed record SketchbookSectionEvidence(
    int StationZ,
    double MaxHalfBreadth,
    int MaxHalfBreadthY,
    int TopOccupiedY,
    double TumblehomeDelta,
    int ContourCount,
    int ClosedContourCount,
    bool HasClosedContour)
{
    /// <summary>
    /// True when the widest row sits below the top row and the hull loses breadth above it: the
    /// inward-upper-side signature a bow-on section inset is there to show.
    /// </summary>
    public bool IsTumblehome => MaxHalfBreadthY < TopOccupiedY && TumblehomeDelta > 0;
}

/// <summary>
/// Measures the midship section evidence from a real <see cref="GeneratedHull" />. This is the
/// runtime product path; it mirrors the committed native-audit measurement exactly, but it never
/// reads or edits the audit's own copy.
/// </summary>
public static class SketchbookSectionEvidenceReader
{
    /// <summary>
    /// Measures the widest station of a generated hull and cuts the analytic corrected surface
    /// there. The section plane is the station's own cell coordinate, matching the audit so the
    /// product and the committed fixture agree.
    /// </summary>
    public static SketchbookSectionEvidence Measure(GeneratedHull hull)
    {
        ArgumentNullException.ThrowIfNull(hull);
        var geometry = new SurfaceQualityGeometry(hull);
        var centerX = (hull.MinX + hull.MaxX) / 2.0;
        var rowsByZ = new SortedDictionary<int, List<(int Y, double HalfBreadth)>>();
        foreach (var (row, maxX) in geometry.MaxXByRow)
        {
            var minX = geometry.MinXByRow[row];
            var halfBreadth = Math.Max(maxX - centerX, centerX - minX);
            if (!rowsByZ.TryGetValue(row.Z, out var list))
            {
                list = [];
                rowsByZ[row.Z] = list;
            }

            list.Add((row.Y, halfBreadth));
        }

        var bestStation = 0;
        var bestBreadth = double.NegativeInfinity;
        foreach (var (station, rows) in rowsByZ)
        {
            var stationBreadth = rows.Max(row => row.HalfBreadth);
            if (stationBreadth > bestBreadth)
            {
                bestBreadth = stationBreadth;
                bestStation = station;
            }
        }

        if (bestBreadth == double.NegativeInfinity)
            return new SketchbookSectionEvidence(0, 0, 0, 0, 0, 0, 0, false);

        var chosen = rowsByZ[bestStation];
        var maxHalfBreadth = chosen.Max(row => row.HalfBreadth);
        var maxHalfBreadthY = chosen.Where(row => row.HalfBreadth == maxHalfBreadth).Min(row => row.Y);
        var topOccupiedY = chosen.Max(row => row.Y);
        var topHalfBreadth = chosen.Single(row => row.Y == topOccupiedY).HalfBreadth;
        var cut = new SurfaceSectionQuery(geometry).Cut(SectionAxis.Z, bestStation);
        return new SketchbookSectionEvidence(
            bestStation,
            maxHalfBreadth,
            maxHalfBreadthY,
            topOccupiedY,
            maxHalfBreadth - topHalfBreadth,
            cut.Contours.Count,
            cut.Contours.Count(contour => contour.IsClosed),
            cut.Contours.Any(contour => contour.IsClosed));
    }

    /// <summary>
    /// The station's position along the hull's own length as a 0..1 cut fraction, clamped to the
    /// preview's documented range. A zero-length hull returns zero.
    /// </summary>
    public static double CutFraction(GeneratedHull hull, SketchbookSectionEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(hull);
        ArgumentNullException.ThrowIfNull(evidence);
        var span = hull.MaxZ - hull.MinZ;
        if (span <= 0)
            return 0d;
        return Math.Clamp((evidence.StationZ - hull.MinZ) / (double)span, 0d, 1d);
    }
}
