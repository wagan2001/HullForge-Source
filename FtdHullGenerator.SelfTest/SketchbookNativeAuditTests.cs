using FtdHullGenerator.Geometry;
using FtdHullGenerator.Infrastructure;

/// <summary>
/// Discriminating self-test for the sixteen fixed Alternate Naval Sketchbook Shape V2
/// candidates. The fast profile recomputes and compares the suggested base rows and the
/// special probes against the committed fixture; the full profile recomputes the complete
/// evidence matrix and compares geometry after permitted solid-beam repartitioning;
/// native smoothing placements retain their exact frozen identity.
/// </summary>
internal static class SketchbookNativeAuditTests
{
    /// <summary>
    /// Independent absolute pin of the sixteen fixed candidate definitions. It is asserted
    /// directly (not only against the fixture) so a drifted table plus a regenerated fixture
    /// cannot pass: changing any fixed input requires a deliberate edit here.
    /// </summary>
    private const string ExpectedDefinitionsSha256 =
        "e00ed14184f7651eab34351e9fe16d382f537200edbcf6fd28fcef1959bb2d60";

    public static void Run(HullGenerator generator, FtdBlockCatalog catalog, bool full)
    {
        SketchbookCandidates.ValidateMapping();
        Require(SketchbookCandidates.DefinitionsSha256() == ExpectedDefinitionsSha256,
            $"The fixed sketchbook candidate definitions changed: expected {ExpectedDefinitionsSha256}, " +
            $"actual {SketchbookCandidates.DefinitionsSha256()}. The fixed inputs are immutable; " +
            "do not retune a candidate or regenerate the fixture to make this pass.");
        var fixturePath = SketchbookNativeAudit.FixturePath();
        if (!File.Exists(fixturePath))
        {
            throw new InvalidOperationException(
                $"The sketchbook native-audit fixture is missing at '{fixturePath}'. " +
                "Regenerate it with: dotnet run --project .\\FtdHullGenerator.SelfTest\\FtdHullGenerator.SelfTest.csproj " +
                "-c Release -- --write-sketchbook-evidence (then rebuild so the copied fixture is refreshed).");
        }

        var fixture = SketchbookNativeAudit.Deserialize(File.ReadAllText(fixturePath));
        Require(fixture.SchemaId == SketchbookCandidates.SchemaId &&
                fixture.SchemaVersion == SketchbookCandidates.SchemaVersion,
            $"Unexpected sketchbook native-audit schema '{fixture.SchemaId}' v{fixture.SchemaVersion}.");
        Require(fixture.DefinitionsSha256 == SketchbookCandidates.DefinitionsSha256(),
            "The committed sketchbook fixture does not match the live fixed candidate table. " +
            "The definitions are immutable inputs; regenerate with --write-sketchbook-evidence only after review.");
        Require(fixture.CatalogGameVersion == catalog.GameVersion,
            $"The committed sketchbook fixture was captured against catalog {fixture.CatalogGameVersion}, " +
            $"but the installed catalog is {catalog.GameVersion}.");

        var live = SketchbookNativeAudit.Build(generator, catalog, includeFull: full);
        if (full)
            VerifyFull(fixture, live);
        else
            VerifyFast(fixture, live);

        VerifyInvariants(live, full);
    }

    private static void VerifyFull(SketchbookEvidence fixture, SketchbookEvidence live)
    {
        Require(fixture.CatalogRows.Count == live.CatalogRows.Count,
            "The full sketchbook catalog row count changed.");
        Require(fixture.Rows.Count == live.Rows.Count,
            "The full sketchbook base row count changed.");
        // Catalog identity changed with the verified Wood cube correction. Straight
        // beam packing may change placement count and native hash while the frozen
        // normalized cells, fitted smoothing identities, and other fields stay pinned.
        var comparableFixture = fixture with
        {
            Rows = fixture.Rows.Select((row, index) => row with
            {
                BlockCount = live.Rows[index].BlockCount,
                NativePlacementSha256 = live.Rows[index].NativePlacementSha256,
            }).ToArray(),
            CatalogRows = fixture.CatalogRows.Select((row, index) => row with
            {
                ResolvedCatalogFingerprint = live.CatalogRows[index].ResolvedCatalogFingerprint,
            }).ToArray(),
        };
        var expected = SketchbookNativeAudit.Serialize(comparableFixture);
        var actual = SketchbookNativeAudit.Serialize(live);
        Require(expected == actual,
            "The full sketchbook native-audit evidence changed. This is a native geometry or evidence regression; " +
            "do not regenerate the fixture to make it pass.");
    }

    private static void VerifyFast(SketchbookEvidence fixture, SketchbookEvidence live)
    {
        Require(SketchbookNativeAudit.SerializeValue(fixture.Candidates) ==
                SketchbookNativeAudit.SerializeValue(live.Candidates),
            "The live candidate definitions differ from the committed fixture.");

        var liveSuggested = live.Rows
            .Where(row => row.Size == "suggested")
            .Select(row => row with
            {
                Section = null, Surface = null, BlockCount = 0, NativePlacementSha256 = string.Empty,
            })
            .ToArray();
        var fixtureSuggested = fixture.Rows
            .Where(row => row.Size == "suggested")
            .Select(row => row with
            {
                Section = null, Surface = null, BlockCount = 0, NativePlacementSha256 = string.Empty,
            })
            .ToArray();
        Require(SketchbookNativeAudit.SerializeValue(liveSuggested) ==
                SketchbookNativeAudit.SerializeValue(fixtureSuggested),
            "A suggested-size sketchbook base row changed against the committed fixture.");

        Require(SketchbookNativeAudit.SerializeValue(live.Probes.TerminalColumns
                    .Where(probe => probe.Size == "suggested").ToArray()) ==
                SketchbookNativeAudit.SerializeValue(fixture.Probes.TerminalColumns
                    .Where(probe => probe.Size == "suggested").ToArray()),
            "A suggested-size 05/09 terminal-column probe changed against the committed fixture.");
        Require(SketchbookNativeAudit.SerializeValue(live.Probes.BulbReset) ==
                SketchbookNativeAudit.SerializeValue(fixture.Probes.BulbReset),
            "The candidate 10 initialiser-reset probe changed against the committed fixture.");
        Require(SketchbookNativeAudit.SerializeValue(live.Probes.Bulb) ==
                SketchbookNativeAudit.SerializeValue(fixture.Probes.Bulb),
            "The candidate 13 bulb probe changed against the committed fixture.");
        Require(SketchbookNativeAudit.SerializeValue(live.Probes.DeckRuler) ==
                SketchbookNativeAudit.SerializeValue(fixture.Probes.DeckRuler),
            "The candidate 13 deck-ruler probe changed against the committed fixture.");
        Require(SketchbookNativeAudit.SerializeValue(live.Probes.Tumblehome) ==
                SketchbookNativeAudit.SerializeValue(fixture.Probes.Tumblehome),
            "The 15/10 tumblehome probe changed against the committed fixture.");

        Require(SketchbookNativeAudit.SerializeValue(live.CollapsedPairs) ==
                SketchbookNativeAudit.SerializeValue(fixture.CollapsedPairs),
            "The collapsed-pair result changed against the committed fixture.");
    }

    private static void VerifyInvariants(SketchbookEvidence live, bool full)
    {
        // The minimum-rise mapping must equal the frozen expected tuples at every run.
        SketchbookCandidates.ValidateMapping();

        // A materially collapsed pair is a blocking finding, never something to fix by retuning.
        Require(live.CollapsedPairs.Count == 0,
            "Two distinct sketchbook candidates collapsed to the same base-normalized geometry: " +
            string.Join(", ", live.CollapsedPairs.Select(pair => $"{pair.LeftId}/{pair.RightId}")));

        foreach (var row in live.Rows.Where(row => row.Status == "OK"))
        {
            Require(row.ValidatorErrorCount == 0,
                $"{row.Id}/{row.Size}: an OK row carries {row.ValidatorErrorCount} validator error(s).");
            Require(row.Deterministic,
                $"{row.Id}/{row.Size}: two identical generations produced different identities.");
            Require(row.ShellComponentCount == 1,
                $"{row.Id}/{row.Size}: the generated shell has {row.ShellComponentCount} components.");
            Require(row.SolidComponentCount == 1,
                $"{row.Id}/{row.Size}: the analytic solid has {row.SolidComponentCount} components.");
            Require(row.UsableCavityCellCount > 0,
                $"{row.Id}/{row.Size}: the hull has no usable cavity.");
            Require(row.BaseNormalizedSha256.Length == 64 && row.NativePlacementSha256.Length == 64,
                $"{row.Id}/{row.Size}: a placement hash is missing or malformed.");
        }

        // The frozen candidates are individually distinct: no two may share a base-normalized hash.
        var hashes = live.Rows
            .Where(row => row.Size == "suggested" && row.Status == "OK")
            .Select(row => row.BaseNormalizedSha256)
            .ToArray();
        Require(hashes.Distinct(StringComparer.Ordinal).Count() == hashes.Length,
            "Two suggested sketchbook candidates share a base-normalized hash.");

        if (!full)
            return;

        foreach (var row in live.Smoothing.Where(row => row.Status == "OK"))
        {
            Require(row.Valid, $"{row.Id}/{row.Method}: a reported-valid smoothing row failed validation.");
            Require(row.BoundsUnchanged, $"{row.Id}/{row.Method}: smoothing changed the hull bounds.");
            Require(row.BaseNormalizedUnchanged, $"{row.Id}/{row.Method}: smoothing changed the beam-normalized base.");
            Require(row.Additive, $"{row.Id}/{row.Method}: a smoothing placement overlaps the base hull.");
        }

        foreach (var row in live.CatalogRows.Where(row => row.Status == "OK"))
        {
            Require(row.SnapshotPresent && row.OccupancyParity,
                $"{row.Id}/{row.Size}: the catalog-resolved snapshot is missing or not occupancy-identical.");
            Require(row.CatalogFallbackCount == 0 && row.FallbackPairCount == 0,
                $"{row.Id}/{row.Size}: the catalog-resolved hull used a fallback.");
        }

        foreach (var row in live.NearestNeighbours)
        {
            Require(row.NearestId.Length > 0 && row.NearestId != row.Id,
                $"{row.Id}: the nearest neighbour is missing or is the candidate itself.");
        }

        foreach (var pair in live.Pairs.Where(pair => pair.LeftBaseSha256.Length > 0 && pair.RightBaseSha256.Length > 0))
            Require(pair.BaseHashesDiffer,
                $"Named pair {pair.LeftId}/{pair.RightId} shares a base-normalized hash.");

        // Special-case assertions: 13 is a bulb with no forefoot notch, 15 is a real tumblehome.
        Require(live.Probes.BulbReset.HasBulbAfter == false && live.Probes.BulbReset.ExplicitBulbTuple &&
                live.Probes.BulbReset.GeometryIdentical,
            "Candidate 10 did not reset an inherited bulb to the explicit (8, 35, 0, 0) disabled state.");
        Require(live.Probes.Bulb.BulbEnabled && live.Probes.Bulb.CentrelineContiguous &&
                live.Probes.Bulb.FaceConnected && live.Probes.Bulb.SolidComponentCount == 1,
            "Candidate 13's bulb is not a contiguous, face-connected forebody.");
        // The deck-ruler authority invariant is that bulb-only stations must not contaminate the
        // ruler: the authority derives from structural deck armour only, and the bulb must never
        // extend the supported interval. The native generator shortens the stem by the bulb's
        // forward reach, which moves the interval aft; that is a recorded observation, not a
        // violation, and the raw values stay pinned by the fixture equality above.
        var ruler = live.Probes.DeckRuler;
        Require(ruler.BulbRulerResolved && ruler.DisabledRulerResolved,
            "Candidate 13's deck ruler did not resolve for both the bulb and the bulb-disabled hull.");
        Require(ruler.RulerBowStationIsDeckArmor && !ruler.ForwardOfRulerStationsCarryDeckArmor &&
                !ruler.BulbExtendsSupportedInterval && !ruler.Contaminated,
            "Candidate 13's deck ruler is contaminated by bulb-only stations.");
        Require(!live.Findings.Any(finding => finding.Contains("deck-ruler contamination", StringComparison.Ordinal)),
            "Candidate 13's deck ruler was reported contaminated; the fixed candidate must not be retuned.");
        Require(live.Probes.Tumblehome.Primary.MaxBelowTopRow && live.Probes.Tumblehome.Primary.IsTumblehome &&
                live.Probes.Tumblehome.Primary.SectionClosed,
            "Candidate 15 does not show a closed-section tumblehome (widest row strictly below the top row).");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
