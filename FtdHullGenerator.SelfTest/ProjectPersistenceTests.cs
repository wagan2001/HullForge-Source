using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Components;
using FtdHullGenerator.Domain.Design;
using FtdHullGenerator.Domain.Layout;
using FtdHullGenerator.Domain.Projects;
using FtdHullGenerator.Domain.Superstructures;
using FtdHullGenerator.Infrastructure.Projects;
using FtdHullGenerator.Serialization.Projects;
using FtdHullGenerator.Serialization.Projects.Migrations;

/// <summary>
/// DOC-01 and DOC-02/03: the persisted envelope, the explicit DTO adapters for the types that do
/// not round-trip, the bounded reader, atomic replacement with a backup, and the supported
/// generation-parameter sidecar migration. Expectations are written out by hand rather than
/// derived from the serializer being tested.
/// </summary>
internal static class ProjectPersistenceTests
{
    public static void Run(bool full)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"HullForgeProjectTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            VerifyRichRoundTrip();
            VerifyEvenWidthBarbetteRejectedAfterLoad();
            VerifyLegacyBarbetteMigrationIsExplicit();
            VerifyUnavailableRefinementRejectedAfterLoad();
            VerifyLegacyInternalStructureDefaults();
            VerifyCanonicalDeterminism();
            VerifyNoDotNetTypeNamesOrDerivedValues();
            VerifyCorruptAndTruncatedRejected();
            VerifyUnsupportedFormatRejected();
            VerifyFutureMajorRejected();
            VerifyInvalidValuesRejected();
            VerifyUnsafeNamesRejected();
            VerifyByteAndArrayBudgets();
            VerifyAtomicSaveAndBackup(tempRoot);
            VerifyFailedSavePreservesOriginal(tempRoot);
            VerifyMissingDirectoryReportsError(tempRoot);
            VerifyAccessAndEngineValidationAfterLoad(tempRoot);
            VerifyDependencyValidation(tempRoot);
            VerifyGenerationParametersMigration();
            VerifyMigrationFallbackAndRejections();
            VerifyNativeBlueprintRejected();
            VerifyMigrationRegistryIsNamedAndDeterministic();
            if (full)
                VerifyManyLayerRoundTrip();
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }

        Console.WriteLine(
            "Project persistence: envelope round trip with non-trivial armor, internals, barbettes, " +
            "superstructure, smoothing intent, datum and style provenance; corrupt/future/bounded " +
            "rejection; atomic save with backup; maturity re-validation; sidecar migration passed.");
    }

    // ---------------------------------------------------------------------------------------------
    // Round trip
    // ---------------------------------------------------------------------------------------------

    private static void VerifyRichRoundTrip()
    {
        var document = RichDocument();
        Require(!document.Validate().HasErrors(), $"The rich fixture must validate. {document.Validate().Summary()}");

        var serialized = ProjectDocumentSerializer.Serialize(document);
        Require(serialized.Succeeded, $"The rich document must serialize. {Describe(serialized.Diagnostics)}");
        var json = serialized.Json!;

        var read = ProjectDocumentSerializer.Deserialize(json);
        Require(read.Succeeded, $"The rich document must read back. {Describe(read.Diagnostics)}");
        var restored = read.Document!;

        // The critical hazard: ArmorLayout used to throw and ArmorLayer used to become Air.
        Require(restored.Hull.HullArmor.Equals(document.Hull.HullArmor),
            "The hull armor stack did not round-trip by value.");
        Require(restored.Hull.HullArmor.Layers.Count == 3 &&
                restored.Hull.HullArmor.Layers[1].IsAir &&
                restored.Hull.HullArmor.Layers[2].Material == MaterialKind.HeavyArmor &&
                restored.Hull.HullArmor.Layers[2].Construction == ArmorConstruction.Pole,
            "The air gap and pole layer did not survive: the middle layer must be air and the inner " +
            "layer must be HeavyArmor Pole.");
        Require(restored.Hull.DeckArmor!.Equals(document.Hull.DeckArmor!),
            "The deck armor stack did not round-trip.");
        Require(restored.Hull.BottomArmor!.Equals(document.Hull.BottomArmor!) &&
                restored.Hull.BottomArmor.Layers[0].Construction == ArmorConstruction.BeamSlopeDown,
            "The bottom armor beam-slope layer did not round-trip.");
        Require(restored.Hull.EffectiveBottomArmor.Equals(document.Hull.EffectiveBottomArmor),
            "The effective bottom-armor fallback changed.");

        Require(restored.Hull.Length == 120 && restored.Hull.Width == 21 && restored.Hull.Height == 14 &&
                restored.Hull.Smoothing == SmoothingMethod.HybridSlopeFill &&
                restored.Hull.HybridFillOffset == 3 &&
                restored.Hull.BowStyle == BowStyle.Raked &&
                restored.Hull.SternStyle == SternStyle.Cruiser,
            "The scalar hull parameters did not round-trip.");
        Require(restored.Hull.HasBulb && restored.Hull.Bulb == document.Hull.Bulb,
            "The bulb settings did not round-trip.");
        Require(restored.Hull.Shape!.Bow == document.Hull.Shape!.Bow &&
                restored.Hull.Shape.Body == document.Hull.Shape.Body &&
                restored.Hull.Shape.Stern == document.Hull.Shape.Stern &&
                restored.Hull.Shape.Profile == document.Hull.Shape.Profile,
            "The Shape V2 controls did not round-trip.");
        Require(restored.Hull.Superstructure == document.Hull.Superstructure,
            "The legacy superstructure settings did not round-trip.");

        Require(restored.Datum == document.Datum, "The layout datum did not round-trip.");
        Require(restored.Datum!.LayoutBowZ.Metres == 159.5 && restored.Datum.CenterPlaneX.Metres == 10,
            "The half-metre bow datum did not survive exactly.");

        Require(restored.Internals.RequestedPlaneCount == 6 &&
                restored.Internals.Find(InternalPlaneFamily.LongitudinalBulkhead) is { Enabled: true, Count: 4 } &&
                restored.Internals.Find(InternalPlaneFamily.InternalDeck) is { Enabled: true, Count: 2 } &&
                restored.Internals.Find(InternalPlaneFamily.TransverseBulkhead) is { Enabled: false },
            "The internal structure families did not round-trip.");
        Require(restored.Internals.Find(InternalPlaneFamily.LongitudinalBulkhead)!.Spacing.Metres == 8 &&
                restored.Internals.Find(InternalPlaneFamily.InternalDeck)!.Offset.Metres == 2,
            "The internal family measures did not round-trip.");
        var restoredLongitudinal = restored.Internals.Find(InternalPlaneFamily.LongitudinalBulkhead)!;
        var restoredDeck = restored.Internals.Find(InternalPlaneFamily.InternalDeck)!;
        Require(restoredLongitudinal.SpacingKind == InternalSpacingKind.ClearCompartmentGap &&
                restoredLongitudinal.Datum == InternalPlaneDatum.CenterPlane &&
                restoredLongitudinal.Direction == InternalRepeatDirection.Both &&
                !restoredLongitudinal.IncludeCentralPlane &&
                restoredLongitudinal.RepetitionExtent == new DesignSpan(
                    DesignMeasure.FromMetres(-8), DesignMeasure.FromMetres(8)),
            "The longitudinal spacing/datum/direction/extent intent did not round-trip.");
        Require(restoredDeck.CountMode == InternalPlaneCountMode.RepeatToBoundary &&
                restoredDeck.Datum == InternalPlaneDatum.HullOrigin &&
                restoredDeck.Direction == InternalRepeatDirection.Positive &&
                restored.Internals.JunctionPriority.SequenceEqual([
                    InternalPlaneFamily.InternalDeck,
                    InternalPlaneFamily.TransverseBulkhead,
                    InternalPlaneFamily.LongitudinalBulkhead,
                ]),
            "The internal repeat mode or explicit junction priority did not round-trip.");

        Require(restored.Barbettes.Length == 2 &&
                restored.Barbettes[0].ClearDiameter.Metres == 9 &&
                restored.Barbettes[0].ClearDepthMetres == 3 &&
                restored.Barbettes[0].TopOffsetMetres == 1 &&
                restored.Barbettes[0].SideArmor.Thickness == 2 &&
                restored.Barbettes[0].RoofArmor.Layers[0].Material == MaterialKind.Wood &&
                restored.Barbettes[0].BottomArmor.Layers[1].Material == MaterialKind.Lead &&
                restored.Barbettes[0].NeckClearSizeMetres == 3 &&
                restored.Barbettes[0].NeckArmor.Thickness == 1,
            "The frozen clear-volume barbette definitions did not round-trip.");
        Require(restored.Barbettes[1].Id == "barbette-b" && restored.Barbettes[1].NodeId == "ring-b" &&
                restored.Barbettes[1].NeckClearSizeMetres == 1,
            "The second barbette identity did not round-trip.");

        Require(restored.Superstructure.Enabled &&
                restored.Superstructure.ModuleCount == 2 &&
                restored.Superstructure.Layers.Length == 2 &&
                restored.Superstructure.Layers[0].Modules[0].Length.Metres == 12 &&
                restored.Superstructure.TowerRoots.Length == 1 &&
                restored.Superstructure.TowerRoots[0].OffsetFromSpanCenter.Metres == 2 &&
                restored.Superstructure.TowerRoots[0].AllowableOffsetRange?.Metres == 3,
            "The modular superstructure did not round-trip.");
        Require(restored.Superstructure.Legacy is { UseLegacyGenerator: true } &&
                restored.Superstructure.Legacy.Settings.Levels == 4,
            "The legacy superstructure compatibility settings did not round-trip.");

        Require(restored.Smoothing == document.Smoothing &&
                restored.Smoothing.Refinement == DecorativeRefinementKind.None &&
                restored.Smoothing.AlgorithmVersion == 2 &&
                restored.Smoothing.RefinementMaxRunMetres == DesignLimits.LegacyRefinementRunMetres,
            "The smoothing intent did not round-trip.");
        Require(restored.AppliedStyle == document.AppliedStyle &&
                restored.AppliedStyle!.StyleId == "style-german" &&
                restored.AppliedStyle.CopiedFields.Dimensions == false,
            "The style provenance did not round-trip.");
        Require(restored.Extensions!.Values.Count == 2 &&
                restored.Extensions.Values["future.payload"] == "kept",
            "The bounded extension payload did not round-trip.");

        Require(restored.Arrangement.Nodes.Length == 3 &&
                restored.Arrangement.NamedGaps.Length == 1 &&
                restored.Arrangement.NamedGaps[0].Value.Metres == 5 &&
                restored.Arrangement.ResolveGapValue(restored.Arrangement.Gaps[0]).Metres == 5,
            "The arrangement, including the shared named gap, did not round-trip.");
        Require(restored.Arrangement.FindNode("span")!.Handles.Length == 1 &&
                restored.Arrangement.FindNode("span")!.Handles[0].OffsetFromNodeCenter.Metres == 2,
            "The arrangement handles did not round-trip.");
    }

    private static void VerifyCanonicalDeterminism()
    {
        var document = RichDocument();
        var first = ProjectDocumentSerializer.Serialize(document).Json!;
        var second = ProjectDocumentSerializer.Serialize(document).Json!;
        Require(first == second, "Serializing the same document twice must be byte-for-byte identical.");

        var restored = ProjectDocumentSerializer.Deserialize(first).Document!;
        var reserialized = ProjectDocumentSerializer.Serialize(restored).Json!;
        Require(first == reserialized, "A deserialize/serialize cycle must be canonical.");
    }

    private static void VerifyEvenWidthBarbetteRejectedAfterLoad()
    {
        var source = RichDocument();
        var root = JsonNode.Parse(ProjectDocumentSerializer.Serialize(source).Json!)?.AsObject()
            ?? throw new InvalidOperationException("The canonical project JSON must be an object.");
        root["document"]!["hull"]!["width"] = 20;

        var read = ProjectDocumentSerializer.Deserialize(root.ToJsonString());
        Require(!read.Succeeded && read.Document is not null &&
                read.Diagnostics.Any(diagnostic =>
                    diagnostic.Code == DesignDiagnosticCodes.BarbetteOddHullWidthRequired &&
                    diagnostic.Message == "Centerline barbettes require an odd hull width.") &&
                read.Document.Barbettes.SequenceEqual(source.Barbettes),
            "Loading an even-width project must retain its barbettes as invalid intent, not move or delete them.");
    }

    private static void VerifyLegacyBarbetteMigrationIsExplicit()
    {
        var source = RichDocument();
        var root = JsonNode.Parse(ProjectDocumentSerializer.Serialize(source).Json!)?.AsObject()
            ?? throw new InvalidOperationException("The canonical project JSON must be an object.");
        var barbettes = root["document"]!["barbettes"]!.AsArray();
        var legacy = barbettes[0]!.AsObject();
        foreach (var frozen in new[]
                 {
                     "clearDiameter", "clearDepthMetres", "topOffsetMetres",
                     "sideArmor", "roofArmor", "bottomArmor", "neckArmor", "neckClearSizeMetres",
                 })
            legacy.Remove(frozen);
        legacy["outerDiameter"] = 26;
        legacy["clearBoreDiameter"] = 18;
        legacy["wallThicknessMetres"] = 2;
        legacy["heightMetres"] = 3;
        legacy["wellDepthMetres"] = 2;
        legacy["material"] = nameof(MaterialKind.Metal);
        legacy["requiresFlatDeckSupport"] = true;

        var read = ProjectDocumentSerializer.Deserialize(root.ToJsonString());
        Require(!read.Succeeded &&
                read.Diagnostics.Any(diagnostic =>
                    diagnostic.Code == ProjectDiagnosticCodes.BarbetteMigrationUnsupported),
            "A pre-frozen outside-diameter barbette must be refused explicitly instead of having armor " +
            $"invented. {Describe(read.Diagnostics)}");
    }

    private static void VerifyUnavailableRefinementRejectedAfterLoad()
    {
        var source = RichDocument();
        var root = JsonNode.Parse(ProjectDocumentSerializer.Serialize(source).Json!)?.AsObject()
            ?? throw new InvalidOperationException("The canonical project JSON must be an object.");
        root["document"]!["smoothing"]!["refinement"] = nameof(DecorativeRefinementKind.VerticalRuns);
        root["document"]!["smoothing"]!["refinementMaxRunMetres"] = 8;

        var read = ProjectDocumentSerializer.Deserialize(root.ToJsonString());
        Require(!read.Succeeded && read.Document is not null &&
                read.Document.Smoothing.Refinement == DecorativeRefinementKind.VerticalRuns &&
                read.Document.Smoothing.RefinementMaxRunMetres == 8 &&
                read.Document.Smoothing.ExplicitRefinement is null &&
                read.Diagnostics.Any(diagnostic =>
                    diagnostic.Code == DesignDiagnosticCodes.DecorativeRefinementUnavailable &&
                    diagnostic.IsError),
            $"A loaded refinement request must be retained losslessly but fail closed as unavailable. " +
            $"Succeeded={read.Succeeded}; DocumentNull={read.Document is null}; " +
            $"Refinement={read.Document?.Smoothing.Refinement}; " +
            $"Run={read.Document?.Smoothing.RefinementMaxRunMetres}; " +
            $"Diagnostics={string.Join(" | ", read.Diagnostics)}");
    }

    private static void VerifyLegacyInternalStructureDefaults()
    {
        var json = ProjectDocumentSerializer.Serialize(RichDocument()).Json!;
        var root = JsonNode.Parse(json)?.AsObject()
            ?? throw new InvalidOperationException("The canonical project JSON must be an object.");
        var internals = root["document"]?["internals"]?.AsObject()
            ?? throw new InvalidOperationException("The rich fixture must contain internal structure JSON.");

        internals.Remove("junctionPriority");
        foreach (var family in internals["families"]?.AsArray() ?? [])
        {
            var familyObject = family?.AsObject()
                ?? throw new InvalidOperationException("Each internal family must be a JSON object.");
            familyObject.Remove("spacingKind");
            familyObject.Remove("datum");
            familyObject.Remove("direction");
            familyObject.Remove("countMode");
            familyObject.Remove("includeCentralPlane");
            familyObject.Remove("repetitionExtent");
        }

        var read = ProjectDocumentSerializer.Deserialize(root.ToJsonString());
        Require(read.Succeeded,
            $"A schema-v1 project written before B01 must retain its documented defaults. {Describe(read.Diagnostics)}");
        var restored = read.Document!.Internals;
        Require(restored.JunctionPriority.SequenceEqual(InternalStructure.DefaultJunctionPriority),
            "A legacy project without junction priority must receive the canonical default order.");
        Require(restored.Families.All(family =>
                family.SpacingKind == InternalSpacingKind.CenterPitch &&
                family.CountMode == InternalPlaneCountMode.FixedCount &&
                family.RepetitionExtent is null &&
                family.Datum is null && family.Direction is null) &&
                restored.Find(InternalPlaneFamily.LongitudinalBulkhead) is
                {
                    EffectiveDatum: InternalPlaneDatum.CenterPlane,
                    EffectiveDirection: InternalRepeatDirection.Both,
                } &&
                restored.Find(InternalPlaneFamily.InternalDeck) is
                {
                    EffectiveDatum: InternalPlaneDatum.HullOrigin,
                    EffectiveDirection: InternalRepeatDirection.Positive,
                },
            "A legacy internal family must receive the B01 spacing, count, extent, datum, and direction defaults.");

        var longitudinal = internals["families"]!.AsArray()[0]!.AsObject();
        longitudinal["count"] = 1;
        longitudinal["mirrorSymmetry"] = false;
        var oddRead = ProjectDocumentSerializer.Deserialize(root.ToJsonString());
        Require(oddRead.Succeeded &&
                oddRead.Document!.Internals.Find(InternalPlaneFamily.LongitudinalBulkhead) is
                { MirrorSymmetry: true, IncludeCentralPlane: true },
            $"A legacy odd longitudinal count must migrate to its symmetric central-plane meaning. {Describe(oddRead.Diagnostics)}");

        longitudinal["count"] = 2;
        var evenRead = ProjectDocumentSerializer.Deserialize(root.ToJsonString());
        Require(evenRead.Succeeded &&
                evenRead.Document!.Internals.Find(InternalPlaneFamily.LongitudinalBulkhead) is
                { MirrorSymmetry: true, IncludeCentralPlane: false },
            $"A legacy even longitudinal count must migrate to symmetric pairs. {Describe(evenRead.Diagnostics)}");
    }

    private static void VerifyNoDotNetTypeNamesOrDerivedValues()
    {
        var json = ProjectDocumentSerializer.Serialize(RichDocument()).Json!;
        foreach (var forbidden in new[] { "FtdHullGenerator", "$type", "System.", "AssemblyVersion" })
            Require(!json.Contains(forbidden, StringComparison.Ordinal),
                $"The payload leaked a .NET type marker '{forbidden}'.");

        // Computed/derived members must not be persisted, and the DTOs must use plain values.
        foreach (var forbidden in new[]
                 {
                     "usePoles", "isAir", "realizedOuterDiameter", "outerHalfExtentMetres",
                     "effectiveShape", "effectiveBulb", "overallHeight", "twiceMetres", "TwiceMetres",
                 })
            Require(!json.Contains(forbidden, StringComparison.Ordinal),
                $"The payload persisted the derived member '{forbidden}'.");

        Require(json.Contains("\"format\": \"HullForge.ShipDocument\"", StringComparison.Ordinal),
            "The envelope format identifier is missing.");
        Require(json.Contains("\"formatVersion\": 1", StringComparison.Ordinal),
            "The envelope format version is missing.");
    }

    // ---------------------------------------------------------------------------------------------
    // Rejection and budgets
    // ---------------------------------------------------------------------------------------------

    private static void VerifyCorruptAndTruncatedRejected()
    {
        foreach (var payload in new[] { "", "null", "{ not json", "[1,2,3]" })
            Require(!ProjectDocumentSerializer.Deserialize(payload).Succeeded,
                $"A malformed payload '{payload}' must be rejected.");

        var valid = ProjectDocumentSerializer.Serialize(RichDocument()).Json!;
        var truncated = valid[..(valid.Length / 2)];
        var truncatedResult = ProjectDocumentSerializer.Deserialize(truncated);
        Require(!truncatedResult.Succeeded &&
                truncatedResult.Diagnostics.Any(diagnostic => diagnostic.Code == ProjectDiagnosticCodes.InvalidJson),
            "A truncated payload must be rejected with the invalid-JSON code.");
    }

    private static void VerifyUnsupportedFormatRejected()
    {
        var result = ProjectDocumentSerializer.Deserialize(
            """{"format":"Something.Else","formatVersion":1}""");
        Require(!result.Succeeded &&
                result.Diagnostics.Any(diagnostic => diagnostic.Code == ProjectDiagnosticCodes.UnsupportedFormat),
            "An unknown format must be rejected explicitly.");
    }

    private static void VerifyFutureMajorRejected()
    {
        var valid = ProjectDocumentSerializer.Serialize(RichDocument()).Json!;

        var futureEnvelope = valid.Replace("\"formatVersion\": 1", "\"formatVersion\": 2", StringComparison.Ordinal);
        var envelopeResult = ProjectDocumentSerializer.Deserialize(futureEnvelope);
        Require(!envelopeResult.Succeeded && envelopeResult.IsReadOnlyFallback &&
                envelopeResult.Diagnostics.Any(diagnostic => diagnostic.Code == ProjectDiagnosticCodes.FutureVersion),
            "A future envelope major must be an explicit read-only fallback, not default settings.");

        var futureSchema = valid.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 99", StringComparison.Ordinal);
        var schemaResult = ProjectDocumentSerializer.Deserialize(futureSchema);
        Require(!schemaResult.Succeeded && schemaResult.IsReadOnlyFallback &&
                schemaResult.Diagnostics.Any(diagnostic => diagnostic.Code == ProjectDiagnosticCodes.FutureVersion),
            "A future document schema must be an explicit read-only fallback, not default settings.");
    }

    private static void VerifyInvalidValuesRejected()
    {
        var valid = ProjectDocumentSerializer.Serialize(RichDocument()).Json!;

        var badEnum = valid.Replace("\"smoothing\": \"HybridSlopeFill\"", "\"smoothing\": \"NotAMethod\"",
            StringComparison.Ordinal);
        Require(!ProjectDocumentSerializer.Deserialize(badEnum).Succeeded,
            "An unsupported enum name must be rejected.");

        var badInternalEnum = valid.Replace("\"spacingKind\": \"ClearCompartmentGap\"",
            "\"spacingKind\": \"NotASpacingKind\"", StringComparison.Ordinal);
        Require(!ProjectDocumentSerializer.Deserialize(badInternalEnum).Succeeded,
            "An unsupported internal spacing kind must be rejected.");

        var badPriority = valid.Replace("\"junctionPriority\": [", "\"junctionPriority\": [\"NotAFamily\",",
            StringComparison.Ordinal);
        Require(!ProjectDocumentSerializer.Deserialize(badPriority).Succeeded,
            "An unsupported internal junction-priority family must be rejected.");

        var reversedRoot = JsonNode.Parse(valid)!.AsObject();
        var reversedSpan = reversedRoot["document"]!["internals"]!["families"]![0]!["repetitionExtent"]!.AsObject();
        reversedSpan["start"] = 16;
        reversedSpan["end"] = -16;
        Require(!ProjectDocumentSerializer.Deserialize(reversedRoot.ToJsonString()).Succeeded,
            "A reversed internal repetition extent must return a diagnostic rather than throw.");

        var badCoordinate = valid.Replace("\"outerHalfExtent\": 13", "\"outerHalfExtent\": 2147483647",
            StringComparison.Ordinal);
        var coordinateResult = ProjectDocumentSerializer.Deserialize(badCoordinate);
        Require(!coordinateResult.Succeeded &&
                coordinateResult.Diagnostics.Any(diagnostic =>
                    diagnostic.Code == ProjectDiagnosticCodes.InvalidValue),
            "A coordinate outside the design range must be rejected before use.");

        var badNumber = valid.Replace("\"bowFullness\": 0.2", "\"bowFullness\": 1e999", StringComparison.Ordinal);
        var numberResult = ProjectDocumentSerializer.Deserialize(badNumber);
        Require(!numberResult.Succeeded,
            "A non-finite number must be rejected.");
    }

    private static void VerifyUnsafeNamesRejected()
    {
        var safe = ShipDocument.CreateNew("Safe", HullEditorSettings.Default);
        foreach (var unsafeName in new[] { "../escape", "sub/dir", "CON", "a\\b" })
        {
            var document = safe with { Name = unsafeName };
            var result = ProjectDocumentSerializer.Serialize(document);
            Require(!result.Succeeded &&
                    result.Diagnostics.Any(diagnostic => diagnostic.Code == ProjectDiagnosticCodes.UnsafeName),
                $"The unsafe name '{unsafeName}' must be rejected.");

            var save = ProjectStore.Save(document, Path.Combine(Path.GetTempPath(), "hf-unsafe.hfship"));
            Require(!save.Succeeded, $"Saving the unsafe name '{unsafeName}' must fail.");
        }
    }

    private static void VerifyByteAndArrayBudgets()
    {
        var oversized = new string(' ', ProjectDocumentSerializer.MaxDocumentBytes + 1);
        var oversizedResult = ProjectDocumentSerializer.Deserialize(oversized);
        Require(!oversizedResult.Succeeded &&
                oversizedResult.Diagnostics.Any(diagnostic => diagnostic.Code == ProjectDiagnosticCodes.FileTooLarge),
            "A payload past the byte budget must be rejected before parsing.");

        var hugeArray = "{\"format\":\"HullForge.ShipDocument\",\"formatVersion\":1,\"document\":{\"schemaVersion\":1," +
                        "\"barbettes\":[" +
                        string.Join(',', Enumerable.Repeat("0", ProjectDocumentSerializer.MaxArrayLength + 1)) +
                        "]}}";
        var arrayResult = ProjectDocumentSerializer.Deserialize(hugeArray);
        Require(!arrayResult.Succeeded &&
                arrayResult.Diagnostics.Any(diagnostic => diagnostic.Code == ProjectDiagnosticCodes.NestingLimit),
            "An array past the object-count budget must be rejected before binding.");
    }

    // ---------------------------------------------------------------------------------------------
    // Store: atomic save, backup and failure handling
    // ---------------------------------------------------------------------------------------------

    private static void VerifyAtomicSaveAndBackup(string tempRoot)
    {
        var path = Path.Combine(tempRoot, "atomic.hfship");
        var first = ShipDocument.CreateNew("Save one", HullEditorSettings.Default);
        var firstSave = ProjectStore.Save(first, path);
        Require(firstSave.Succeeded && firstSave.Path is not null && File.Exists(path),
            $"The first save must create the file. {Describe(firstSave.Diagnostics)}");
        Require(firstSave.BackupPath is null, "The first save has no previous file to back up.");
        Require(Directory.GetFiles(tempRoot, "*.tmp-*").Length == 0, "A temporary file was left behind.");
        var firstContent = File.ReadAllText(path);

        var second = first with { Name = "Save two" };
        var secondSave = ProjectStore.Save(second, path);
        Require(secondSave.Succeeded && secondSave.BackupPath is not null,
            $"The second save must keep a backup. {Describe(secondSave.Diagnostics)}");
        var backup = secondSave.BackupPath!;
        Require(File.Exists(backup), "The backup file must exist after a replacement.");
        Require(File.ReadAllText(backup) == firstContent,
            "The backup must hold the previous valid file.");
        Require(Directory.GetFiles(tempRoot, "*.tmp-*").Length == 0, "A temporary file was left behind.");

        var loaded = ProjectStore.Load(path);
        Require(loaded.Succeeded && loaded.Document!.Name == "Save two",
            $"The saved document must reload. {Describe(loaded.Diagnostics)}");
    }

    private static void VerifyFailedSavePreservesOriginal(string tempRoot)
    {
        var path = Path.Combine(tempRoot, "preserve.hfship");
        var valid = ShipDocument.CreateNew("Preserve", HullEditorSettings.Default);
        Require(ProjectStore.Save(valid, path).Succeeded, "The baseline save must succeed.");
        var original = File.ReadAllText(path);

        var unsafeDocument = valid with { Name = "../escape" };
        var unsafeSave = ProjectStore.Save(unsafeDocument, path);
        Require(!unsafeSave.Succeeded, "An unsafe document must not be written.");
        Require(File.ReadAllText(path) == original, "A refused save must leave the previous file untouched.");

        var invalidArrangement = valid with
        {
            Arrangement = new Arrangement(
                [ArrangementNode.Create("a", ArrangementNodeKind.Barbette, "b", DesignMeasure.FromMetres(5)),
                 ArrangementNode.Create("b", ArrangementNodeKind.Barbette, "b", DesignMeasure.FromMetres(5))],
                [], [], DesignMeasure.Zero, DesignMeasure.Zero,
                ArrangementAnchorKind.BowDatum, ArrangementResizePolicy.PreserveAbsolute, null),
        };
        var invalidSave = ProjectStore.Save(invalidArrangement, path);
        Require(!invalidSave.Succeeded, "An invalid document must not be written.");
        Require(File.ReadAllText(path) == original, "A refused save must leave the previous file untouched.");
    }

    private static void VerifyMissingDirectoryReportsError(string tempRoot)
    {
        var document = ShipDocument.CreateNew("Missing", HullEditorSettings.Default);
        var path = Path.Combine(tempRoot, "no-such-folder", "ship.hfship");
        var save = ProjectStore.Save(document, path);
        Require(!save.Succeeded && save.Path is null &&
                save.Diagnostics.Any(diagnostic => diagnostic.Code == ProjectDiagnosticCodes.WriteFailed),
            "A missing directory must report a write failure, never a simulated success.");
        Require(!File.Exists(path), "A failed write must not create the target file.");

        var badExtension = ProjectStore.Save(document, Path.Combine(tempRoot, "ship.txt"));
        Require(!badExtension.Succeeded &&
                badExtension.Diagnostics.Any(diagnostic => diagnostic.Code == ProjectDiagnosticCodes.PathUnsafe),
            "An unsupported extension must be rejected before writing.");
    }

    // ---------------------------------------------------------------------------------------------
    // Runtime exposure preservation and dependencies
    // ---------------------------------------------------------------------------------------------

    private static void VerifyAccessAndEngineValidationAfterLoad(string tempRoot)
    {
        var stableParameters = HullParameters.Default with { Smoothing = SmoothingMethod.HybridSlopeFill };
        var stable = ShipDocument.CreateNew("Stable expanded method", stableParameters);
        var path = Path.Combine(tempRoot, "stable-expanded.hfship");
        Require(ProjectStore.Save(stable, path).Succeeded, "Saving expanded stable intent must succeed.");
        Require(ProjectStore.Load(path).Succeeded,
            "A former commercial feature must load without entitlement checks.");

        var oversized = ShipDocument.CreateNew("Oversized",
            HullParameters.Default with { Length = 250, Width = 21, Height = 14 });
        var oversizedPath = Path.Combine(tempRoot, "oversized.hfship");
        Require(ProjectStore.Save(oversized, oversizedPath).Succeeded, "Saving the oversized intent must succeed.");
        Require(ProjectStore.Load(oversizedPath).Succeeded,
            "Unrestricted typed dimensions were still routed through commercial access.");

        // Loading preserves unavailable intent; generation exposure rejects it later without mutation.
        var deferred = ShipDocument.CreateNew("Deferred",
            HullParameters.Default with { Smoothing = SmoothingMethod.InvertedTriangleFill });
        var deferredPath = Path.Combine(tempRoot, "deferred.hfship");
        Require(ProjectStore.Save(deferred, deferredPath).Succeeded, "Saving the deferred intent must succeed.");
        var deferredLoad = ProjectStore.Load(deferredPath);
        Require(deferredLoad.Succeeded &&
                deferredLoad.Document!.Hull.Smoothing == SmoothingMethod.InvertedTriangleFill &&
                HullEditorSettings.ValidateFeatureAvailability(deferredLoad.Document.Hull,
                    new FeatureExposurePolicy(true)) is not null,
            "Unavailable intent did not round-trip intact or escaped the central policy.");

        var experimental = ShipDocument.CreateNew("Experimental internals", HullParameters.Default) with
        {
            Internals = new InternalStructure([
                new InternalStructureFamily(InternalPlaneFamily.TransverseBulkhead, true, 1,
                    MaterialKind.Metal, DesignMeasure.FromMetres(8), 1, DesignMeasure.Zero, false),
                InternalStructureFamily.Disabled(InternalPlaneFamily.LongitudinalBulkhead),
                InternalStructureFamily.Disabled(InternalPlaneFamily.InternalDeck),
            ]),
        };
        var experimentalPath = Path.Combine(tempRoot, "experimental.hfship");
        Require(ProjectStore.Save(experimental, experimentalPath).Succeeded,
            "Saving experimental intent must succeed.");
        var experimentalLoad = ProjectStore.Load(experimentalPath);
        Require(experimentalLoad.Succeeded && experimentalLoad.Document!.Internals.RequestedPlaneCount == 1 &&
                !FtdHullGenerator.UI.Components.InternalStructureFeatureAccess.CanPreviewOrExport(
                    experimentalLoad.Document, new FeatureExposurePolicy(false), out _) &&
                FtdHullGenerator.UI.Components.InternalStructureFeatureAccess.CanPreviewOrExport(
                    experimentalLoad.Document, new FeatureExposurePolicy(true), out _),
            "Experimental intent did not survive loading across the runtime toggle.");
    }

    private static void VerifyDependencyValidation(string tempRoot)
    {
        var historical = ShipDocument.CreateNew("Historical", HullEditorSettings.Default) with
        {
            Source = HullSource.Historical("us-iowa-1943", "1.0", "sha256-deadbeef"),
        };
        Require(!historical.Validate().HasErrors(), "A complete historical source must validate.");

        var path = Path.Combine(tempRoot, "historical.hfship");
        Require(ProjectStore.Save(historical, path).Succeeded, "Saving the historical document must succeed.");

        var uncheckedLoad = ProjectStore.Load(path, resolver: null);
        Require(uncheckedLoad.Succeeded &&
                uncheckedLoad.Diagnostics.Any(diagnostic =>
                    diagnostic.Code == ProjectDiagnosticCodes.MissingDependency && !diagnostic.IsError),
            "Without a resolver the dependency is reported as a recoverable warning.");

        var missingLoad = ProjectStore.Load(path, new FixedAssetResolver(exists: false));
        Require(!missingLoad.Succeeded &&
                missingLoad.Diagnostics.Any(diagnostic =>
                    diagnostic.Code == ProjectDiagnosticCodes.MissingDependency && diagnostic.IsError),
            "A resolver that reports a missing asset must produce an error, not a substituted hull.");

        var presentLoad = ProjectStore.Load(path, new FixedAssetResolver(exists: true));
        Require(presentLoad.Succeeded,
            $"A present asset must load cleanly. {Describe(presentLoad.Diagnostics)}");
    }

    // ---------------------------------------------------------------------------------------------
    // Sidecar migration
    // ---------------------------------------------------------------------------------------------

    private static void VerifyGenerationParametersMigration()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "generation-parameters-v1.json");
        Require(File.Exists(fixture), "The generation-parameter migration fixture is missing.");
        var json = File.ReadAllText(fixture);

        var result = ProjectDocumentSerializer.Deserialize(json);
        Require(result.Succeeded, $"The sidecar must migrate. {Describe(result.Diagnostics)}");
        Require(result.MigratedFrom == "HullForge.GenerationParameters v1",
            $"The migration provenance was '{result.MigratedFrom}'.");
        var document = result.Document!;

        Require(document.Name == "HF_RichMigration_120x21x14",
            "The migrated document must take its name from the sidecar's blueprint file.");
        var hull = document.Hull;
        Require(hull.Length == 120 && hull.Width == 21 && hull.Height == 14,
            "The migrated dimensions are wrong.");
        Require(hull.HullArmor.Layers.Count == 3 &&
                hull.HullArmor.Layers[1].IsAir &&
                hull.HullArmor.Layers[2].Material == MaterialKind.HeavyArmor &&
                hull.HullArmor.Layers[2].Construction == ArmorConstruction.Pole,
            "The migrated hull armor lost its air gap or pole layer.");
        Require(hull.DeckArmor is { } deck && deck.Thickness == 2 &&
                deck.Layers[1].Material == MaterialKind.Wood,
            "The migrated deck armor is wrong.");
        Require(hull.BottomArmor is { } bottom &&
                bottom.Layers[0].Construction == ArmorConstruction.BeamSlopeDown,
            "The migrated bottom armor is wrong.");
        Require(hull.Smoothing == SmoothingMethod.HybridSlopeFill && hull.HybridFillOffset == 3,
            "The migrated smoothing intent is wrong.");
        Require(hull.BowStyle == BowStyle.Raked && hull.SternStyle == SternStyle.Cruiser,
            "The migrated bow/stern styles are wrong.");
        Require(hull.HasBulb && hull.Bulb == new BulbSettings(10, 40, 5, 0),
            "The migrated bulb is wrong.");
        Require(hull.Shape is { } shape && shape.Bow.Fullness == 0.2 && shape.Body.FlatBottom == 0 &&
                shape.Profile.SternKeelRise == 3,
            "The migrated Shape V2 controls are wrong.");
        Require(hull.Superstructure is { Enabled: true, Levels: 4 } superstructure &&
                superstructure.Material == MaterialKind.LightweightAlloy &&
                superstructure.Smoothing == SuperstructureSmoothingMethod.HorizontalSlopeFill,
            "The migrated superstructure settings are wrong.");
        Require(document.Source.Kind == HullSourceKind.RegionalShapeV2,
            "A migrated sidecar must become a regional-shape project.");
        Require(document.Arrangement.Nodes.Length == 0 && document.Barbettes.Length == 0,
            "A migrated sidecar must not invent arrangement or barbettes.");
    }

    private static void VerifyMigrationFallbackAndRejections()
    {
        // The real writer emits the authored Shape and the effective Shape. A hand-trimmed record
        // that drops the authored one must still reconstruct the effective generation request.
        const string minimal = """
        {
          "Format": "HullForge.GenerationParameters",
          "FormatVersion": 1,
          "BlueprintFile": "minimal.blueprint",
          "GameVersion": "4.0.0",
          "Parameters": {
            "Length": 80,
            "Width": 18,
            "Height": 11,
            "BowFullness": 0.1,
            "SternFullness": 0.2,
            "CrossSectionCurve": 0.3,
            "HullArmor": [ { "Material": "Metal", "Construction": "Solid" } ],
            "DeckArmor": null,
            "Beamify": true,
            "Smoothing": "None",
            "HybridFillOffset": 1,
            "BowStyle": "Pointed",
            "SternStyle": "Transom",
            "HasBulb": false,
            "Bulb": null,
            "Shape": null,
            "BottomArmor": null,
            "Superstructure": null,
            "EffectiveShape": {
              "Bow": { "Fullness": 0.1, "Flare": 0.15, "EntranceLengthPercent": 45 },
              "Body": { "Style": "Rounded", "Fullness": 0.3, "SideShape": 0.1, "Chine": -0.65, "FlatBottom": 0.0 },
              "Stern": { "Fullness": 0.2, "SideShape": 0.0, "RunLengthPercent": 25 },
              "Profile": { "BowDeckRise": 0, "SternDeckRise": 0, "BowKeelRise": 0, "SternKeelRise": 0 }
            },
            "EffectiveBulb": { "LengthPercent": 8, "WidthPercent": 35, "ForeAftPercent": 0, "RisePercent": 0 },
            "EffectiveBottomArmor": [ { "Material": "Metal", "Construction": "Solid" } ],
            "EffectiveSuperstructure": {
              "Enabled": false, "Style": "CenterIsland", "Levels": 3, "Material": "Metal",
              "Smoothing": "None", "ForeAftPercent": 8
            }
          }
        }
        """;
        var minimalResult = ProjectDocumentSerializer.Deserialize(minimal);
        Require(minimalResult.Succeeded, $"A trimmed sidecar must migrate. {Describe(minimalResult.Diagnostics)}");
        Require(minimalResult.Document!.Hull.Shape!.Body.Style == BodyStyle.Rounded,
            "The migration must fall back to the effective shape when the authored shape is absent.");
        Require(minimalResult.Document.Hull.EffectiveBottomArmor.Thickness == 1,
            "The migration must fall back to the effective bottom armor.");

        var future = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures",
                "generation-parameters-v1.json"))
            .Replace("\"FormatVersion\": 1", "\"FormatVersion\": 2", StringComparison.Ordinal);
        var futureResult = ProjectDocumentSerializer.Deserialize(future);
        Require(!futureResult.Succeeded && futureResult.IsReadOnlyFallback &&
                futureResult.Diagnostics.Any(diagnostic => diagnostic.Code == ProjectDiagnosticCodes.FutureVersion),
            "A future sidecar version must be an explicit read-only fallback.");

        var missingParameters = """{"Format":"HullForge.GenerationParameters","FormatVersion":1}""";
        var missingResult = ProjectDocumentSerializer.Deserialize(missingParameters);
        Require(!missingResult.Succeeded &&
                missingResult.Diagnostics.Any(diagnostic =>
                    diagnostic.Code == ProjectDiagnosticCodes.MigrationUnsupported),
            "A sidecar without Parameters must be rejected.");
    }

    private static void VerifyNativeBlueprintRejected()
    {
        var blueprint = """
        {
          "Name": "HF_Test_100x21x12",
          "SavedTotalBlockCount": 3,
          "ItemDictionary": { "1": "00000000-0000-0000-0000-000000000001" },
          "Blueprint": { "BLP": [ "0,0,0" ], "BLR": [ 0 ] }
        }
        """;
        var result = ProjectDocumentSerializer.Deserialize(blueprint);
        Require(!result.Succeeded &&
                result.Diagnostics.Any(diagnostic => diagnostic.Code == ProjectDiagnosticCodes.NotAProjectInput),
            "A native .blueprint must be rejected as a project input, not silently converted.");
    }

    private static void VerifyMigrationRegistryIsNamedAndDeterministic()
    {
        Require(ProjectMigrationRegistry.Migrations.Count == 1,
            "There must be exactly one supported sidecar migration today.");
        var migration = ProjectMigrationRegistry.Migrations[0];
        Require(migration.Name == "generation-parameters-v1-to-ship-document-v1",
            $"The migration name changed to '{migration.Name}'.");
        Require(migration.SourceFormatVersion == 1, "The migration must read sidecar version 1.");
        Require(migration.CanMigrate("HullForge.GenerationParameters", 1) &&
                !migration.CanMigrate("HullForge.GenerationParameters", 2) &&
                !migration.CanMigrate("Other", 1),
            "The migration must match only its own format and version.");
    }

    // ---------------------------------------------------------------------------------------------
    // Fixtures and helpers
    // ---------------------------------------------------------------------------------------------

    private static void VerifyManyLayerRoundTrip()
    {
        // The full profile pushes the armor stack and the internal plane count well past the
        // fast-profile case, so the bounded reader is exercised on a larger real document.
        var parameters = HullParameters.Default with
        {
            Length = 200,
            Width = 25,
            Height = 18,
            HullArmor = new ArmorLayout(Enumerable.Repeat(0, 24)
                .Select(index => index % 4 == 3
                    ? ArmorLayer.Air
                    : new ArmorLayer(index % 2 == 0 ? MaterialKind.Metal : MaterialKind.HeavyArmor))),
        };
        var document = ShipDocument.CreateNew("Large stack", parameters) with
        {
            Internals = new InternalStructure([
                new InternalStructureFamily(InternalPlaneFamily.LongitudinalBulkhead, true, 1, MaterialKind.Metal,
                    DesignMeasure.FromMetres(4), 60, DesignMeasure.Zero, true),
                new InternalStructureFamily(InternalPlaneFamily.InternalDeck, true, 1, MaterialKind.Metal,
                    DesignMeasure.FromMetres(4), 30, DesignMeasure.FromMetres(2), false),
                InternalStructureFamily.Disabled(InternalPlaneFamily.TransverseBulkhead),
            ]),
        };
        Require(!document.Validate().HasErrors(), $"The large fixture must validate. {document.Validate().Summary()}");
        var json = ProjectDocumentSerializer.Serialize(document).Json!;
        var restored = ProjectDocumentSerializer.Deserialize(json);
        Require(restored.Succeeded, $"The large document must read back. {Describe(restored.Diagnostics)}");
        var restoredDocument = restored.Document!;
        Require(restoredDocument.Hull.HullArmor.Equals(document.Hull.HullArmor),
            "The 24-layer armor stack must round-trip.");
        Require(restoredDocument.Internals.RequestedPlaneCount == 90,
            "The large internal plane count must round-trip.");
    }

    private static ShipDocument RichDocument()
    {
        var parameters = HullParameters.Default with
        {
            Length = 120,
            Width = 21,
            Height = 14,
            BowFullness = 0.2,
            SternFullness = -0.15,
            CrossSectionCurve = 0.4,
            HullArmor = new ArmorLayout([
                new ArmorLayer(MaterialKind.Metal),
                ArmorLayer.Air,
                new ArmorLayer(MaterialKind.HeavyArmor, ArmorConstruction.Pole),
            ]),
            DeckArmor = new ArmorLayout([MaterialKind.Metal, MaterialKind.Wood]),
            BottomArmor = new ArmorLayout([new ArmorLayer(MaterialKind.Metal, ArmorConstruction.BeamSlopeDown)]),
            Beamify = true,
            Smoothing = SmoothingMethod.HybridSlopeFill,
            HybridFillOffset = 3,
            BowStyle = BowStyle.Raked,
            SternStyle = SternStyle.Cruiser,
            HasBulb = true,
            Bulb = new BulbSettings(10, 40, 5, 0),
            Shape = new HullShapeSettings(
                new BowShapeSettings(0.2, 0.25, 40),
                new BodyShapeSettings(BodyStyle.Custom, 0.4, 0.1, -0.6, 0),
                new SternShapeSettings(-0.15, 0, 25),
                new HullProfileSettings(1, 2, 0, 3)),
            Superstructure = new SuperstructureSettings(true, SuperstructureStyle.JapanesePagoda, 4,
                MaterialKind.LightweightAlloy, SuperstructureSmoothingMethod.HorizontalSlopeFill, 8),
        };

        var namedGap = new NamedArrangementGap("gun-gap", "Gun gap", ArrangementMeasure.ClearEdgeGap,
            DesignMeasure.FromMetres(5));
        var nodes = new[]
        {
            ArrangementNode.Create("ring-a", ArrangementNodeKind.Barbette, "barbette-a",
                DesignMeasure.FromMetres(6.5)),
            ArrangementNode.Create("ring-b", ArrangementNodeKind.Barbette, "barbette-b",
                DesignMeasure.FromMetres(5)),
            ArrangementNode.Create("span", ArrangementNodeKind.SuperstructureSpan, "tower",
                DesignMeasure.FromMetres(4),
                new ArrangementHandle("tower-root", "Tower root", DesignMeasure.FromMetres(2))),
        };
        var arrangement = new Arrangement(
            [.. nodes],
            [
                new ArrangementGap("gap-ab", ArrangementMeasure.ClearEdgeGap, DesignMeasure.Zero, namedGap.Id),
                new ArrangementGap("gap-bspan", ArrangementMeasure.ClearEdgeGap, DesignMeasure.FromMetres(3)),
            ],
            [namedGap],
            DesignMeasure.FromMetres(10),
            DesignMeasure.FromMetres(10),
            ArrangementAnchorKind.BowDatum,
            ArrangementResizePolicy.PreserveAbsolute,
            "gap-ab");

        var internals = new InternalStructure([
            new InternalStructureFamily(InternalPlaneFamily.LongitudinalBulkhead, true, 1, MaterialKind.Metal,
                DesignMeasure.FromMetres(8), 4, DesignMeasure.Zero, true)
            {
                SpacingKind = InternalSpacingKind.ClearCompartmentGap,
                Datum = InternalPlaneDatum.CenterPlane,
                Direction = InternalRepeatDirection.Both,
                RepetitionExtent = new DesignSpan(DesignMeasure.FromMetres(-8), DesignMeasure.FromMetres(8)),
            },
            new InternalStructureFamily(InternalPlaneFamily.InternalDeck, true, 1, MaterialKind.Metal,
                DesignMeasure.FromMetres(6), 2, DesignMeasure.FromMetres(2), false)
            {
                Datum = InternalPlaneDatum.HullOrigin,
                Direction = InternalRepeatDirection.Positive,
                CountMode = InternalPlaneCountMode.RepeatToBoundary,
            },
            InternalStructureFamily.Disabled(InternalPlaneFamily.TransverseBulkhead),
        ])
        {
            JunctionPriority = [
                InternalPlaneFamily.InternalDeck,
                InternalPlaneFamily.TransverseBulkhead,
                InternalPlaneFamily.LongitudinalBulkhead,
            ],
        };

        var superstructure = new SuperstructureLayout(true,
            [
                new SuperstructureLayer("layer-1", 1,
                [
                    new SuperstructureBoxModule("box-1", DesignMeasure.Zero, DesignMeasure.Zero,
                        DesignMeasure.FromMetres(12), DesignMeasure.FromMetres(8), 4),
                ]),
                new SuperstructureLayer("layer-2", 2,
                [
                    new SuperstructureBoxModule("box-2", DesignMeasure.FromMetres(1), DesignMeasure.Zero,
                        DesignMeasure.FromMetres(8), DesignMeasure.FromMetres(6), 3),
                ]),
            ],
            [new SuperstructureTowerRoot("tower-root", "span", DesignMeasure.FromMetres(2),
                DesignMeasure.FromMetres(3))],
            LegacySuperstructureSettings.FromParameters(parameters.EffectiveSuperstructure));

        var extensions = new DocumentExtensions(ImmutableDictionary<string, string>.Empty
            .Add("future.payload", "kept")
            .Add("a.key", "first"));

        return ShipDocument.CreateNew("Rich hull", parameters, documentId: "doc-rich-001") with
        {
            Arrangement = arrangement,
            Datum = new LayoutDatum(DesignMeasure.FromMetres(159.5), DesignMeasure.FromMetres(10)),
            Internals = internals,
            Barbettes =
            [
                BarbetteDefinition.Create("barbette-a", "ring-a", DesignMeasure.FromMetres(9), 3,
                    topOffsetMetres: 1, neckClearSizeMetres: 3,
                    sideArmor: new ArmorLayout([MaterialKind.Metal, MaterialKind.HeavyArmor]),
                    roofArmor: new ArmorLayout([MaterialKind.Wood]),
                    bottomArmor: new ArmorLayout([MaterialKind.Metal, MaterialKind.Lead]),
                    neckArmor: new ArmorLayout([MaterialKind.Metal])),
                BarbetteDefinition.Create("barbette-b", "ring-b", DesignMeasure.FromMetres(7), 2,
                    neckClearSizeMetres: 1),
            ],
            Superstructure = superstructure,
            Smoothing = new SmoothingSettings(SmoothingMethod.HybridSlopeFill, 2),
            AppliedStyle = new AppliedStyleProvenance("style-german", 3,
                new CopiedStyleFields(true, true, true, true, true, true, false), "German style"),
            Extensions = extensions,
        };
    }

    private sealed class FixedAssetResolver : IHullAssetResolver
    {
        private readonly bool _exists;

        public FixedAssetResolver(bool exists) => _exists = exists;

        public bool Exists(string assetId, string assetVersion, string assetHash) => _exists;
    }

    private static string Describe(IReadOnlyList<DesignDiagnostic> diagnostics) =>
        diagnostics.Count == 0 ? "no findings" : string.Join("; ", diagnostics.Select(item => item.ToString()));

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
