namespace FtdHullGenerator.Domain;

public enum SuperstructureStyle
{
    CenterIsland = 0,
    FrenchHotel = 1,
    JapanesePagoda = 2,
}

public enum SuperstructureSmoothingMethod
{
    None = 0,
    HorizontalSlopeFill = 1,
}

/// <summary>Settings for the stable legacy deck-mounted structure.</summary>
public sealed record SuperstructureSettings(
    bool Enabled,
    SuperstructureStyle Style,
    int Levels,
    MaterialKind Material,
    SuperstructureSmoothingMethod Smoothing,
    int ForeAftPercent = 0)
{
    public const int MinimumLevels = 1;
    public const int MaximumLevels = 8;

    /// <summary>Percentage of the occupied length the structure's centre may move.</summary>
    public const int MinimumForeAftPercent = -40;
    public const int MaximumForeAftPercent = 40;

    /// <summary>
    /// The Japanese Pagoda's historic station, kept as the default so the shipped
    /// placement does not move. Positive percentages are toward the bow.
    /// </summary>
    public const int DefaultForeAftPercent = 8;

    public static SuperstructureSettings Default { get; } = new(
        false, SuperstructureStyle.JapanesePagoda, 3, MaterialKind.Metal,
        SuperstructureSmoothingMethod.None, DefaultForeAftPercent);

    /// <summary>Conservative height above its supporting deck, including every roof slab.</summary>
    public int AddedHeight => !Enabled || Levels < MinimumLevels || !Enum.IsDefined(Style)
        ? 0
        : Enumerable.Range(1, Math.Min(Levels, MaximumLevels))
            .Sum(level => ClearHeight(Style, level) + 1);

    public IReadOnlyList<string> Validate(bool hasDeck)
    {
        if (!Enabled)
            return [];

        var errors = new List<string>();
        if (!Enum.IsDefined(Style))
            errors.Add("The selected superstructure style is not supported.");
        if (Levels is < MinimumLevels or > MaximumLevels)
            errors.Add($"Superstructure levels must be between {MinimumLevels} and {MaximumLevels}.");
        if (!Enum.IsDefined(Material))
            errors.Add("The selected superstructure material is not supported.");
        if (!Enum.IsDefined(Smoothing))
            errors.Add("The selected superstructure smoothing method is not supported.");
        if (ForeAftPercent is < MinimumForeAftPercent or > MaximumForeAftPercent)
            errors.Add($"The superstructure fore/aft offset must be between {MinimumForeAftPercent} and {MaximumForeAftPercent} percent.");
        if (!hasDeck)
            errors.Add("A superstructure requires a deck.");
        return errors;
    }

    public static int ClearHeight(SuperstructureStyle style, int level) => style switch
    {
        SuperstructureStyle.CenterIsland => level == 1 ? 4 : 2,
        SuperstructureStyle.FrenchHotel => level == 1 ? 4 : level <= 3 ? 3 : 2,
        SuperstructureStyle.JapanesePagoda => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(style), style, "Unknown superstructure style."),
    };
}
