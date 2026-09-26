namespace FtdHullGenerator.Domain.Historical;

/// <summary>
/// Visual grouping metadata for one historical-library nation.
/// </summary>
/// <remarks>
/// Nation is presentation only. It is never read by generation, armor, smoothing,
/// barbette rules, validation, materials or export. The flag is a compact
/// national-colour chip drawn from <see cref="AccentColor"/> and <see cref="FlagGlyph"/>
/// rather than a bundled third-party image, so the normal library needs no asset
/// redistribution rights.
/// </remarks>
public sealed record HistoricalNation(
    string Code,
    string DisplayName,
    string FlagGlyph,
    string AccentColor);

/// <summary>
/// One hand-authored historical starting hull in the normal 2.0 library.
/// </summary>
/// <remarks>
/// A preset is a launcher, not a mode. <see cref="ToParameters"/> overrides only the
/// dimensions, bow/stern style and Shape V2 settings of the caller's parameter set, so
/// armor, construction, smoothing and every other setting stay exactly where the user
/// left them and every slider stays freely editable afterward. The editable metre values
/// are Hull Forge starting approximations of the published dimensions; they are not a
/// measured historical reconstruction and no third-party mesh or drawing is bundled.
/// </remarks>
public sealed record HistoricalHullPreset(
    string Id,
    HistoricalNation Nation,
    string Name,
    string Designation,
    string Era,
    string BuildStatus,
    string PublishedDimensions,
    int Length,
    int Width,
    int Height,
    BowStyle BowStyle,
    SternStyle SternStyle,
    HullShapeSettings Shape,
    string SourceNote)
{
    /// <summary>The editable metre envelope, for the browser's compact size line.</summary>
    public string EnvelopeSummary => $"{Length} × {Width} × {Height} m";

    /// <summary>Compact era/status line for the browser entry.</summary>
    public string ContextLine => $"{Era} · {BuildStatus}";

    /// <summary>
    /// Applies this preset's dimensions and Shape V2 starting values to an existing
    /// parameter set, leaving armor, construction, smoothing, superstructure and every
    /// other setting untouched. This is the same override the editor's normal state path
    /// commits, so applying a preset is an ordinary editable hull rather than a
    /// continuing national or class mode.
    /// </summary>
    public HullParameters ToParameters(HullParameters basis)
    {
        ArgumentNullException.ThrowIfNull(basis);
        return basis with
        {
            Length = Length,
            Width = Width,
            Height = Height,
            BowStyle = BowStyle,
            SternStyle = SternStyle,
            Shape = Shape,
        };
    }
}

/// <summary>The five researched navies, in the browser's stable presentation order.</summary>
public static class HistoricalNations
{
    public static HistoricalNation UnitedStates { get; } = new("US", "United States", "US", "#3C3B6E");
    public static HistoricalNation France { get; } = new("FR", "France", "FR", "#0055A4");
    public static HistoricalNation Italy { get; } = new("IT", "Italy", "IT", "#008C45");
    public static HistoricalNation Germany { get; } = new("DE", "Germany", "DE", "#C8102E");
    public static HistoricalNation RussiaSovietUnion { get; } = new("RU", "Russia / Soviet Union", "RU", "#0039A6");

    public static IReadOnlyList<HistoricalNation> All { get; } =
    [
        UnitedStates, France, Italy, Germany, RussiaSovietUnion,
    ];
}

/// <summary>
/// The normal 2.0 historical starting library. Every entry is a real researched class
/// or design from the H01 five-nation roster, hand-authored as a useful Shape V2
/// starting envelope. The library carries no confidence/fidelity workflow, no
/// nation-specific behaviour and no bundled third-party asset.
/// </summary>
/// <remarks>
/// Omissions are deliberate and documented. The French and Italian battlecruiser facets
/// are absent because the roster records that no ship of that type was completed
/// (<c>not_applicable_verified</c>); inventing an entry would be dishonest. Cancelled and
/// planned designs that do have published dimensions are kept, labelled as such in
/// <see cref="HistoricalHullPreset.BuildStatus"/> and in their source note.
/// </remarks>
public static class HistoricalPresetCatalog
{
    private const string RosterSourceNote =
        "Approximate Hull Forge starting envelope authored from the published length and beam in the " +
        "H01 five-nation roster (Wikipedia, read 2026-09-12). No third-party drawing, mesh or " +
        "restricted asset is bundled, and these editable metre values are not a measured reconstruction.";

    /// <summary>Every entry, grouped in nation order and then by class facet.</summary>
    public static IReadOnlyList<HistoricalHullPreset> All { get; } =
    [
        // ── United States ───────────────────────────────────────────────────────────
        Entry("us-elco-pt", HistoricalNations.UnitedStates,
            "Elco 80 ft motor torpedo boat", "Motor Torpedo Boat (PT)",
            "World War II (1942–1945)", "built",
            "L 24.4 m (80 ft); B 6.30 m (20 ft 8 in)",
            24, 7, 4, BowStyle.Raked, SternStyle.Transom,
            Form(BodyStyle.HardChine, 0.0, 0.15, 0.85, 0, -0.05, 0.30, 32, 0.05, 0.10, 30, 1, 0, 1, 0)),

        Entry("us-fletcher-dd", HistoricalNations.UnitedStates,
            "Fletcher class", "Destroyer (DD)",
            "World War II (1942–1945)", "built",
            "L 114.8 m (376.5 ft); B 12.0 m; 2,050 t standard",
            115, 13, 8, BowStyle.Raked, SternStyle.Transom,
            Form(BodyStyle.Rounded, 0.40, 0.15, -0.45, 0, 0.05, 0.45, 45, 0.20, 0.05, 30, 2, 1, 2, 1)),

        Entry("us-cleveland-cl", HistoricalNations.UnitedStates,
            "Cleveland class", "Light cruiser (CL)",
            "World War II", "built",
            "L 182.9 m (600 ft) wl; B 20.2 m; 11,744 t standard",
            183, 21, 11, BowStyle.Raked, SternStyle.Cruiser,
            Form(BodyStyle.Rounded, 0.55, 0.18, -0.40, 0, 0.10, 0.50, 45, 0.30, 0.10, 30, 2, 1, 2, 1)),

        Entry("us-baltimore-ca", HistoricalNations.UnitedStates,
            "Baltimore class", "Heavy cruiser (CA)",
            "World War II", "built",
            "L 202.4 m (664 ft) wl; B 21.6 m; 13,600 t standard",
            202, 21, 12, BowStyle.Clipper, SternStyle.Cruiser,
            Form(BodyStyle.Rounded, 0.62, 0.22, -0.35, 0, 0.12, 0.55, 44, 0.34, 0.12, 29, 3, 1, 2, 1)),

        Entry("us-lexington-cc", HistoricalNations.UnitedStates,
            "Lexington class", "Battlecruiser (CC)",
            "Designed World War I; never completed", "cancelled",
            "L 266.4 m (874 ft) o/a; B 32.1 m; 43,500 t",
            266, 33, 16, BowStyle.Clipper, SternStyle.Cruiser,
            Form(BodyStyle.Rounded, 0.70, 0.26, -0.30, 0, 0.15, 0.65, 42, 0.38, 0.15, 28, 3, 1, 2, 1),
            note: RosterSourceNote + " This design was cancelled before completion, so it is a paper " +
                  "starting envelope rather than a completed ship."),

        Entry("us-iowa-bb", HistoricalNations.UnitedStates,
            "Iowa class", "Battleship (BB)",
            "World War II", "built",
            "L 262.1 m (860 ft) pp; B 33.0 m; 48,110 t standard",
            262, 33, 17, BowStyle.Clipper, SternStyle.Transom,
            Form(BodyStyle.U, 0.80, 0.28, -0.30, 0, 0.15, 0.70, 40, 0.42, 0.15, 27, 4, 2, 3, 2)),

        Entry("us-south-carolina-bb", HistoricalNations.UnitedStates,
            "South Carolina class", "Dreadnought battleship (BB)",
            "1910", "built",
            "L 138.0 m (452 ft 9 in) o/a; B 24.5 m; 16,000 t design",
            138, 25, 14, BowStyle.Pointed, SternStyle.Transom,
            Form(BodyStyle.Rounded, 0.72, 0.22, -0.35, 0, 0.10, 0.55, 42, 0.36, 0.12, 28, 3, 1, 2, 1)),

        // ── France ──────────────────────────────────────────────────────────────────
        Entry("fr-bougainville-aviso", HistoricalNations.France,
            "Bougainville class", "Aviso (colonial sloop)",
            "1930s–World War II", "built",
            "L 103.7 m o/a; B 12.7 m; 1,969 t standard",
            104, 13, 8, BowStyle.Raked, SternStyle.Cruiser,
            Form(BodyStyle.Rounded, 0.42, 0.16, -0.45, 0, 0.05, 0.40, 40, 0.22, 0.08, 30, 2, 1, 1, 1)),

        Entry("fr-elan-aviso", HistoricalNations.France,
            "Élan class", "Aviso / minesweeping sloop",
            "1930s–World War II", "built",
            "L 77.5 m o/a; B 8.92 m; 895 t deep",
            78, 9, 6, BowStyle.Raked, SternStyle.Cruiser,
            Form(BodyStyle.Rounded, 0.38, 0.14, -0.50, 0, 0.00, 0.35, 38, 0.20, 0.08, 30, 1, 1, 1, 1)),

        Entry("fr-le-fantasque-dd", HistoricalNations.France,
            "Le Fantasque class", "Destroyer (contre-torpilleur)",
            "1930s–World War II", "built",
            "L 132.4 m; B 12 m; 2,569 t standard",
            132, 13, 9, BowStyle.Raked, SternStyle.Cruiser,
            Form(BodyStyle.Rounded, 0.42, 0.18, -0.40, 0, 0.08, 0.50, 46, 0.24, 0.08, 30, 2, 1, 2, 1)),

        Entry("fr-la-galissonniere-cl", HistoricalNations.France,
            "La Galissonnière class", "Light cruiser",
            "1930s–World War II", "built",
            "L 179 m; B 17.5 m; 7,600 t standard",
            179, 17, 11, BowStyle.Raked, SternStyle.Cruiser,
            Form(BodyStyle.Rounded, 0.52, 0.18, -0.42, 0, 0.10, 0.50, 44, 0.28, 0.10, 30, 2, 1, 2, 1)),

        Entry("fr-suffren-ca", HistoricalNations.France,
            "Suffren class", "Heavy cruiser",
            "1930s–World War II", "built",
            "L 194 m o/a; B 19.26 m; 10,160 t standard",
            194, 19, 12, BowStyle.Clipper, SternStyle.Cruiser,
            Form(BodyStyle.Rounded, 0.60, 0.22, -0.36, 0, 0.12, 0.55, 44, 0.32, 0.12, 29, 3, 1, 2, 1)),

        Entry("fr-richelieu-bb", HistoricalNations.France,
            "Richelieu class", "Fast battleship",
            "World War II", "built (2 completed, 2 cancelled)",
            "L 247.85 m o/a; B 33.08 m; 37,250 t standard",
            248, 33, 17, BowStyle.Clipper, SternStyle.Transom,
            Form(BodyStyle.U, 0.80, 0.30, -0.28, 0, 0.15, 0.70, 40, 0.42, 0.16, 27, 4, 2, 3, 2)),

        Entry("fr-courbet-bb", HistoricalNations.France,
            "Courbet class", "Dreadnought battleship",
            "World War I", "built",
            "L 166 m o/a; B 27 m; 23,475 t normal",
            166, 27, 14, BowStyle.Pointed, SternStyle.Transom,
            Form(BodyStyle.Rounded, 0.74, 0.24, -0.34, 0, 0.12, 0.58, 42, 0.36, 0.12, 28, 3, 1, 2, 1)),

        // ── Italy ───────────────────────────────────────────────────────────────────
        Entry("it-spica-tb", HistoricalNations.Italy,
            "Spica class", "Torpedo boat",
            "1930s–World War II", "built",
            "L 81.4 m o/a; B 8.1 m; 795 t standard",
            81, 9, 6, BowStyle.Raked, SternStyle.Transom,
            Form(BodyStyle.Rounded, 0.35, 0.14, -0.45, 0, 0.02, 0.38, 38, 0.18, 0.06, 30, 1, 1, 1, 1)),

        Entry("it-soldati-dd", HistoricalNations.Italy,
            "Soldati class", "Destroyer",
            "World War II", "built",
            "L 106.7 m o/a; B 10.15 m; 1,820–1,850 t standard",
            107, 11, 8, BowStyle.Raked, SternStyle.Cruiser,
            Form(BodyStyle.Rounded, 0.42, 0.16, -0.42, 0, 0.06, 0.46, 45, 0.22, 0.08, 30, 2, 1, 2, 1)),

        Entry("it-condottieri-cl", HistoricalNations.Italy,
            "Condottieri class", "Light cruiser",
            "1930s–World War II", "built",
            "L 169.3–187 m; B 15.5–18.9 m; 5,323–11,350 t standard",
            176, 17, 11, BowStyle.Raked, SternStyle.Cruiser,
            Form(BodyStyle.Rounded, 0.50, 0.20, -0.40, 0, 0.10, 0.52, 45, 0.28, 0.10, 30, 2, 1, 2, 1),
            note: RosterSourceNote + " The class spans several groups; the editable envelope uses the " +
                  "mid-range published length and beam."),

        Entry("it-zara-ca", HistoricalNations.Italy,
            "Zara class", "Heavy cruiser",
            "1930s–World War II", "built",
            "L 182.8 m o/a; B 20.62 m; 11,326–11,712 t standard",
            183, 21, 12, BowStyle.Clipper, SternStyle.Cruiser,
            Form(BodyStyle.Rounded, 0.62, 0.22, -0.34, 0, 0.12, 0.56, 44, 0.32, 0.12, 29, 3, 1, 2, 1)),

        Entry("it-littorio-bb", HistoricalNations.Italy,
            "Littorio class", "Fast battleship",
            "World War II", "built (3 completed, 1 cancelled)",
            "L 237.76 m o/a; B 32.82 m; 40,724 t standard",
            238, 33, 16, BowStyle.Clipper, SternStyle.Transom,
            Form(BodyStyle.U, 0.78, 0.28, -0.28, 0, 0.15, 0.68, 40, 0.40, 0.15, 27, 4, 2, 3, 2)),

        Entry("it-dante-alighieri-bb", HistoricalNations.Italy,
            "Dante Alighieri", "Dreadnought battleship",
            "1913–1928", "built",
            "L 168.1 m o/a; B 26.6 m; 19,552 t normal",
            168, 27, 14, BowStyle.Pointed, SternStyle.Transom,
            Form(BodyStyle.Rounded, 0.74, 0.24, -0.34, 0, 0.12, 0.58, 42, 0.36, 0.12, 28, 3, 1, 2, 1)),

        // ── Germany ─────────────────────────────────────────────────────────────────
        Entry("de-s-boat", HistoricalNations.Germany,
            "S-boat (Schnellboot)", "Fast attack craft (E-boat)",
            "World War II", "built",
            "L 34.94 m (S-100 type); B 5.28 m; 112 t full",
            35, 5, 4, BowStyle.Raked, SternStyle.Transom,
            Form(BodyStyle.HardChine, 0.05, 0.18, 0.80, 0, -0.05, 0.32, 32, 0.08, 0.10, 28, 1, 0, 1, 0)),

        Entry("de-type-1936-dd", HistoricalNations.Germany,
            "Type 1936 destroyers", "Destroyer (Zerstörer)",
            "World War II", "built",
            "L 123.4–125.1 m o/a; B 11.75 m; 2,411 t standard",
            124, 11, 8, BowStyle.Raked, SternStyle.Transom,
            Form(BodyStyle.Rounded, 0.42, 0.16, -0.44, 0, 0.06, 0.46, 45, 0.22, 0.08, 30, 2, 1, 2, 1)),

        Entry("de-konigsberg-cl", HistoricalNations.Germany,
            "Königsberg class (1927)", "Light cruiser",
            "1920s–World War II", "built",
            "L 174 m; B 15.3 m; 6.28 m draft",
            174, 15, 10, BowStyle.Raked, SternStyle.Cruiser,
            Form(BodyStyle.Rounded, 0.50, 0.18, -0.42, 0, 0.08, 0.48, 44, 0.26, 0.10, 30, 2, 1, 2, 1)),

        Entry("de-admiral-hipper-ca", HistoricalNations.Germany,
            "Admiral Hipper class", "Heavy cruiser",
            "World War II", "built",
            "L 202.8 m o/a; B 21.3 m; 7.2 m draft",
            203, 21, 12, BowStyle.Clipper, SternStyle.Cruiser,
            Form(BodyStyle.Rounded, 0.62, 0.22, -0.34, 0, 0.12, 0.56, 44, 0.32, 0.12, 29, 3, 1, 2, 1)),

        Entry("de-o-class-cc", HistoricalNations.Germany,
            "O class", "Battlecruiser",
            "pre-World War II", "planned / cancelled",
            "L 246 m wl; B 30 m; ~28,900–31,652 t",
            246, 29, 16, BowStyle.Clipper, SternStyle.Cruiser,
            Form(BodyStyle.Rounded, 0.72, 0.26, -0.30, 0, 0.14, 0.62, 42, 0.38, 0.14, 28, 3, 1, 2, 1),
            note: RosterSourceNote + " This design was planned and cancelled, so it is a paper starting " +
                  "envelope rather than a completed ship."),

        Entry("de-bismarck-bb", HistoricalNations.Germany,
            "Bismarck class", "Fast battleship",
            "World War II", "built",
            "L 251 m o/a; B 36 m; 41,700 t standard",
            251, 35, 17, BowStyle.Clipper, SternStyle.Transom,
            Form(BodyStyle.U, 0.82, 0.30, -0.28, 0, 0.15, 0.70, 40, 0.42, 0.16, 27, 4, 2, 3, 2)),

        Entry("de-nassau-bb", HistoricalNations.Germany,
            "Nassau class", "Dreadnought battleship",
            "1909–World War I", "built",
            "L 146.1 m; B 26.9 m; 8.76 m draft",
            146, 27, 14, BowStyle.Pointed, SternStyle.Transom,
            Form(BodyStyle.Rounded, 0.74, 0.24, -0.34, 0, 0.12, 0.58, 42, 0.36, 0.12, 28, 3, 1, 2, 1)),

        // ── Russia / Soviet Union ───────────────────────────────────────────────────
        Entry("ru-mo-class", HistoricalNations.RussiaSovietUnion,
            "MO class (small hunter)", "Patrol boat / submarine chaser",
            "World War II", "built",
            "L 26.9 m; B 4.02 m; 50.6 t standard",
            27, 5, 4, BowStyle.Raked, SternStyle.Transom,
            Form(BodyStyle.HardChine, 0.05, 0.15, 0.80, 0, -0.05, 0.30, 32, 0.08, 0.10, 28, 1, 0, 1, 0)),

        Entry("ru-g5-mtb", HistoricalNations.RussiaSovietUnion,
            "G-5 class", "Motor torpedo boat",
            "1930s–World War II", "built",
            "L 18.85–19.1 m; B 3.5 m; 16.26 t standard",
            19, 5, 3, BowStyle.Raked, SternStyle.Transom,
            Form(BodyStyle.HardChine, 0.0, 0.12, 0.85, 0, -0.08, 0.28, 30, 0.05, 0.08, 28, 1, 0, 1, 0)),

        Entry("ru-gnevny-dd", HistoricalNations.RussiaSovietUnion,
            "Gnevny class (Project 7)", "Destroyer",
            "1930s–World War II", "built",
            "L 112.8 m; B 10.2 m; 1,612 t standard",
            113, 11, 8, BowStyle.Raked, SternStyle.Transom,
            Form(BodyStyle.Rounded, 0.40, 0.16, -0.44, 0, 0.06, 0.46, 45, 0.22, 0.08, 30, 2, 1, 2, 1)),

        Entry("ru-kirov-cruiser", HistoricalNations.RussiaSovietUnion,
            "Kirov class (Project 26)", "Cruiser (light/heavy disputed)",
            "1930s–World War II", "built",
            "L 191.3 m; B 17.66 m; 7,890 t standard",
            191, 17, 11, BowStyle.Raked, SternStyle.Cruiser,
            Form(BodyStyle.Rounded, 0.52, 0.18, -0.42, 0, 0.10, 0.50, 44, 0.28, 0.10, 30, 2, 1, 2, 1),
            note: RosterSourceNote + " Sources dispute whether the 180 mm-armed Kirov class is a light or " +
                  "heavy cruiser; the dispute is recorded rather than resolved."),

        Entry("ru-kronshtadt-cc", HistoricalNations.RussiaSovietUnion,
            "Kronshtadt class (Project 69)", "Battlecruiser",
            "pre-World War II", "planned / cancelled",
            "L 250.5 m o/a; B 31.6 m; 39,660 t standard",
            250, 31, 16, BowStyle.Clipper, SternStyle.Cruiser,
            Form(BodyStyle.Rounded, 0.72, 0.26, -0.30, 0, 0.14, 0.62, 42, 0.38, 0.14, 28, 3, 1, 2, 1),
            note: RosterSourceNote + " This design was planned and cancelled, so it is a paper starting " +
                  "envelope rather than a completed ship."),

        Entry("ru-sovetsky-soyuz-bb", HistoricalNations.RussiaSovietUnion,
            "Sovetsky Soyuz class (Project 23)", "Battleship",
            "pre-World War II", "planned / cancelled",
            "L 269.4 m o/a; B 38.9 m; 59,150 t standard",
            269, 39, 17, BowStyle.Clipper, SternStyle.Transom,
            Form(BodyStyle.U, 0.82, 0.30, -0.28, 0, 0.15, 0.70, 40, 0.42, 0.16, 27, 4, 2, 3, 2),
            note: RosterSourceNote + " This design was planned and cancelled, so it is a paper starting " +
                  "envelope rather than a completed ship."),

        Entry("ru-gangut-bb", HistoricalNations.RussiaSovietUnion,
            "Gangut class", "Dreadnought battleship",
            "1914–World War II", "built",
            "L 181.2 m; B 26.9 m; 24,800 t",
            181, 27, 14, BowStyle.Pointed, SternStyle.Transom,
            Form(BodyStyle.Rounded, 0.74, 0.24, -0.34, 0, 0.12, 0.58, 42, 0.36, 0.12, 28, 3, 1, 2, 1)),
    ];

    /// <summary>Finds one entry by its stable id, or null when the id is unknown.</summary>
    public static HistoricalHullPreset? Find(string id) =>
        All.FirstOrDefault(preset => string.Equals(preset.Id, id, StringComparison.Ordinal));

    /// <summary>
    /// The catalog's own integrity contract: stable unique ids, non-empty presentation
    /// text, odd single-centreline widths, and Shape V2 values the validator accepts for
    /// the entry's own midship height. Returns an empty list when the catalog is sound.
    /// </summary>
    public static IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var preset in All)
        {
            if (string.IsNullOrWhiteSpace(preset.Id))
                errors.Add("A historical preset has no stable id.");
            else if (!ids.Add(preset.Id))
                errors.Add($"Two historical presets share the id '{preset.Id}'.");

            if (string.IsNullOrWhiteSpace(preset.Name) || string.IsNullOrWhiteSpace(preset.Designation))
                errors.Add($"Historical preset '{preset.Id}' is missing its display name or designation.");
            if (string.IsNullOrWhiteSpace(preset.Nation.DisplayName) ||
                string.IsNullOrWhiteSpace(preset.Nation.FlagGlyph) ||
                string.IsNullOrWhiteSpace(preset.Nation.AccentColor))
                errors.Add($"Historical preset '{preset.Id}' has incomplete nation presentation metadata.");
            if (string.IsNullOrWhiteSpace(preset.SourceNote))
                errors.Add($"Historical preset '{preset.Id}' has no source note.");

            if (preset.Width < HullParameters.MinimumWidth || preset.Width % 2 == 0)
                errors.Add($"Historical preset '{preset.Id}' needs an odd width of at least 1 m, not {preset.Width}.");
            if (preset.Length < HullParameters.MinimumLength)
                errors.Add($"Historical preset '{preset.Id}' length {preset.Length} m is below the supported minimum.");
            if (preset.Height < HullParameters.MinimumHeight)
                errors.Add($"Historical preset '{preset.Id}' height {preset.Height} m is below the supported minimum.");

            foreach (var error in preset.Shape.Validate(preset.Height))
                errors.Add($"Historical preset '{preset.Id}': {error}");

            var parameters = preset.ToParameters(HullParameters.Default);
            foreach (var error in parameters.Validate())
                errors.Add($"Historical preset '{preset.Id}': {error}");
        }

        return errors;
    }

    private static HistoricalHullPreset Entry(
        string id,
        HistoricalNation nation,
        string name,
        string designation,
        string era,
        string buildStatus,
        string publishedDimensions,
        int length,
        int width,
        int height,
        BowStyle bowStyle,
        SternStyle sternStyle,
        HullShapeSettings shape,
        string? note = null) =>
        new(id, nation, name, designation, era, buildStatus, publishedDimensions,
            length, width, height, bowStyle, sternStyle, shape, note ?? RosterSourceNote);

    /// <summary>
    /// One readable Shape V2 bundle: bow values, body values, stern values and the four
    /// whole-metre profile rises, in the order the editor presents them.
    /// </summary>
    private static HullShapeSettings Form(
        BodyStyle body,
        double bodyFullness,
        double bodySide,
        double bodyChine,
        double flatBottom,
        double bowFullness,
        double bowFlare,
        int entrance,
        double sternFullness,
        double sternSide,
        int run,
        int bowDeckRise = 0,
        int sternDeckRise = 0,
        int bowKeelRise = 0,
        int sternKeelRise = 0) =>
        new(
            new BowShapeSettings(bowFullness, bowFlare, entrance),
            new BodyShapeSettings(body, bodyFullness, bodySide, bodyChine, flatBottom),
            new SternShapeSettings(sternFullness, sternSide, run),
            new HullProfileSettings(bowDeckRise, sternDeckRise, bowKeelRise, sternKeelRise));
}
