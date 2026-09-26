namespace FtdHullGenerator.Domain;

/// <summary>
/// The structural parts Hull Forge can place. This is deliberately a <em>subset</em> of what
/// From the Depths offers: the game's own families also include offsets, wedges, wedge
/// front/back, applique plates and mimics, none of which are modelled here.
/// </summary>
/// <remarks>
/// Three different things are easy to confuse, and must not be:
/// this enum is Hull Forge's supported vocabulary; the game has a wider part set;
/// <see cref="Infrastructure.FtdBlockCatalog" /> resolves what the installed game provides.
/// Widening this vocabulary requires independent native placement evidence.
/// Declaration order is the serialized contract: append new families, never renumber.
/// Footprint, handedness and mirror partner for each value live in
/// <see cref="BlockShapeMetadata" />, which is the single source of truth for all three.
/// </remarks>
public enum BlockShape
{
    Cube,
    Slope1,
    Slope2,
    Slope3,
    Slope4,
    /// <summary>One-metre triangle corner with the game's left-handed mesh.</summary>
    CornerLeft,
    CornerLeft2,
    CornerLeft3,
    CornerLeft4,
    /// <summary>One-metre triangle corner with the game's right-handed mesh.</summary>
    CornerRight,
    CornerRight2,
    CornerRight3,
    CornerRight4,
    /// <summary>One-metre inverted triangle corner with the game's left-handed mesh.</summary>
    InverseCornerLeft,
    InverseCornerLeft2,
    InverseCornerLeft3,
    InverseCornerLeft4,
    /// <summary>One-metre inverted triangle corner with the game's right-handed mesh.</summary>
    InverseCornerRight,
    InverseCornerRight2,
    InverseCornerRight3,
    InverseCornerRight4,
    Corner,
    InverseCorner,
    /// <summary>Two-metre beam. Occupies two cells along its local +Z.</summary>
    Beam2,
    /// <summary>Three-metre beam. Occupies three cells along its local +Z.</summary>
    Beam3,
    /// <summary>Four-metre beam. Occupies four cells along its local +Z.</summary>
    Beam4,
    /// <summary>One-metre rounded pole.</summary>
    Pole1,
    /// <summary>Two-metre rounded pole. Occupies two cells along its local +Z.</summary>
    Pole2,
    /// <summary>Three-metre rounded pole. Occupies three cells along its local +Z.</summary>
    Pole3,
    /// <summary>Four-metre rounded pole. Occupies four cells along its local +Z.</summary>
    Pole4,
    // Appended native structural families; existing serialized enum values stay stable.
    BeamSlope2,
    BeamSlope3,
    BeamSlope4,
    BeamSlopeMirrored2,
    BeamSlopeMirrored3,
    BeamSlopeMirrored4,
    SquareCornerLeft,
    SquareCornerLeft2,
    SquareCornerLeft3,
    SquareCornerLeft4,
    SquareCornerRight,
    SquareCornerRight2,
    SquareCornerRight3,
    SquareCornerRight4,
    SquareBackedCornerLeft2,
    SquareBackedCornerLeft3,
    SquareBackedCornerLeft4,
    SquareBackedCornerRight2,
    SquareBackedCornerRight3,
    SquareBackedCornerRight4,
    SlopeTransitionLeft12,
    SlopeTransitionLeft13,
    SlopeTransitionLeft14,
    SlopeTransitionLeft23,
    SlopeTransitionLeft24,
    SlopeTransitionLeft34,
    SlopeTransitionRight12,
    SlopeTransitionRight13,
    SlopeTransitionRight14,
    SlopeTransitionRight23,
    SlopeTransitionRight24,
    SlopeTransitionRight34,
    InverseTransitionLeft12,
    InverseTransitionLeft13,
    InverseTransitionLeft14,
    InverseTransitionLeft23,
    InverseTransitionLeft24,
    InverseTransitionLeft34,
    InverseTransitionRight12,
    InverseTransitionRight13,
    InverseTransitionRight14,
    InverseTransitionRight23,
    InverseTransitionRight24,
    InverseTransitionRight34,
}
