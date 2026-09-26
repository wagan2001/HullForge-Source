using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Geometry;
using FtdHullGenerator.Infrastructure;

internal static class AutomaticSmoothingBaselineTests
{
    private const string CorpusId = "hull-forge-a01-common-base-v2";
    private const string SupersededCorpusId = "hull-forge-a01-common-base-v1";

    /// <summary>
    /// Canonical (LF-normalized) SHA-256 of the frozen v1 fixture. It is asserted directly so a
    /// rewrite of v1 fails the test even though v2 is the corpus the comparison now runs against.
    /// </summary>
    private const string V1FixtureSha256 =
        "02cc4c289f1120ffd551dec8ed995fca9649d535d30d623e1951d86f5890828e";

    /// <summary>
    /// The exactly-13 rows the HF-01 cavity guard changes. They are the defect set recorded in the
    /// v2 supersedes block, not an outcome-selected subset.
    /// </summary>
    private static readonly (string CaseId, string Method)[] ExpectedChangedRows =
    [
        ("review-rounded-flared", "HorizontalSlopeFill"),
        ("review-rounded-flared", "HybridSlopeFill"),
        ("review-deep-v-long-entrance", "HorizontalSlopeFill"),
        ("review-deep-v-long-entrance", "HybridSlopeFill"),
        ("review-flat-wide-short-ends", "HorizontalSlopeFill"),
        ("review-raised-sheer-tumblehome", "HorizontalSlopeFill"),
        ("small-odd-short-transitions", "HorizontalSlopeFill"),
        ("small-odd-short-transitions", "HybridSlopeFill"),
        ("small-even-raised-transitions", "HorizontalSlopeFill"),
        ("large-rounded-long-runs", "HorizontalSlopeFill"),
        ("large-rounded-long-runs", "HybridSlopeFill"),
        ("large-raised-deep-v", "HorizontalSlopeFill"),
        ("large-raised-deep-v", "HybridSlopeFill"),
    ];

    private static readonly (string Name, int Value)[] ProtectedMethods =
    [
        (nameof(SmoothingMethod.None), 0),
        (nameof(SmoothingMethod.VerticalSlopeFill), 1),
        (nameof(SmoothingMethod.HorizontalSlopeFill), 2),
        (nameof(SmoothingMethod.HybridSlopeFill), 5),
        (nameof(SmoothingMethod.CombinedSlopeFill), 4),
    ];

    private static readonly string[] ExpectedCaseIds =
    [
        "review-rounded-flared",
        "review-deep-v-long-entrance",
        "review-flat-wide-short-ends",
        "review-raised-sheer-tumblehome",
        "small-odd-short-transitions",
        "small-even-raised-transitions",
        "large-rounded-long-runs",
        "large-raised-deep-v",
    ];

    private static readonly string[] FrozenHoldoutIds =
    [
        "review-deep-v-long-entrance",
        "review-raised-sheer-tumblehome",
        "small-even-raised-transitions",
        "large-raised-deep-v",
    ];

    // Independent of the fixture's own declaration. Update only by creating a new corpus identity.
    private const string FrozenHoldoutDefinitionSha256 =
        "bfbde399f74d6fae962a6dd6b1be1d577e920bf338ec0369c26a267778497215";

    public static void Run()
    {
        var fixtureDirectory = Path.Combine(
            Path.GetDirectoryName(typeof(AutomaticSmoothingBaselineTests).Assembly.Location)!, "Fixtures");
        var v1Text = File.ReadAllText(Path.Combine(fixtureDirectory, "automatic-smoothing-a01.json"));
        var v1 = JsonSerializer.Deserialize<Corpus>(v1Text, JsonOptions)
                 ?? throw new InvalidOperationException("A01 v1 corpus JSON was empty.");
        var corpus = JsonSerializer.Deserialize<Corpus>(
                         File.ReadAllText(Path.Combine(fixtureDirectory, "automatic-smoothing-a01-v2.json")), JsonOptions)
                     ?? throw new InvalidOperationException("A01 v2 corpus JSON was empty.");

        // v1 is frozen evidence: its canonical bytes must never change, even though v2 now carries
        // the compared results. This is the machine check on that preservation.
        Require(HashNormalizedText(v1Text) == V1FixtureSha256,
            $"The frozen v1 A01 fixture changed: expected {V1FixtureSha256}, actual {HashNormalizedText(v1Text)}.");
        Require(v1.SchemaVersion == 1 && v1.CorpusId == SupersededCorpusId,
            $"Unexpected v1 A01 corpus identity {v1.CorpusId} schema {v1.SchemaVersion}.");
        Require(corpus.SchemaVersion == 1 && corpus.CorpusId == CorpusId,
            $"Unexpected v2 A01 corpus identity {corpus.CorpusId} schema {corpus.SchemaVersion}.");
        VerifySupersessionContract(v1, corpus);
        Require(corpus.Methods.Select(method => (method.Name, method.Value)).SequenceEqual(ProtectedMethods),
            "The A01 comparison method names, serialized values, or common-base order changed.");
        Require(corpus.HybridOffset == 3, "The protected offset-Hybrid comparison must use a 3 m top band.");
        Require(corpus.Cases.Select(item => item.Id).SequenceEqual(ExpectedCaseIds),
            "The A01 corpus case set or order changed; create a new corpus identity instead of rewriting it.");
        Require(corpus.HoldoutCaseIds.SequenceEqual(FrozenHoldoutIds),
            "The frozen A01 holdout membership changed.");

        VerifyMetricContract(corpus);
        VerifyRenderContract(corpus);
        VerifyPackingNormalization();

        var holdoutHash = Hash(string.Join("\n", corpus.Cases
            .Where(item => FrozenHoldoutIds.Contains(item.Id, StringComparer.Ordinal))
            .Select(CanonicalCase)));
        Require(holdoutHash == FrozenHoldoutDefinitionSha256,
            $"The frozen A01 holdout definitions changed: expected {FrozenHoldoutDefinitionSha256}, actual {holdoutHash}.");

        var parametersById = corpus.Cases.ToDictionary(item => item.Id, ToParameters, StringComparer.Ordinal);
        VerifyShapeV2ReviewCases(parametersById);
        Require(corpus.Cases.Any(item => item.Tags.Contains("flat-wide-no-op", StringComparer.Ordinal)),
            "The corpus lost its explicit FlatWide no-op case.");
        Require(corpus.Cases.Any(item => item.Tags.Contains("small", StringComparer.Ordinal)) &&
                corpus.Cases.Any(item => item.Tags.Contains("large", StringComparer.Ordinal)) &&
                corpus.Cases.Any(item => item.Tags.Contains("long-run", StringComparer.Ordinal)) &&
                corpus.Cases.Any(item => item.Tags.Contains("short-transition", StringComparer.Ordinal)) &&
                corpus.Cases.Any(item => item.Tags.Contains("raised-profile", StringComparer.Ordinal)),
            "The A01 corpus no longer covers small, large, long-run, short-transition, and raised-profile cases.");

        var expectedResults = corpus.Results.ToDictionary(
            result => (result.CaseId, result.Method), result => result);
        var expectedCartesianCount = corpus.Cases.Count * ProtectedMethods.Length;
        Require(expectedResults.Count == expectedCartesianCount && corpus.Results.Count == expectedCartesianCount,
            $"A01 results contain {corpus.Results.Count} unique/non-unique rows instead of {expectedCartesianCount} complete rows.");
        var generator = new HullGenerator();
        var usedCatalogKeys = new HashSet<(MaterialKind Material, BlockShape Shape)>();

        foreach (var item in corpus.Cases)
        {
            var parameters = parametersById[item.Id];
            var baseline = generator.Generate(parameters with { Smoothing = SmoothingMethod.None });
            var baselineCells = baseline.Blocks.SelectMany(block => block.OccupiedCells).ToHashSet();
            var normalizedBaseHash = HashNormalizedBase(baseline.Blocks);

            foreach (var (methodName, _) in ProtectedMethods)
            {
                var method = Enum.Parse<SmoothingMethod>(methodName);
                var candidate = generator.Generate(parameters with { Smoothing = method });
                var errors = HullGeometryValidator.Validate(candidate);
                Require(errors.Count == 0,
                    $"{item.Id}/{method} failed hard geometry validation: {string.Join("; ", errors)}");
                Require((candidate.MinX, candidate.MaxX, candidate.MinY, candidate.MaxY, candidate.MinZ, candidate.MaxZ) ==
                        (baseline.MinX, baseline.MaxX, baseline.MinY, baseline.MaxY, baseline.MinZ, baseline.MaxZ),
                    $"{item.Id}/{method} changed the common base bounds.");

                var basePlacements = candidate.Blocks.Where(block => block.Origin != BlockOrigin.Smoothing).ToArray();
                Require(HashNormalizedBase(basePlacements) == normalizedBaseHash,
                    $"{item.Id}/{method} changed the beam-normalized common base.");
                var smoothing = candidate.Blocks.Where(block => block.Origin == BlockOrigin.Smoothing).ToArray();
                Require(smoothing.SelectMany(block => block.OccupiedCells).All(cell => !baselineCells.Contains(cell)),
                    $"{item.Id}/{method} overlaps the common base.");

                foreach (var block in candidate.Blocks)
                    usedCatalogKeys.Add((block.Material,
                        block.KeepsFittedShape ? block.Shape : BlockShape.Cube));

                var actual = new Result
                {
                    CaseId = item.Id,
                    Method = methodName,
                    BaseNormalizedSha256 = normalizedBaseHash,
                    SmoothingNativeSha256 = HashNativeSmoothing(smoothing),
                    BasePlacementCount = basePlacements.Length,
                    SmoothingPlacementCount = smoothing.Length,
                    OccupiedCellCount = candidate.OccupiedCellCount,
                };
                CompareResult(expectedResults[(item.Id, methodName)], actual);
            }

            if (item.FlatNoOpMethod is not null)
            {
                var noOpMethod = Enum.Parse<SmoothingMethod>(item.FlatNoOpMethod);
                var noOp = generator.Generate(parameters with { Smoothing = noOpMethod });
                Require(noOp.Blocks.All(block => block.Origin != BlockOrigin.Smoothing),
                    $"{item.Id}/{noOpMethod} is the frozen valid flat no-op but emitted smoothing placements.");
                Require(HashNormalizedBase(noOp.Blocks) == normalizedBaseHash,
                    $"{item.Id}/{noOpMethod} changed the normalized base in its no-op path.");
            }
        }

        VerifyCatalog(corpus.Catalog, usedCatalogKeys);
        Console.WriteLine($"A01 automatic smoothing baseline v2: {corpus.Cases.Count} cases x " +
                          $"{ProtectedMethods.Length} protected methods, v1 byte preservation, supersession contract, " +
                          "common-base identity, hard/heuristic separation, catalog snapshot, render matrix, and " +
                          "frozen holdout passed.");
    }

    /// <summary>
    /// v2 supersedes the corpus identity, not the frozen inputs. This proves the cases, methods,
    /// holdout, metrics, renders and decision rule are unchanged, that v1 is named with its exact
    /// fixture hash, and that the documented defect rows are exactly the rows v1 and v2 disagree on.
    /// </summary>
    private static void VerifySupersessionContract(Corpus v1, Corpus v2)
    {
        Require(v2.HybridOffset == v1.HybridOffset &&
                v2.Methods.SequenceEqual(v1.Methods) &&
                v2.HoldoutCaseIds.SequenceEqual(v1.HoldoutCaseIds) &&
                v2.DecisionRule == v1.DecisionRule &&
                v2.Catalog.GameVersion == v1.Catalog.GameVersion &&
                v2.Catalog.Scope == v1.Catalog.Scope &&
                v2.Catalog.RequiredResolutionSha256 == v1.Catalog.RequiredResolutionSha256,
            "The v2 corpus changed a frozen v1 input outside its results.");
        Require(v2.Cases.Select(CanonicalCase).SequenceEqual(v1.Cases.Select(CanonicalCase)),
            "The v2 corpus changed a v1 case definition.");
        Require(v2.HardChecks.Select(check => (check.Id, check.Unit, check.Aggregation))
                .SequenceEqual(v1.HardChecks.Select(check => (check.Id, check.Unit, check.Aggregation))),
            "The v2 corpus changed the frozen hard-check contract.");
        Require(v2.AppearanceMetrics.Select(CanonicalMetric).SequenceEqual(v1.AppearanceMetrics.Select(CanonicalMetric)),
            "The v2 corpus changed the frozen appearance-metric contract.");
        Require(v2.Renders.CanvasWidth == v1.Renders.CanvasWidth &&
                v2.Renders.CanvasHeight == v1.Renders.CanvasHeight &&
                v2.Renders.Views.SequenceEqual(v1.Renders.Views) &&
                v2.Renders.Crops.Select(crop => (crop.Id, crop.SourceView, crop.X, crop.Y, crop.Width, crop.Height))
                    .SequenceEqual(v1.Renders.Crops.Select(crop =>
                        (crop.Id, crop.SourceView, crop.X, crop.Y, crop.Width, crop.Height))),
            "The v2 corpus changed the frozen render recipe.");

        var supersedes = v2.Supersedes
                         ?? throw new InvalidOperationException("The v2 A01 corpus has no supersedes block.");
        Require(supersedes.CorpusId == SupersededCorpusId && supersedes.FixtureSha256 == V1FixtureSha256,
            "The v2 supersedes block does not name the frozen v1 identity and fixture hash.");
        Require(supersedes.Defect.Contains("cavity", StringComparison.OrdinalIgnoreCase) &&
                supersedes.ClassifierPredicate.Contains("RoleAt", StringComparison.Ordinal) &&
                supersedes.ClassifierPredicate.Contains("Outside", StringComparison.Ordinal),
            "The v2 supersedes block does not record the defect and the independent classifier predicate.");

        // The documented changed-row set must be exactly the rows where v1 and v2 outputs differ,
        // and exactly the independently pinned HF-01 defect set.
        var v1Results = v1.Results.ToDictionary(result => (result.CaseId, result.Method));
        var differing = v2.Results
            .Where(result => v1Results[(result.CaseId, result.Method)].SmoothingNativeSha256 !=
                             result.SmoothingNativeSha256)
            .Select(result => (result.CaseId, result.Method))
            .ToHashSet();
        var documented = supersedes.ChangedRows.Select(row => (row.CaseId, row.Method)).ToHashSet();
        Require(differing.SetEquals(documented),
            "The v2 changed-row set is not the exact v1/v2 difference.");
        Require(documented.SetEquals(ExpectedChangedRows),
            "The documented defect row set is not the independently pinned HF-01 defect set.");
        Require(supersedes.ChangedRows.Count == ExpectedChangedRows.Length &&
                supersedes.ChangedRows.Select(row => (row.CaseId, row.Method)).Distinct().Count() ==
                ExpectedChangedRows.Length,
            "The v2 changedRows array is not exactly the 13 unique defect rows.");
        foreach (var row in supersedes.ChangedRows)
        {
            var before = v1Results[(row.CaseId, row.Method)];
            var after = v2.Results.Single(result => (result.CaseId, result.Method) == (row.CaseId, row.Method));
            Require(row.OldSmoothingPlacementCount == before.SmoothingPlacementCount &&
                    row.OldSmoothingNativeSha256 == before.SmoothingNativeSha256 &&
                    row.NewSmoothingPlacementCount == after.SmoothingPlacementCount &&
                    row.NewSmoothingNativeSha256 == after.SmoothingNativeSha256,
                $"The v2 changed row {row.CaseId}/{row.Method} does not record the exact old and new outputs.");
            Require(after.SmoothingPlacementCount < before.SmoothingPlacementCount,
                $"The v2 changed row {row.CaseId}/{row.Method} did not remove any cavity-facing placement.");
        }
        Require(v1.Results.Count == v2.Results.Count &&
                v1.Results.Where(result => !documented.Contains((result.CaseId, result.Method)))
                    .All(result => v2.Results.Single(other =>
                            (other.CaseId, other.Method) == (result.CaseId, result.Method))
                        .SmoothingNativeSha256 == result.SmoothingNativeSha256),
            "The v2 corpus changed a row outside the documented defect set.");
    }

    private static string CanonicalMetric(AppearanceMetric metric) =>
        $"{metric.Id}|{metric.Unit}|{metric.PerCaseAggregation}|{metric.SuiteAggregation}|" +
        $"{metric.Authoritative}|{metric.GateRole}|{metric.Definition}";


    private static void VerifyMetricContract(Corpus corpus)
    {
        Require(corpus.HardChecks.Count >= 7 && corpus.HardChecks.All(check => check.Id.StartsWith("hard.", StringComparison.Ordinal)),
            "A01 hard checks must be explicit hard.* entries.");
        Require(corpus.AppearanceMetrics.Count == 4,
            "A01 must define exactly the four pre-tuning appearance metric families.");
        var required = new[]
        {
            "appearance.target-surface-deviation",
            "appearance.uncovered-intended-surface",
            "appearance.patch-boundary-discontinuity",
            "appearance.unnecessary-fragmentation",
        };
        Require(corpus.AppearanceMetrics.Select(metric => metric.Id).SequenceEqual(required),
            "A01 appearance metric identities or order changed.");
        Require(corpus.AppearanceMetrics.All(metric => !string.IsNullOrWhiteSpace(metric.Unit) &&
                                                       !string.IsNullOrWhiteSpace(metric.PerCaseAggregation) &&
                                                       !string.IsNullOrWhiteSpace(metric.SuiteAggregation) &&
                                                       !metric.Authoritative &&
                                                       metric.GateRole == "advisory"),
            "Every appearance metric needs units, both aggregation levels, and an advisory/non-authoritative declaration.");
        Require(!corpus.HardChecks.Select(check => check.Id)
                    .Intersect(corpus.AppearanceMetrics.Select(metric => metric.Id), StringComparer.Ordinal).Any(),
            "Hard checks and heuristic appearance metrics must be disjoint.");
        Require(corpus.DecisionRule.StartsWith("lexicographic:", StringComparison.Ordinal),
            "A01 must not collapse correctness and appearance into an opaque weighted score.");
    }

    private static void VerifyRenderContract(Corpus corpus)
    {
        Require(corpus.Renders.CanvasWidth == 1000 && corpus.Renders.CanvasHeight == 700,
            "A01 fixed render recipes must retain HullRender's 1000 x 700 canvas.");
        Require(corpus.Renders.Views.SequenceEqual(["Side", "Top", "Bow", "Isometric"]),
            "A01 must render the fixed side/top/bow/isometric view set.");
        Require(corpus.Renders.Crops.Select(crop => crop.Id).SequenceEqual(
                    ["side-stern-seam", "side-bow-seam", "bow-section-seam"]),
            "A01 local seam crop identities changed.");
        foreach (var crop in corpus.Renders.Crops)
        {
            Require(corpus.Renders.Views.Contains(crop.SourceView, StringComparer.Ordinal) &&
                    crop.X >= 0 && crop.Y >= 0 && crop.Width > 0 && crop.Height > 0 &&
                    crop.X + crop.Width <= corpus.Renders.CanvasWidth &&
                    crop.Y + crop.Height <= corpus.Renders.CanvasHeight,
                $"A01 crop {crop.Id} is outside the fixed render canvas or names an unknown view.");
        }
        Require(corpus.Cases.All(item => item.Render),
            "No A01 case may be selectively omitted from fixed-view rendering.");
    }

    private static void VerifyPackingNormalization()
    {
        var beam4 = new BlockPlacement(BlockShape.Beam4, MaterialKind.Metal, 0, 0, 0, 0)
        {
            Origin = BlockOrigin.Shell,
            ArmorDepth = 1,
            Construction = ArmorConstruction.Solid,
            ArmorRegion = ArmorRegion.Side,
        };
        var twoBeam2 = new[]
        {
            beam4 with { Shape = BlockShape.Beam2 },
            beam4 with { Shape = BlockShape.Beam2, Z = 2 },
        };
        Require(HashNormalizedBase([beam4]) == HashNormalizedBase(twoBeam2),
            "A01 base normalization treated equivalent 4 m and 2 m + 2 m straight-beam packing as different geometry.");

        var slope4 = beam4 with { Shape = BlockShape.Slope4, Origin = BlockOrigin.Smoothing, ArmorDepth = 0 };
        var twoSlope2 = new[]
        {
            slope4 with { Shape = BlockShape.Slope2 },
            slope4 with { Shape = BlockShape.Slope2, Z = 2 },
        };
        Require(HashNativeSmoothing([slope4]) != HashNativeSmoothing(twoSlope2),
            "A01 native smoothing identity incorrectly normalized a 4 m slope into two 2 m slopes.");
    }

    private static void VerifyCatalog(CatalogContract expected,
        IReadOnlyCollection<(MaterialKind Material, BlockShape Shape)> keys)
    {
        var gameDirectory = FtdInstallationLocator.FindInstalledGame();
        Require(gameDirectory is not null,
            "A01 direct evidence requires the installed Core_Structural catalog named by the corpus.");
        var catalog = FtdBlockCatalog.Load(gameDirectory!);
        Require(catalog.GameVersion == expected.GameVersion,
            $"A01 catalog version mismatch: expected {expected.GameVersion}, actual {catalog.GameVersion}.");
        var lines = keys.OrderBy(key => key.Material).ThenBy(key => key.Shape)
            .Select(key =>
            {
                var resolved = catalog.Resolve(key.Material, key.Shape);
                Require(!resolved.IsFallback,
                    $"A01 catalog snapshot cannot resolve {key.Material}/{key.Shape} without fallback.");
                return $"{key.Material}|{key.Shape}|{resolved.Guid:D}";
            });
        var fingerprint = Hash(string.Join("\n", lines));
        Require(fingerprint == expected.RequiredResolutionSha256,
            $"A01 catalog resolution fingerprint changed: expected {expected.RequiredResolutionSha256}, actual {fingerprint}.");
    }

    private static void CompareResult(Result expected, Result actual)
    {
        // Base placement count is deliberately informational. Straight-beam packing and
        // decomposition are not geometry identity; the normalized base hash above is the gate.
        // Smoothing placements remain native and exact because slope length/shape cannot be
        // normalized away.
        Require(expected.CaseId == actual.CaseId &&
                expected.Method == actual.Method &&
                expected.BaseNormalizedSha256 == actual.BaseNormalizedSha256 &&
                expected.SmoothingNativeSha256 == actual.SmoothingNativeSha256 &&
                expected.SmoothingPlacementCount == actual.SmoothingPlacementCount &&
                expected.OccupiedCellCount == actual.OccupiedCellCount,
            $"Protected A01 output changed for {actual.CaseId}/{actual.Method}. " +
            $"Expected base={expected.BaseNormalizedSha256}, smoothing={expected.SmoothingNativeSha256}, " +
            $"placements={expected.BasePlacementCount} informational base+{expected.SmoothingPlacementCount} exact smoothing, cells={expected.OccupiedCellCount}; " +
            $"actual base={actual.BaseNormalizedSha256}, smoothing={actual.SmoothingNativeSha256}, " +
            $"placements={actual.BasePlacementCount} informational base+{actual.SmoothingPlacementCount} exact smoothing, cells={actual.OccupiedCellCount}.");
    }

    private static string HashNormalizedBase(IEnumerable<BlockPlacement> blocks)
    {
        var lines = blocks.SelectMany(block => block.OccupiedCells.Select(cell =>
        {
            var family = BlockShapeMetadata.Get(block.Shape).Family;
            return $"{cell.X},{cell.Y},{cell.Z}|{block.Material}|{family}|{block.Origin}|" +
                   $"{block.ArmorDepth}|{block.Construction}|{block.ArmorRegion}";
        })).Order(StringComparer.Ordinal);
        return Hash(string.Join("\n", lines));
    }

    private static string HashNativeSmoothing(IEnumerable<BlockPlacement> blocks)
    {
        var lines = blocks.Select(block =>
        {
            var info = BlockShapeMetadata.Get(block.Shape);
            return $"{block.X},{block.Y},{block.Z}|{block.Material}|{block.Shape}|{info.Family}|" +
                   $"{info.Length}|{info.Mirrored}|{info.ShortLength}|{block.Rotation}|{block.Origin}|" +
                   $"{block.ArmorDepth}|{block.Construction}|{block.ArmorRegion}";
        }).Order(StringComparer.Ordinal);
        return Hash(string.Join("\n", lines));
    }

    private static string CanonicalCase(Case item) =>
        $"{item.Id}|{item.Split}|{item.Length}|{item.Width}|{item.Height}|{item.BowStyle}|{item.SternStyle}|" +
        $"{item.Bow.Fullness:R}|{item.Bow.Flare:R}|{item.Bow.EntranceLengthPercent}|" +
        $"{item.Body.Style}|{item.Body.Fullness:R}|{item.Body.SideShape:R}|{item.Body.Chine:R}|{item.Body.FlatBottom:R}|" +
        $"{item.Stern.Fullness:R}|{item.Stern.SideShape:R}|{item.Stern.RunLengthPercent}|" +
        $"{item.Profile.BowDeckRise}|{item.Profile.SternDeckRise}|{item.Profile.BowKeelRise}|{item.Profile.SternKeelRise}|" +
        $"{item.HasBulb}|{item.Beamify}|{item.HybridOffset}";

    private static HullParameters ToParameters(Case item)
    {
        var body = new BodyShapeSettings(Enum.Parse<BodyStyle>(item.Body.Style), item.Body.Fullness,
            item.Body.SideShape, item.Body.Chine, item.Body.FlatBottom);
        var shape = new HullShapeSettings(
            new BowShapeSettings(item.Bow.Fullness, item.Bow.Flare, item.Bow.EntranceLengthPercent),
            body,
            new SternShapeSettings(item.Stern.Fullness, item.Stern.SideShape, item.Stern.RunLengthPercent),
            new HullProfileSettings(item.Profile.BowDeckRise, item.Profile.SternDeckRise,
                item.Profile.BowKeelRise, item.Profile.SternKeelRise));
        return HullParameters.Default with
        {
            Length = item.Length,
            Width = item.Width,
            Height = item.Height,
            BowStyle = Enum.Parse<BowStyle>(item.BowStyle),
            SternStyle = Enum.Parse<SternStyle>(item.SternStyle),
            Shape = shape,
            BowFullness = shape.Bow.Fullness,
            SternFullness = shape.Stern.Fullness,
            CrossSectionCurve = shape.Body.Fullness,
            HasBulb = item.HasBulb,
            Bulb = item.HasBulb ? BulbSettings.Default : null,
            Beamify = item.Beamify,
            HybridFillOffset = item.HybridOffset,
            Smoothing = SmoothingMethod.None,
        };
    }

    private static void VerifyShapeV2ReviewCases(IReadOnlyDictionary<string, HullParameters> cases)
    {
        var basis = HullParameters.Default with { Length = 96, Width = 25, Height = 15, Beamify = true, HybridFillOffset = 3 };
        var expected = new Dictionary<string, HullParameters>(StringComparer.Ordinal)
        {
            ["review-rounded-flared"] = basis with
            {
                BowStyle = BowStyle.Pointed, SternStyle = SternStyle.Transom,
                BowFullness = 0.25, SternFullness = 0.10, CrossSectionCurve = 0.35,
                Shape = new HullShapeSettings(new BowShapeSettings(0.25, 0.55, 42),
                    new BodyShapeSettings(BodyStyle.Rounded, 0.35, 0.40, -0.65),
                    new SternShapeSettings(0.10, 0.15, 28), HullProfileSettings.Flat),
            },
            ["review-deep-v-long-entrance"] = basis with
            {
                Length = 112, Width = 23, Height = 18, BowStyle = BowStyle.Raked, SternStyle = SternStyle.Canoe,
                BowFullness = -0.60, SternFullness = -0.30, CrossSectionCurve = -0.60,
                Shape = new HullShapeSettings(new BowShapeSettings(-0.60, 0.70, 70),
                    new BodyShapeSettings(BodyStyle.DeepV, -0.60, 0.30, -0.10),
                    new SternShapeSettings(-0.30, 0.20, 42), HullProfileSettings.Flat),
            },
            ["review-flat-wide-short-ends"] = basis with
            {
                Length = 72, Width = 35, Height = 12, BowStyle = BowStyle.Blunt, SternStyle = SternStyle.Square,
                BowFullness = 0.55, SternFullness = 0.65, CrossSectionCurve = 0.85,
                Shape = new HullShapeSettings(new BowShapeSettings(0.55, -0.15, 15),
                    new BodyShapeSettings(BodyStyle.FlatWide, 0.85, 0.05, 0.45, 1),
                    new SternShapeSettings(0.65, 0.10, 12), HullProfileSettings.Flat),
            },
            ["review-raised-sheer-tumblehome"] = basis with
            {
                Length = 84, Width = 31, Height = 24, BowStyle = BowStyle.Spoon, SternStyle = SternStyle.Counter,
                BowFullness = 0.20, SternFullness = 0.25, CrossSectionCurve = 0.55,
                Shape = new HullShapeSettings(new BowShapeSettings(0.20, -0.25, 38),
                    new BodyShapeSettings(BodyStyle.Tumblehome, 0.55, -0.70, -0.35),
                    new SternShapeSettings(0.25, -0.55, 35), new HullProfileSettings(6, 4, 5, 3)),
                HasBulb = true, Bulb = BulbSettings.Default,
            },
        };
        foreach (var (id, parameters) in expected)
            Require(cases[id] == parameters, $"A01 case {id} no longer exactly matches the existing Shape V2 review geometry.");
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    /// <summary>
    /// Hashes fixture text with canonical LF line endings so the pinned identity is stable across
    /// Windows CRLF and Linux LF checkouts.
    /// </summary>
    private static string HashNormalizedText(string text) =>
        Hash(text.Replace("\r\n", "\n", StringComparison.Ordinal));

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private sealed record Corpus
    {
        public int SchemaVersion { get; init; }
        public string CorpusId { get; init; } = "";
        public int HybridOffset { get; init; }
        public CatalogContract Catalog { get; init; } = new();
        public List<MethodEntry> Methods { get; init; } = [];
        public List<HardCheck> HardChecks { get; init; } = [];
        public List<AppearanceMetric> AppearanceMetrics { get; init; } = [];
        public string DecisionRule { get; init; } = "";
        public RenderContract Renders { get; init; } = new();
        public List<string> HoldoutCaseIds { get; init; } = [];
        public List<Case> Cases { get; init; } = [];
        public List<Result> Results { get; init; } = [];
        public SupersedesContract? Supersedes { get; init; }
    }

    private sealed record SupersedesContract
    {
        public string CorpusId { get; init; } = "";
        public string FixtureSha256 { get; init; } = "";
        public string Defect { get; init; } = "";
        public string ClassifierPredicate { get; init; } = "";
        public List<ChangedRow> ChangedRows { get; init; } = [];
    }

    private sealed record ChangedRow
    {
        public string CaseId { get; init; } = "";
        public string Method { get; init; } = "";
        public int OldSmoothingPlacementCount { get; init; }
        public int NewSmoothingPlacementCount { get; init; }
        public string OldSmoothingNativeSha256 { get; init; } = "";
        public string NewSmoothingNativeSha256 { get; init; } = "";
    }

    private sealed record CatalogContract
    {
        public string Scope { get; init; } = "";
        public string GameVersion { get; init; } = "";
        public string RequiredResolutionSha256 { get; init; } = "";
    }

    private sealed record MethodEntry(string Name, int Value);
    private sealed record HardCheck(string Id, string Unit, string Aggregation);
    private sealed record AppearanceMetric
    {
        public string Id { get; init; } = "";
        public string Unit { get; init; } = "";
        public string PerCaseAggregation { get; init; } = "";
        public string SuiteAggregation { get; init; } = "";
        public bool Authoritative { get; init; }
        public string GateRole { get; init; } = "";
        public string Definition { get; init; } = "";
    }

    private sealed record RenderContract
    {
        public int CanvasWidth { get; init; }
        public int CanvasHeight { get; init; }
        public List<string> Views { get; init; } = [];
        public List<RenderCrop> Crops { get; init; } = [];
    }

    private sealed record RenderCrop(string Id, string SourceView, int X, int Y, int Width, int Height);

    private sealed record Case
    {
        public string Id { get; init; } = "";
        public string Split { get; init; } = "";
        public List<string> Tags { get; init; } = [];
        public bool Render { get; init; }
        public int Length { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public string BowStyle { get; init; } = "";
        public string SternStyle { get; init; } = "";
        public ShapeBow Bow { get; init; } = new();
        public ShapeBody Body { get; init; } = new();
        public ShapeStern Stern { get; init; } = new();
        public ShapeProfile Profile { get; init; } = new();
        public bool HasBulb { get; init; }
        public bool Beamify { get; init; }
        public int HybridOffset { get; init; }
        public string? FlatNoOpMethod { get; init; }
    }

    private sealed record ShapeBow
    {
        public double Fullness { get; init; }
        public double Flare { get; init; }
        public int EntranceLengthPercent { get; init; }
    }

    private sealed record ShapeBody
    {
        public string Style { get; init; } = "";
        public double Fullness { get; init; }
        public double SideShape { get; init; }
        public double Chine { get; init; }
        public double FlatBottom { get; init; }
    }

    private sealed record ShapeStern
    {
        public double Fullness { get; init; }
        public double SideShape { get; init; }
        public int RunLengthPercent { get; init; }
    }

    private sealed record ShapeProfile
    {
        public int BowDeckRise { get; init; }
        public int SternDeckRise { get; init; }
        public int BowKeelRise { get; init; }
        public int SternKeelRise { get; init; }
    }

    private sealed record Result
    {
        public string CaseId { get; init; } = "";
        public string Method { get; init; } = "";
        public string BaseNormalizedSha256 { get; init; } = "";
        public string SmoothingNativeSha256 { get; init; } = "";
        public int BasePlacementCount { get; init; }
        public int SmoothingPlacementCount { get; init; }
        public int OccupiedCellCount { get; init; }
    }
}
