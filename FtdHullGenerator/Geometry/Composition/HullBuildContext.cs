using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Composition;
using FtdHullGenerator.Domain.Design;

namespace FtdHullGenerator.Geometry.Composition;

/// <summary>
/// Stable diagnostic codes for the read-only hull context. Contract C10 makes a code part
/// of the contract, so these are added to, never repurposed.
/// </summary>
public static class HullContextDiagnosticCodes
{
    /// <summary>An armor layer the real generation could not fit inside the hull profile.</summary>
    public const string ArmorLayerDoesNotFit = "HUL001";

    /// <summary>A shell/cavity topology defect the real generation would refuse.</summary>
    public const string TopologyInvalid = "HUL002";
}

/// <summary>
/// A read-only view of the hull solid and armor intent that the real generator evaluated.
/// Contract C05: it exists so composition features can query usable cavity, reserved armor
/// air, local deck/bottom surfaces, protected shell cells and symmetry without depending on
/// the private voxel solid or rebuilding an approximate hull.
/// </summary>
/// <remarks>
/// The instance is constructed only by <c>HullGenerator.CreateContext</c>. It holds the
/// occupancy, deck-interior and deck/floor queries as delegates that capture the generator's
/// private solid, so no voxel-solid type or cell array crosses this boundary and no second
/// copy of the data is kept here.
/// </remarks>
public sealed class HullBuildContext
{
    private readonly Func<int, int, int, bool> _isOccupied;
    private readonly Func<int, int, int, bool> _isDeckInterior;
    private readonly Func<int, int> _deckYAt;
    private readonly Func<int, int> _floorYAt;
    private readonly IReadOnlyDictionary<HullCell, HullCellIntent> _armor;
    private readonly IReadOnlySet<HullCell> _protectedShell;
    private readonly IReadOnlyList<HullCellIntent> _armorOrdered;
    private readonly IReadOnlyList<DesignDiagnostic> _diagnostics;
    private readonly Func<CancellationToken, GeneratedHull> _composeFeatureFree;

    internal HullBuildContext(
        int minX,
        int maxX,
        int minY,
        int maxY,
        int minZ,
        int maxZ,
        int referenceDeckY,
        Func<int, int, int, bool> isOccupied,
        Func<int, int, int, bool> isDeckInterior,
        Func<int, int> deckYAt,
        Func<int, int> floorYAt,
        IReadOnlyDictionary<HullCell, HullCellIntent> armor,
        IReadOnlySet<HullCell> protectedShell,
        DeckOpeningMask deckOpenings,
        IReadOnlyList<string> armorErrors,
        IReadOnlyList<string> topologyErrors,
        Func<CancellationToken, GeneratedHull> composeFeatureFree)
    {
        MinX = minX;
        MaxX = maxX;
        MinY = minY;
        MaxY = maxY;
        MinZ = minZ;
        MaxZ = maxZ;
        ReferenceDeckY = referenceDeckY;
        _isOccupied = isOccupied;
        _isDeckInterior = isDeckInterior;
        _deckYAt = deckYAt;
        _floorYAt = floorYAt;
        _armor = armor;
        _protectedShell = protectedShell;
        DeckOpenings = deckOpenings;
        ArgumentNullException.ThrowIfNull(composeFeatureFree);
        _composeFeatureFree = composeFeatureFree;

        Lattice = new CenterlineLattice(minX, maxX);
        CenterPlaneX = Lattice.CenterPlane;
        _armorOrdered = Array.AsReadOnly(armor.Values
            .OrderBy(intent => intent.Cell.Z)
            .ThenBy(intent => intent.Cell.Y)
            .ThenBy(intent => intent.Cell.X)
            .ToArray());

        var diagnostics = new List<DesignDiagnostic>(armorErrors.Count + topologyErrors.Count);
        foreach (var error in armorErrors)
            diagnostics.Add(DesignDiagnostic.Error(HullContextDiagnosticCodes.ArmorLayerDoesNotFit, error));
        foreach (var error in topologyErrors)
            diagnostics.Add(DesignDiagnostic.Error(HullContextDiagnosticCodes.TopologyInvalid, error));
        _diagnostics = Array.AsReadOnly(diagnostics.ToArray());
    }

    public int MinX { get; }
    public int MaxX { get; }
    public int MinY { get; }
    public int MaxY { get; }
    public int MinZ { get; }
    public int MaxZ { get; }

    /// <summary>
    /// The ship-wide reference deck plane as a nominal deck-surface cell, independent of local
    /// bow/stern deck rise. Barbette vertical placement is measured below this plane, never from the
    /// local deck elevation.
    /// </summary>
    public int ReferenceDeckY { get; }

    /// <summary>Twice the actual centre plane, equal to <c>MinX + MaxX</c>.</summary>
    public int MirrorSum => Lattice.MirrorSum;

    /// <summary>The hull's real lateral lattice, including odd/even centre parity.</summary>
    public CenterlineLattice Lattice { get; }

    /// <summary>The true mirror plane. Half-integral on an even-width hull.</summary>
    public DesignMeasure CenterPlaneX { get; }

    /// <summary>Gets the number of armor intents, including deliberate air reservations.</summary>
    public int ArmorCellCount => _armor.Count;

    /// <summary>Gets the number of armor intents that carry a real structural material.</summary>
    public int StructuralArmorCellCount => _armorOrdered.Count(intent => intent.IsStructuralArmor);

    /// <summary>Gets the number of armor intents that deliberately reserve empty air.</summary>
    public int ReservedAirCellCount => _armorOrdered.Count(intent => intent.IsReservedAir);

    /// <summary>The mask the caller supplied for authorized deck apertures.</summary>
    public DeckOpeningMask DeckOpenings { get; }

    /// <summary>Armor and topology findings the real generation would report.</summary>
    public IReadOnlyList<DesignDiagnostic> Diagnostics => _diagnostics;

    /// <summary>True when the analytic solid exactly as generated occupies the cell.</summary>
    public bool IsOccupied(int x, int y, int z) => _isOccupied(x, y, z);

    /// <summary>Gets the armor intent for a cell, or false when it holds no armor intent.</summary>
    public bool TryGetArmor(int x, int y, int z, out HullCellIntent intent)
    {
        if (_armor.TryGetValue(new HullCell(x, y, z), out var found))
        {
            intent = found;
            return true;
        }

        intent = null!;
        return false;
    }

    /// <summary>True when the armor layer deliberately reserves empty air rather than cavity.</summary>
    public bool IsReservedArmorAir(int x, int y, int z) =>
        _armor.TryGetValue(new HullCell(x, y, z), out var intent) && intent.IsReservedAir;

    /// <summary>
    /// True for an exposed side, bottom or rim armor cell a feature cut must never remove.
    /// The deck is deliberately excluded: a deck aperture is what the mask authorizes.
    /// </summary>
    public bool IsProtectedShellCell(int x, int y, int z) =>
        _protectedShell.Contains(new HullCell(x, y, z));

    /// <summary>Classifies a cell against the evaluated solid and armor intent.</summary>
    public HullCellRole RoleAt(int x, int y, int z)
    {
        if (!_isOccupied(x, y, z))
            return HullCellRole.Outside;
        if (_armor.TryGetValue(new HullCell(x, y, z), out var intent))
            return intent.Role;
        // A deckless hull's opened top-interior row is solid but not usable interior.
        if (_isDeckInterior(x, y, z))
            return HullCellRole.Outside;
        return HullCellRole.Cavity;
    }

    /// <summary>
    /// True when the cell is occupied by the solid, holds no armor or reserved air, and is
    /// not a deck-interior-only cell. This is the interior a component may fill.
    /// </summary>
    public bool IsUsableCavity(int x, int y, int z) => RoleAt(x, y, z) == HullCellRole.Cavity;

    /// <summary>The local supporting deck elevation at a station, or <c>int.MinValue</c> when none.</summary>
    public int DeckYAt(int z) =>
        z < MinZ || z > MaxZ ? int.MinValue : _deckYAt(z);

    /// <summary>The local inner-bottom elevation at a station, or <c>int.MinValue</c> when none.</summary>
    public int FloorYAt(int z) =>
        z < MinZ || z > MaxZ ? int.MinValue : _floorYAt(z);

    /// <summary>
    /// True only when the cell is both an exposed deck-armor cell and named by the supplied
    /// mask. A mask entry that names a side, bottom, internal, air or cavity cell is ignored.
    /// </summary>
    public bool IsAuthorizedDeckOpening(HullCell cell) =>
        DeckOpenings.Allows(cell) &&
        _armor.TryGetValue(cell, out var intent) &&
        intent.Role == HullCellRole.DeckArmor;

    /// <summary>Enumerates every armor intent in stable Z, then Y, then X order.</summary>
    public IEnumerable<HullCellIntent> EnumerateArmor() => _armorOrdered;

    /// <summary>
    /// Materializes the exact feature-free evaluation captured by this context. Keeping the
    /// callback internal prevents component code from bypassing the composition adapter while
    /// still ensuring the adapter consumes this evaluation rather than rebuilding an approximate
    /// solid from public queries.
    /// </summary>
    internal GeneratedHull ComposeFeatureFree(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _composeFeatureFree(cancellationToken);
    }
}
