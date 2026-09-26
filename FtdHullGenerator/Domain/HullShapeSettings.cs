namespace FtdHullGenerator.Domain;

/// <summary>
/// Bow-region controls. Positive flare widens the upper section; negative flare
/// draws it inward. Entrance length is a percentage of the hull length.
/// </summary>
public sealed record BowShapeSettings(
    double Fullness,
    double Flare,
    int EntranceLengthPercent);

/// <summary>
/// Midship-section controls. Positive side shape means flare and negative means
/// tumblehome; positive chine is harder and negative chine is softer. Flat bottom
/// is the breadth the lowest row holds before the section turns up: zero leaves
/// the original tapered keel and one gives a full-beam horizontal floor with a
/// direct right-angle join to the side wall.
/// </summary>
public sealed record BodyShapeSettings(
    BodyStyle Style,
    double Fullness,
    double SideShape,
    double Chine,
    double FlatBottom = 0)
{
    /// <summary>Returns the canonical editable values for a body-style choice.</summary>
    public static BodyShapeSettings ForStyle(BodyStyle style) => style switch
    {
        BodyStyle.Rounded => new(style, 0.35, 0.10, -0.65),
        BodyStyle.V => new(style, -0.10, 0.10, -0.35),
        BodyStyle.DeepV => new(style, -0.55, 0.20, -0.15),
        BodyStyle.U => new(style, 0.65, 0.05, -0.45),
        BodyStyle.FlatWide => new(style, 0.85, 0.05, 0.35, FlatBottom: 1),
        BodyStyle.HardChine => new(style, 0.20, 0.05, 0.85),
        BodyStyle.Tumblehome => new(style, 0.55, -0.65, -0.40),
        BodyStyle.Custom => new(style, 0.35, 0.10, -0.65),
        _ => throw new ArgumentOutOfRangeException(nameof(style), style, "Unknown body style."),
    };
}

/// <summary>
/// Stern-region controls. Positive side shape means flare and negative means
/// tumblehome; run length is a percentage of the hull length.
/// </summary>
public sealed record SternShapeSettings(
    double Fullness,
    double SideShape,
    int RunLengthPercent);

/// <summary>
/// Whole-metre changes to the deck and keel at each end, measured from the
/// midship deck and keel respectively.
/// </summary>
public sealed record HullProfileSettings(
    int BowDeckRise,
    int SternDeckRise,
    int BowKeelRise,
    int SternKeelRise)
{
    public static HullProfileSettings Flat { get; } = new(0, 0, 0, 0);
}

/// <summary>
/// The complete three-region shape and side-profile definition used by Shape V2.
/// </summary>
public sealed record HullShapeSettings(
    BowShapeSettings Bow,
    BodyShapeSettings Body,
    SternShapeSettings Stern,
    HullProfileSettings Profile)
{
    /// <summary>
    /// The 2.0 shape-control envelope. The original Shape V2 vocabulary stopped at
    /// -0.9..0.9; 2.0 opens the same controls to a materially wider but still finite
    /// travel. A value inside the former envelope evaluates exactly as it always did, so
    /// existing presets, projects and frozen fixtures are unchanged; values outside it
    /// simply continue the same analytic curves instead of being silently clamped by the
    /// UI. The generator and validator, not an arbitrary control gate, decide whether a
    /// particular combination is realizable.
    /// </summary>
    public const double MinimumControl = -1.5;
    public const double MaximumControl = 1.5;
    public const double MinimumFlatBottom = 0;
    public const double MaximumFlatBottom = 1;
    public const int MinimumRegionLengthPercent = 5;
    public const int MaximumRegionLengthPercent = 80;

    public static HullShapeSettings Default { get; } = new(
        new BowShapeSettings(0, 0.15, 45),
        BodyShapeSettings.ForStyle(BodyStyle.Rounded),
        new SternShapeSettings(0, 0, 25),
        HullProfileSettings.Flat);

    /// <summary>
    /// Adapts the original three scalar controls to Shape V2. The resulting body
    /// is Custom because the legacy cross-section value was not a named style.
    /// </summary>
    public static HullShapeSettings FromLegacy(
        double bowFullness,
        double sternFullness,
        double crossSectionCurve) => new(
        Default.Bow with { Fullness = bowFullness },
        Default.Body with { Style = BodyStyle.Custom, Fullness = crossSectionCurve },
        Default.Stern with { Fullness = sternFullness },
        HullProfileSettings.Flat);

    /// <summary>Validates the active shape controls against one hull's midship height.</summary>
    public IReadOnlyList<string> Validate(int midshipHeight)
    {
        var errors = new List<string>();
        AddControlError(errors, Bow.Fullness, "Bow fullness");
        AddControlError(errors, Bow.Flare, "Bow flare");
        AddControlError(errors, Body.Fullness, "Body fullness");
        AddControlError(errors, Body.SideShape, "Body side shape");
        AddControlError(errors, Body.Chine, "Body chine");
        if (!double.IsFinite(Body.FlatBottom) ||
            Body.FlatBottom is < MinimumFlatBottom or > MaximumFlatBottom)
        {
            errors.Add($"Body flat bottom must be a finite number between {MinimumFlatBottom} and {MaximumFlatBottom}.");
        }
        AddControlError(errors, Stern.Fullness, "Stern fullness");
        AddControlError(errors, Stern.SideShape, "Stern side shape");

        if (!Enum.IsDefined(Body.Style))
            errors.Add("The selected body style is not supported.");
        if (Bow.EntranceLengthPercent is < MinimumRegionLengthPercent or > MaximumRegionLengthPercent)
            errors.Add($"Bow entrance length must be between {MinimumRegionLengthPercent}% and {MaximumRegionLengthPercent}% of the hull length.");
        if (Stern.RunLengthPercent is < MinimumRegionLengthPercent or > MaximumRegionLengthPercent)
            errors.Add($"Stern run length must be between {MinimumRegionLengthPercent}% and {MaximumRegionLengthPercent}% of the hull length.");

        var maximumRise = Math.Max(0, midshipHeight - 2);
        AddRiseError(errors, Profile.BowDeckRise, "Bow deck rise", maximumRise);
        AddRiseError(errors, Profile.SternDeckRise, "Stern deck rise", maximumRise);
        AddRiseError(errors, Profile.BowKeelRise, "Bow keel rise", maximumRise);
        AddRiseError(errors, Profile.SternKeelRise, "Stern keel rise", maximumRise);
        return errors;
    }

    private static void AddControlError(ICollection<string> errors, double value, string name)
    {
        if (!double.IsFinite(value) || value is < MinimumControl or > MaximumControl)
            errors.Add($"{name} must be a finite number between {MinimumControl} and {MaximumControl}.");
    }

    private static void AddRiseError(ICollection<string> errors, int value, string name, int maximum)
    {
        if (value < 0 || value > maximum)
            errors.Add($"{name} must be between 0 m and {maximum} m for this hull height.");
    }
}
