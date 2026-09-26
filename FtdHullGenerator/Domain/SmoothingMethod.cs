namespace FtdHullGenerator.Domain;

/// <summary>
/// A hull-smoothing technique. Additive fills follow shell generation and beam
/// merging; inverted construction plans its contour before armor generation and
/// installs fitted assemblies before the remaining cubes are merged.
/// </summary>
public enum SmoothingMethod
{
    /// <summary>No smoothing. The exported hull is the raw shell/beam lattice.</summary>
    None = 0,

    /// <summary>
    /// Bevels re-entrant steps in the port/starboard side edges where the edge
    /// height changes along the hull. Longitudinal treads use 12/14 and vertical
    /// risers use 4/6, mirrored port and starboard.
    /// </summary>
    VerticalSlopeFill = 1,

    /// <summary>Bevels beam-width steps with 1–4m slopes at rotations 16–19 and 22/23.</summary>
    HorizontalSlopeFill = 2,

    /// <summary>
    /// Bevels vertical gaps between adjacent side columns with 2–4m slopes at
    /// rotations 5/7 and 9/11. Kevin's pre-built hull collection supplies the
    /// orientation vocabulary; the additive contour rule remains experimental.
    /// </summary>
    CrossSectionSlopeFill = 3,

    /// <summary>
    /// Applies the vertical, horizontal, and cross-section candidate rules to the
    /// same base hull, resolving overlaps as complete mirrored pairs.
    /// </summary>
    CombinedSlopeFill = 4,

    /// <summary>
    /// Uses vertical fill below a configurable top band and horizontal fill
    /// throughout that band, with horizontal candidates winning conflicts.
    /// </summary>
    HybridSlopeFill = 5,

    /// <summary>
    /// Coordinates horizontal slopes and triangle/inverted assemblies, allowing
    /// bounded local contour changes while preserving the selected cross-section.
    /// </summary>
    InvertedTriangleFill = 6,

    /// <summary>
    /// The normal VerticalSlopeFill hull plus a parameter-free automatic visual
    /// extension of each eligible native 4 m vertical smoothing anchor. Native
    /// placements, occupancy, collision, armor and connectivity are unchanged.
    /// Appended after 6 so every existing serialized value keeps its meaning.
    /// </summary>
    DecoVertical = 7,

    /// <summary>
    /// The normal HorizontalSlopeFill hull plus a parameter-free automatic visual
    /// extension of each eligible native 4 m horizontal smoothing anchor. Native
    /// placements, occupancy, collision, armor and connectivity are unchanged.
    /// Appended after 7 so every existing serialized value keeps its meaning.
    /// </summary>
    DecoHorizontal = 8,
}
