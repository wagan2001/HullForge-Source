namespace FtdHullGenerator.Domain;

/// <summary>
/// The proportions and position of a bulbous bow, each as a whole-number percentage of
/// the hull's own dimension so a preset keeps its look at any size. The bulb is a
/// round-sectioned ellipsoid: <see cref="LengthPercent" /> of the hull length long and
/// <see cref="WidthPercent" /> of the beam across. Its centre sits
/// <see cref="ForeAftPercent" /> of the hull length ahead of the stem (negative moves it
/// aft, into the forefoot) and its underside rises <see cref="RisePercent" /> of the hull
/// height above the keel.
/// </summary>
public readonly record struct BulbSettings(int LengthPercent, int WidthPercent, int ForeAftPercent, int RisePercent)
{
    public const int MinimumLengthPercent = 2;
    public const int MaximumLengthPercent = 30;
    public const int MinimumWidthPercent = 10;
    public const int MaximumWidthPercent = 70;
    public const int MinimumForeAftPercent = -40;
    public const int MaximumForeAftPercent = 40;
    public const int MinimumRisePercent = -50;
    public const int MaximumRisePercent = 50;

    /// <summary>A modest bulb centred on the stem and seated on the keel: 8% of the length long, 35% of the beam across.</summary>
    public static BulbSettings Default { get; } = new(8, 35, 0, 0);

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (LengthPercent < MinimumLengthPercent || LengthPercent > MaximumLengthPercent)
            errors.Add($"Bulb length must be between {MinimumLengthPercent}% and {MaximumLengthPercent}% of the hull length.");
        if (WidthPercent < MinimumWidthPercent || WidthPercent > MaximumWidthPercent)
            errors.Add($"Bulb width must be between {MinimumWidthPercent}% and {MaximumWidthPercent}% of the hull width.");
        if (ForeAftPercent < MinimumForeAftPercent || ForeAftPercent > MaximumForeAftPercent)
            errors.Add($"Bulb fore/aft offset must be between {MinimumForeAftPercent}% and {MaximumForeAftPercent}% of the hull length.");
        if (RisePercent < MinimumRisePercent || RisePercent > MaximumRisePercent)
            errors.Add($"Bulb rise must be between {MinimumRisePercent}% and {MaximumRisePercent}% of the hull height.");
        return errors;
    }

    public override string ToString() => $"L{LengthPercent}% W{WidthPercent}% Z{ForeAftPercent:+0;-0;0}% Y{RisePercent}%";
}
