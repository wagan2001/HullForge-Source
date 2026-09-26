namespace FtdHullGenerator.Domain.Composition;

/// <summary>
/// What the evaluated hull and armor stage intends a lattice cell to be. This is the
/// pre-materialization vocabulary of contract C05: a feature reads intent and cavity,
/// it does not re-derive the hull or mutate the generator's private solid.
/// </summary>
public enum HullCellRole
{
    /// <summary>The cell is outside the analytic hull solid.</summary>
    Outside,

    /// <summary>The cell is inside the solid and belongs to neither armor nor a deck opening.</summary>
    Cavity,

    /// <summary>An exposed side-armor cell, including the deck rim the hull layout owns.</summary>
    SideArmor,

    /// <summary>An exposed keel/bottom-armor cell.</summary>
    BottomArmor,

    /// <summary>An exposed deck-armor cell, the only family a deck aperture may open.</summary>
    DeckArmor,

    /// <summary>A structural armor cell behind the exposed surface.</summary>
    InternalArmor,

    /// <summary>A deliberately empty one-metre armor layer, distinct from usable cavity.</summary>
    ReservedArmorAir,
}

/// <summary>An integer lattice cell coordinate.</summary>
public readonly record struct HullCell(int X, int Y, int Z);

/// <summary>
/// One armor-stage cell and its ownership. <see cref="Material"/> is null exactly when the
/// cell is a deliberate air reservation; <see cref="Region"/> is the surface family that
/// owns the cell and <see cref="Depth"/> is how many metres inward it sits.
/// </summary>
public sealed record HullCellIntent(
    HullCell Cell,
    HullCellRole Role,
    MaterialKind? Material,
    ArmorRegion? Region,
    int Depth,
    ArmorConstruction Construction)
{
    /// <summary>True when the cell carries a real structural armor material.</summary>
    public bool IsStructuralArmor =>
        Role is HullCellRole.SideArmor or HullCellRole.BottomArmor or
            HullCellRole.DeckArmor or HullCellRole.InternalArmor;

    /// <summary>True when the layer deliberately reserves empty air rather than usable cavity.</summary>
    public bool IsReservedAir => Role == HullCellRole.ReservedArmorAir;
}

/// <summary>
/// An explicit, immutable set of deck cells a feature is authorized to open. The mask is
/// deliberately narrow: a named cell is only actionable when the cell is also deck armor,
/// so this is not a general-purpose "cut anything" switch.
/// </summary>
public sealed class DeckOpeningMask
{
    private readonly HashSet<HullCell> _cells;

    private DeckOpeningMask(HashSet<HullCell> cells)
    {
        _cells = cells;
    }

    /// <summary>The empty mask. It authorizes no cell at all.</summary>
    public static DeckOpeningMask None { get; } = new([]);

    /// <summary>Gets how many cells the mask names.</summary>
    public int Count => _cells.Count;

    /// <summary>
    /// Copies the named cells into an immutable mask. The caller's sequence may be
    /// mutated afterward without changing this mask.
    /// </summary>
    public static DeckOpeningMask FromCells(IEnumerable<HullCell> cells)
    {
        ArgumentNullException.ThrowIfNull(cells);
        var set = new HashSet<HullCell>();
        foreach (var cell in cells)
            set.Add(cell);
        return set.Count == 0 ? None : new DeckOpeningMask(set);
    }

    /// <summary>True when this mask names the cell. Naming alone is not authorization.</summary>
    public bool Allows(HullCell cell) => _cells.Contains(cell);
}
