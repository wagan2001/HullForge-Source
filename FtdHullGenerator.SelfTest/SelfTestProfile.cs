using FtdHullGenerator.Domain;
using FtdHullGenerator.Geometry;
using FtdHullGenerator.Infrastructure;

internal enum SelfTestProfile
{
    Fast,
    Full,
    Experimental,
    All,
}

internal sealed record TestContext(
    HullGenerator Generator,
    FtdBlockCatalog Catalog,
    bool Full,
    bool Experimental);

internal static class SelfTestProfiles
{
    public const int MaximumWorkers = 64;

    public static IEnumerable<SmoothingMethod> ProductSmoothingMethods =>
        Enum.GetValues<SmoothingMethod>()
            .Where(method => method is not SmoothingMethod.None and not SmoothingMethod.InvertedTriangleFill);

    /// <summary>
    /// Resolves a requested worker count for the deterministic matrices. Non-positive
    /// requests mean "auto" and follow the CLI's processor-count default; explicit
    /// requests are bounded to [1, <see cref="MaximumWorkers"/>].
    /// </summary>
    public static int ResolveWorkerCount(int requested) => Math.Clamp(
        requested <= 0 ? Environment.ProcessorCount : requested,
        1,
        MaximumWorkers);
}

internal sealed record SelfTestArguments(
    SelfTestProfile Profile,
    bool List,
    int WorkerCount,
    bool Timings,
    string? Error)
{
    public static SelfTestArguments Fast { get; } = new(SelfTestProfile.Fast, false, Environment.ProcessorCount, false, null);

    /// <summary>When set, the process runs only the sketchbook evidence writer and exits.</summary>
    public bool WriteSketchbookEvidence { get; init; }
}

internal static class SelfTestArgumentParser
{
    public const string Usage =
        "Usage: dotnet run --project .\\FtdHullGenerator.SelfTest\\FtdHullGenerator.SelfTest.csproj -c Release -- [--profile fast|full|experimental|all] [--workers N] [--timings] [--list] [--write-sketchbook-evidence]";

    public static SelfTestArguments Parse(string[] args)
    {
        if (args.Length == 0)
            return SelfTestArguments.Fast;

        if (args.Length == 1 && string.Equals(args[0], "--list", StringComparison.OrdinalIgnoreCase))
            return new SelfTestArguments(SelfTestProfile.Fast, true, Environment.ProcessorCount, false, null);

        SelfTestProfile? profile = null;
        var list = false;
        var timings = false;
        var writeSketchbookEvidence = false;
        var workers = Environment.ProcessorCount;
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (string.Equals(argument, "--list", StringComparison.OrdinalIgnoreCase))
            {
                list = true;
                continue;
            }

            if (string.Equals(argument, "--write-sketchbook-evidence", StringComparison.OrdinalIgnoreCase))
            {
                writeSketchbookEvidence = true;
                continue;
            }

            if (string.Equals(argument, "--timings", StringComparison.OrdinalIgnoreCase))
            {
                timings = true;
                continue;
            }

            if (string.Equals(argument, "--workers", StringComparison.OrdinalIgnoreCase))
            {
                if (++index >= args.Length)
                    return new SelfTestArguments(default, false, 0, false,
                        "The --workers option requires a value.");
                if (!TryParseWorkers(args[index], out workers))
                    return new SelfTestArguments(default, false, 0, false,
                        $"Invalid worker count '{args[index]}'.");
                continue;
            }

            if (string.Equals(argument, "--profile", StringComparison.OrdinalIgnoreCase))
            {
                if (++index >= args.Length)
                    return new SelfTestArguments(default, false, 0, false, "The --profile option requires a value.");
                if (!TryParseProfile(args[index], out var parsed))
                    return new SelfTestArguments(default, false, 0, false, $"Unknown profile '{args[index]}'.");
                profile = parsed;
                continue;
            }

            const string prefix = "--profile=";
            if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var value = argument[prefix.Length..];
                if (!TryParseProfile(value, out var parsed))
                    return new SelfTestArguments(default, false, 0, false, $"Unknown profile '{value}'.");
                profile = parsed;
                continue;
            }

            const string workersPrefix = "--workers=";
            if (argument.StartsWith(workersPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var value = argument[workersPrefix.Length..];
                if (!TryParseWorkers(value, out workers))
                    return new SelfTestArguments(default, false, 0, false,
                        $"Invalid worker count '{value}'.");
                continue;
            }

            return new SelfTestArguments(default, false, 0, false, $"Unknown option '{argument}'.");
        }

        return new SelfTestArguments(profile ?? SelfTestProfile.Fast, list, workers, timings, null)
        {
            WriteSketchbookEvidence = writeSketchbookEvidence,
        };
    }

    private static bool TryParseProfile(string value, out SelfTestProfile profile)
    {
        switch (value.ToLowerInvariant())
        {
            case "fast":
                profile = SelfTestProfile.Fast;
                return true;
            case "full":
                profile = SelfTestProfile.Full;
                return true;
            case "experimental":
                profile = SelfTestProfile.Experimental;
                return true;
            case "all":
                profile = SelfTestProfile.All;
                return true;
            default:
                profile = default;
                return false;
        }
    }

    private static bool TryParseWorkers(string value, out int workers)
    {
        if (!int.TryParse(value, out workers) || workers <= 0)
            return false;
        if (workers > SelfTestProfiles.MaximumWorkers)
            return false;
        return true;
    }
}

internal static class SelfTestGroupCatalog
{
    public static string Current { get; set; } = "core";

    private static readonly (string Name, string Profiles, string Description)[] Groups =
    [
        ("core", "fast, full", "Parameters, naming, geometry validation, construction, catalog, and export schema."),
        ("design-contracts", "fast, full", "V2 document, coordinate, arrangement, component and extension contracts."),
        ("layout", "fast, full", "Shared arrangement chain solver, linked gaps, parity and resize policy."),
        ("layout-frame", "fast, full", "Derived resolved layout frame identity/ruler algebra, odd whole-metre clear-diameter invariant, whole-metre arrangement anchor, stale/cancelled preparation suppression and editor-history isolation."),
        ("hull-context", "fast, full", "Read-only hull/armor/cavity context, reserved air, local deck/floor and deck masks."),
        ("barbettes", "fast, full", "Conservative bore masks, odd-hull-only product generation, digital ring walls, measured footprints, flat support, deck apertures, and protected wells."),
        ("barbette-ownership", "fast, full", "Evaluated-hull barbette reconciliation: protected skin, outermost-first truncation, clear-depth shortfall, clear separation, midpoint/forward ownership, deck cuts and preview/export identity."),
        ("barbette-safety", "fast, full", "HF-06 invalid-result gates: rejected barbettes never expand protected cells/reservations/ownership, over-budget necks fail the checked planar preflight before allocation, and the direct Chebyshev-ring perimeter is exact and cancellable."),
        ("modular-superstructures", "fast, full", "Unioned hollow box layers, interior connectivity, exact support, measured footprints, wells, pagoda data, and legacy dispatch."),
        ("internal-structure", "fast, full", "Deterministic datum-driven internal planes, parity, clipping, voids, junction priority, budgets, and conservative support."),
        ("composition-parity", "fast, full", "Feature-free context adapter parity under normalized occupancy and exact native smoothing identity."),
        ("armor-seams", "fast, full", "Unequal armor ownership, reserved air, profile/fill transitions, and positive-area native contact repair."),
        ("ship-composition", "fast, full", "Measured component arrangement, protected wells/cuts, ownership, packing, catalog binding, and revision-safe resolved export."),
        ("decoration-codec", "fast, full", "Lossless opaque decoration preservation, segmented lengths, malformed-input rejection, and revision-safe export binding."),
        ("native-extension-contract", "fast, full", "Explicit 5-40 m extension contract, donor boundaries, scale limit and unchanged starting edge."),
        ("handmade-decoration-evidence", "fast, full", "Immutable handmade V/H sources, all 96 raw records, host identity, mirror pairs and D02 byte identity."),
        ("vertical-slope-extension", "fast, full", "Explicit Vertical extensions: 64 golden payloads, 5-40 m, materials, final anchors, mirrors, cancellation and codec limits."),
        ("horizontal-slope-extension", "fast, full", "Explicit Horizontal extensions: 32 golden records, 5-40 m, materials, source binding, occupancy and payload limits."),
        ("resolved-slope-refinement", "fast, full", "Shared preview/export snapshot, persistence, stale rejection, native parity and payload limits."),
        ("deco-slope-selection", "fast, full", "Normal Deco Vertical/Horizontal automatic anchor discovery, longest-valid 5-40 m selection, shorter-donor fallback, native parity, mirroring and preview/export agreement."),
        ("historical-authoring", "fast, full", "Bounded source import, independent fitting, exact sampled fallback composition, fidelity, packaging, CLI safety, and runtime binding."),
        ("historical-library", "fast, full", "Historical catalog integrity, nation-as-metadata, representative generation/export, and editor application/independence through the compatibility apply path (no UI surface)."),
        ("alternate-naval-sketchbook", "fast, full", "Immutable 16-entry Alternate Naval Sketchbook catalog, exact bundles, suggested/keep size policies, immutable-baseline scaling, one-revision editor apply, components/diagnostics, persistence and native generation."),
        ("sketchbook-product-surface", "fast, full", "Hull Presets product surface: one editor button and a modeless launch-opened window, 16-card catalog coverage, family grouping, minimal no-copy cards, native catalog-resolved thumbnails and bow-section insets, fixture-matched section evidence and one-transaction apply wiring."),
        ("release-candidate", "fast, full", "Integrated frozen 2.0 workflow: historical start, edited Shape V2, layered armor, multiple centerline barbettes with the ruler, Deco V/H native parity, INT01 barbette routing and resolved native export."),
        ("project-persistence", "fast, full", "Versioned .hfship envelope, explicit armor DTO adapters, atomic save/backup, access re-validation, and sidecar migration."),
        ("editor-state", "fast, full", "Immutable project transactions, savepoints, recovery, lossless control projection, and revision-safe preview/export."),
        ("workspace-shell", "fast, full", "Docked and detached workspace identity, draft/history resolution, preview invalidation, focus, shortcuts, and presentation preferences."),
        ("barbette-workspace", "fast, full", "Frozen barbette editor and drag ruler: one-metre snapping, independent drag, selection, exact margins/clear gaps, invalid-placement feedback and transaction behavior."),
        ("internal-structure-editor", "fast, full", "Developer-gated three-family editor, full-range input, diagnostics, persistence/export, guides and provenance cutaway selection."),
        ("frozen-product-surface", "fast, full", "Frozen 2.0 exposure matrix, absent deferred UI surface, normal Deco V/H and barbettes, and Grid/Ocean presentation/export invariance."),
        ("stabilization-team-c", "fast, full", "HF-07/08/09 plus the HF-03/05 product wiring: barbette Construction surface absent, BAR019 rejection, revision-matched resolved diagnostics, top-offset wording, one catalog-resolved export-authoritative plain-hull snapshot, first-Add against the derived frame, no background datum commit, stale frame suppression and the odd whole-metre diameter UI."),
        ("sketchbook-native-audit", "fast, full", "Native reproduction of the sixteen fixed Alternate Naval Sketchbook Shape V2 bundles: exact initializer mapping, suggested/minimum bounds/occupancy/hash/cavity/role/connectivity/section evidence, smoothing comparison, catalog/export parity and the 03/16, 05/09, 06/10, 10/15, 01/12, 13 and 15 probes."),
        ("quality", "fast, full", "Smoothing quality, surface coverage, fairness, and truncation checks."),
        ("editor", "fast, full", "Editor policy, naming, randomized shape smoke, and export."),
        ("superstructure", "fast, full", "Composite construction smoke; the full profile adds style/level/material matrices."),
        ("shape-v2", "fast, full", "Regional shape behavior, the slider-first 2.0 body-form experience, the widened envelope, and representative end styles; the full profile adds the 252-case matrix."),
        ("automatic-baseline", "full", "Immutable common-base automatic-smoothing corpus, holdout, export-effective catalog fingerprint, and fixed render contract."),
        ("slope-fills", "fast, full", "Frozen oracle checks and lightweight edge cases; the full profile adds 96-variant sweeps and 300m timing."),
        ("presets-and-styles", "fast, full", "Animal roster checks and a style smoke; the full profile adds both preset sizes and the 1,152-case sweep."),
        ("bulbs-and-profiles", "fast, full", "Representative bulb and profile-cut behavior; the full profile adds broader performance and matrix coverage."),
        ("envelopes", "fast, full", "Native block envelopes, face winding, volume, and footprint invariants."),
        ("experimental-construction", "experimental, all", "Deferred inverted-triangle construction, native construction geometry, and export."),
    ];

    public static void Print(TextWriter writer)
    {
        writer.WriteLine("Hull Forge self-test groups:");
        foreach (var (name, profiles, description) in Groups)
            writer.WriteLine($"  {name,-24} {profiles,-22} {description}");
    }
}
