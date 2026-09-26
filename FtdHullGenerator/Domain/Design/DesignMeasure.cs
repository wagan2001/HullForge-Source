using System.Text.Json.Serialization;

namespace FtdHullGenerator.Domain.Design;

/// <summary>
/// A design-space measurement stored as an integer number of half-metres.
/// </summary>
/// <remarks>
/// Contract C02: placement anchors stay integer cells, but design centres, handles,
/// measured extents and gap arithmetic use these twice-metre integers so a value that
/// lands on a half-cell centre is still exact. Convert to metres only at display and
/// geometry boundaries. One metre is two units, one lattice cell anchor at index
/// <c>x</c> is <c>2x</c>, and a plane halfway between two cells is representable.
/// </remarks>
public readonly record struct DesignMeasure : IComparable<DesignMeasure>
{
    /// <summary>
    /// The one persisted member. The annotation matters: this is a non-positional record struct, and
    /// without it System.Text.Json silently constructs the default value instead of failing, which
    /// would zero every design coordinate in a saved project.
    /// </summary>
    [JsonConstructor]
    public DesignMeasure(int twiceMetres)
    {
        TwiceMetres = twiceMetres;
    }

    /// <summary>Half-metre integer value. Exactly twice the measurement in metres.</summary>
    public int TwiceMetres { get; }

    public static DesignMeasure Zero { get; } = new(0);

    public static DesignMeasure FromTwiceMetres(int twiceMetres) => new(twiceMetres);

    public static DesignMeasure FromMetres(int metres) => new(checked(metres * 2));

    /// <summary>The design coordinate of the integer lattice cell at <paramref name="cellIndex"/>.</summary>
    public static DesignMeasure FromCellAnchor(int cellIndex) => new(checked(cellIndex * 2));

    public static DesignMeasure FromMetres(double metres)
    {
        if (!double.IsFinite(metres))
            throw new ArgumentOutOfRangeException(nameof(metres), metres, "A design measurement must be finite.");

        var twice = Math.Round(metres * 2.0, MidpointRounding.ToEven);
        if (twice is < int.MinValue or > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(metres), metres, "A design measurement is out of range.");

        return new DesignMeasure((int)twice);
    }

    /// <summary>The exact measurement in metres. Half-metre values are exact in binary floating point.</summary>
    [JsonIgnore]
    public double Metres => TwiceMetres / 2.0;

    /// <summary>True when this value lands on a whole metre (an even number of twice-metre units).</summary>
    [JsonIgnore]
    public bool IsWholeMetre => (TwiceMetres & 1) == 0;

    /// <summary>True when this value lands on an integer lattice cell anchor.</summary>
    [JsonIgnore]
    public bool IsCellAnchor => IsWholeMetre;

    /// <summary>The largest whole metre not greater than this measurement.</summary>
    [JsonIgnore]
    public int FloorMetres
    {
        get
        {
            // Integer division truncates toward zero, so a negative half-metre needs one step down.
            var floor = TwiceMetres / 2;
            if (TwiceMetres < 0 && TwiceMetres % 2 != 0)
                floor--;
            return floor;
        }
    }

    public static DesignMeasure Min(DesignMeasure left, DesignMeasure right) =>
        left <= right ? left : right;

    public static DesignMeasure Max(DesignMeasure left, DesignMeasure right) =>
        left >= right ? left : right;

    public DesignMeasure Clamp(DesignMeasure minimum, DesignMeasure maximum) =>
        this < minimum ? minimum : this > maximum ? maximum : this;

    /// <summary>The exact half of this measurement, or <c>null</c> when it is not exactly halvable.</summary>
    public DesignMeasure? TryHalve() => (TwiceMetres & 1) == 0 ? new DesignMeasure(TwiceMetres / 2) : null;

    /// <summary>The magnitude of this measurement. Checked, so the one unrepresentable value throws.</summary>
    [JsonIgnore]
    public DesignMeasure Magnitude => TwiceMetres >= 0
        ? this
        : TwiceMetres == int.MinValue
            ? new DesignMeasure(int.MaxValue)
            : new DesignMeasure(-TwiceMetres);

    /// <summary>True when the measurement is inside the design-space coordinate bound.</summary>
    [JsonIgnore]
    public bool IsWithinDesignBounds => Magnitude.TwiceMetres <= DesignLimits.MaxDesignTwiceMetres;

    public static DesignMeasure operator +(DesignMeasure left, DesignMeasure right) =>
        new(checked(left.TwiceMetres + right.TwiceMetres));

    public static DesignMeasure operator -(DesignMeasure left, DesignMeasure right) =>
        new(checked(left.TwiceMetres - right.TwiceMetres));

    public static DesignMeasure operator -(DesignMeasure value) => new(checked(-value.TwiceMetres));

    public static DesignMeasure operator *(DesignMeasure value, int factor) =>
        new(checked(value.TwiceMetres * factor));

    public static DesignMeasure operator *(int factor, DesignMeasure value) =>
        new(checked(value.TwiceMetres * factor));

    public static bool operator <(DesignMeasure left, DesignMeasure right) => left.TwiceMetres < right.TwiceMetres;

    public static bool operator >(DesignMeasure left, DesignMeasure right) => left.TwiceMetres > right.TwiceMetres;

    public static bool operator <=(DesignMeasure left, DesignMeasure right) => left.TwiceMetres <= right.TwiceMetres;

    public static bool operator >=(DesignMeasure left, DesignMeasure right) => left.TwiceMetres >= right.TwiceMetres;

    public int CompareTo(DesignMeasure other) => TwiceMetres.CompareTo(other.TwiceMetres);

    public override string ToString() =>
        IsWholeMetre ? $"{TwiceMetres / 2} m" : $"{Metres:0.##} m";
}

/// <summary>
/// A closed interval in design space: <see cref="Start"/> and <see cref="End"/> are both part of
/// the span, and two spans overlap only when they share more than a boundary point. Adjacent
/// components therefore abut without being reported as colliding.
/// </summary>
public readonly record struct DesignSpan
{
    public DesignSpan(DesignMeasure start, DesignMeasure end)
    {
        if (end < start)
            throw new ArgumentException("A design span cannot end before it starts.", nameof(end));

        Start = start;
        End = end;
    }

    public DesignMeasure Start { get; }

    public DesignMeasure End { get; }

    public DesignMeasure Length => End - Start;

    public bool IsEmpty => End <= Start;

    public bool Contains(DesignMeasure value) => value >= Start && value <= End;

    public bool Overlaps(DesignSpan other) => Start < other.End && other.Start < End;

    public DesignSpan Shift(DesignMeasure offset) => new(Start + offset, End + offset);

    public override string ToString() => $"[{Start.TwiceMetres}, {End.TwiceMetres}]";
}

/// <summary>
/// The real lateral lattice of one hull: which cells exist and where its true mirror
/// plane falls.
/// </summary>
/// <remarks>
/// Hull Forge does not mirror about <c>x = 0</c>. It mirrors about
/// <c>(MinX + MaxX) / 2</c>, so a column mirrors to <c>MirrorSum - column</c>. That plane
/// lands on a cell on an odd-width hull and between two cells on an even-width hull, which
/// is what decides whether a centred slab of a given thickness is representable at all.
/// </remarks>
public readonly record struct CenterlineLattice
{
    public CenterlineLattice(int minCell, int maxCell)
    {
        if (maxCell < minCell)
            throw new ArgumentException("A centerline lattice cannot end before it starts.", nameof(maxCell));

        MinCell = minCell;
        MaxCell = maxCell;
    }

    public int MinCell { get; }

    public int MaxCell { get; }

    public int Width => checked(MaxCell - MinCell + 1);

    /// <summary>Twice the centre plane, in twice-metre units. Equals <c>MinCell + MaxCell</c>.</summary>
    public int MirrorSum => checked(MinCell + MaxCell);

    /// <summary>The true mirror plane. Half-integral on an even-width hull.</summary>
    public DesignMeasure CenterPlane => DesignMeasure.FromTwiceMetres(MirrorSum);

    /// <summary>True when the plane falls on a real cell column (an odd-width hull).</summary>
    public bool HasCenterColumn => (MirrorSum & 1) == 0;

    /// <summary>The column a given column mirrors to.</summary>
    public int MirrorColumn(int column) => MirrorSum - column;

    /// <summary>
    /// True when a centred slab of <paramref name="thickness"/> cells can be placed without
    /// shifting it off the centre plane. Odd thickness needs a centre column; even thickness
    /// needs the plane to fall between two columns.
    /// </summary>
    public bool IsThicknessRepresentable(int thickness) =>
        thickness >= 1 && (MirrorSum - (thickness - 1)) % 2 == 0;

    /// <summary>
    /// The first (lowest X) column of a centred slab. Throws when the thickness is not
    /// representable, because silently shifting a bulkhead off the centre plane is the
    /// defect this contract exists to prevent.
    /// </summary>
    public int FirstColumnFor(int thickness)
    {
        if (!IsThicknessRepresentable(thickness))
            throw new ArgumentOutOfRangeException(nameof(thickness), thickness,
                "A centred slab of this thickness is not representable on this lattice.");

        return (MirrorSum - (thickness - 1)) / 2;
    }

    /// <summary>
    /// The nearest representable thickness to <paramref name="requested"/>, preferring the
    /// thinner option, and never below one. Offered to the user as a correction, never applied
    /// as a silent substitution.
    /// </summary>
    public int SuggestCompatibleThickness(int requested)
    {
        if (requested < 1)
            requested = 1;
        if (IsThicknessRepresentable(requested))
            return requested;
        if (IsThicknessRepresentable(requested - 1))
            return requested - 1;
        return requested + 1;
    }

    public override string ToString() =>
        $"[{MinCell}, {MaxCell}] centre {CenterPlane}";
}
