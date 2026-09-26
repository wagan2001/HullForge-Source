namespace FtdHullGenerator.Domain;

/// <summary>
/// A signed unit step along one lattice axis.
/// </summary>
public readonly record struct AxisDirection(int X, int Y, int Z)
{
    public AxisDirection Negate() => new(-X, -Y, -Z);

    public static AxisDirection Cross(AxisDirection left, AxisDirection right) => new(
        left.Y * right.Z - left.Z * right.Y,
        left.Z * right.X - left.X * right.Z,
        left.X * right.Y - left.Y * right.X);
}

/// <summary>
/// One of From the Depths' 24 block orientations, expressed as world-space axes.
/// </summary>
public readonly record struct RotationAxes(AxisDirection Forward, AxisDirection Up)
{
    public AxisDirection Right => AxisDirection.Cross(Up, Forward);
}

/// <summary>
/// The game's rotation vocabulary. Rotation indices 0 through 23 are the values
/// accepted by the blueprint BLR array and by Blueprint.GetBLRAsQuaternions.
/// There are exactly 24 and they are the full proper-rotation group: never invent one.
/// </summary>
/// <remarks>
/// The table below defines all supported rotation indices. Changes require checking
/// native blueprint placements and mirror behavior against independent game evidence.
/// The corner resolver in HullGenerator and the multi-cell footprint expansion in
/// <see cref="BlockPlacement.OccupiedCells" /> both read this table, so it lives in
/// Domain rather than in either consumer. The handedness convention
/// <c>Right = Cross(Up, Forward)</c> is load-bearing: it is what makes the verified
/// left/right triangle-corner meshes resolve to the correct handed item.
/// </remarks>
public static class BlockRotations
{
    /// <summary>The six face-neighbour directions on the lattice.</summary>
    public static readonly AxisDirection[] AxisDirections =
    [
        new(-1, 0, 0), new(1, 0, 0),
        new(0, -1, 0), new(0, 1, 0),
        new(0, 0, -1), new(0, 0, 1),
    ];

    /// <summary>The lowest and highest valid From the Depths rotation index.</summary>
    public const int MinimumRotation = 0;

    public const int MaximumRotation = 23;

    /// <summary>Gets whether a rotation index is one the game can represent.</summary>
    public static bool IsValid(int rotation) => rotation is >= MinimumRotation and <= MaximumRotation;

    // From The Depths' Core.Quats order, represented as world forward / world up.
    public static RotationAxes GetRotationAxes(int rotation) => rotation switch
    {
        0 => new(new(0, 0, 1), new(0, 1, 0)),
        1 => new(new(1, 0, 0), new(0, 1, 0)),
        2 => new(new(0, 0, -1), new(0, 1, 0)),
        3 => new(new(-1, 0, 0), new(0, 1, 0)),
        4 => new(new(0, -1, 0), new(0, 0, 1)),
        5 => new(new(0, -1, 0), new(1, 0, 0)),
        6 => new(new(0, -1, 0), new(0, 0, -1)),
        7 => new(new(0, -1, 0), new(-1, 0, 0)),
        8 => new(new(0, 1, 0), new(0, 0, 1)),
        9 => new(new(0, 1, 0), new(1, 0, 0)),
        10 => new(new(0, 1, 0), new(0, 0, -1)),
        11 => new(new(0, 1, 0), new(-1, 0, 0)),
        12 => new(new(0, 0, 1), new(0, -1, 0)),
        13 => new(new(1, 0, 0), new(0, -1, 0)),
        14 => new(new(0, 0, -1), new(0, -1, 0)),
        15 => new(new(-1, 0, 0), new(0, -1, 0)),
        16 => new(new(0, 0, 1), new(1, 0, 0)),
        17 => new(new(0, 0, -1), new(1, 0, 0)),
        18 => new(new(0, 0, 1), new(-1, 0, 0)),
        19 => new(new(0, 0, -1), new(-1, 0, 0)),
        20 => new(new(1, 0, 0), new(0, 0, 1)),
        21 => new(new(-1, 0, 0), new(0, 0, 1)),
        22 => new(new(1, 0, 0), new(0, 0, -1)),
        23 => new(new(-1, 0, 0), new(0, 0, -1)),
        _ => throw new ArgumentOutOfRangeException(nameof(rotation), rotation, "Rotation must be between 0 and 23."),
    };
}
