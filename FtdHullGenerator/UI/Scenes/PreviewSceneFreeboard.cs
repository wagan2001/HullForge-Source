namespace FtdHullGenerator.UI.Scenes;

/// <summary>
/// Presentation-only bridge for the Ocean freeboard control: the authoritative ship-wide
/// reference deck elevation plus the hull's lowest occupied cell.
/// </summary>
/// <remarks>
/// <paramref name="ReferenceDeckElevationMetres" /> mirrors <c>HullBuildContext.ReferenceDeckY</c>,
/// which <c>HullGenerator.CreateContext</c> sets to <c>HullParameters.Height - 1</c>: the
/// nominal deck-skin cell anchor, independent of local bow/stern deck rise. It is deliberately
/// <em>not</em> <c>GeneratedHull.MaxY</c> or <c>HullParameters.OverallHeight</c>, which include
/// deck rise, bulbs and superstructure and are not the product deck datum.
/// <paramref name="HullMinY" /> is used only to bound slider travel; it never becomes a datum.
/// </remarks>
internal readonly record struct PreviewSceneDeckDatum(int ReferenceDeckElevationMetres, int HullMinY);

/// <summary>
/// Converts between the persisted visual waterline and the derived freeboard, and bounds the
/// Ocean freeboard control. Freeboard is presentation only: it never reinterprets the stored
/// <see cref="PreviewSceneSettings.Waterline" /> meaning and never enters generation, physical
/// bounds, statistics or export.
/// </summary>
/// <remarks>
/// Coordinate convention: in the preview world frame a lattice cell anchor <c>y</c> is the
/// plane at world elevation <c>y</c> metres. With
/// <c>referenceDeckElevation = ReferenceDeckY = HullParameters.Height - 1</c>:
/// <code>
/// freeboard = referenceDeckElevation - waterline
/// waterline = referenceDeckElevation - freeboard
/// </code>
/// so a larger freeboard moves the visual water surface down. The persisted/internal datum
/// stays <see cref="PreviewSceneSettings.Waterline" />; freeboard is a derived UI view and is
/// never serialized.
/// </remarks>
internal static class PreviewSceneFreeboard
{
    /// <summary>The freeboard control's snap step, in metres.</summary>
    public const double StepMetres = 0.5;

    /// <summary>How far below the hull's lowest occupied cell the control may travel, in metres.</summary>
    public const double MarginMetres = 5.0;

    /// <summary>True only for the scene whose visual waterline the freeboard control edits.</summary>
    public static bool IsAvailable(PreviewSceneKind kind) => kind == PreviewSceneKind.Ocean;

    /// <summary>Derives freeboard in metres from the reference deck elevation and visual waterline.</summary>
    public static double FromWaterline(double referenceDeckElevationMetres, double waterline) =>
        referenceDeckElevationMetres - waterline;

    /// <summary>Derives the visual waterline in metres from the reference deck elevation and freeboard.</summary>
    public static double ToWaterline(double referenceDeckElevationMetres, double freeboard) =>
        referenceDeckElevationMetres - freeboard;

    /// <summary>
    /// The inclusive slider travel for one hull. The base range runs from a zero freeboard
    /// (water at the reference deck) down past the hull's lowest occupied cell; a current value
    /// outside it expands the range outward in whole <see cref="StepMetres" /> multiples so a
    /// legacy persisted waterline is always representable. The range is always at least one
    /// step wide.
    /// </summary>
    public static (double Min, double Max) Range(PreviewSceneDeckDatum datum, double currentFreeboard)
    {
        var min = 0d;
        var max = datum.ReferenceDeckElevationMetres - datum.HullMinY + MarginMetres;
        if (double.IsFinite(currentFreeboard))
        {
            if (currentFreeboard < min)
                min = Math.Floor(currentFreeboard / StepMetres) * StepMetres;
            if (currentFreeboard > max)
                max = Math.Ceiling(currentFreeboard / StepMetres) * StepMetres;
        }

        if (max < min + StepMetres)
            max = min + StepMetres;
        return (min, max);
    }

    /// <summary>Rounds a freeboard to the nearest whole <see cref="StepMetres" />.</summary>
    public static double Snap(double freeboard) =>
        Math.Round(freeboard / StepMetres, MidpointRounding.AwayFromZero) * StepMetres;
}
