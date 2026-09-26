namespace FtdHullGenerator.Geometry;

/// <summary>
/// Optional diagnostic hints supplied by a caller that knows the intended hull features. The context is
/// purely advisory input to the validator: it never changes generation, smoothing, export or the
/// detectors' thresholds. It carries explicit protected feature masks/regions and the sub-cell sampling
/// offsets the section queries should use, so a caller can say "this chine is intentional" without the
/// validator guessing a chine from the widest point of the hull.
/// </summary>
/// <remarks>
/// A null context is equivalent to <see cref="Empty" />: no cell is protected, no region is named, and
/// the documented default sampling offset is used. Every member is optional and immutable.
/// </remarks>
public sealed class SmoothingDiagnosticContext
{
    /// <summary>The shared context with no protected features and the default sampling offset.</summary>
    public static SmoothingDiagnosticContext Empty { get; } = new();

    /// <summary>
    /// Cells the caller declares intentional, e.g. a designed chine, flare shoulder or open deck edge.
    /// A finding whose evidence lies entirely inside protected cells is not attributed to smoothing.
    /// </summary>
    public IReadOnlySet<(int X, int Y, int Z)> ProtectedCells { get; init; } =
        new HashSet<(int X, int Y, int Z)>();

    /// <summary>
    /// Named protected regions, keyed by a caller-chosen name such as <c>chine.starboard</c>,
    /// <c>flare</c> or <c>deck.opening</c>. Names are compared ordinally.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlySet<(int X, int Y, int Z)>> NamedRegions { get; init; } =
        new Dictionary<string, IReadOnlySet<(int X, int Y, int Z)>>(StringComparer.Ordinal);

    /// <summary>
    /// Explicit declaration that the hull has an intended chine or shoulder landmark. When true the
    /// landmark detector may report chine/shoulder drift even if the baseline profile alone is
    /// ambiguous. Default false, so a chine is never inferred from the widest point alone.
    /// </summary>
    public bool HasProtectedChine { get; init; }

    /// <summary>
    /// Sub-cell offset, in cells, added to each section plane coordinate. Default 0.0 samples cell
    /// centres (the non-degenerate choice); 0.5 lands on the shared face planes and is reported as
    /// uncertainty. Deterministic for a fixed value.
    /// </summary>
    public double SectionOffset { get; init; } = 0.0;

    /// <summary>True when the exact cell is declared intentional by the caller.</summary>
    public bool IsProtected((int X, int Y, int Z) cell) => ProtectedCells.Contains(cell);

    /// <summary>True when every evidence cell is protected, so the finding is not smoothing's fault.</summary>
    public bool IsRegionProtected(IEnumerable<(int X, int Y, int Z)> cells)
    {
        ArgumentNullException.ThrowIfNull(cells);
        var any = false;
        foreach (var cell in cells)
        {
            any = true;
            if (!ProtectedCells.Contains(cell)) return false;
        }

        return any;
    }

    /// <summary>Looks up a named region by exact ordinal name.</summary>
    public bool TryGetRegion(string name, out IReadOnlySet<(int X, int Y, int Z)> cells)
    {
        ArgumentNullException.ThrowIfNull(name);
        return NamedRegions.TryGetValue(name, out cells!);
    }

    /// <summary>
    /// True when the caller supplied any explicit chine evidence: the flag or a region whose name
    /// contains <c>chine</c>. The validator uses this only to decide whether chine drift may be
    /// reported confidently; it never treats the widest point as a chine on its own.
    /// </summary>
    public bool HasChineEvidence =>
        HasProtectedChine ||
        NamedRegions.Keys.Any(name => name.Contains("chine", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The union of every named region and <see cref="ProtectedCells" />, or empty when nothing is
    /// declared. Used to test whether a whole evidence region is intentional.
    /// </summary>
    public IReadOnlySet<(int X, int Y, int Z)> AllProtectedCells
    {
        get
        {
            if (NamedRegions.Count == 0) return ProtectedCells;
            var union = new HashSet<(int X, int Y, int Z)>(ProtectedCells);
            foreach (var region in NamedRegions.Values) union.UnionWith(region);
            return union;
        }
    }
}
