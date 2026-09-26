using System.Globalization;
using System.Text.RegularExpressions;
using FtdHullGenerator.Domain.Historical;

namespace FtdHullGenerator.Domain.Sketchbook;

/// <summary>
/// The immutable sixteen-entry Alternate Naval Sketchbook. Each entry is a complete, versioned
/// design candidate: stable id, suggested and minimum envelope, explicit Shape V2 bundle,
/// explicit bulb state and a comparison-only smoothing recommendation.
/// </summary>
/// <remarks>
/// The catalog is fixed data. Nothing here derives a value from another entry, the caller or the
/// installed game, and the entries are never retuned to make a generated hull validate. Applying
/// an entry is a normal editor transaction through
/// <c>FtdHullGenerator.UI.Editor.SketchbookEditorContract</c>.
/// </remarks>
public static class AlternateNavalSketchbookCatalog
{
    /// <summary>The catalog revision this build understands. Append-only.</summary>
    public const string CatalogVersion = "ans-1";

    /// <summary>
    /// The honest provenance statement for the accepted design candidate. The Python
    /// transcription is the only evidence; nothing about native parity, smoothing, cavities,
    /// components, installed-catalog availability, WPF, export or an in-game load/re-save has
    /// been proven.
    /// </summary>
    public const string ProvenanceNote =
        "Accepted Alternate Naval Sketchbook design candidate. The design study used a Python " +
        "transcription only; native C# parity, smoothing, cavities, components, installed catalog, " +
        "WPF, export and From the Depths load/re-save are not proven.";

    private const int ExpectedEntryCount = 16;

    private static readonly Regex StableIdPattern = new("^[a-z0-9-]+$", RegexOptions.CultureInvariant);

    /// <summary>The explicit disabled bulb every entry without a bulb writes, never null and never inherited.</summary>
    private static BulbSettings DisabledBulb { get; } = new(8, 35, 0, 0);

    /// <summary>Every entry, in ordinal order.</summary>
    public static IReadOnlyList<AlternateNavalSketchbookEntry> All { get; } =
    [
        Entry(
            "ans-01-channel-knife", 1, "Channel Knife Torpedo Boat",
            "Torpedo boat",
            "MTB/Higgins PT vocabulary, stylized and not a replica",
            36, 9, 5, 25, 7, 4,
            BowStyle.Spoon, SternStyle.Transom,
            -0.20, 0.50, 45,
            -0.15, 0.10, 0.90, 0.05,
            0.20, 0.05, 18,
            1, 0, 0, 0,
            false, DisabledBulb, SmoothingMethod.HorizontalSlopeFill, "",
            "Torpedo & patrol craft",
            ["hard chine", "V-like lower sections", "fine entrance", "full working transom"],
            "MTB/Higgins PT vocabulary, stylized and not a replica."),

        Entry(
            "ans-02-offshore-picket", 2, "Offshore Picket Cutter",
            "Offshore picket cutter",
            "double-ended cutter/whaler cues in an alternate powered picket",
            58, 11, 7, 39, 9, 6,
            BowStyle.Spoon, SternStyle.Canoe,
            0.40, 0.25, 27,
            0.55, 0.10, -0.80, 0.16,
            0.10, -0.15, 40,
            2, 1, 0, 1,
            false, DisabledBulb, SmoothingMethod.VerticalSlopeFill, "",
            "Torpedo & patrol craft",
            ["short rounded entrance", "soft bilges", "fine canoe stern", "rise at both ends"],
            "Double-ended cutter/whaler cues in an alternate powered picket."),

        Entry(
            "ans-03-shoal-water-gunboat", 3, "Shoal-Water Assault Gunboat",
            "Shoal-water assault gunboat",
            "shallow-water military/work-craft cues",
            48, 13, 5, 31, 9, 4,
            BowStyle.Raked, SternStyle.Square,
            0.35, 0.05, 22,
            0.65, 0.00, 0.65, 0.72,
            0.75, 0.00, 12,
            0, 0, 0, 0,
            false, DisabledBulb, SmoothingMethod.None, "",
            "Torpedo & patrol craft",
            ["broad flat lower body", "short entry", "full square stern", "level deck and keel"],
            "Shallow-water military and work-craft cues."),

        Entry(
            "ans-04-greyhound", 4, "Greyhound Torpedo Destroyer",
            "Torpedo destroyer",
            "intentional alternate-history high-speed exaggeration",
            126, 13, 9, 81, 11, 7,
            BowStyle.Spoon, SternStyle.Canoe,
            -0.70, 0.50, 70,
            -0.20, 0.10, -0.45, 0.02,
            -0.55, 0.00, 25,
            2, 0, 0, 2,
            false, DisabledBulb, SmoothingMethod.HorizontalSlopeFill, "",
            "Torpedo & patrol craft",
            ["extremely long fine entrance", "narrow lower sections", "canoe stern", "nearly absent flat floor"],
            "Intentional alternate-history high-speed exaggeration."),

        Entry(
            "ans-05-high-forecastle", 5, "High-Forecastle Ocean Destroyer",
            "Ocean destroyer",
            "raised-forecastle destroyer seed, smooth Shape V2 not a stepped forecastle",
            142, 17, 11, 91, 13, 8,
            BowStyle.Raked, SternStyle.Counter,
            0.65, 1.05, 36,
            0.35, 0.45, -0.65, 0.18,
            0.15, 0.10, 26,
            5, 0, 1, 1,
            false, DisabledBulb, SmoothingMethod.VerticalSlopeFill,
            "inspect final approximately 7 m single-column terminal region",
            "Destroyers & leaders",
            ["strongly raised bow", "strong flare", "fuller middle", "counter stern"],
            "Raised-forecastle destroyer seed; smooth Shape V2, not a stepped forecastle."),

        Entry(
            "ans-06-ram-bow-leader", 6, "Ram-Bow Armored Leader",
            "Armored flotilla leader",
            "ram-bow/tumblehome-era cues synthesized into a twentieth-century flotilla leader",
            132, 19, 10, 89, 13, 8,
            BowStyle.Axe, SternStyle.Transom,
            0.90, 0.25, 24,
            0.35, -0.18, 0.90, 0.28,
            0.55, -0.05, 20,
            1, 0, 0, 1,
            false, DisabledBulb, SmoothingMethod.None, "",
            "Destroyers & leaders",
            ["reverse/axe bow", "faceted lower form", "mildly inward upper sides", "broad transom"],
            "Ram-bow and tumblehome-era cues synthesized into a twentieth-century flotilla leader."),

        Entry(
            "ans-07-fleet-torpedo-cruiser", 7, "Fleet Torpedo Cruiser",
            "Fleet torpedo cruiser",
            "full forward/middle body, broad lower floor, long narrowing aft run",
            174, 23, 12, 115, 17, 9,
            BowStyle.Pointed, SternStyle.Cruiser,
            0.75, 0.35, 24,
            0.48, 0.20, -0.40, 0.36,
            -0.65, 0.10, 49,
            1, 3, 10, 4,
            false, DisabledBulb, SmoothingMethod.HorizontalSlopeFill,
            "The 10 m BowKeelRise is intentional and must not be corrected.",
            "Cruisers",
            ["full forward and middle body", "broad lower floor", "long narrowing aft run", "altered after/keel profile"],
            "Alternate-history fleet torpedo-cruiser synthesis."),

        Entry(
            "ans-08-atlantic-raider", 8, "Atlantic Commerce Raider",
            "Commerce raider",
            "commerce-raider role/cruiser proportions, no national underwater claim",
            214, 23, 13, 145, 17, 10,
            BowStyle.Spoon, SternStyle.Counter,
            -0.85, 0.10, 72,
            0.25, 0.00, -0.75, 0.12,
            -0.10, -0.25, 16,
            0, 1, 1, 3,
            false, DisabledBulb, SmoothingMethod.HorizontalSlopeFill, "",
            "Cruisers",
            ["longest entrance", "lean rounded body", "restrained sheer", "compact counter stern"],
            "Commerce-raider role and cruiser proportions; no national underwater claim."),

        Entry(
            "ans-09-northern-cruiser", 9, "Northern Ocean Cruiser",
            "Ocean cruiser",
            "high flared bow, fine lower forebody, major forward deck rise, rounded cruiser stern",
            194, 25, 15, 125, 19, 11,
            BowStyle.Clipper, SternStyle.Cruiser,
            0.90, 1.20, 36,
            0.00, 0.55, -0.35, 0.08,
            0.20, 0.35, 30,
            6, 2, 2, 2,
            false, DisabledBulb, SmoothingMethod.VerticalSlopeFill,
            "inspect final approximately 7 m single-column terminal region",
            "Cruisers",
            ["high flared bow", "fine lower forebody", "major forward deck rise", "rounded cruiser stern"],
            "Alternate-history northern ocean-cruiser synthesis."),

        Entry(
            "ans-10-treaty-breaker", 10, "Treaty-Breaker Large Cruiser",
            "Large cruiser",
            "very full rounded body, moderate broad floor, restrained sheer, full transom",
            228, 29, 15, 151, 21, 11,
            BowStyle.Spoon, SternStyle.Transom,
            0.25, 0.25, 36,
            0.85, 0.05, -0.65, 0.32,
            0.80, 0.15, 20,
            2, 0, 1, 1,
            false, DisabledBulb, SmoothingMethod.VerticalSlopeFill,
            "rejected bulb experiment must not return",
            "Cruisers",
            ["very full rounded body", "moderate broad floor", "restrained sheer", "full transom"],
            "Alternate-history treaty-breaker large-cruiser synthesis."),

        Entry(
            "ans-11-long-run-battlecruiser", 11, "Long-Run Fleet Battlecruiser",
            "Battlecruiser",
            "long fairing both ends, almost no parallel middle, fine cruiser stern, moderate lower breadth; battlecruiser-line seed",
            282, 31, 17, 181, 23, 13,
            BowStyle.Spoon, SternStyle.Cruiser,
            -0.55, 0.55, 58,
            0.25, 0.20, -0.55, 0.18,
            -0.50, 0.05, 40,
            3, 1, 2, 4,
            false, DisabledBulb, SmoothingMethod.HorizontalSlopeFill, "",
            "Cruisers",
            ["long fairing both ends", "almost no parallel middle", "fine cruiser stern", "moderate lower breadth"],
            "Battlecruiser-line seed."),

        Entry(
            "ans-12-fast-battleship", 12, "Ocean Fast Battleship",
            "Fast battleship",
            "long fine entrance, broad rounded middle, useful lower breadth, compact transom run; exaggerated fast-battleship plan-form cues",
            272, 35, 18, 181, 25, 13,
            BowStyle.Spoon, SternStyle.Transom,
            -0.80, 0.65, 62,
            0.70, 0.10, -0.70, 0.30,
            0.35, 0.10, 20,
            4, 0, 1, 2,
            false, DisabledBulb, SmoothingMethod.HorizontalSlopeFill, "",
            "Capital ships",
            ["long fine entrance", "broad rounded middle", "useful lower breadth", "compact transom run"],
            "Exaggerated fast-battleship plan-form cues."),

        Entry(
            "ans-13-pacific-super-battleship", 13, "Pacific Super-Battleship",
            "Super-battleship",
            "broad full sections, wide lower floor, short/full entrance, integrated bulb",
            302, 45, 21, 199, 31, 15,
            BowStyle.Spoon, SternStyle.Cruiser,
            0.55, 0.45, 30,
            0.90, 0.05, -0.80, 0.58,
            0.60, -0.05, 30,
            4, 1, 1, 2,
            true, new BulbSettings(12, 45, -4, 0), SmoothingMethod.VerticalSlopeFill,
            "inspect bulb attachment, no forefoot notch, and ruler/deck authority not contaminated by bulb-only stations",
            "Capital ships",
            ["broad full sections", "wide lower floor", "short and full entrance", "integrated bulb"],
            "Alternate-history Pacific super-battleship synthesis."),

        Entry(
            "ans-14-round-bilge-dreadnought", 14, "Round-Bilge Dreadnought",
            "Dreadnought",
            "compact transitions, round bilges, broad lower body, restrained sheer; dreadnought faired-line/round-bilge cues",
            188, 33, 16, 121, 25, 12,
            BowStyle.Raked, SternStyle.Cruiser,
            0.80, 0.05, 24,
            0.80, -0.12, -0.85, 0.40,
            0.30, -0.20, 31,
            1, 1, 0, 1,
            false, DisabledBulb, SmoothingMethod.VerticalSlopeFill, "",
            "Capital ships",
            ["compact transitions", "round bilges", "broad lower body", "restrained sheer"],
            "Dreadnought faired-line and round-bilge cues."),

        Entry(
            "ans-15-tumblehome-citadel", 15, "Tumblehome Citadel Ship",
            "Citadel ship",
            "maximum breadth below deck, pronounced inward upper sides, rounded lower hull, broad fantail; tumblehome is a required native probe",
            168, 29, 15, 111, 21, 11,
            BowStyle.Spoon, SternStyle.Fantail,
            0.05, -0.40, 38,
            0.75, -1.05, -0.55, 0.22,
            0.10, -0.70, 30,
            2, 2, 0, 1,
            false, DisabledBulb, SmoothingMethod.VerticalSlopeFill,
            "top-only thumbnail is dishonest, include section/bow/equivalent inset",
            "Capital ships",
            ["maximum breadth below deck", "pronounced inward upper sides", "rounded lower hull", "broad fantail"],
            "Alternate-history tumblehome citadel-ship synthesis."),

        Entry(
            "ans-16-coastal-siege-dreadnought", 16, "Coastal Siege Dreadnought",
            "Coastal siege dreadnought",
            "extremely broad floor, blunt bow, full square stern, level deck/keel; monitor/coastal-capital breadth cues",
            156, 39, 10, 99, 27, 8,
            BowStyle.Blunt, SternStyle.Square,
            -0.50, 0.25, 37,
            0.90, 0.05, 0.20, 0.85,
            0.40, 0.10, 18,
            0, 0, 0, 0,
            false, DisabledBulb, SmoothingMethod.None, "",
            "Capital ships",
            ["extremely broad floor", "blunt bow", "full square stern", "level deck and keel"],
            "Monitor and coastal-capital breadth cues."),
    ];

    /// <summary>Finds one entry by its stable id using an ordinal comparison, or null when unknown.</summary>
    public static AlternateNavalSketchbookEntry? Find(string id) =>
        All.FirstOrDefault(entry => string.Equals(entry.Id, id, StringComparison.Ordinal));

    /// <summary>
    /// The catalog's own integrity contract. Returns an empty list when the catalog is sound:
    /// sixteen entries, stable unique lowercase ids that collide with no historical id or animal
    /// preset name, supported odd-width suggested and minimum envelopes, validator-clean Shape V2
    /// values, an explicit Custom body, an explicit valid bulb, the exact bulb rule, and no two
    /// entries sharing one geometry bundle.
    /// </summary>
    public static IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (All.Count != ExpectedEntryCount)
            errors.Add($"The sketchbook must hold exactly {ExpectedEntryCount} entries, but holds {All.Count}.");

        var historicalIds = HistoricalPresetCatalog.All.Select(preset => preset.Id)
            .ToHashSet(StringComparer.Ordinal);
        var animalNames = HullShapePreset.All.Select(preset => preset.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var signatures = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in All)
        {
            if (string.IsNullOrWhiteSpace(entry.Id))
                errors.Add("A sketchbook entry has no stable id.");
            else if (!StableIdPattern.IsMatch(entry.Id))
                errors.Add($"Sketchbook entry id '{entry.Id}' is not a lowercase [a-z0-9-] token.");
            else if (!ids.Add(entry.Id))
                errors.Add($"Two sketchbook entries share the id '{entry.Id}'.");

            if (historicalIds.Contains(entry.Id))
                errors.Add($"Sketchbook id '{entry.Id}' collides with a historical preset id.");
            if (animalNames.Contains(entry.Id))
                errors.Add($"Sketchbook id '{entry.Id}' collides with an animal preset name.");

            if (string.IsNullOrWhiteSpace(entry.Name) || string.IsNullOrWhiteSpace(entry.Role) ||
                string.IsNullOrWhiteSpace(entry.Inspiration) || entry.RepresentationNote is null)
                errors.Add($"Sketchbook entry '{entry.Id}' is missing its display name, role, inspiration or note.");

            // Team C presentation metadata. Geometry and the Team B fields are validated above and
            // are deliberately not part of this contract.
            if (string.IsNullOrWhiteSpace(entry.Family))
                errors.Add($"Sketchbook entry '{entry.Id}' has no presentation family.");
            if (entry.Traits is null || entry.Traits.Count is < 2 or > 4)
            {
                errors.Add($"Sketchbook entry '{entry.Id}' must declare between two and four traits, " +
                           $"but declares {entry.Traits?.Count ?? 0}.");
            }
            else
            {
                var traits = new HashSet<string>(StringComparer.Ordinal);
                foreach (var trait in entry.Traits)
                {
                    if (string.IsNullOrWhiteSpace(trait))
                        errors.Add($"Sketchbook entry '{entry.Id}' has an empty presentation trait.");
                    else if (!traits.Add(trait))
                        errors.Add($"Sketchbook entry '{entry.Id}' repeats the presentation trait '{trait}'.");
                }
            }
            if (string.IsNullOrWhiteSpace(entry.Provenance))
                errors.Add($"Sketchbook entry '{entry.Id}' has no presentation provenance.");

            ValidateEnvelope(errors, entry, entry.SuggestedLength, entry.SuggestedWidth, entry.SuggestedHeight, "suggested");
            ValidateEnvelope(errors, entry, entry.MinimumLength, entry.MinimumWidth, entry.MinimumHeight, "minimum");

            if (entry.Shape is null)
            {
                errors.Add($"Sketchbook entry '{entry.Id}' has no Shape V2 bundle.");
                continue;
            }

            if (entry.Shape.Body.Style != BodyStyle.Custom)
                errors.Add($"Sketchbook entry '{entry.Id}' must declare BodyStyle.Custom, not {entry.Shape.Body.Style}.");

            foreach (var error in entry.Shape.Validate(entry.SuggestedHeight))
                errors.Add($"Sketchbook entry '{entry.Id}': {error}");

            foreach (var error in entry.Bulb.Validate())
                errors.Add($"Sketchbook entry '{entry.Id}' bulb: {error}");

            var expectedBulb = entry.Ordinal == 13 ? new BulbSettings(12, 45, -4, 0) : DisabledBulb;
            var expectedHasBulb = entry.Ordinal == 13;
            if (entry.HasBulb != expectedHasBulb || entry.Bulb != expectedBulb)
                errors.Add($"Sketchbook entry '{entry.Id}' does not carry the exact bulb rule for ordinal {entry.Ordinal}.");

            var signature = Signature(entry);
            if (!signatures.Add(signature))
                errors.Add($"Sketchbook entry '{entry.Id}' duplicates another entry's complete geometry bundle.");
        }

        return errors;
    }

    private static void ValidateEnvelope(
        List<string> errors,
        AlternateNavalSketchbookEntry entry,
        int length,
        int width,
        int height,
        string label)
    {
        if (length < HullParameters.MinimumLength || width < HullParameters.MinimumWidth ||
            height < HullParameters.MinimumHeight)
            errors.Add($"Sketchbook entry '{entry.Id}' {label} envelope is below the supported minimum.");
        if (width % 2 != 1)
            errors.Add($"Sketchbook entry '{entry.Id}' {label} width {width} must be odd.");
    }

    private static string Signature(AlternateNavalSketchbookEntry entry)
    {
        var shape = entry.Shape;
        var builder = new System.Text.StringBuilder();
        Append(builder, entry.BowStyle);
        Append(builder, entry.SternStyle);
        Append(builder, shape.Bow.Fullness);
        Append(builder, shape.Bow.Flare);
        Append(builder, shape.Bow.EntranceLengthPercent);
        Append(builder, shape.Body.Style);
        Append(builder, shape.Body.Fullness);
        Append(builder, shape.Body.SideShape);
        Append(builder, shape.Body.Chine);
        Append(builder, shape.Body.FlatBottom);
        Append(builder, shape.Stern.Fullness);
        Append(builder, shape.Stern.SideShape);
        Append(builder, shape.Stern.RunLengthPercent);
        Append(builder, shape.Profile.BowDeckRise);
        Append(builder, shape.Profile.SternDeckRise);
        Append(builder, shape.Profile.BowKeelRise);
        Append(builder, shape.Profile.SternKeelRise);
        Append(builder, entry.HasBulb);
        Append(builder, entry.Bulb.LengthPercent);
        Append(builder, entry.Bulb.WidthPercent);
        Append(builder, entry.Bulb.ForeAftPercent);
        Append(builder, entry.Bulb.RisePercent);
        Append(builder, entry.SuggestedLength);
        Append(builder, entry.SuggestedWidth);
        Append(builder, entry.SuggestedHeight);
        return builder.ToString();
    }

    private static void Append(System.Text.StringBuilder builder, object value)
    {
        if (value is IFormattable formattable)
            builder.Append(formattable.ToString(null, CultureInfo.InvariantCulture));
        else
            builder.Append(value);
        builder.Append('|');
    }

    private static AlternateNavalSketchbookEntry Entry(
        string id,
        int ordinal,
        string name,
        string role,
        string inspiration,
        int suggestedLength,
        int suggestedWidth,
        int suggestedHeight,
        int minimumLength,
        int minimumWidth,
        int minimumHeight,
        BowStyle bowStyle,
        SternStyle sternStyle,
        double bowFullness,
        double bowFlare,
        int entranceLengthPercent,
        double bodyFullness,
        double bodySideShape,
        double bodyChine,
        double flatBottom,
        double sternFullness,
        double sternSideShape,
        int runLengthPercent,
        int bowDeckRise,
        int sternDeckRise,
        int bowKeelRise,
        int sternKeelRise,
        bool hasBulb,
        BulbSettings bulb,
        SmoothingMethod recommendedSmoothing,
        string representationNote,
        string family,
        IReadOnlyList<string> traits,
        string provenance) =>
        new(
            id,
            ordinal,
            name,
            role,
            inspiration,
            suggestedLength,
            suggestedWidth,
            suggestedHeight,
            minimumLength,
            minimumWidth,
            minimumHeight,
            bowStyle,
            sternStyle,
            new HullShapeSettings(
                new BowShapeSettings(bowFullness, bowFlare, entranceLengthPercent),
                new BodyShapeSettings(BodyStyle.Custom, bodyFullness, bodySideShape, bodyChine, flatBottom),
                new SternShapeSettings(sternFullness, sternSideShape, runLengthPercent),
                new HullProfileSettings(bowDeckRise, sternDeckRise, bowKeelRise, sternKeelRise)),
            hasBulb,
            bulb,
            recommendedSmoothing,
            representationNote,
            family,
            traits,
            provenance);
}
