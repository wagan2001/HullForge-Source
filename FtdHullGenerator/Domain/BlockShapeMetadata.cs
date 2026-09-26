namespace FtdHullGenerator.Domain;

/// <summary>Geometric family, independent of material and installed item names.</summary>
public enum StructuralFamily
{
    Box, Pole, Slope, Triangle, InverseTriangle, BeamSlope,
    SquareCorner, SquareBackedCorner, SlopeTransition, InverseTransition,
}

/// <summary>Native footprint and mirror data shared by generation, validation and rendering.</summary>
/// <param name="Family">The geometric family; the analytic envelope is built from it by
/// <see cref="Geometry.StructuralShapeGeometry" />.</param>
/// <param name="Length">Cells occupied along local +Z. For a slope it is also the run over
/// which the part rises exactly one metre.</param>
/// <param name="Mirrored">True for the right-handed mesh of a handed family.</param>
/// <param name="ShortLength">Transition families only: the slope length the part transitions
/// from. Always smaller than <paramref name="Length" />.</param>
public readonly record struct StructuralShapeInfo(StructuralFamily Family, int Length, bool Mirrored = false, int ShortLength = 0);

/// <summary>
/// The single source of truth for every registered shape's footprint length, handedness and
/// mirror partner. Generation, validation, rendering and export all read it, so a shape's
/// footprint can never disagree between preview and export.
/// </summary>
/// <remarks>
/// Operational contract, and the one place it is narrower than From the Depths: every family
/// registered here declares SizePos.z = Length - 1 with SizeNeg = 0, so a placement reserves the
/// anchor plus the cells ahead of it along local +Z. That is a property of this subset, not of
/// the game — some native parts (the applique Plate family) extend along local -Z instead, and
/// admitting one would require the general SizeInfo rule in
/// <see cref="BlockPlacement.OccupiedCells" /> before it could be placed correctly.
/// Handedness is resolved from measured mesh geometry, never from a part name.
/// </remarks>
public static class BlockShapeMetadata
{
    public static StructuralShapeInfo Get(BlockShape shape) => shape switch
    {
        BlockShape.Cube => new(StructuralFamily.Box, 1, false, 0),
        BlockShape.Slope1 => new(StructuralFamily.Slope, 1, false, 0),
        BlockShape.Pole1 => new(StructuralFamily.Pole, 1, false, 0),
        BlockShape.CornerLeft => new(StructuralFamily.Triangle, 1, false, 0),
        BlockShape.CornerRight => new(StructuralFamily.Triangle, 1, true, 0),
        BlockShape.InverseCornerLeft => new(StructuralFamily.InverseTriangle, 1, false, 0),
        BlockShape.InverseCornerRight => new(StructuralFamily.InverseTriangle, 1, true, 0),
        BlockShape.SquareCornerLeft => new(StructuralFamily.SquareCorner, 1, false, 0),
        BlockShape.SquareCornerRight => new(StructuralFamily.SquareCorner, 1, true, 0),
        BlockShape.Beam2 => new(StructuralFamily.Box, 2, false, 0),
        BlockShape.Slope2 => new(StructuralFamily.Slope, 2, false, 0),
        BlockShape.Pole2 => new(StructuralFamily.Pole, 2, false, 0),
        BlockShape.CornerLeft2 => new(StructuralFamily.Triangle, 2, false, 0),
        BlockShape.CornerRight2 => new(StructuralFamily.Triangle, 2, true, 0),
        BlockShape.InverseCornerLeft2 => new(StructuralFamily.InverseTriangle, 2, false, 0),
        BlockShape.InverseCornerRight2 => new(StructuralFamily.InverseTriangle, 2, true, 0),
        BlockShape.SquareCornerLeft2 => new(StructuralFamily.SquareCorner, 2, false, 0),
        BlockShape.SquareCornerRight2 => new(StructuralFamily.SquareCorner, 2, true, 0),
        BlockShape.SquareBackedCornerLeft2 => new(StructuralFamily.SquareBackedCorner, 2, false, 0),
        BlockShape.SquareBackedCornerRight2 => new(StructuralFamily.SquareBackedCorner, 2, true, 0),
        BlockShape.BeamSlope2 => new(StructuralFamily.BeamSlope, 2, false, 0),
        BlockShape.BeamSlopeMirrored2 => new(StructuralFamily.BeamSlope, 2, true, 0),
        BlockShape.Beam3 => new(StructuralFamily.Box, 3, false, 0),
        BlockShape.Slope3 => new(StructuralFamily.Slope, 3, false, 0),
        BlockShape.Pole3 => new(StructuralFamily.Pole, 3, false, 0),
        BlockShape.CornerLeft3 => new(StructuralFamily.Triangle, 3, false, 0),
        BlockShape.CornerRight3 => new(StructuralFamily.Triangle, 3, true, 0),
        BlockShape.InverseCornerLeft3 => new(StructuralFamily.InverseTriangle, 3, false, 0),
        BlockShape.InverseCornerRight3 => new(StructuralFamily.InverseTriangle, 3, true, 0),
        BlockShape.SquareCornerLeft3 => new(StructuralFamily.SquareCorner, 3, false, 0),
        BlockShape.SquareCornerRight3 => new(StructuralFamily.SquareCorner, 3, true, 0),
        BlockShape.SquareBackedCornerLeft3 => new(StructuralFamily.SquareBackedCorner, 3, false, 0),
        BlockShape.SquareBackedCornerRight3 => new(StructuralFamily.SquareBackedCorner, 3, true, 0),
        BlockShape.BeamSlope3 => new(StructuralFamily.BeamSlope, 3, false, 0),
        BlockShape.BeamSlopeMirrored3 => new(StructuralFamily.BeamSlope, 3, true, 0),
        BlockShape.Beam4 => new(StructuralFamily.Box, 4, false, 0),
        BlockShape.Slope4 => new(StructuralFamily.Slope, 4, false, 0),
        BlockShape.Pole4 => new(StructuralFamily.Pole, 4, false, 0),
        BlockShape.CornerLeft4 => new(StructuralFamily.Triangle, 4, false, 0),
        BlockShape.CornerRight4 => new(StructuralFamily.Triangle, 4, true, 0),
        BlockShape.InverseCornerLeft4 => new(StructuralFamily.InverseTriangle, 4, false, 0),
        BlockShape.InverseCornerRight4 => new(StructuralFamily.InverseTriangle, 4, true, 0),
        BlockShape.SquareCornerLeft4 => new(StructuralFamily.SquareCorner, 4, false, 0),
        BlockShape.SquareCornerRight4 => new(StructuralFamily.SquareCorner, 4, true, 0),
        BlockShape.SquareBackedCornerLeft4 => new(StructuralFamily.SquareBackedCorner, 4, false, 0),
        BlockShape.SquareBackedCornerRight4 => new(StructuralFamily.SquareBackedCorner, 4, true, 0),
        BlockShape.BeamSlope4 => new(StructuralFamily.BeamSlope, 4, false, 0),
        BlockShape.BeamSlopeMirrored4 => new(StructuralFamily.BeamSlope, 4, true, 0),
        BlockShape.Corner => new(StructuralFamily.Triangle, 1, false, 0),
        BlockShape.InverseCorner => new(StructuralFamily.InverseTriangle, 1, false, 0),
        BlockShape.SlopeTransitionLeft12 => new(StructuralFamily.SlopeTransition, 2, false, 1),
        BlockShape.SlopeTransitionLeft13 => new(StructuralFamily.SlopeTransition, 3, false, 1),
        BlockShape.SlopeTransitionLeft14 => new(StructuralFamily.SlopeTransition, 4, false, 1),
        BlockShape.SlopeTransitionLeft23 => new(StructuralFamily.SlopeTransition, 3, false, 2),
        BlockShape.SlopeTransitionLeft24 => new(StructuralFamily.SlopeTransition, 4, false, 2),
        BlockShape.SlopeTransitionLeft34 => new(StructuralFamily.SlopeTransition, 4, false, 3),
        BlockShape.SlopeTransitionRight12 => new(StructuralFamily.SlopeTransition, 2, true, 1),
        BlockShape.SlopeTransitionRight13 => new(StructuralFamily.SlopeTransition, 3, true, 1),
        BlockShape.SlopeTransitionRight14 => new(StructuralFamily.SlopeTransition, 4, true, 1),
        BlockShape.SlopeTransitionRight23 => new(StructuralFamily.SlopeTransition, 3, true, 2),
        BlockShape.SlopeTransitionRight24 => new(StructuralFamily.SlopeTransition, 4, true, 2),
        BlockShape.SlopeTransitionRight34 => new(StructuralFamily.SlopeTransition, 4, true, 3),
        BlockShape.InverseTransitionLeft12 => new(StructuralFamily.InverseTransition, 2, false, 1),
        BlockShape.InverseTransitionLeft13 => new(StructuralFamily.InverseTransition, 3, false, 1),
        BlockShape.InverseTransitionLeft14 => new(StructuralFamily.InverseTransition, 4, false, 1),
        BlockShape.InverseTransitionLeft23 => new(StructuralFamily.InverseTransition, 3, false, 2),
        BlockShape.InverseTransitionLeft24 => new(StructuralFamily.InverseTransition, 4, false, 2),
        BlockShape.InverseTransitionLeft34 => new(StructuralFamily.InverseTransition, 4, false, 3),
        BlockShape.InverseTransitionRight12 => new(StructuralFamily.InverseTransition, 2, true, 1),
        BlockShape.InverseTransitionRight13 => new(StructuralFamily.InverseTransition, 3, true, 1),
        BlockShape.InverseTransitionRight14 => new(StructuralFamily.InverseTransition, 4, true, 1),
        BlockShape.InverseTransitionRight23 => new(StructuralFamily.InverseTransition, 3, true, 2),
        BlockShape.InverseTransitionRight24 => new(StructuralFamily.InverseTransition, 4, true, 2),
        BlockShape.InverseTransitionRight34 => new(StructuralFamily.InverseTransition, 4, true, 3),
        _ => throw new ArgumentOutOfRangeException(nameof(shape)),
    };

    public static int CellLength(BlockShape shape) => Get(shape).Length;

    public static BlockShape MirrorShape(BlockShape shape) => shape switch
    {
        BlockShape.CornerLeft => BlockShape.CornerRight,
        BlockShape.CornerRight => BlockShape.CornerLeft,
        BlockShape.InverseCornerLeft => BlockShape.InverseCornerRight,
        BlockShape.InverseCornerRight => BlockShape.InverseCornerLeft,
        BlockShape.SquareCornerLeft => BlockShape.SquareCornerRight,
        BlockShape.SquareCornerRight => BlockShape.SquareCornerLeft,
        BlockShape.CornerLeft2 => BlockShape.CornerRight2,
        BlockShape.CornerRight2 => BlockShape.CornerLeft2,
        BlockShape.InverseCornerLeft2 => BlockShape.InverseCornerRight2,
        BlockShape.InverseCornerRight2 => BlockShape.InverseCornerLeft2,
        BlockShape.SquareCornerLeft2 => BlockShape.SquareCornerRight2,
        BlockShape.SquareCornerRight2 => BlockShape.SquareCornerLeft2,
        BlockShape.SquareBackedCornerLeft2 => BlockShape.SquareBackedCornerRight2,
        BlockShape.SquareBackedCornerRight2 => BlockShape.SquareBackedCornerLeft2,
        BlockShape.BeamSlope2 => BlockShape.BeamSlopeMirrored2,
        BlockShape.BeamSlopeMirrored2 => BlockShape.BeamSlope2,
        BlockShape.CornerLeft3 => BlockShape.CornerRight3,
        BlockShape.CornerRight3 => BlockShape.CornerLeft3,
        BlockShape.InverseCornerLeft3 => BlockShape.InverseCornerRight3,
        BlockShape.InverseCornerRight3 => BlockShape.InverseCornerLeft3,
        BlockShape.SquareCornerLeft3 => BlockShape.SquareCornerRight3,
        BlockShape.SquareCornerRight3 => BlockShape.SquareCornerLeft3,
        BlockShape.SquareBackedCornerLeft3 => BlockShape.SquareBackedCornerRight3,
        BlockShape.SquareBackedCornerRight3 => BlockShape.SquareBackedCornerLeft3,
        BlockShape.BeamSlope3 => BlockShape.BeamSlopeMirrored3,
        BlockShape.BeamSlopeMirrored3 => BlockShape.BeamSlope3,
        BlockShape.CornerLeft4 => BlockShape.CornerRight4,
        BlockShape.CornerRight4 => BlockShape.CornerLeft4,
        BlockShape.InverseCornerLeft4 => BlockShape.InverseCornerRight4,
        BlockShape.InverseCornerRight4 => BlockShape.InverseCornerLeft4,
        BlockShape.SquareCornerLeft4 => BlockShape.SquareCornerRight4,
        BlockShape.SquareCornerRight4 => BlockShape.SquareCornerLeft4,
        BlockShape.SquareBackedCornerLeft4 => BlockShape.SquareBackedCornerRight4,
        BlockShape.SquareBackedCornerRight4 => BlockShape.SquareBackedCornerLeft4,
        BlockShape.BeamSlope4 => BlockShape.BeamSlopeMirrored4,
        BlockShape.BeamSlopeMirrored4 => BlockShape.BeamSlope4,
        BlockShape.SlopeTransitionLeft12 => BlockShape.SlopeTransitionRight12,
        BlockShape.SlopeTransitionLeft13 => BlockShape.SlopeTransitionRight13,
        BlockShape.SlopeTransitionLeft14 => BlockShape.SlopeTransitionRight14,
        BlockShape.SlopeTransitionLeft23 => BlockShape.SlopeTransitionRight23,
        BlockShape.SlopeTransitionLeft24 => BlockShape.SlopeTransitionRight24,
        BlockShape.SlopeTransitionLeft34 => BlockShape.SlopeTransitionRight34,
        BlockShape.SlopeTransitionRight12 => BlockShape.SlopeTransitionLeft12,
        BlockShape.SlopeTransitionRight13 => BlockShape.SlopeTransitionLeft13,
        BlockShape.SlopeTransitionRight14 => BlockShape.SlopeTransitionLeft14,
        BlockShape.SlopeTransitionRight23 => BlockShape.SlopeTransitionLeft23,
        BlockShape.SlopeTransitionRight24 => BlockShape.SlopeTransitionLeft24,
        BlockShape.SlopeTransitionRight34 => BlockShape.SlopeTransitionLeft34,
        BlockShape.InverseTransitionLeft12 => BlockShape.InverseTransitionRight12,
        BlockShape.InverseTransitionLeft13 => BlockShape.InverseTransitionRight13,
        BlockShape.InverseTransitionLeft14 => BlockShape.InverseTransitionRight14,
        BlockShape.InverseTransitionLeft23 => BlockShape.InverseTransitionRight23,
        BlockShape.InverseTransitionLeft24 => BlockShape.InverseTransitionRight24,
        BlockShape.InverseTransitionLeft34 => BlockShape.InverseTransitionRight34,
        BlockShape.InverseTransitionRight12 => BlockShape.InverseTransitionLeft12,
        BlockShape.InverseTransitionRight13 => BlockShape.InverseTransitionLeft13,
        BlockShape.InverseTransitionRight14 => BlockShape.InverseTransitionLeft14,
        BlockShape.InverseTransitionRight23 => BlockShape.InverseTransitionLeft23,
        BlockShape.InverseTransitionRight24 => BlockShape.InverseTransitionLeft24,
        BlockShape.InverseTransitionRight34 => BlockShape.InverseTransitionLeft34,
        // Historical aliases identify unhanded candidates, not catalog-emitted parts.
        _ => shape,
    };
}

