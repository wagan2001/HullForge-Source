namespace FtdHullGenerator.Domain;

public sealed record HullParameters(
    int Length,
    int Width,
    int Height,
    double BowFullness,
    double SternFullness,
    double CrossSectionCurve,
    ArmorLayout HullArmor,
    ArmorLayout? DeckArmor,
    bool Beamify,
    SmoothingMethod Smoothing = SmoothingMethod.None,
    int HybridFillOffset = 1,
    BowStyle BowStyle = BowStyle.Pointed,
    SternStyle SternStyle = SternStyle.Transom,
    bool HasBulb = false,
    BulbSettings? Bulb = null,
    HullShapeSettings? Shape = null,
    ArmorLayout? BottomArmor = null,
    SuperstructureSettings? Superstructure = null)
{
    /// <summary>Older callers share the side stack with the bottom.</summary>
    public ArmorLayout EffectiveBottomArmor => BottomArmor ?? HullArmor;
    /// <summary>The bulb proportions in force: the explicit setting, or the default when none was given.</summary>
    public BulbSettings EffectiveBulb => Bulb ?? BulbSettings.Default;

    /// <summary>
    /// The active regional shape. Calls made through the original constructor are
    /// adapted from its three scalar controls so existing integrations keep working.
    /// </summary>
    public HullShapeSettings EffectiveShape =>
        Shape ?? HullShapeSettings.FromLegacy(BowFullness, SternFullness, CrossSectionCurve);
    public SuperstructureSettings EffectiveSuperstructure => Superstructure ?? SuperstructureSettings.Default;

    /// <summary>Profile height before sampling; final occupied height also includes any bulb below the keel.</summary>
    public int OverallHeight
    {
        get
        {
            var value = (long)Height + Math.Max(EffectiveShape.Profile.BowDeckRise, EffectiveShape.Profile.SternDeckRise) +
                        EffectiveSuperstructure.AddedHeight;
            return (int)Math.Clamp(value, int.MinValue, int.MaxValue);
        }
    }

    public const int MinimumLength = 3;
    public const int MinimumWidth = 1;
    public const int MinimumHeight = 2;

    public static HullParameters Default { get; } = new(
        Length: 100,
        Width: 21,
        Height: 12,
        BowFullness: 0,
        SternFullness: 0,
        CrossSectionCurve: 0.35,
        HullArmor: ArmorLayout.Single(MaterialKind.Metal),
        DeckArmor: ArmorLayout.Single(MaterialKind.Metal),
        Beamify: true,
        Smoothing: SmoothingMethod.None,
        Shape: HullShapeSettings.Default);

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Length < MinimumLength)
            errors.Add($"Length must be at least {MinimumLength} m.");
        if (Width < MinimumWidth)
            errors.Add($"Width must be at least {MinimumWidth} m.");
        if (Height < MinimumHeight)
            errors.Add($"Height must be at least {MinimumHeight} m.");
        errors.AddRange(EffectiveShape.Validate(Height));
        if (Length > 0 && OverallHeight > 0 && (long)Length * OverallHeight > Array.MaxLength)
            errors.Add("Length × height is too large for the in-memory hull profile.");
        if (Length > 0 && Width > 0 && OverallHeight > 0 && HullArmor is not null)
        {
            var surfaceUpperBound = 2m * ((decimal)Length * Width +
                                           (decimal)Length * OverallHeight +
                                           (decimal)Width * OverallHeight);
            var depth = Math.Max(Math.Max(HullArmor.Thickness, EffectiveBottomArmor.Thickness), DeckArmor?.Thickness ?? 0);
            var estimatedCells = surfaceUpperBound * Math.Max(depth, 1);
            var availableBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            if (availableBytes > 0 && estimatedCells * 256m > availableBytes / 4m)
                errors.Add("This hull is too large for the memory currently available. Reduce its dimensions or armor depth.");
        }
        if (!Enum.IsDefined(Smoothing))
            errors.Add("The selected hull smoothing method is not supported.");
        if (!Enum.IsDefined(BowStyle))
            errors.Add("The selected bow style is not supported.");
        if (!Enum.IsDefined(SternStyle))
            errors.Add("The selected stern style is not supported.");
        if (HasBulb)
            errors.AddRange(EffectiveBulb.Validate());
        if (HybridFillOffset < 1)
            errors.Add("Hybrid fill offset must be at least 1 m. Its upper bound follows the generated hull height.");
        if (HullArmor is null)
            errors.Add("Choose at least one hull armor layer.");
        else if (HullArmor.Layers[0].IsAir)
            errors.Add("The outer hull armor layer cannot be air. Put air between structural layers to create an air gap.");
        if (BottomArmor is not null && BottomArmor.Layers[0].IsAir)
            errors.Add("The outer bottom armor layer cannot be air.");
        if (DeckArmor is { Thickness: < 1 })
            errors.Add("Choose at least one deck armor layer.");
        else if (DeckArmor is not null && DeckArmor.Layers[0].IsAir)
            errors.Add("The top deck armor layer cannot be air. Put air below a structural deck layer to create an air gap.");
        errors.AddRange(EffectiveSuperstructure.Validate(HasDeck));
        return errors;
    }

    /// <summary>
    /// Gets whether the port/starboard symmetry plane passes through one block
    /// column. Even widths remain supported, but their symmetry plane falls
    /// between the two centre columns.
    /// </summary>
    public bool HasSingleBlockCenterline => Width % 2 != 0;

    public bool HasDeck => DeckArmor is not null;

    public MaterialKind SurfaceMaterial => HullArmor.SurfaceMaterial;
}
