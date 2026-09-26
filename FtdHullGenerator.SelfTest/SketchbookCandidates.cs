using System.Security.Cryptography;
using System.Text;
using FtdHullGenerator.Domain;

/// <summary>
/// Which declared size of one Alternate Naval Sketchbook candidate is being applied.
/// </summary>
internal enum SketchbookSizeKind
{
    Suggested,
    Minimum,
}

/// <summary>
/// One fully resolved size of a candidate: the requested midship dimensions plus the
/// profile rises that actually apply at that size.
/// </summary>
internal sealed record SketchbookSize(
    int Length,
    int Width,
    int Height,
    HullProfileSettings AppliedProfile);

/// <summary>
/// One fixed "Alternate Naval Sketchbook" Shape V2 candidate. This is audit-owned test
/// data, deliberately not a product catalog: it transcribes the frozen candidate table
/// exactly and owns the only supported way to initialise a <see cref="HullParameters"/>
/// from it.
/// </summary>
/// <remarks>
/// Every candidate fixes <see cref="BodyStyle.Custom"/>, <c>Beamify = true</c> and
/// <c>Smoothing = None</c> for base generation. The bulb state is always explicit:
/// disabled candidates carry <c>HasBulb = false</c> with an explicit
/// <c>BulbSettings(8, 35, 0, 0)</c>, never a null/inherited value.
/// </remarks>
internal sealed record SketchbookCandidate(
    string Id,
    string Name,
    int SuggestedLength,
    int SuggestedWidth,
    int SuggestedHeight,
    int MinimumLength,
    int MinimumWidth,
    int MinimumHeight,
    BowStyle BowStyle,
    SternStyle SternStyle,
    BowShapeSettings Bow,
    BodyShapeSettings Body,
    SternShapeSettings Stern,
    HullProfileSettings BaselineProfile,
    HullProfileSettings ExpectedMinimumProfile,
    bool HasBulb,
    BulbSettings Bulb,
    string Recommended)
{
    public SketchbookSize SuggestedSize =>
        new(SuggestedLength, SuggestedWidth, SuggestedHeight, BaselineProfile);

    /// <summary>
    /// The minimum size with every baseline rise scaled independently from the immutable
    /// baseline: <c>RoundAwayFromZero(baseline * (H_current - 1) / (H_suggested - 1))</c>,
    /// computed from the baseline every time and rounded once.
    /// </summary>
    public SketchbookSize MinimumSize => new(
        MinimumLength,
        MinimumWidth,
        MinimumHeight,
        ScaleProfile(BaselineProfile, SuggestedHeight, MinimumHeight));

    public SketchbookSize Size(SketchbookSizeKind kind) =>
        kind == SketchbookSizeKind.Suggested ? SuggestedSize : MinimumSize;

    private static HullProfileSettings ScaleProfile(
        HullProfileSettings baseline, int suggestedHeight, int currentHeight)
    {
        var factor = (currentHeight - 1) / (double)(suggestedHeight - 1);
        return new HullProfileSettings(
            Scale(baseline.BowDeckRise, factor),
            Scale(baseline.SternDeckRise, factor),
            Scale(baseline.BowKeelRise, factor),
            Scale(baseline.SternKeelRise, factor));
    }

    private static int Scale(int baseline, double factor) =>
        (int)Math.Round(baseline * factor, MidpointRounding.AwayFromZero);
}

/// <summary>
/// The sixteen fixed candidates and the one supported initialiser mapping. The definitions
/// are immutable inputs: changing them invalidates the committed evidence fixture.
/// </summary>
internal static class SketchbookCandidates
{
    public const string SchemaId = "hull-forge.sketchbook-native-audit";
    public const int SchemaVersion = 1;

    /// <summary>The frozen candidate table, in 01..16 order.</summary>
    public static IReadOnlyList<SketchbookCandidate> All { get; } =
    [
        new("01", "Channel Knife Torpedo Boat", 36, 9, 5, 25, 7, 4,
            BowStyle.Spoon, SternStyle.Transom,
            new BowShapeSettings(-0.20, 0.50, 45),
            new BodyShapeSettings(BodyStyle.Custom, -0.15, 0.10, 0.90, 0.05),
            new SternShapeSettings(0.20, 0.05, 18),
            new HullProfileSettings(1, 0, 0, 0), new HullProfileSettings(1, 0, 0, 0),
            false, new BulbSettings(8, 35, 0, 0), "Horizontal"),
        new("02", "Offshore Picket Cutter", 58, 11, 7, 39, 9, 6,
            BowStyle.Spoon, SternStyle.Canoe,
            new BowShapeSettings(0.40, 0.25, 27),
            new BodyShapeSettings(BodyStyle.Custom, 0.55, 0.10, -0.80, 0.16),
            new SternShapeSettings(0.10, -0.15, 40),
            new HullProfileSettings(2, 1, 0, 1), new HullProfileSettings(2, 1, 0, 1),
            false, new BulbSettings(8, 35, 0, 0), "Vertical"),
        new("03", "Shoal-Water Assault Gunboat", 48, 13, 5, 31, 9, 4,
            BowStyle.Raked, SternStyle.Square,
            new BowShapeSettings(0.35, 0.05, 22),
            new BodyShapeSettings(BodyStyle.Custom, 0.65, 0.00, 0.65, 0.72),
            new SternShapeSettings(0.75, 0.00, 12),
            new HullProfileSettings(0, 0, 0, 0), new HullProfileSettings(0, 0, 0, 0),
            false, new BulbSettings(8, 35, 0, 0), "None"),
        new("04", "Greyhound Torpedo Destroyer", 126, 13, 9, 81, 11, 7,
            BowStyle.Spoon, SternStyle.Canoe,
            new BowShapeSettings(-0.70, 0.50, 70),
            new BodyShapeSettings(BodyStyle.Custom, -0.20, 0.10, -0.45, 0.02),
            new SternShapeSettings(-0.55, 0.00, 25),
            new HullProfileSettings(2, 0, 0, 2), new HullProfileSettings(2, 0, 0, 2),
            false, new BulbSettings(8, 35, 0, 0), "Horizontal"),
        new("05", "High-Forecastle Ocean Destroyer", 142, 17, 11, 91, 13, 8,
            BowStyle.Raked, SternStyle.Counter,
            new BowShapeSettings(0.65, 1.05, 36),
            new BodyShapeSettings(BodyStyle.Custom, 0.35, 0.45, -0.65, 0.18),
            new SternShapeSettings(0.15, 0.10, 26),
            new HullProfileSettings(5, 0, 1, 1), new HullProfileSettings(4, 0, 1, 1),
            false, new BulbSettings(8, 35, 0, 0), "Vertical"),
        new("06", "Ram-Bow Armored Leader", 132, 19, 10, 89, 13, 8,
            BowStyle.Axe, SternStyle.Transom,
            new BowShapeSettings(0.90, 0.25, 24),
            new BodyShapeSettings(BodyStyle.Custom, 0.35, -0.18, 0.90, 0.28),
            new SternShapeSettings(0.55, -0.05, 20),
            new HullProfileSettings(1, 0, 0, 1), new HullProfileSettings(1, 0, 0, 1),
            false, new BulbSettings(8, 35, 0, 0), "None"),
        new("07", "Fleet Torpedo Cruiser", 174, 23, 12, 115, 17, 9,
            BowStyle.Pointed, SternStyle.Cruiser,
            new BowShapeSettings(0.75, 0.35, 24),
            new BodyShapeSettings(BodyStyle.Custom, 0.48, 0.20, -0.40, 0.36),
            new SternShapeSettings(-0.65, 0.10, 49),
            new HullProfileSettings(1, 3, 10, 4), new HullProfileSettings(1, 2, 7, 3),
            false, new BulbSettings(8, 35, 0, 0), "Horizontal"),
        new("08", "Atlantic Commerce Raider", 214, 23, 13, 145, 17, 10,
            BowStyle.Spoon, SternStyle.Counter,
            new BowShapeSettings(-0.85, 0.10, 72),
            new BodyShapeSettings(BodyStyle.Custom, 0.25, 0.00, -0.75, 0.12),
            new SternShapeSettings(-0.10, -0.25, 16),
            new HullProfileSettings(0, 1, 1, 3), new HullProfileSettings(0, 1, 1, 2),
            false, new BulbSettings(8, 35, 0, 0), "Horizontal"),
        new("09", "Northern Ocean Cruiser", 194, 25, 15, 125, 19, 11,
            BowStyle.Clipper, SternStyle.Cruiser,
            new BowShapeSettings(0.90, 1.20, 36),
            new BodyShapeSettings(BodyStyle.Custom, 0.00, 0.55, -0.35, 0.08),
            new SternShapeSettings(0.20, 0.35, 30),
            new HullProfileSettings(6, 2, 2, 2), new HullProfileSettings(4, 1, 1, 1),
            false, new BulbSettings(8, 35, 0, 0), "Vertical"),
        new("10", "Treaty-Breaker Large Cruiser", 228, 29, 15, 151, 21, 11,
            BowStyle.Spoon, SternStyle.Transom,
            new BowShapeSettings(0.25, 0.25, 36),
            new BodyShapeSettings(BodyStyle.Custom, 0.85, 0.05, -0.65, 0.32),
            new SternShapeSettings(0.80, 0.15, 20),
            new HullProfileSettings(2, 0, 1, 1), new HullProfileSettings(1, 0, 1, 1),
            false, new BulbSettings(8, 35, 0, 0), "Vertical"),
        new("11", "Long-Run Fleet Battlecruiser", 282, 31, 17, 181, 23, 13,
            BowStyle.Spoon, SternStyle.Cruiser,
            new BowShapeSettings(-0.55, 0.55, 58),
            new BodyShapeSettings(BodyStyle.Custom, 0.25, 0.20, -0.55, 0.18),
            new SternShapeSettings(-0.50, 0.05, 40),
            new HullProfileSettings(3, 1, 2, 4), new HullProfileSettings(2, 1, 2, 3),
            false, new BulbSettings(8, 35, 0, 0), "Horizontal"),
        new("12", "Ocean Fast Battleship", 272, 35, 18, 181, 25, 13,
            BowStyle.Spoon, SternStyle.Transom,
            new BowShapeSettings(-0.80, 0.65, 62),
            new BodyShapeSettings(BodyStyle.Custom, 0.70, 0.10, -0.70, 0.30),
            new SternShapeSettings(0.35, 0.10, 20),
            new HullProfileSettings(4, 0, 1, 2), new HullProfileSettings(3, 0, 1, 1),
            false, new BulbSettings(8, 35, 0, 0), "Horizontal"),
        new("13", "Pacific Super-Battleship", 302, 45, 21, 199, 31, 15,
            BowStyle.Spoon, SternStyle.Cruiser,
            new BowShapeSettings(0.55, 0.45, 30),
            new BodyShapeSettings(BodyStyle.Custom, 0.90, 0.05, -0.80, 0.58),
            new SternShapeSettings(0.60, -0.05, 30),
            new HullProfileSettings(4, 1, 1, 2), new HullProfileSettings(3, 1, 1, 1),
            true, new BulbSettings(12, 45, -4, 0), "Vertical"),
        new("14", "Round-Bilge Dreadnought", 188, 33, 16, 121, 25, 12,
            BowStyle.Raked, SternStyle.Cruiser,
            new BowShapeSettings(0.80, 0.05, 24),
            new BodyShapeSettings(BodyStyle.Custom, 0.80, -0.12, -0.85, 0.40),
            new SternShapeSettings(0.30, -0.20, 31),
            new HullProfileSettings(1, 1, 0, 1), new HullProfileSettings(1, 1, 0, 1),
            false, new BulbSettings(8, 35, 0, 0), "Vertical"),
        new("15", "Tumblehome Citadel Ship", 168, 29, 15, 111, 21, 11,
            BowStyle.Spoon, SternStyle.Fantail,
            new BowShapeSettings(0.05, -0.40, 38),
            new BodyShapeSettings(BodyStyle.Custom, 0.75, -1.05, -0.55, 0.22),
            new SternShapeSettings(0.10, -0.70, 30),
            new HullProfileSettings(2, 2, 0, 1), new HullProfileSettings(1, 1, 0, 1),
            false, new BulbSettings(8, 35, 0, 0), "Vertical"),
        new("16", "Coastal Siege Dreadnought", 156, 39, 10, 99, 27, 8,
            BowStyle.Blunt, SternStyle.Square,
            new BowShapeSettings(-0.50, 0.25, 37),
            new BodyShapeSettings(BodyStyle.Custom, 0.90, 0.05, 0.20, 0.85),
            new SternShapeSettings(0.40, 0.10, 18),
            new HullProfileSettings(0, 0, 0, 0), new HullProfileSettings(0, 0, 0, 0),
            false, new BulbSettings(8, 35, 0, 0), "None"),
    ];

    public static SketchbookCandidate Find(string id) =>
        All.Single(candidate => candidate.Id == id);

    /// <summary>
    /// The complete initialiser mapping handed to Team B. It overwrites every field the
    /// candidate owns: dimensions, bow/stern style, the whole Shape V2 bundle with a Custom
    /// body, all four profile rises, the legacy scalar aliases, flat bottom, beamification,
    /// base smoothing, and the complete explicit bulb state.
    /// </summary>
    /// <param name="candidate">The fixed candidate to apply.</param>
    /// <param name="basis">Any existing parameter set; every owned field is overwritten.</param>
    /// <param name="currentHeight">The midship height of the size to apply (suggested or minimum).</param>
    public static HullParameters ApplyTo(
        SketchbookCandidate candidate, HullParameters basis, int currentHeight)
    {
        var size = candidate.SuggestedHeight == currentHeight
            ? candidate.SuggestedSize
            : candidate.MinimumHeight == currentHeight
                ? candidate.MinimumSize
                : throw new ArgumentOutOfRangeException(
                    nameof(currentHeight), currentHeight,
                    $"Candidate {candidate.Id} has no declared size with midship height {currentHeight}.");
        return Apply(candidate, basis, size);
    }

    public static HullParameters ApplyTo(
        SketchbookCandidate candidate, HullParameters basis, SketchbookSizeKind kind) =>
        Apply(candidate, basis, candidate.Size(kind));

    public static HullParameters ParametersFor(SketchbookCandidate candidate, SketchbookSizeKind kind) =>
        ApplyTo(candidate, HullParameters.Default, kind);

    private static HullParameters Apply(
        SketchbookCandidate candidate, HullParameters basis, SketchbookSize size)
    {
        var shape = new HullShapeSettings(
            candidate.Bow,
            candidate.Body,
            candidate.Stern,
            size.AppliedProfile);
        return basis with
        {
            Length = size.Length,
            Width = size.Width,
            Height = size.Height,
            BowStyle = candidate.BowStyle,
            SternStyle = candidate.SternStyle,
            Shape = shape,
            BowFullness = shape.Bow.Fullness,
            SternFullness = shape.Stern.Fullness,
            CrossSectionCurve = shape.Body.Fullness,
            HasBulb = candidate.HasBulb,
            Bulb = candidate.Bulb,
            Beamify = true,
            Smoothing = SmoothingMethod.None,
        };
    }

    /// <summary>
    /// Canonical, order-independent definitions text. Changing any fixed candidate input
    /// changes <see cref="DefinitionsSha256"/> and invalidates the committed fixture.
    /// </summary>
    public static string DefinitionsCanonical() =>
        string.Join("\n", All.Select(CanonicalDefinition));

    public static string CanonicalDefinition(SketchbookCandidate candidate) =>
        $"{candidate.Id}|{candidate.Name}|" +
        $"{candidate.SuggestedLength}x{candidate.SuggestedWidth}x{candidate.SuggestedHeight}|" +
        $"{candidate.MinimumLength}x{candidate.MinimumWidth}x{candidate.MinimumHeight}|" +
        $"{candidate.BowStyle}|{candidate.SternStyle}|" +
        $"bow({candidate.Bow.Fullness:R},{candidate.Bow.Flare:R},{candidate.Bow.EntranceLengthPercent})|" +
        $"body({candidate.Body.Style},{candidate.Body.Fullness:R},{candidate.Body.SideShape:R}," +
        $"{candidate.Body.Chine:R},{candidate.Body.FlatBottom:R})|" +
        $"stern({candidate.Stern.Fullness:R},{candidate.Stern.SideShape:R},{candidate.Stern.RunLengthPercent})|" +
        $"profile({candidate.BaselineProfile.BowDeckRise},{candidate.BaselineProfile.SternDeckRise}," +
        $"{candidate.BaselineProfile.BowKeelRise},{candidate.BaselineProfile.SternKeelRise})|" +
        $"expectedMinProfile({candidate.ExpectedMinimumProfile.BowDeckRise}," +
        $"{candidate.ExpectedMinimumProfile.SternDeckRise}," +
        $"{candidate.ExpectedMinimumProfile.BowKeelRise},{candidate.ExpectedMinimumProfile.SternKeelRise})|" +
        $"bulb({candidate.HasBulb},{candidate.Bulb.LengthPercent},{candidate.Bulb.WidthPercent}," +
        $"{candidate.Bulb.ForeAftPercent},{candidate.Bulb.RisePercent})|" +
        candidate.Recommended;

    public static string DefinitionsSha256() => Sha256(DefinitionsCanonical());

    /// <summary>
    /// Asserts the computed minimum rises equal the frozen expected tuples. This is the
    /// mapping check that fails if the scaling formula or the fixed table drifts.
    /// </summary>
    public static void ValidateMapping()
    {
        var duplicate = All.GroupBy(candidate => candidate.Id).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"Duplicate sketchbook candidate id '{duplicate.Key}'.");
        if (All.Count != 16)
            throw new InvalidOperationException($"The sketchbook table must hold exactly 16 candidates, not {All.Count}.");

        foreach (var candidate in All)
        {
            var suggested = candidate.SuggestedSize;
            if (suggested.AppliedProfile != candidate.BaselineProfile)
                throw new InvalidOperationException(
                    $"Candidate {candidate.Id}: the suggested size must use the exact baseline rises.");
            var minimum = candidate.MinimumSize;
            if (minimum.AppliedProfile != candidate.ExpectedMinimumProfile)
                throw new InvalidOperationException(
                    $"Candidate {candidate.Id}: scaled minimum rises {minimum.AppliedProfile} do not match " +
                    $"the expected tuple {candidate.ExpectedMinimumProfile}.");
            if (candidate.HasBulb != (candidate.Id == "13"))
                throw new InvalidOperationException(
                    $"Candidate {candidate.Id}: only candidate 13 may enable the bulb.");
            if (candidate.Id == "13" && candidate.Bulb != new BulbSettings(12, 45, -4, 0))
                throw new InvalidOperationException("Candidate 13 must use the exact bulb (12, 45, -4, 0).");
            if (candidate.Id != "13" && candidate.Bulb != new BulbSettings(8, 35, 0, 0))
                throw new InvalidOperationException(
                    $"Candidate {candidate.Id}: a disabled bulb must still carry the explicit (8, 35, 0, 0).");
        }
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
