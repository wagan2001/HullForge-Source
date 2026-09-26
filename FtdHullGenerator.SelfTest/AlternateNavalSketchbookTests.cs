using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FtdHullGenerator;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Components;
using FtdHullGenerator.Domain.Design;
using FtdHullGenerator.Domain.Historical;
using FtdHullGenerator.Domain.Layout;
using FtdHullGenerator.Domain.Projects;
using FtdHullGenerator.Domain.Sketchbook;
using FtdHullGenerator.Domain.Superstructures;
using FtdHullGenerator.Geometry;
using FtdHullGenerator.Infrastructure;
using FtdHullGenerator.Serialization;
using FtdHullGenerator.Serialization.Projects;
using FtdHullGenerator.UI.Editor;
using static SlopeFillTestSupport;

/// <summary>
/// The Alternate Naval Sketchbook contract: the immutable sixteen-entry catalog, the deterministic
/// shape/size policies, the one-transaction editor apply, and the legacy catalogs that must not
/// move. Every check is written to fail against a leaky, scaling-accumulating or no-op
/// implementation.
/// </summary>
internal static class AlternateNavalSketchbookTests
{
    public static void Run(HullGenerator generator, FtdBlockCatalog catalog, bool full)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(catalog);

        VerifyExactBundleTable();
        VerifyCatalogIntegrity();
        VerifyLegacyCatalogsUntouched();
        VerifySuggestedDimensionsExactness();
        VerifyBulbStateAndLeakage();
        VerifyKeepCurrentDimensions();
        VerifyDatumParityContract();
        VerifyNoDriftAndImmutableBaseline();
        VerifyIndependentStatePreservation();
        VerifySmoothingRecommendationIsMetadataOnly();
        VerifyEditorTransactionUndoRedo();
        VerifyComponentsSurviveWithDiagnostics();
        VerifyPersistenceCompatibility();
        VerifyDistinctness();
        VerifyNativeSmoke(generator, catalog, full);

        Console.WriteLine(full
            ? "Alternate naval sketchbook: 16-entry exact bundle table, integrity, legacy catalogs, " +
              "suggested/keep policies, no-drift, independent state, one-revision undo/redo, " +
              "components/diagnostics, persistence and all 16 native generation/validation passed."
            : "Alternate naval sketchbook: 16-entry exact bundle table, integrity, legacy catalogs, " +
              "suggested/keep policies, no-drift, independent state, one-revision undo/redo, " +
              "components/diagnostics, persistence and representative native generation passed.");
    }

    // ---------------------------------------------------------------------------------------
    // 1. Exact bundle table.
    // ---------------------------------------------------------------------------------------

    private sealed record ExpectedBundle(
        string Id,
        int Ordinal,
        string Name,
        int Length,
        int Width,
        int Height,
        int MinimumLength,
        int MinimumWidth,
        int MinimumHeight,
        BowStyle Bow,
        SternStyle Stern,
        double BowFullness,
        double BowFlare,
        int Entrance,
        double BodyFullness,
        double BodySide,
        double BodyChine,
        double FlatBottom,
        double SternFullness,
        double SternSide,
        int Run,
        int BowDeckRise,
        int SternDeckRise,
        int BowKeelRise,
        int SternKeelRise,
        bool HasBulb,
        BulbSettings Bulb,
        SmoothingMethod Smoothing);

    // Written independently of the catalog so a transposed field or a silent retune fails here.
    private static readonly ExpectedBundle[] ExpectedBundles =
    [
        new("ans-01-channel-knife", 1, "Channel Knife Torpedo Boat",
            36, 9, 5, 25, 7, 4, BowStyle.Spoon, SternStyle.Transom,
            -0.20, 0.50, 45, -0.15, 0.10, 0.90, 0.05, 0.20, 0.05, 18, 1, 0, 0, 0,
            false, new BulbSettings(8, 35, 0, 0), SmoothingMethod.HorizontalSlopeFill),
        new("ans-02-offshore-picket", 2, "Offshore Picket Cutter",
            58, 11, 7, 39, 9, 6, BowStyle.Spoon, SternStyle.Canoe,
            0.40, 0.25, 27, 0.55, 0.10, -0.80, 0.16, 0.10, -0.15, 40, 2, 1, 0, 1,
            false, new BulbSettings(8, 35, 0, 0), SmoothingMethod.VerticalSlopeFill),
        new("ans-03-shoal-water-gunboat", 3, "Shoal-Water Assault Gunboat",
            48, 13, 5, 31, 9, 4, BowStyle.Raked, SternStyle.Square,
            0.35, 0.05, 22, 0.65, 0.00, 0.65, 0.72, 0.75, 0.00, 12, 0, 0, 0, 0,
            false, new BulbSettings(8, 35, 0, 0), SmoothingMethod.None),
        new("ans-04-greyhound", 4, "Greyhound Torpedo Destroyer",
            126, 13, 9, 81, 11, 7, BowStyle.Spoon, SternStyle.Canoe,
            -0.70, 0.50, 70, -0.20, 0.10, -0.45, 0.02, -0.55, 0.00, 25, 2, 0, 0, 2,
            false, new BulbSettings(8, 35, 0, 0), SmoothingMethod.HorizontalSlopeFill),
        new("ans-05-high-forecastle", 5, "High-Forecastle Ocean Destroyer",
            142, 17, 11, 91, 13, 8, BowStyle.Raked, SternStyle.Counter,
            0.65, 1.05, 36, 0.35, 0.45, -0.65, 0.18, 0.15, 0.10, 26, 5, 0, 1, 1,
            false, new BulbSettings(8, 35, 0, 0), SmoothingMethod.VerticalSlopeFill),
        new("ans-06-ram-bow-leader", 6, "Ram-Bow Armored Leader",
            132, 19, 10, 89, 13, 8, BowStyle.Axe, SternStyle.Transom,
            0.90, 0.25, 24, 0.35, -0.18, 0.90, 0.28, 0.55, -0.05, 20, 1, 0, 0, 1,
            false, new BulbSettings(8, 35, 0, 0), SmoothingMethod.None),
        new("ans-07-fleet-torpedo-cruiser", 7, "Fleet Torpedo Cruiser",
            174, 23, 12, 115, 17, 9, BowStyle.Pointed, SternStyle.Cruiser,
            0.75, 0.35, 24, 0.48, 0.20, -0.40, 0.36, -0.65, 0.10, 49, 1, 3, 10, 4,
            false, new BulbSettings(8, 35, 0, 0), SmoothingMethod.HorizontalSlopeFill),
        new("ans-08-atlantic-raider", 8, "Atlantic Commerce Raider",
            214, 23, 13, 145, 17, 10, BowStyle.Spoon, SternStyle.Counter,
            -0.85, 0.10, 72, 0.25, 0.00, -0.75, 0.12, -0.10, -0.25, 16, 0, 1, 1, 3,
            false, new BulbSettings(8, 35, 0, 0), SmoothingMethod.HorizontalSlopeFill),
        new("ans-09-northern-cruiser", 9, "Northern Ocean Cruiser",
            194, 25, 15, 125, 19, 11, BowStyle.Clipper, SternStyle.Cruiser,
            0.90, 1.20, 36, 0.00, 0.55, -0.35, 0.08, 0.20, 0.35, 30, 6, 2, 2, 2,
            false, new BulbSettings(8, 35, 0, 0), SmoothingMethod.VerticalSlopeFill),
        new("ans-10-treaty-breaker", 10, "Treaty-Breaker Large Cruiser",
            228, 29, 15, 151, 21, 11, BowStyle.Spoon, SternStyle.Transom,
            0.25, 0.25, 36, 0.85, 0.05, -0.65, 0.32, 0.80, 0.15, 20, 2, 0, 1, 1,
            false, new BulbSettings(8, 35, 0, 0), SmoothingMethod.VerticalSlopeFill),
        new("ans-11-long-run-battlecruiser", 11, "Long-Run Fleet Battlecruiser",
            282, 31, 17, 181, 23, 13, BowStyle.Spoon, SternStyle.Cruiser,
            -0.55, 0.55, 58, 0.25, 0.20, -0.55, 0.18, -0.50, 0.05, 40, 3, 1, 2, 4,
            false, new BulbSettings(8, 35, 0, 0), SmoothingMethod.HorizontalSlopeFill),
        new("ans-12-fast-battleship", 12, "Ocean Fast Battleship",
            272, 35, 18, 181, 25, 13, BowStyle.Spoon, SternStyle.Transom,
            -0.80, 0.65, 62, 0.70, 0.10, -0.70, 0.30, 0.35, 0.10, 20, 4, 0, 1, 2,
            false, new BulbSettings(8, 35, 0, 0), SmoothingMethod.HorizontalSlopeFill),
        new("ans-13-pacific-super-battleship", 13, "Pacific Super-Battleship",
            302, 45, 21, 199, 31, 15, BowStyle.Spoon, SternStyle.Cruiser,
            0.55, 0.45, 30, 0.90, 0.05, -0.80, 0.58, 0.60, -0.05, 30, 4, 1, 1, 2,
            true, new BulbSettings(12, 45, -4, 0), SmoothingMethod.VerticalSlopeFill),
        new("ans-14-round-bilge-dreadnought", 14, "Round-Bilge Dreadnought",
            188, 33, 16, 121, 25, 12, BowStyle.Raked, SternStyle.Cruiser,
            0.80, 0.05, 24, 0.80, -0.12, -0.85, 0.40, 0.30, -0.20, 31, 1, 1, 0, 1,
            false, new BulbSettings(8, 35, 0, 0), SmoothingMethod.VerticalSlopeFill),
        new("ans-15-tumblehome-citadel", 15, "Tumblehome Citadel Ship",
            168, 29, 15, 111, 21, 11, BowStyle.Spoon, SternStyle.Fantail,
            0.05, -0.40, 38, 0.75, -1.05, -0.55, 0.22, 0.10, -0.70, 30, 2, 2, 0, 1,
            false, new BulbSettings(8, 35, 0, 0), SmoothingMethod.VerticalSlopeFill),
        new("ans-16-coastal-siege-dreadnought", 16, "Coastal Siege Dreadnought",
            156, 39, 10, 99, 27, 8, BowStyle.Blunt, SternStyle.Square,
            -0.50, 0.25, 37, 0.90, 0.05, 0.20, 0.85, 0.40, 0.10, 18, 0, 0, 0, 0,
            false, new BulbSettings(8, 35, 0, 0), SmoothingMethod.None),
    ];

    private static void VerifyExactBundleTable()
    {
        Require(AlternateNavalSketchbookCatalog.All.Count == 16,
            $"The sketchbook holds {AlternateNavalSketchbookCatalog.All.Count} entries instead of 16.");
        Require(ExpectedBundles.Length == 16, "The independent expected table must hold 16 rows.");

        for (var index = 0; index < ExpectedBundles.Length; index++)
        {
            var expected = ExpectedBundles[index];
            var entry = AlternateNavalSketchbookCatalog.All[index];
            var shape = entry.Shape;
            var where = $"row {index} ({expected.Id})";

            Require(entry.Id == expected.Id && entry.Ordinal == expected.Ordinal && entry.Name == expected.Name,
                $"{where}: identity mismatch, found '{entry.Id}' #{entry.Ordinal} '{entry.Name}'.");
            Require(entry.SuggestedLength == expected.Length && entry.SuggestedWidth == expected.Width &&
                    entry.SuggestedHeight == expected.Height,
                $"{where}: suggested envelope mismatch, found " +
                $"{entry.SuggestedLength}x{entry.SuggestedWidth}x{entry.SuggestedHeight}.");
            Require(entry.MinimumLength == expected.MinimumLength && entry.MinimumWidth == expected.MinimumWidth &&
                    entry.MinimumHeight == expected.MinimumHeight,
                $"{where}: minimum envelope mismatch, found " +
                $"{entry.MinimumLength}x{entry.MinimumWidth}x{entry.MinimumHeight}.");
            Require(entry.BowStyle == expected.Bow && entry.SternStyle == expected.Stern,
                $"{where}: bow/stern style mismatch, found {entry.BowStyle}/{entry.SternStyle}.");
            Require(shape.Bow.Fullness == expected.BowFullness && shape.Bow.Flare == expected.BowFlare &&
                    shape.Bow.EntranceLengthPercent == expected.Entrance,
                $"{where}: bow shape mismatch, found " +
                $"({shape.Bow.Fullness},{shape.Bow.Flare},{shape.Bow.EntranceLengthPercent}).");
            Require(shape.Body.Fullness == expected.BodyFullness && shape.Body.SideShape == expected.BodySide &&
                    shape.Body.Chine == expected.BodyChine && shape.Body.FlatBottom == expected.FlatBottom,
                $"{where}: body shape mismatch, found " +
                $"({shape.Body.Fullness},{shape.Body.SideShape},{shape.Body.Chine},{shape.Body.FlatBottom}).");
            Require(shape.Stern.Fullness == expected.SternFullness && shape.Stern.SideShape == expected.SternSide &&
                    shape.Stern.RunLengthPercent == expected.Run,
                $"{where}: stern shape mismatch, found " +
                $"({shape.Stern.Fullness},{shape.Stern.SideShape},{shape.Stern.RunLengthPercent}).");
            Require(shape.Profile.BowDeckRise == expected.BowDeckRise &&
                    shape.Profile.SternDeckRise == expected.SternDeckRise &&
                    shape.Profile.BowKeelRise == expected.BowKeelRise &&
                    shape.Profile.SternKeelRise == expected.SternKeelRise,
                $"{where}: profile mismatch, found " +
                $"({shape.Profile.BowDeckRise},{shape.Profile.SternDeckRise}," +
                $"{shape.Profile.BowKeelRise},{shape.Profile.SternKeelRise}).");
            Require(entry.HasBulb == expected.HasBulb && entry.Bulb == expected.Bulb,
                $"{where}: bulb mismatch, found hasBulb={entry.HasBulb} bulb={entry.Bulb}.");
            Require(entry.RecommendedSmoothing == expected.Smoothing,
                $"{where}: smoothing recommendation mismatch, found {entry.RecommendedSmoothing}.");
            Require(entry.Shape.Body.Style == BodyStyle.Custom,
                $"{where}: body style must be Custom.");
            Require(entry.EnvelopeSummary ==
                    $"{expected.Length} × {expected.Width} × {expected.Height} m" &&
                    entry.MinimumSummary ==
                    $"{expected.MinimumLength} × {expected.MinimumWidth} × {expected.MinimumHeight} m",
                $"{where}: envelope summaries do not match the dimensions.");
        }
    }

    // ---------------------------------------------------------------------------------------
    // 2. Catalog integrity and identity.
    // ---------------------------------------------------------------------------------------

    private static void VerifyCatalogIntegrity()
    {
        var errors = AlternateNavalSketchbookCatalog.Validate();
        Require(errors.Count == 0,
            "The sketchbook failed its own integrity contract: " + string.Join("; ", errors));
        Require(AlternateNavalSketchbookCatalog.CatalogVersion == "ans-1",
            $"Catalog version is '{AlternateNavalSketchbookCatalog.CatalogVersion}', not 'ans-1'.");
        Require(AlternateNavalSketchbookCatalog.ProvenanceNote.Contains("not proven", StringComparison.Ordinal),
            "The provenance note no longer records that the design candidate is unproven.");

        var ids = AlternateNavalSketchbookCatalog.All.Select(entry => entry.Id).ToArray();
        Require(ids.Distinct(StringComparer.Ordinal).Count() == ids.Length,
            "The sketchbook contains duplicate ids.");
        Require(ids.All(id => id.Length > 0 && id.All(character =>
                (character >= 'a' && character <= 'z') || (character >= '0' && character <= '9') || character == '-')),
            "A sketchbook id is not a lowercase [a-z0-9-] token.");
        Require(AlternateNavalSketchbookCatalog.All.Select(entry => entry.Ordinal).SequenceEqual(Enumerable.Range(1, 16)),
            "The sketchbook ordinals are not 1 through 16 in catalog order.");

        var historicalIds = HistoricalPresetCatalog.All.Select(preset => preset.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in ids)
            Require(!historicalIds.Contains(id), $"Sketchbook id '{id}' collides with a historical preset id.");
        Require(AlternateNavalSketchbookCatalog.All.All(entry => entry.Shape.Body.Style == BodyStyle.Custom),
            "A sketchbook entry does not declare BodyStyle.Custom.");
        Require(AlternateNavalSketchbookCatalog.Find("ans-09-northern-cruiser")?.Ordinal == 9 &&
                AlternateNavalSketchbookCatalog.Find("does-not-exist") is null,
            "Sketchbook lookup by id is not ordinal and exact.");
    }

    // ---------------------------------------------------------------------------------------
    // 3. Legacy catalogs untouched.
    // ---------------------------------------------------------------------------------------

    private static readonly string[] FrozenHistoricalIds =
    [
        "us-elco-pt", "us-fletcher-dd", "us-cleveland-cl", "us-baltimore-ca", "us-lexington-cc",
        "us-iowa-bb", "us-south-carolina-bb",
        "fr-bougainville-aviso", "fr-elan-aviso", "fr-le-fantasque-dd", "fr-la-galissonniere-cl",
        "fr-suffren-ca", "fr-richelieu-bb", "fr-courbet-bb",
        "it-spica-tb", "it-soldati-dd", "it-condottieri-cl", "it-zara-ca", "it-littorio-bb",
        "it-dante-alighieri-bb",
        "de-s-boat", "de-type-1936-dd", "de-konigsberg-cl", "de-admiral-hipper-ca", "de-o-class-cc",
        "de-bismarck-bb", "de-nassau-bb",
        "ru-mo-class", "ru-g5-mtb", "ru-gnevny-dd", "ru-kirov-cruiser", "ru-kronshtadt-cc",
        "ru-sovetsky-soyuz-bb", "ru-gangut-bb",
    ];

    private static readonly string[] FrozenAnimalNames =
    [
        "Dolphin", "Marlin", "Orca", "Manta", "Shark", "Whale", "Barracuda", "Tuna",
        "Ray", "Swordfish", "Seal", "Narwhal", "Hammerhead", "Sailfish", "Pike", "Sturgeon",
    ];

    // Fingerprint field order per historical entry, joined by '|' with rows separated by '\n':
    //   Id | Length | Width | Height | BowStyle | SternStyle |
    //   Bow.Fullness | Bow.Flare | Bow.EntranceLengthPercent |
    //   Body.Style | Body.Fullness | Body.SideShape | Body.Chine | Body.FlatBottom |
    //   Stern.Fullness | Stern.SideShape | Stern.RunLengthPercent |
    //   Profile.BowDeckRise | Profile.SternDeckRise | Profile.BowKeelRise | Profile.SternKeelRise
    // Doubles use the invariant round-trip format; the whole string is SHA-256 hashed to
    // uppercase hexadecimal. The value below is the frozen oracle for the 34 existing entries.
    private const string HistoricalFingerprintValue =
        "0CBD38E8811FB29C7A4235A3E6CEB517C622313A1E35A882CDFBB0A5FEED60CE";

    private static void VerifyLegacyCatalogsUntouched()
    {
        Require(HistoricalPresetCatalog.All.Count == 34,
            $"The historical catalog holds {HistoricalPresetCatalog.All.Count} entries instead of 34.");
        Require(HistoricalPresetCatalog.All.Select(preset => preset.Id).SequenceEqual(FrozenHistoricalIds),
            "The historical catalog ids changed or reordered.");
        Require(HullShapePreset.All.Count == 16,
            $"The animal roster holds {HullShapePreset.All.Count} presets instead of 16.");
        Require(HullShapePreset.All.Select(preset => preset.Name).SequenceEqual(FrozenAnimalNames),
            "The animal preset names changed or reordered.");

        var fingerprint = HistoricalFingerprint();
        Require(fingerprint == HistoricalFingerprintValue,
            $"The historical numeric fingerprint changed: computed {fingerprint}, pinned {HistoricalFingerprintValue}.");
    }

    private static string HistoricalFingerprint()
    {
        var builder = new StringBuilder();
        foreach (var preset in HistoricalPresetCatalog.All)
        {
            var shape = preset.Shape;
            builder.Append(preset.Id).Append('|')
                .Append(preset.Length).Append('|')
                .Append(preset.Width).Append('|')
                .Append(preset.Height).Append('|')
                .Append(preset.BowStyle).Append('|')
                .Append(preset.SternStyle).Append('|')
                .Append(Invariant(shape.Bow.Fullness)).Append('|')
                .Append(Invariant(shape.Bow.Flare)).Append('|')
                .Append(shape.Bow.EntranceLengthPercent).Append('|')
                .Append(shape.Body.Style).Append('|')
                .Append(Invariant(shape.Body.Fullness)).Append('|')
                .Append(Invariant(shape.Body.SideShape)).Append('|')
                .Append(Invariant(shape.Body.Chine)).Append('|')
                .Append(Invariant(shape.Body.FlatBottom)).Append('|')
                .Append(Invariant(shape.Stern.Fullness)).Append('|')
                .Append(Invariant(shape.Stern.SideShape)).Append('|')
                .Append(shape.Stern.RunLengthPercent).Append('|')
                .Append(shape.Profile.BowDeckRise).Append('|')
                .Append(shape.Profile.SternDeckRise).Append('|')
                .Append(shape.Profile.BowKeelRise).Append('|')
                .Append(shape.Profile.SternKeelRise)
                .Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static string Invariant(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------------------------------
    // 4. Suggested Dimensions exactness.
    // ---------------------------------------------------------------------------------------

    private static void VerifySuggestedDimensionsExactness()
    {
        var basis = ShipDocument.CreateNew("ANS suggested basis", ForeignBasis());
        foreach (var id in new[] { "ans-01-channel-knife", "ans-08-atlantic-raider", "ans-13-pacific-super-battleship" })
        {
            var entry = RequireEntry(id);
            var application = SketchbookShapeInitializer.Apply(entry, SketchbookSizePolicy.SuggestedDimensions, basis);
            var hull = application.Document.Hull;

            Require(hull.Length == entry.SuggestedLength && hull.Width == entry.SuggestedWidth &&
                    hull.Height == entry.SuggestedHeight,
                $"{id}: suggested dimensions were not applied exactly, found {hull.Length}x{hull.Width}x{hull.Height}.");
            Require(hull.Shape == entry.Shape && hull.EffectiveShape == entry.Shape,
                $"{id}: the exact baseline Shape V2 bundle was not applied.");
            Require(hull.BowFullness == entry.Shape.Bow.Fullness &&
                    hull.SternFullness == entry.Shape.Stern.Fullness &&
                    hull.CrossSectionCurve == entry.Shape.Body.Fullness,
                $"{id}: the legacy shape aliases did not track the Shape V2 bundle.");
            Require(hull.EffectiveShape.Body.Style == BodyStyle.Custom,
                $"{id}: the applied body style is not Custom.");
            Require(hull.HasBulb == entry.HasBulb && hull.Bulb == entry.Bulb,
                $"{id}: the explicit bulb state was not applied exactly.");
            Require(hull.BowStyle == entry.BowStyle && hull.SternStyle == entry.SternStyle,
                $"{id}: the bow/stern style was not applied.");
        }
    }

    private static HullParameters ForeignBasis() => HullEditorSettings.Default with
    {
        Length = 77,
        Width = 21,
        Height = 12,
        BowStyle = BowStyle.Clipper,
        SternStyle = SternStyle.Fantail,
        BowFullness = 0.33,
        SternFullness = -0.44,
        CrossSectionCurve = 0.11,
        HasBulb = true,
        Bulb = new BulbSettings(30, 65, -2, 0),
        Shape = new HullShapeSettings(
            new BowShapeSettings(0.33, -0.5, 60),
            new BodyShapeSettings(BodyStyle.Custom, 0.11, 0.2, 0.3, 0.4),
            new SternShapeSettings(-0.44, 0.5, 55),
            new HullProfileSettings(1, 2, 3, 0)),
    };

    // ---------------------------------------------------------------------------------------
    // 5. Disabled bulb reset and leakage.
    // ---------------------------------------------------------------------------------------

    private static void VerifyBulbStateAndLeakage()
    {
        var foreign = ShipDocument.CreateNew("ANS bulb basis", ForeignBasis());

        var after01 = SketchbookShapeInitializer.Apply(
            RequireEntry("ans-01-channel-knife"), SketchbookSizePolicy.SuggestedDimensions, foreign).Document.Hull;
        Require(!after01.HasBulb && after01.Bulb == new BulbSettings(8, 35, 0, 0),
            $"Entry 01 left hasBulb={after01.HasBulb} bulb={after01.Bulb} instead of an explicit disabled default.");

        var after13 = SketchbookShapeInitializer.Apply(
            RequireEntry("ans-13-pacific-super-battleship"), SketchbookSizePolicy.SuggestedDimensions, foreign)
            .Document.Hull;
        Require(after13.HasBulb && after13.Bulb == new BulbSettings(12, 45, -4, 0),
            $"Entry 13 left hasBulb={after13.HasBulb} bulb={after13.Bulb} instead of the exact enabled bulb.");

        var liveBulbBasis = foreign with
        {
            Hull = foreign.Hull with { HasBulb = after13.HasBulb, Bulb = after13.Bulb },
        };
        var after02 = SketchbookShapeInitializer.Apply(
            RequireEntry("ans-02-offshore-picket"), SketchbookSizePolicy.SuggestedDimensions,
            liveBulbBasis).Document.Hull;
        Require(!after02.HasBulb && after02.Bulb == new BulbSettings(8, 35, 0, 0),
            $"Entry 02 did not reset a live bulb, leaving hasBulb={after02.HasBulb} bulb={after02.Bulb}.");

        var disabledBasis = foreign with { Hull = foreign.Hull with { HasBulb = false, Bulb = null } };
        var enabledFromDisabled = SketchbookShapeInitializer.Apply(
            RequireEntry("ans-13-pacific-super-battleship"), SketchbookSizePolicy.SuggestedDimensions,
            disabledBasis).Document.Hull;
        Require(enabledFromDisabled.HasBulb && enabledFromDisabled.Bulb == new BulbSettings(12, 45, -4, 0),
            "Entry 13 did not turn a disabled bulb on with its exact settings.");
    }

    // ---------------------------------------------------------------------------------------
    // 6. Keep Current Dimensions retention including an even beam.
    // ---------------------------------------------------------------------------------------

    private static void VerifyKeepCurrentDimensions()
    {
        var basis = ShipDocument.CreateNew("ANS keep basis", HullEditorSettings.Default with
        {
            Length = 100,
            Width = 18,
            Height = 8,
        });

        var entry05 = RequireEntry("ans-05-high-forecastle");
        var keep05 = SketchbookShapeInitializer.Apply(
            entry05, SketchbookSizePolicy.KeepCurrentDimensions, basis).Document.Hull;
        Require(keep05.Length == 100 && keep05.Width == 18 && keep05.Height == 8,
            $"Keeping the current dimensions changed them to {keep05.Length}x{keep05.Width}x{keep05.Height}.");
        Require(keep05.EffectiveShape.Profile == new HullProfileSettings(4, 0, 1, 1),
            $"Entry 05 at H=8 scaled to {keep05.EffectiveShape.Profile} instead of (4,0,1,1).");
        // Only the profile rises scale. The dimensionless controls and the bulb percentages are
        // copied verbatim from the entry, whatever the current size.
        Require(keep05.EffectiveShape.Bow == entry05.Shape.Bow &&
                keep05.EffectiveShape.Body == entry05.Shape.Body &&
                keep05.EffectiveShape.Stern == entry05.Shape.Stern,
            "Keeping the current dimensions scaled or rewrote a dimensionless Shape V2 control.");
        Require(keep05.HasBulb == entry05.HasBulb && keep05.Bulb == entry05.Bulb,
            "Keeping the current dimensions changed the explicit bulb state.");

        var keep05H6 = KeepProfile("ans-05-high-forecastle", 6);
        Require(keep05H6 == new HullProfileSettings(3, 0, 1, 1),
            $"Entry 05 at H=6 scaled to {keep05H6} instead of (3,0,1,1); AwayFromZero is not being used.");

        var keep09 = KeepProfile("ans-09-northern-cruiser", 8);
        Require(keep09 == new HullProfileSettings(3, 1, 1, 1),
            $"Entry 09 at H=8 scaled to {keep09} instead of (3,1,1,1).");

        var keep13 = KeepProfile("ans-13-pacific-super-battleship", 10);
        Require(keep13 == new HullProfileSettings(2, 0, 0, 1),
            $"Entry 13 at H=10 scaled to {keep13} instead of (2,0,0,1).");

        // No-clamp contract. Entry 07's baseline BowKeelRise is 10 m at a suggested height of 12 m,
        // so at a 5 m current height it scales to 4 m - above the validator's 3 m bound for that
        // height. The initializer must keep the unclamped value and report the finding. A clamp to
        // the bound (3) or a silent zeroing would pass validation and is rejected here.
        var noClampBasis = ShipDocument.CreateNew("ANS no clamp", HullEditorSettings.Default with
        {
            Length = 100,
            Width = 18,
            Height = 5,
        });
        var noClamp = SketchbookShapeInitializer.Apply(
            RequireEntry("ans-07-fleet-torpedo-cruiser"), SketchbookSizePolicy.KeepCurrentDimensions, noClampBasis);
        Require(noClamp.Document.Hull.EffectiveShape.Profile == new HullProfileSettings(0, 1, 4, 1),
            $"Entry 07 at H=5 scaled to {noClamp.Document.Hull.EffectiveShape.Profile} instead of the " +
            "unclamped (0,1,4,1); an out-of-range rise was clamped or disabled.");
        Require(noClamp.HasErrors && noClamp.Diagnostics.Any(diagnostic =>
                diagnostic.Code == DesignDiagnosticCodes.HullParametersInvalid &&
                diagnostic.Message.Contains("Bow keel rise", StringComparison.Ordinal)),
            "An out-of-range scaled rise was not reported as an honest DOC009 diagnostic.");

        Require(Throws(() => SketchbookShapeInitializer.ScaleRise(1, 1, 5)),
            "ScaleRise accepted a suggested height below 2 m.");
        Require(Throws(() => SketchbookShapeInitializer.ScaleRise(1, 11, 0)),
            "ScaleRise accepted a current height below 1 m.");
    }

    private static bool Throws(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return true;
        }
    }

    // The persisted centre plane follows real lattice parity: odd widths mirror about X=0 and
    // even widths about X=-0.5. The datum only changes when that plane actually moves.
    private static void VerifyDatumParityContract()
    {
        var evenBasis = ShipDocument.CreateNew("ANS datum even", HullEditorSettings.Default with
        {
            Length = 100,
            Width = 18,
            Height = 8,
        }) with { Datum = null };

        var evenKeep = SketchbookShapeInitializer.Apply(
            RequireEntry("ans-05-high-forecastle"), SketchbookSizePolicy.KeepCurrentDimensions, evenBasis).Document;
        Require(evenKeep.Hull.Width == 18 && evenKeep.Datum is not null &&
                evenKeep.Datum.CenterPlaneX == DesignMeasure.FromTwiceMetres(-1),
            "An even-width Keep apply did not persist the between-column centre plane.");

        var oddBasis = ShipDocument.CreateNew("ANS datum odd", HullEditorSettings.Default with
        {
            Length = 100,
            Width = 17,
            Height = 8,
        }) with { Datum = null };
        var oddKeep = SketchbookShapeInitializer.Apply(
            RequireEntry("ans-05-high-forecastle"), SketchbookSizePolicy.KeepCurrentDimensions, oddBasis).Document;
        Require(oddKeep.Hull.Width == 17 && oddKeep.Datum is null,
            "An odd-width Keep apply created a datum instead of preserving the null origin.");

        var oddFromEven = SketchbookShapeInitializer.Apply(
            RequireEntry("ans-05-high-forecastle"), SketchbookSizePolicy.SuggestedDimensions, evenKeep).Document;
        Require(oddFromEven.Hull.Width == 17 && oddFromEven.Datum is not null &&
                oddFromEven.Datum.CenterPlaneX == DesignMeasure.Zero,
            "An even-to-odd Suggested apply did not return the centre plane to zero.");
    }

    private static HullProfileSettings KeepProfile(string id, int height)
    {
        var basis = ShipDocument.CreateNew($"ANS keep {id} H{height}", HullEditorSettings.Default with
        {
            Length = 100,
            Width = 18,
            Height = height,
        });
        return SketchbookShapeInitializer.Apply(
            RequireEntry(id), SketchbookSizePolicy.KeepCurrentDimensions, basis).Document.Hull.EffectiveShape.Profile;
    }

    // ---------------------------------------------------------------------------------------
    // 7. No drift and an immutable baseline.
    // ---------------------------------------------------------------------------------------

    private static void VerifyNoDriftAndImmutableBaseline()
    {
        var entry05 = RequireEntry("ans-05-high-forecastle");
        var baselineShape = entry05.Shape;
        var baselineProfile = entry05.Shape.Profile;

        var basis = ShipDocument.CreateNew("ANS drift basis", HullEditorSettings.Default with
        {
            Length = 100,
            Width = 18,
            Height = 8,
        });

        var first = SketchbookShapeInitializer.Apply(
            entry05, SketchbookSizePolicy.KeepCurrentDimensions, basis).Document;
        var second = SketchbookShapeInitializer.Apply(
            entry05, SketchbookSizePolicy.KeepCurrentDimensions, first).Document;
        Require(second.Hull.EffectiveShape.Profile == new HullProfileSettings(4, 0, 1, 1) &&
                second.Hull.EffectiveShape.Profile == first.Hull.EffectiveShape.Profile,
            "A repeated Keep application drifted instead of recomputing from the immutable baseline.");

        var entry09 = RequireEntry("ans-09-northern-cruiser");
        var after09 = SketchbookShapeInitializer.Apply(
            entry09, SketchbookSizePolicy.KeepCurrentDimensions, basis).Document;
        var after05 = SketchbookShapeInitializer.Apply(
            entry05, SketchbookSizePolicy.KeepCurrentDimensions, after09).Document;
        Require(after05.Hull.EffectiveShape.Profile == new HullProfileSettings(4, 0, 1, 1),
            $"Entry 05 after entry 09 scaled to {after05.Hull.EffectiveShape.Profile} instead of (4,0,1,1).");

        Require(entry05.Shape == baselineShape && entry05.Shape.Profile == baselineProfile,
            "Applying an entry mutated the catalog entry's baseline shape.");
    }

    // ---------------------------------------------------------------------------------------
    // 8. Independent-state preservation.
    // ---------------------------------------------------------------------------------------

    private static void VerifyIndependentStatePreservation()
    {
        foreach (var deckArmor in new ArmorLayout?[] { null, ArmorLayout.Single(MaterialKind.Wood) })
        {
            var parameters = HullEditorSettings.Default with
            {
                Length = 100,
                Width = 18,
                Height = 8,
                HullArmor = new ArmorLayout([MaterialKind.Metal, MaterialKind.HeavyArmor, MaterialKind.Rubber]),
                DeckArmor = deckArmor,
                BottomArmor = ArmorLayout.Single(MaterialKind.Lead),
                Beamify = false,
                Smoothing = SmoothingMethod.HorizontalSlopeFill,
                HybridFillOffset = 3,
                Superstructure = new SuperstructureSettings(
                    true, SuperstructureStyle.FrenchHotel, 4, MaterialKind.LightweightAlloy,
                    SuperstructureSmoothingMethod.HorizontalSlopeFill, 12),
            };

            var barbette = BarbetteDefinition.Create("barbette-1", "barbette-1-node", DesignMeasure.FromMetres(3), 3);
            var node = ArrangementNode.Create("barbette-1-node", ArrangementNodeKind.Barbette, "barbette-1",
                DesignMeasure.FromMetres(2));
            var internals = new InternalStructure([
                InternalStructureFamily.Disabled(InternalPlaneFamily.LongitudinalBulkhead),
                InternalStructureFamily.Disabled(InternalPlaneFamily.InternalDeck) with { Enabled = true, Count = 2 },
                InternalStructureFamily.Disabled(InternalPlaneFamily.TransverseBulkhead),
            ]);
            var superstructure = new SuperstructureLayout(
                true,
                [new SuperstructureLayer("layer-1", 1,
                    [new SuperstructureBoxModule("module-1", DesignMeasure.Zero, DesignMeasure.Zero,
                        DesignMeasure.FromMetres(6), DesignMeasure.FromMetres(4), 3)])],
                [],
                LegacySuperstructureSettings.None);
            var smoothing = SmoothingSettings.FromParameters(parameters) with { AlgorithmVersion = 2 };
            var extensions = new DocumentExtensions(
                ImmutableDictionary<string, string>.Empty.Add("ans.test", "kept"));
            var appliedStyle = new AppliedStyleProvenance("style-1", 1, CopiedStyleFields.Everything, "Style One");

            var basis = ShipDocument.CreateNew("ANS independent", parameters, "ans-independent-doc", "hf-ans-test") with
            {
                Source = HullSource.Historical("asset-1", "1", "hash-1"),
                Arrangement = new Arrangement([node], [], [],
                    DesignMeasure.FromMetres(4), DesignMeasure.FromMetres(6),
                    ArrangementAnchorKind.SternDatum, ArrangementResizePolicy.ScaleWithLength),
                // The suggested entry is odd-width, so a zero centre plane is the parity-stable datum.
                Datum = new LayoutDatum(DesignMeasure.FromMetres(2), DesignMeasure.Zero),
                Internals = internals,
                Barbettes = [barbette],
                Superstructure = superstructure,
                Smoothing = smoothing,
                AppliedStyle = appliedStyle,
                Extensions = extensions,
            };

            var application = SketchbookShapeInitializer.Apply(
                RequireEntry("ans-05-high-forecastle"), SketchbookSizePolicy.SuggestedDimensions, basis);
            var applied = application.Document;
            var label = deckArmor is null ? "deckless" : "decked";

            Require(applied.Name == basis.Name && applied.DocumentId == basis.DocumentId &&
                    applied.GenerationVersion == basis.GenerationVersion && applied.Source == basis.Source,
                $"{label}: identity or source changed.");
            Require(ReferenceEquals(applied.Arrangement, basis.Arrangement) &&
                    applied.Arrangement == basis.Arrangement,
                $"{label}: the arrangement changed.");
            Require(applied.Datum == basis.Datum && ReferenceEquals(applied.Datum, basis.Datum),
                $"{label}: the parity-stable datum changed.");
            Require(ReferenceEquals(applied.Internals, basis.Internals) && applied.Internals == basis.Internals,
                $"{label}: the internals changed.");
            Require(applied.Barbettes == basis.Barbettes && applied.Barbettes.Length == 1,
                $"{label}: the barbettes changed.");
            Require(ReferenceEquals(applied.Superstructure, basis.Superstructure) &&
                    applied.Superstructure == basis.Superstructure,
                $"{label}: the superstructure layout changed.");
            Require(ReferenceEquals(applied.Smoothing, basis.Smoothing) && applied.Smoothing == basis.Smoothing,
                $"{label}: the smoothing settings changed.");
            Require(ReferenceEquals(applied.AppliedStyle, basis.AppliedStyle) && applied.AppliedStyle == basis.AppliedStyle,
                $"{label}: the applied-style provenance changed.");
            Require(ReferenceEquals(applied.Extensions, basis.Extensions) && applied.Extensions == basis.Extensions,
                $"{label}: the extensions changed.");

            var hull = applied.Hull;
            Require(hull.HullArmor.Equals(basis.Hull.HullArmor), $"{label}: hull armor changed.");
            Require(Equals(hull.DeckArmor, basis.Hull.DeckArmor), $"{label}: deck armor changed.");
            Require(Equals(hull.BottomArmor, basis.Hull.BottomArmor), $"{label}: bottom armor changed.");
            Require(hull.Beamify == basis.Hull.Beamify, $"{label}: beamify changed.");
            Require(hull.Smoothing == basis.Hull.Smoothing, $"{label}: smoothing method changed.");
            Require(hull.HybridFillOffset == basis.Hull.HybridFillOffset, $"{label}: hybrid offset changed.");
            Require(Equals(hull.Superstructure, basis.Hull.Superstructure), $"{label}: superstructure settings changed.");
            Require(hull.Smoothing == applied.Smoothing.NativeMethod,
                $"{label}: the smoothing intent diverged from the wrapped parameters.");
        }
    }

    // ---------------------------------------------------------------------------------------
    // 9. Smoothing recommendation is metadata only.
    // ---------------------------------------------------------------------------------------

    private static void VerifySmoothingRecommendationIsMetadataOnly()
    {
        var basis = ShipDocument.CreateNew("ANS smoothing metadata", HullEditorSettings.Default with
        {
            Length = 100,
            Width = 18,
            Height = 8,
            Smoothing = SmoothingMethod.None,
        });

        var entry01 = RequireEntry("ans-01-channel-knife");
        var entry09 = RequireEntry("ans-09-northern-cruiser");
        Require(entry01.RecommendedSmoothing == SmoothingMethod.HorizontalSlopeFill &&
                entry09.RecommendedSmoothing == SmoothingMethod.VerticalSlopeFill,
            "The comparison smoothing recommendations are no longer exposed as expected.");

        var applied01 = SketchbookShapeInitializer.Apply(
            entry01, SketchbookSizePolicy.SuggestedDimensions, basis).Document;
        Require(applied01.Hull.Smoothing == SmoothingMethod.None && applied01.Smoothing.NativeMethod == SmoothingMethod.None,
            "Entry 01's Horizontal recommendation leaked into the applied hull smoothing.");

        var applied09 = SketchbookShapeInitializer.Apply(
            entry09, SketchbookSizePolicy.SuggestedDimensions, basis).Document;
        Require(applied09.Hull.Smoothing == SmoothingMethod.None && applied09.Smoothing.NativeMethod == SmoothingMethod.None,
            "Entry 09's Vertical recommendation leaked into the applied hull smoothing.");

        // The recommendation is comparison metadata for every entry, not only the two spot checks.
        foreach (var entry in AlternateNavalSketchbookCatalog.All)
        {
            var applied = SketchbookShapeInitializer.Apply(
                entry, SketchbookSizePolicy.SuggestedDimensions, basis).Document;
            Require(applied.Hull.Smoothing == SmoothingMethod.None &&
                    applied.Smoothing.NativeMethod == SmoothingMethod.None,
                $"Entry '{entry.Id}' leaked its {entry.RecommendedSmoothing} recommendation into the applied smoothing.");
        }
    }

    // ---------------------------------------------------------------------------------------
    // 10. One revision, Undo/Redo, no derived history.
    // ---------------------------------------------------------------------------------------

    private static void VerifyEditorTransactionUndoRedo()
    {
        var basis = ShipDocument.CreateNew("ANS transaction", HullEditorSettings.Default);
        var session = new EditorSession(basis);
        Require(session.Revision == 1 && !session.CanUndo && !session.CanRedo,
            "A fresh editor session did not start at revision 1 with no history.");

        var entry05 = RequireEntry("ans-05-high-forecastle");
        var expected = SketchbookShapeInitializer.Apply(
            entry05, SketchbookSizePolicy.SuggestedDimensions, basis).Document;

        var application = SketchbookEditorContract.Apply(session, entry05, SketchbookSizePolicy.SuggestedDimensions);
        Require(session.Revision == 2, $"Applying one entry created revision {session.Revision}, not 2.");
        Require(session.Document == expected && application.Document == expected,
            "The committed document does not equal the resolved application.");

        Require(session.Undo() && session.Document == basis && !session.CanUndo && session.CanRedo,
            "One undo did not restore the basis as the only history item.");
        Require(session.Redo() && session.Document == expected,
            "Redo did not restore the applied document.");

        var revisionBeforeNoOp = session.Revision;
        SketchbookEditorContract.Apply(session, entry05, SketchbookSizePolicy.SuggestedDimensions);
        Require(session.Revision == revisionBeforeNoOp,
            "Re-applying the current entry created a new revision instead of being a no-op.");

        var revisionBeforeChange = session.Revision;
        SketchbookEditorContract.Apply(session, RequireEntry("ans-09-northern-cruiser"),
            SketchbookSizePolicy.SuggestedDimensions);
        Require(session.Revision == revisionBeforeChange + 1,
            "Applying a different entry did not create exactly one revision.");
    }

    // ---------------------------------------------------------------------------------------
    // 11. Components survive with diagnostics.
    // ---------------------------------------------------------------------------------------

    private static void VerifyComponentsSurviveWithDiagnostics()
    {
        var barbette = BarbetteDefinition.Create("barbette-1", "barbette-1-node", DesignMeasure.FromMetres(3), 3);
        var node = ArrangementNode.Create("barbette-1-node", ArrangementNodeKind.Barbette, "barbette-1",
            DesignMeasure.FromMetres(2));
        var basis = ShipDocument.CreateNew("ANS barbette", HullEditorSettings.Default with
        {
            Length = 100,
            Width = 18,
            Height = 8,
        }) with
        {
            Arrangement = new Arrangement([node], [], [],
                DesignMeasure.Zero, DesignMeasure.Zero,
                ArrangementAnchorKind.BowDatum, ArrangementResizePolicy.PreserveAbsolute),
            Barbettes = [barbette],
        };

        var entry01 = RequireEntry("ans-01-channel-knife");
        var keep = SketchbookShapeInitializer.Apply(
            entry01, SketchbookSizePolicy.KeepCurrentDimensions, basis);
        Require(keep.Document.Barbettes.Length == 1 && keep.Document.Barbettes[0] == barbette,
            "Keeping the current dimensions dropped or rewrote the barbette.");
        Require(keep.Document.Arrangement.FindNode("barbette-1-node") is not null,
            "Keeping the current dimensions dropped the barbette's arrangement node.");
        Require(keep.Diagnostics.Any(diagnostic =>
                diagnostic.Code == DesignDiagnosticCodes.BarbetteOddHullWidthRequired),
            "An even-width hull with a centerline barbette did not report BAR008.");

        var suggested = SketchbookShapeInitializer.Apply(
            entry01, SketchbookSizePolicy.SuggestedDimensions, basis);
        Require(suggested.Document.Barbettes.Length == 1 &&
                suggested.Document.Arrangement.FindNode("barbette-1-node") is not null,
            "Suggested dimensions dropped or rewrote the barbette or its arrangement node.");
        Require(suggested.Document.Hull.HasSingleBlockCenterline,
            "Entry 01's suggested width is no longer odd.");
        Require(!suggested.Diagnostics.Any(diagnostic =>
                diagnostic.Code == DesignDiagnosticCodes.BarbetteOddHullWidthRequired),
            "An odd-width hull still reported BAR008.");
    }

    // ---------------------------------------------------------------------------------------
    // 12. Persistence and compatibility.
    // ---------------------------------------------------------------------------------------

    private static void VerifyPersistenceCompatibility()
    {
        var barbette = BarbetteDefinition.Create("barbette-1", "barbette-1-node", DesignMeasure.FromMetres(3), 3);
        var node = ArrangementNode.Create("barbette-1-node", ArrangementNodeKind.Barbette, "barbette-1",
            DesignMeasure.FromMetres(2));
        var internals = new InternalStructure([
            InternalStructureFamily.Disabled(InternalPlaneFamily.LongitudinalBulkhead),
            InternalStructureFamily.Disabled(InternalPlaneFamily.InternalDeck) with { Enabled = true, Count = 2 },
            InternalStructureFamily.Disabled(InternalPlaneFamily.TransverseBulkhead),
        ]);
        var basis = ShipDocument.CreateNew("ANS round trip", HullEditorSettings.Default) with
        {
            Arrangement = new Arrangement([node], [], [],
                DesignMeasure.Zero, DesignMeasure.Zero,
                ArrangementAnchorKind.BowDatum, ArrangementResizePolicy.PreserveAbsolute),
            Barbettes = [barbette],
            Internals = internals,
        };

        var applied = SketchbookShapeInitializer.Apply(
            RequireEntry("ans-03-shoal-water-gunboat"), SketchbookSizePolicy.SuggestedDimensions, basis).Document;

        var serialized = ProjectDocumentSerializer.Serialize(applied);
        Require(serialized.Succeeded && serialized.Json is not null,
            "The applied sketchbook document did not serialize: " +
            string.Join("; ", serialized.Diagnostics.Select(diagnostic => diagnostic.ToString())));

        var reloaded = ProjectDocumentSerializer.Deserialize(serialized.Json!);
        Require(reloaded.Succeeded && reloaded.Document is not null,
            "The serialized sketchbook document did not deserialize: " +
            string.Join("; ", reloaded.Diagnostics.Select(diagnostic => diagnostic.ToString())));
        var round = reloaded.Document!;

        Require(round.Hull.Length == applied.Hull.Length && round.Hull.Width == applied.Hull.Width &&
                round.Hull.Height == applied.Hull.Height,
            "The round trip changed the applied dimensions.");
        Require(round.Hull.EffectiveShape == applied.Hull.EffectiveShape,
            "The round trip changed the applied Shape V2 bundle.");
        Require(round.Hull.HasBulb == applied.Hull.HasBulb && round.Hull.Bulb == applied.Hull.Bulb,
            "The round trip changed the applied bulb state.");
        Require(round.Hull.HullArmor.Equals(applied.Hull.HullArmor) &&
                Equals(round.Hull.DeckArmor, applied.Hull.DeckArmor) &&
                Equals(round.Hull.BottomArmor, applied.Hull.BottomArmor) &&
                round.Hull.Beamify == applied.Hull.Beamify &&
                round.Hull.Smoothing == applied.Hull.Smoothing,
            "The round trip changed the armor, construction or smoothing state.");
        Require(round.Barbettes.Length == 1 && round.Barbettes[0] == barbette,
            "The round trip lost or rewrote the barbette.");
        Require(round.Internals.Families.Length == 3 && round.Internals.Families.SequenceEqual(internals.Families),
            "The round trip lost or rewrote the internals.");

        Require(Findings(applied).SequenceEqual(Findings(round)),
            "The round trip changed the document's validation findings.");

        var fletcher = HistoricalPresetCatalog.Find("us-fletcher-dd")
            ?? throw new InvalidOperationException("The Fletcher entry is missing from the historical library.");
        var fletcherDocument = ShipDocument.CreateNew("Fletcher round trip",
            fletcher.ToParameters(HullEditorSettings.Default));
        var fletcherSerialized = ProjectDocumentSerializer.Serialize(fletcherDocument);
        Require(fletcherSerialized.Succeeded && fletcherSerialized.Json is not null,
            "The untouched historical entry did not serialize.");
        var fletcherReloaded = ProjectDocumentSerializer.Deserialize(fletcherSerialized.Json!);
        Require(fletcherReloaded.Succeeded && fletcherReloaded.Document is not null,
            "The untouched historical entry did not deserialize.");
        var fletcherRound = fletcherReloaded.Document!;
        Require(fletcherRound.Hull.Length == fletcher.Length && fletcherRound.Hull.Width == fletcher.Width &&
                fletcherRound.Hull.Height == fletcher.Height &&
                fletcherRound.Hull.EffectiveShape == fletcher.Shape,
            "The untouched historical entry changed across a round trip.");
    }

    private static IEnumerable<(string Code, DesignSeverity Severity, string Message, string? Field)> Findings(
        ShipDocument document) => document.Validate()
        .Select(diagnostic => (diagnostic.Code, diagnostic.Severity, diagnostic.Message, diagnostic.Field));

    // ---------------------------------------------------------------------------------------
    // 13. Distinctness.
    // ---------------------------------------------------------------------------------------

    private static void VerifyDistinctness()
    {
        var keys = AlternateNavalSketchbookCatalog.All.Select(entry => string.Join('|',
                entry.SuggestedLength, entry.SuggestedWidth, entry.SuggestedHeight,
                entry.BowStyle, entry.SternStyle,
                entry.Shape, entry.HasBulb, entry.Bulb))
            .ToArray();
        Require(keys.Distinct(StringComparer.Ordinal).Count() == keys.Length,
            "Two sketchbook entries share an identical resolved geometry bundle.");
    }

    // ---------------------------------------------------------------------------------------
    // 14. Native smoke and export.
    // ---------------------------------------------------------------------------------------

    private static void VerifyNativeSmoke(HullGenerator generator, FtdBlockCatalog catalog, bool full)
    {
        var ids = full
            ? AlternateNavalSketchbookCatalog.All.Select(entry => entry.Id).ToArray()
            : ["ans-01-channel-knife", "ans-08-atlantic-raider", "ans-13-pacific-super-battleship"];

        GeneratedHull? representative = null;
        var temporary = Path.Combine(Path.GetTempPath(), $"HullForgeANS-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporary);
        try
        {
            foreach (var id in ids)
            {
                var entry = RequireEntry(id);
                var document = SketchbookShapeInitializer.Apply(
                    entry, SketchbookSizePolicy.SuggestedDimensions,
                    ShipDocument.CreateNew($"ANS native {id}", HullEditorSettings.Default)).Document;
                var hull = generator.Generate(document.Hull);
                var errors = HullGeometryValidator.Validate(hull);
                Require(errors.Count == 0,
                    $"{id} failed native geometry validation: {string.Join("; ", errors)}");
                Require(hull.OccupiedLength == entry.SuggestedLength && hull.OccupiedWidth == entry.SuggestedWidth,
                    $"{id} occupied {hull.OccupiedLength}x{hull.OccupiedWidth} instead of " +
                    $"{entry.SuggestedLength}x{entry.SuggestedWidth}.");
                representative ??= hull;
            }

            if (representative is null)
                throw new InvalidOperationException("No representative sketchbook hull was generated.");

            var export = new BlueprintExporter().Export(representative, catalog, temporary, "ANS native smoke");
            Require(File.Exists(export.FilePath), "The representative sketchbook export wrote no file.");
            using var blueprint = JsonDocument.Parse(File.ReadAllText(export.FilePath));
            var craft = blueprint.RootElement.GetProperty("Blueprint");
            Require(craft.GetProperty("MinCords").GetString() ==
                    $"{representative.MinX},{representative.MinY},{representative.MinZ}" &&
                    craft.GetProperty("MaxCords").GetString() ==
                    $"{representative.MaxX},{representative.MaxY},{representative.MaxZ}",
                "The representative sketchbook export wrote bounds that do not match the generated hull.");
            Require(export.OccupiedCellCount == representative.OccupiedCellCount,
                "The representative sketchbook export occupied a different cell count.");
        }
        finally
        {
            if (Directory.Exists(temporary))
                Directory.Delete(temporary, recursive: true);
        }
    }

    private static AlternateNavalSketchbookEntry RequireEntry(string id) =>
        AlternateNavalSketchbookCatalog.Find(id)
        ?? throw new InvalidOperationException($"The sketchbook entry '{id}' is missing.");
}
