using FtdHullGenerator.Domain;

namespace FtdHullGenerator.Geometry;

/// <summary>
/// Evaluates the transverse half-breadth of a Shape V2 control section. Inputs use
/// the UI's control vocabulary: positive fullness broadens the bottom, positive
/// side shape adds flare, positive chine makes the turn of the bilge harder, and
/// flat bottom holds the lowest rows at full beam for a horizontal floor. The
/// control bounds are <see cref="HullShapeSettings.MinimumControl" /> and
/// <see cref="HullShapeSettings.MaximumControl" />; the normalizations below stay
/// anchored to the original 0.9 calibration, so the former -0.9..0.9 range is
/// evaluated exactly as before and the widened travel continues the same curves.
/// </summary>
internal static class RegionalSectionEvaluator
{
    public static double Evaluate(
        double verticalFraction,
        double fullness,
        double sideShape,
        double chine,
        double flatBottom = 0)
    {
        var u = Math.Clamp(verticalFraction, 0, 1);
        var normalizedFullness = Math.Clamp(
            fullness, HullShapeSettings.MinimumControl, HullShapeSettings.MaximumControl);
        var normalizedSide = Math.Clamp(
            sideShape, HullShapeSettings.MinimumControl, HullShapeSettings.MaximumControl);
        var normalizedChine = Math.Clamp(
            chine, HullShapeSettings.MinimumControl, HullShapeSettings.MaximumControl);

        // Tumblehome lowers the maximum-beam shoulder; neutral and flared sections
        // continue widening all the way to the deck.
        var shoulder = MaximumBeamHeight(normalizedSide);
        var lowerU = Math.Clamp(u / shoulder, 0, 1);

        // Fullness changes the lower-body exponent. The soft curve is rounded; the
        // hard curve reaches a distinct chine and then holds its beam vertically.
        var exponent = Math.Pow(2.4, -normalizedFullness);
        var soft = Math.Pow(Math.Sin(Math.PI * lowerU / 2), exponent);
        var fullness01 = (normalizedFullness + 0.9) / 1.8;
        var chineHeight = Lerp(0.68, 0.28, fullness01);
        var hard = Math.Min(1, lowerU / chineHeight);
        var hardness = (normalizedChine + 0.9) / 1.8;
        var factor = Lerp(soft, hard, hardness);

        if (normalizedSide > 0)
        {
            // Flare keeps the deck at maximum beam while pulling the upper side
            // inward below it. The endpoints remain pinned at keel and deck.
            var flare = normalizedSide / 0.9;
            factor *= 1 - 0.25 * flare * (1 - SmoothStep(u));
        }
        else if (normalizedSide < 0 && u > shoulder)
        {
            // Tumblehome reaches maximum beam at the shoulder and draws the deck in.
            var tumblehome = -normalizedSide / 0.9;
            var upper = SmoothStep((u - shoulder) / (1 - shoulder));
            factor = Lerp(1, 1 - 0.28 * tumblehome, upper);
        }

        // The flat-bottom extension holds every row at or below the maximum-beam
        // shoulder at full beam. This is what turns the point-keel lower curve into a
        // horizontal floor whose outer edge is the side wall, so the floor meets the
        // side directly instead of through a taper. Above the shoulder the base curve
        // still applies, so tumblehome (and any shoulder draw-in) survives.
        var normalizedFlat = Math.Clamp(flatBottom, 0, 1);
        if (normalizedFlat > 0 && u <= shoulder)
            factor = Lerp(factor, 1, normalizedFlat);

        return Math.Clamp(factor, 0, 1);
    }

    public static double MaximumBeamHeight(double sideShape)
    {
        var tumblehome = Math.Max(0, -Math.Clamp(
            sideShape, HullShapeSettings.MinimumControl, HullShapeSettings.MaximumControl)) / 0.9;
        return 1 - 0.32 * tumblehome;
    }

    /// <summary>Normalized height of the hard component's chine in the same section.</summary>
    public static double ChineHeight(double fullness, double sideShape) =>
        Lerp(0.68, 0.28, (Math.Clamp(
            fullness, HullShapeSettings.MinimumControl, HullShapeSettings.MaximumControl) + 0.9) / 1.8)
        * MaximumBeamHeight(sideShape);

    private static double Lerp(double from, double to, double amount) => from + (to - from) * amount;

    private static double SmoothStep(double value)
    {
        var t = Math.Clamp(value, 0, 1);
        return t * t * (3 - 2 * t);
    }
}
