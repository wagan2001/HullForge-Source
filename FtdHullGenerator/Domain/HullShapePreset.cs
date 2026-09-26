namespace FtdHullGenerator.Domain;

/// <summary>
/// A named hull form shared by the application, renderer, and self-test. The
/// original scalar properties remain available for source compatibility while
/// <see cref="EffectiveShape" /> carries the complete Shape V2 definition.
/// </summary>
public sealed record HullShapePreset(
    string Name,
    BowStyle Bow,
    SternStyle Stern,
    double BowFullness,
    double SternFullness,
    double CrossSectionCurve,
    bool HasBulb = false,
    BulbSettings? Bulb = null,
    HullShapeSettings? Shape = null,
    string Description = "")
{
    private const double Tolerance = 0.000_001;

    /// <summary>Every animal preset, with all Shape V2 controls specified explicitly.</summary>
    public static IReadOnlyList<HullShapePreset> All { get; } =
    [
        Create(
            "Dolphin", BowStyle.Pointed, SternStyle.Transom,
            HullShapeSettings.Default,
            "Balanced rounded hull with gentle bow flare, a moderate entrance, a compact transom run, and a level profile."),
        Create(
            "Marlin", BowStyle.Raked, SternStyle.Canoe,
            new HullShapeSettings(
                new BowShapeSettings(-0.75, 0.75, 68),
                new BodyShapeSettings(BodyStyle.DeepV, -0.70, 0.35, 0.10),
                new SternShapeSettings(-0.45, 0.15, 55),
                new HullProfileSettings(5, 1, 4, 3)),
            "Very fine raked entrance, flared deep-V body, long canoe run, and strong bow sheer for fast narrow hulls."),
        Create(
            "Orca", BowStyle.Spoon, SternStyle.Transom,
            new HullShapeSettings(
                new BowShapeSettings(0.50, 0.30, 25),
                new BodyShapeSettings(BodyStyle.U, 0.75, -0.25, -0.65),
                new SternShapeSettings(0.45, -0.15, 24),
                new HullProfileSettings(3, 2, 2, 1)),
            "High-volume U body with rounded bilges, restrained tumblehome, a full spoon bow, and a compact transom.",
            hasBulb: true,
            bulb: new BulbSettings(16, 40, -4, 0)),
        Create(
            "Manta", BowStyle.Blunt, SternStyle.Square,
            new HullShapeSettings(
                new BowShapeSettings(0.70, -0.40, 18),
                new BodyShapeSettings(BodyStyle.FlatWide, 0.90, -0.10, 0.70, FlatBottom: 1),
                new SternShapeSettings(0.75, -0.25, 15),
                HullProfileSettings.Flat),
            "Broad low flat-bottom hull with hard shelves, short blunt ends, and a level deck and keel."),
        Create(
            "Shark", BowStyle.Raked, SternStyle.Transom,
            new HullShapeSettings(
                new BowShapeSettings(-0.35, 0.55, 50),
                new BodyShapeSettings(BodyStyle.HardChine, -0.05, 0.25, 0.90),
                new SternShapeSettings(0.05, 0.10, 32),
                new HullProfileSettings(3, 1, 3, 2)),
            "Angular hard-chine hull with a fine flared bow, moderate run, and lifted forefoot."),
        Create(
            "Whale", BowStyle.Blunt, SternStyle.Square,
            new HullShapeSettings(
                new BowShapeSettings(0.70, -0.30, 25),
                new BodyShapeSettings(BodyStyle.Tumblehome, 0.80, -0.55, -0.70),
                new SternShapeSettings(0.60, -0.45, 20),
                new HullProfileSettings(2, 2, 1, 1)),
            "Very full lower body with soft bilges, pronounced tumblehome, broad ends, and a wide displacement bulb.",
            hasBulb: true,
            bulb: new BulbSettings(14, 55, -4, 2)),
        Create(
            "Barracuda", BowStyle.Raked, SternStyle.Canoe,
            new HullShapeSettings(
                new BowShapeSettings(-0.65, 0.50, 64),
                new BodyShapeSettings(BodyStyle.V, -0.35, 0.15, 0.25),
                new SternShapeSettings(-0.50, 0.10, 58),
                new HullProfileSettings(4, 1, 4, 4)),
            "Lean V hull with a long raked entrance, fine canoe run, and strongly lifted keel at both ends."),
        Create(
            "Tuna", BowStyle.Spoon, SternStyle.Counter,
            new HullShapeSettings(
                new BowShapeSettings(0.30, 0.35, 40),
                new BodyShapeSettings(BodyStyle.Rounded, 0.50, 0.20, -0.45),
                new SternShapeSettings(0.25, 0.05, 34),
                new HullProfileSettings(3, 2, 2, 2)),
            "Full but streamlined rounded body with balanced flare, spoon bow, counter stern, and gentle rise at both ends."),
        Create(
            "Ray", BowStyle.Blunt, SternStyle.Square,
            new HullShapeSettings(
                new BowShapeSettings(0.65, -0.65, 15),
                new BodyShapeSettings(BodyStyle.FlatWide, 0.90, -0.35, 0.80, FlatBottom: 1),
                new SternShapeSettings(0.80, -0.50, 15),
                HullProfileSettings.Flat),
            "Extremely wide flat platform with hard chines, inward upper sides, minimal end influence, and a level profile."),
        Create(
            "Swordfish", BowStyle.Raked, SternStyle.Canoe,
            new HullShapeSettings(
                new BowShapeSettings(-0.85, 0.80, 76),
                new BodyShapeSettings(BodyStyle.DeepV, -0.80, 0.35, 0.25),
                new SternShapeSettings(-0.55, 0.20, 62),
                new HullProfileSettings(6, 2, 5, 4)),
            "The finest deep-V profile: maximum entrance length, high bow flare and sheer, and a long lifted canoe run."),
        Create(
            "Seal", BowStyle.Spoon, SternStyle.Canoe,
            new HullShapeSettings(
                new BowShapeSettings(0.50, 0.10, 32),
                new BodyShapeSettings(BodyStyle.U, 0.80, -0.45, -0.70),
                new SternShapeSettings(0.50, -0.50, 38),
                new HullProfileSettings(2, 2, 2, 2)),
            "Compact rounded U hull with soft bilges, inward upper sides, and symmetric spoon/canoe end rise."),
        Create(
            "Narwhal", BowStyle.Pointed, SternStyle.Counter,
            new HullShapeSettings(
                new BowShapeSettings(-0.10, 0.30, 58),
                new BodyShapeSettings(BodyStyle.Tumblehome, 0.55, -0.50, -0.45),
                new SternShapeSettings(0.15, -0.25, 35),
                new HullProfileSettings(3, 1, 2, 1)),
            "Moderate tumblehome with a long pointed entrance, counter stern, and a larger forward bulb.",
            hasBulb: true,
            bulb: new BulbSettings(30, 65, -2, 0)),
        Create(
            "Hammerhead", BowStyle.Axe, SternStyle.Cruiser,
            new HullShapeSettings(
                new BowShapeSettings(-0.55, 0.60, 58),
                new BodyShapeSettings(BodyStyle.HardChine, -0.20, 0.35, 0.85),
                new SternShapeSettings(0.15, 0.10, 30),
                new HullProfileSettings(4, 1, 3, 2)),
            "Fine reverse axe bow, hard-chine body, and a compact rounded cruiser stern."),
        Create(
            "Sailfish", BowStyle.Clipper, SternStyle.Fantail,
            new HullShapeSettings(
                new BowShapeSettings(-0.80, 0.75, 72),
                new BodyShapeSettings(BodyStyle.DeepV, -0.75, 0.45, 0.15),
                new SternShapeSettings(-0.15, 0.25, 48),
                new HullProfileSettings(6, 2, 4, 2)),
            "Long clipper entrance, narrow deep-V body, high bow sheer, and a rounded fantail."),
        Create(
            "Pike", BowStyle.Axe, SternStyle.Fantail,
            new HullShapeSettings(
                new BowShapeSettings(-0.65, 0.35, 65),
                new BodyShapeSettings(BodyStyle.V, -0.40, 0.20, 0.40),
                new SternShapeSettings(-0.25, 0.15, 45),
                new HullProfileSettings(3, 1, 3, 2)),
            "Lean V hull with a projecting axe forefoot and a fine, broad-ended fantail run."),
        Create(
            "Sturgeon", BowStyle.Clipper, SternStyle.Cruiser,
            new HullShapeSettings(
                new BowShapeSettings(0.40, 0.25, 38),
                new BodyShapeSettings(BodyStyle.U, 0.70, -0.20, -0.65),
                new SternShapeSettings(0.50, -0.15, 32),
                new HullProfileSettings(2, 2, 1, 1)),
            "Full displacement U body with curved clipper and cruiser ends and a compact forward bulb.",
            hasBulb: true,
            bulb: new BulbSettings(12, 45, -7, 3)),
    ];

    private static HullShapePreset Create(
        string name,
        BowStyle bow,
        SternStyle stern,
        HullShapeSettings shape,
        string description,
        bool hasBulb = false,
        BulbSettings? bulb = null) =>
        new(
            Name: name,
            Bow: bow,
            Stern: stern,
            BowFullness: shape.Bow.Fullness,
            SternFullness: shape.Stern.Fullness,
            CrossSectionCurve: shape.Body.Fullness,
            HasBulb: hasBulb,
            Bulb: bulb,
            Shape: shape,
            Description: description);

    /// <summary>The complete regional shape supplied by this preset.</summary>
    public HullShapeSettings EffectiveShape =>
        Shape ?? HullShapeSettings.FromLegacy(BowFullness, SternFullness, CrossSectionCurve);

    /// <summary>Finds the preset matching every active shape control, or null.</summary>
    public static HullShapePreset? Match(HullParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return Match(
            parameters.BowStyle,
            parameters.SternStyle,
            parameters.EffectiveShape,
            parameters.HasBulb,
            parameters.EffectiveBulb);
    }

    /// <summary>Finds the preset matching a structured Shape V2 definition, or null.</summary>
    public static HullShapePreset? Match(
        BowStyle bow,
        SternStyle stern,
        HullShapeSettings shape,
        bool hasBulb,
        BulbSettings bulb)
    {
        ArgumentNullException.ThrowIfNull(shape);
        return All.FirstOrDefault(preset =>
            preset.Bow == bow &&
            preset.Stern == stern &&
            ShapeEquals(preset.EffectiveShape, shape) &&
            BulbEquals(preset, hasBulb, bulb));
    }

    /// <summary>
    /// Compatibility overload for callers that still supply the original three
    /// scalars. New code should pass <see cref="HullShapeSettings" /> so every active
    /// shape value participates in matching.
    /// </summary>
    public static HullShapePreset? Match(
        BowStyle bow,
        SternStyle stern,
        double bowFullness,
        double sternFullness,
        double crossSectionCurve,
        bool hasBulb,
        BulbSettings bulb) =>
        All.FirstOrDefault(preset =>
            preset.Bow == bow &&
            preset.Stern == stern &&
            NearlyEqual(preset.BowFullness, bowFullness) &&
            NearlyEqual(preset.SternFullness, sternFullness) &&
            NearlyEqual(preset.CrossSectionCurve, crossSectionCurve) &&
            BulbEquals(preset, hasBulb, bulb));

    /// <summary>The bulb proportions this preset uses when its bulb is switched on.</summary>
    public BulbSettings EffectiveBulb => Bulb ?? BulbSettings.Default;

    /// <summary>Finds a preset by name, ignoring case, or null.</summary>
    public static HullShapePreset? FindByName(string name) =>
        All.FirstOrDefault(preset => string.Equals(preset.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Applies this preset's complete shape while preserving dimensions and construction settings.</summary>
    public HullParameters Apply(HullParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var shape = EffectiveShape;
        return parameters with
        {
            BowStyle = Bow,
            SternStyle = Stern,
            BowFullness = shape.Bow.Fullness,
            SternFullness = shape.Stern.Fullness,
            CrossSectionCurve = shape.Body.Fullness,
            Shape = shape,
            HasBulb = HasBulb,
            Bulb = Bulb ?? parameters.Bulb,
        };
    }

    public override string ToString() => Name;

    private static bool BulbEquals(HullShapePreset preset, bool hasBulb, BulbSettings bulb) =>
        preset.HasBulb == hasBulb && (!hasBulb || preset.EffectiveBulb == bulb);

    private static bool ShapeEquals(HullShapeSettings left, HullShapeSettings right) =>
        left.Body.Style == right.Body.Style &&
        left.Bow.EntranceLengthPercent == right.Bow.EntranceLengthPercent &&
        left.Stern.RunLengthPercent == right.Stern.RunLengthPercent &&
        left.Profile == right.Profile &&
        NearlyEqual(left.Bow.Fullness, right.Bow.Fullness) &&
        NearlyEqual(left.Bow.Flare, right.Bow.Flare) &&
        NearlyEqual(left.Body.Fullness, right.Body.Fullness) &&
        NearlyEqual(left.Body.SideShape, right.Body.SideShape) &&
        NearlyEqual(left.Body.Chine, right.Body.Chine) &&
        NearlyEqual(left.Body.FlatBottom, right.Body.FlatBottom) &&
        NearlyEqual(left.Stern.Fullness, right.Stern.Fullness) &&
        NearlyEqual(left.Stern.SideShape, right.Stern.SideShape);

    private static bool NearlyEqual(double left, double right) => Math.Abs(left - right) < Tolerance;
}
