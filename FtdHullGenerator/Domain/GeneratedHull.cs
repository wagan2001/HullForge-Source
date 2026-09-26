using System.Collections.Immutable;
using FtdHullGenerator.Domain.Composition;

namespace FtdHullGenerator.Domain;

/// <summary>The physical responsibility a generator retained for one occupied cell.</summary>
public enum PhysicalCellRole
{
    HullArmor = 0,
    HullExterior = 1,
    HullSmoothing = 2,
    Barbette = 3,
    InternalStructure = 4,
    ModularSuperstructure = 5,
    LegacySuperstructure = 6,
}

/// <summary>One stable owner contribution to an occupied physical cell.</summary>
public sealed record PhysicalCellOwner(string OwnerId, PhysicalCellRole Role);

/// <summary>
/// Cell-level ownership survives native run packing: one beam may cross several ownership
/// regions, but each expanded metre retains its material and complete contributing owner set.
/// </summary>
public sealed record PhysicalCellProvenance(
    HullCell Cell,
    MaterialKind Material,
    ImmutableArray<PhysicalCellOwner> Owners);

/// <summary>
/// Classifies one lattice cell against the original analytic hull evaluation that produced a
/// hull. It reports the same roles the hull-context <c>RoleAt</c> query does, captured before
/// beam packing and smoothing, so a consumer can tell genuine exterior space from the hollow
/// cavity or reserved armor air without rebuilding the hull from its final placement list.
/// </summary>
/// <param name="x">Lattice X.</param>
/// <param name="y">Lattice Y.</param>
/// <param name="z">Lattice Z.</param>
public delegate HullCellRole HullCellRoleClassifier(int x, int y, int z);

public sealed record GeneratedHull(
    HullParameters Parameters,
    IReadOnlyList<BlockPlacement> Blocks,
    int MinX,
    int MaxX,
    int MinY,
    int MaxY,
    int MinZ,
    int MaxZ)
{
    /// <summary>Non-fatal construction limitations or restored regions for this result.</summary>
    public IReadOnlyList<string> ConstructionNotes { get; init; } = [];

    /// <summary>
    /// The original analytic cell-role classification captured with this hull, when it came from a
    /// full evaluation. It is deliberately not derived from <see cref="Blocks" />. A manually
    /// constructed placement list leaves it null.
    /// </summary>
    public HullCellRoleClassifier? RoleClassifier { get; init; }

    /// <summary>The immutable editor revision this resolved physical output belongs to.</summary>
    public long? SourceRevision { get; init; }

    /// <summary>The stable project identifier this resolved physical output belongs to.</summary>
    public string? SourceDocumentId { get; init; }

    /// <summary>The installed-game catalog version used to resolve the final placement list.</summary>
    public string? ResolvedCatalogVersion { get; init; }

    /// <summary>Deterministic identity of the exact installed catalog snapshot used for resolution.</summary>
    public string? ResolvedCatalogFingerprint { get; init; }

    /// <summary>How many requested native placements were conservatively resolved to cubes.</summary>
    public int CatalogFallbackCount { get; init; }

    /// <summary>Stable expanded-cell ownership for inspection, statistics and later composition.</summary>
    public IReadOnlyList<PhysicalCellProvenance> CellProvenance { get; init; } = [];

    /// <summary>Gets the number of placed blocks. Beams count once regardless of length.</summary>
    public int BlockCount => Blocks.Count;

    /// <summary>
    /// Gets the number of lattice cells the hull fills. This is computed rather than
    /// stored: a record's copy constructor would carry a stale initialized value
    /// across any <c>with</c> expression that replaced <see cref="Blocks" />.
    /// </summary>
    public int OccupiedCellCount => Blocks.Sum(block => block.CellLength);

    /// <summary>Gets the number of lattice cells filled by internal armor, the layers behind the exposed shell.</summary>
    public int InternalArmorCellCount => Blocks.Where(block => block.IsInternalArmor).Sum(block => block.CellLength);

    public int OccupiedLength => MaxZ - MinZ + 1;
    public int OccupiedWidth => MaxX - MinX + 1;
    public int OccupiedHeight => MaxY - MinY + 1;
}
