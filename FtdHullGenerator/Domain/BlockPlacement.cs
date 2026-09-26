namespace FtdHullGenerator.Domain;

public readonly record struct BlockPlacement(
    BlockShape Shape,
    MaterialKind Material,
    int X,
    int Y,
    int Z,
    int Rotation)
{
    /// <summary>Gets the anchor cell. Multi-cell blocks extend from here along their local forward axis.</summary>
    public (int X, int Y, int Z) Position => (X, Y, Z);

    /// <summary>Gets which generation stage produced this placement. Defaults to <see cref="BlockOrigin.Shell" />.</summary>
    public BlockOrigin Origin { get; init; }

    /// <summary>
    /// Gets how many metres inward from the exposed surface this placement sits. Zero is
    /// the exposed hull or deck layer; larger values are internal armor. A merged beam
    /// that spans both takes the shallowest depth of its cells, so any part with an
    /// exposed cell counts as shell. The preview hides internal armor unless asked to
    /// draw it; the exporter ignores the depth entirely.
    /// </summary>
    public int ArmorDepth { get; init; }

    /// <summary>Gets whether this placement is internal armor rather than part of the exposed shell.</summary>
    public bool IsInternalArmor => ArmorDepth > 0;

    /// <summary>
    /// Marks a one-metre source cell for conversion to the material's pole family.
    /// It remains metadata in debug mode, where every layer is deliberately kept as cubes.
    /// </summary>
    public bool UsePoles { get; init; }

    /// <summary>The requested per-layer construction mode for this source armor cell.</summary>
    public ArmorConstruction Construction { get; init; }

    /// <summary>The surface region that owns this armor cell, used to orient patterned members.</summary>
    public ArmorRegion ArmorRegion { get; init; }

    /// <summary>
    /// Gets how many lattice cells this placement occupies. Registered native structural
    /// families declare SizePos.z = length - 1 and SizeNeg = 0, so they reserve the anchor
    /// plus the cells ahead of it along local +Z. The reservation is distinct from the
    /// sloped physical envelope within those cells.
    /// This is narrower than the game's rule on purpose: in From the Depths a footprint is
    /// anchor + R(BLR) * v over the whole box [-SizeNeg, +SizePos], and some native parts
    /// extend along a local negative axis instead.
    /// </summary>
    public int CellLength => BlockShapeMetadata.CellLength(Shape);

    /// <summary>
    /// Gets whether this placement reaches the blueprint as its own shape and rotation
    /// rather than being flattened to a full block. Beams, poles, and fitted smoothing
    /// assemblies retain their catalog shape; a fitted shell candidate is still exported
    /// as a cube. This expresses export intent, not in-game acceptance of a complete
    /// layout—the cross-section and combined methods remain review candidates. The
    /// preview reads this too, so what is drawn is what the exporter will write.
    /// </summary>
    /// <remarks>
    /// Catalog availability is checked at export. Coordinated construction requires its
    /// exact pieces and rejects missing parts; the older modes may use cube fallbacks.
    /// </remarks>
    public bool KeepsFittedShape =>
        Shape is BlockShape.Pole1 or BlockShape.Pole2 or BlockShape.Pole3 or BlockShape.Pole4 ||
        CellLength > 1 || (Origin is BlockOrigin.Smoothing or BlockOrigin.SuperstructureSmoothing && Shape != BlockShape.Cube);

    /// <summary>
    /// Enumerates every lattice cell this placement fills, starting at the anchor
    /// and stepping along the rotation's forward axis (local +Z). For beams and poles that is
    /// world +Z at rotation 0; vertical-fill slopes can run along +/-Z or -Y,
    /// while cross-section slopes run along +/-Y. Single-cell shapes never consult
    /// the rotation table, so an out-of-range rotation stays reportable by the
    /// validator instead of throwing here.
    /// </summary>
    public IEnumerable<(int X, int Y, int Z)> OccupiedCells
    {
        get
        {
            var length = CellLength;
            if (length == 1)
            {
                yield return Position;
                yield break;
            }

            var forward = BlockRotations.GetRotationAxes(Rotation).Forward;
            for (var step = 0; step < length; step++)
                yield return (X + forward.X * step, Y + forward.Y * step, Z + forward.Z * step);
        }
    }
}

public enum ArmorRegion
{
    Side,
    Bottom,
    Deck,
}
