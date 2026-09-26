namespace FtdHullGenerator.Domain;

/// <summary>
/// The shape of the stern, in plan (seen from above) and in profile (seen from the side).
/// <see cref="Transom" /> is the original stern and reproduces it exactly; the stern taper
/// slider still warps the rate of every style's plan curve.
/// </summary>
public enum SternStyle
{
    /// <summary>A flat vertical transom at 65% of the maximum half-beam.</summary>
    Transom = 0,

    /// <summary>A wide flat vertical transom at 85% of the maximum half-beam.</summary>
    Square,

    /// <summary>A 65% transom that slopes so the deck overhangs the keel.</summary>
    Counter,

    /// <summary>A double-ended stern that narrows to a point with a rounded heel; the widest station moves to midships.</summary>
    Canoe,

    /// <summary>A narrow rounded stern with a gently lifted run.</summary>
    Cruiser,

    /// <summary>A broad rounded stern that retains most of the hull beam at its end.</summary>
    Fantail,
}
