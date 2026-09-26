namespace FtdHullGenerator.Domain;

/// <summary>
/// Says which stage of generation produced a placement. The exporter retains a
/// <see cref="Smoothing" /> slope's real catalog shape and rotation. Vertical and
/// horizontal placements are validated against captured in-game ground truth;
/// cross-section and combined placements are explicitly review candidates. A
/// <see cref="Shell" /> placement that is not a cube is still a fitted candidate
/// whose exact game rotation has not passed an in-game fixture, so the exporter
/// keeps flattening it to a full block.
/// </summary>
public enum BlockOrigin
{
    /// <summary>Part of the generated hull shell (cube, beam, or fitted candidate).</summary>
    Shell = 0,

    /// <summary>A fitted surface piece, added by an additive fill or installed by coordinated shell construction.</summary>
    Smoothing = 1,

    /// <summary>A cube belonging to the optional deck-mounted superstructure.</summary>
    Superstructure = 2,

    /// <summary>An additive fitted part produced by superstructure smoothing.</summary>
    SuperstructureSmoothing = 3,
}
