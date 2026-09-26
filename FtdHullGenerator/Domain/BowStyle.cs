namespace FtdHullGenerator.Domain;

/// <summary>
/// The shape of the bow, in plan (seen from above) and in profile (seen from the side).
/// <see cref="Pointed" /> is the original bow and reproduces it exactly; the bow taper
/// slider still warps the rate of every style's plan curve. Any style can additionally
/// carry a bulb, switched on by <see cref="HullParameters.HasBulb" /> and proportioned
/// by <see cref="BulbSettings" />.
/// </summary>
public enum BowStyle
{
    /// <summary>A stem that narrows to a point with a vertical leading edge.</summary>
    Pointed = 0,

    /// <summary>A pointed stem raked forward: the deck reaches the bow first and the keel stops short beneath it.</summary>
    Raked,

    /// <summary>A rounded stem in plan with a gently cut-away forefoot.</summary>
    Spoon,

    /// <summary>A barge-like bow that ends in a flat vertical face rather than a point.</summary>
    Blunt,

    /// <summary>A fine reverse stem whose lower prow reaches farther forward than its deck.</summary>
    Axe,

    /// <summary>A pointed bow with a long, curved forefoot and forward deck overhang.</summary>
    Clipper,
}
