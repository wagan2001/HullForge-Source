namespace FtdHullGenerator.UI;

/// <summary>
/// The rungs a stepped numeric row may land on. A ladder starts at the row's own minimum
/// and counts upward by its step, which is what makes odd-only widths fall out of an
/// ordinary row: start at one, step by two, and every rung is odd without the row knowing
/// anything about parity. Kept separate from the control so the arithmetic can be tested
/// without standing up a window.
/// </summary>
internal static class StepLadder
{
    /// <summary>
    /// Snaps a value onto the nearest rung, then holds it inside the travel. The ceiling
    /// is approached from below: rounding to the nearest rung can otherwise carry the top
    /// of the travel past the maximum, which is how a width of 100 would become 101 on an
    /// odd ladder.
    /// </summary>
    public static int Snap(int value, int origin, int step, int maximum)
    {
        if (step <= 1)
            return Math.Clamp(value, origin, Math.Max(origin, maximum));

        var snapped = Nearest(value, origin, step);
        var ceiling = Below(maximum, origin, step);
        if (ceiling < origin)
            return origin;
        return Math.Clamp(snapped, origin, ceiling);
    }

    /// <summary>The rung nearest a value, ignoring the travel.</summary>
    public static int Nearest(int value, int origin, int step)
    {
        if (step <= 1)
            return value;
        var offset = (int)Math.Round((value - origin) / (double)step, MidpointRounding.AwayFromZero);
        return origin + offset * step;
    }

    /// <summary>The highest rung at or below a value. Used for the top of a travel.</summary>
    public static int Below(int value, int origin, int step)
    {
        if (step <= 1)
            return value;
        return origin + (int)Math.Floor((value - origin) / (double)step) * step;
    }
}
