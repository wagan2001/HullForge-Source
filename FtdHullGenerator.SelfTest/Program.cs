using System.Text.Json;
using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using FtdHullGenerator;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Geometry;
using FtdHullGenerator.Infrastructure;
using FtdHullGenerator.Serialization;
using FtdHullGenerator.UI;

var parsedArguments = SelfTestArgumentParser.Parse(args);
if (parsedArguments.Error is not null)
{
    Console.Error.WriteLine(parsedArguments.Error);
    Console.Error.WriteLine(SelfTestArgumentParser.Usage);
    return 2;
}

if (parsedArguments.List)
{
    SelfTestGroupCatalog.Print(Console.Out);
    return 0;
}

var profile = parsedArguments.Profile;
var runProduct = profile is SelfTestProfile.Fast or SelfTestProfile.Full or SelfTestProfile.All;
var runExperimental = profile is SelfTestProfile.Experimental or SelfTestProfile.All;
var full = profile is SelfTestProfile.Full or SelfTestProfile.All;
var workerCount = parsedArguments.WorkerCount;
var timings = parsedArguments.Timings;
var generator = new HullGenerator();
var gameDirectory = FtdInstallationLocator.FindInstalledGame()
    ?? throw new InvalidOperationException("From The Depths was not found in the default Steam location.");
var catalog = FtdBlockCatalog.Load(gameDirectory);
var context = new TestContext(generator, catalog, full, runExperimental);

if (parsedArguments.WriteSketchbookEvidence)
{
    var (jsonPath, markdownPath) = SketchbookNativeAudit.WriteEvidence(generator, catalog);
    Console.WriteLine(jsonPath);
    Console.WriteLine(markdownPath);
    return 0;
}

void RunSection(string name, Action action)
{
    if (!timings)
    {
        action();
        return;
    }

    var timer = Stopwatch.StartNew();
    action();
    timer.Stop();
    Console.WriteLine($"  [self-test] {name} in {timer.Elapsed.TotalMilliseconds:N0} ms.");
}

try
{
if (runProduct)
{
SelfTestGroupCatalog.Current = "core";
CatalogCubeIdentityTests.Run(generator, catalog);
MultiAxisBeamTests.Run(generator, catalog);
var defaultHull = generator.Generate(HullParameters.Default);
var singleBlockHull = generator.Generate(HullParameters.Default with { Beamify = false });
Assert(HullParameters.Default.Beamify, "Beamification must be enabled by default.");
Assert(HullParameters.Default.HybridFillOffset == 1, "Hybrid fill must default to a one-metre top offset.");
Assert(MainWindow.CreateBlueprintName(HullParameters.Default) == "HF_Dolphin_100x21x12",
    "The default generated name did not use the simple profile and dimensions format.");
var unrestrictedDimensions = HullParameters.Default with { Length = 501, Width = 251, Height = 101 };
Assert(unrestrictedDimensions.Validate().Count == 0, "Dimensions above the slider ranges were rejected by fixed limits.");
var unconventionalParameters = HullParameters.Default with { Length = 20, Width = 30, Height = 35 };
Assert(unconventionalParameters.Validate().Count == 0,
    "Width/length or height/width proportions were still artificially constrained.");
var unconventionalHull = generator.Generate(unconventionalParameters);
Assert(unconventionalHull.OccupiedLength == 20 && unconventionalHull.OccupiedWidth == 30 && unconventionalHull.OccupiedHeight == 35 &&
       HullGeometryValidator.Validate(unconventionalHull).Count == 0,
    "An unconventional but valid dimension proportion did not generate safely.");
using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel();
    var cancellationObserved = false;
    try
    {
        generator.Generate(HullParameters.Default, cancelled.Token);
    }
    catch (OperationCanceledException)
    {
        cancellationObserved = true;
    }
    Assert(cancellationObserved, "Hull generation ignored a pre-cancelled preview request.");
}
Assert(HullParameters.Default.HasSingleBlockCenterline, "The default hull width must use a single-block centreline.");
Assert(defaultHull.OccupiedLength == HullParameters.Default.Length, "Default hull length did not match the request.");
Assert(defaultHull.OccupiedWidth == HullParameters.Default.Width, "Default hull width did not match the request.");
Assert(defaultHull.OccupiedHeight == HullParameters.Default.Height, "Default hull height did not match the request.");
Assert(HullGeometryValidator.Validate(defaultHull).Count == 0, "Default hull failed geometry validation.");
// The fitted shell vocabulary lives in the cube hull. Beamification absorbs a flattened
// candidate into the run that covers it, so the beamified default must not carry it as
// its own placement; BeamRunEligibilityTests pins that contract.
Assert(singleBlockHull.Blocks.Any(block => block.Shape == BlockShape.Slope1), "The cube hull did not use any validated slope placements.");
Assert(singleBlockHull.Blocks.Any(block => block.Shape is BlockShape.CornerLeft or BlockShape.CornerRight), "The cube hull did not use any validated corner placements.");
SelfTestGroupCatalog.Current = "design-contracts";
DesignContractTests.Run();
SelfTestGroupCatalog.Current = "layout";
ArrangementSolverTests.Run();
SelfTestGroupCatalog.Current = "layout-frame";
LayoutFrameTests.Run();
SelfTestGroupCatalog.Current = "hull-context";
HullContextTests.Run();
SelfTestGroupCatalog.Current = "barbettes";
BarbetteGeneratorTests.Run();
SelfTestGroupCatalog.Current = "barbette-ownership";
BarbetteOwnershipTests.Run();
SelfTestGroupCatalog.Current = "barbette-safety";
BarbetteSafetyTests.Run();
SelfTestGroupCatalog.Current = "modular-superstructures";
ModularSuperstructureTests.Run();
SelfTestGroupCatalog.Current = "internal-structure";
InternalStructureTests.Run();
SelfTestGroupCatalog.Current = "internal-barbette-reconciliation";
InternalBarbetteReconciliationTests.Run();
SelfTestGroupCatalog.Current = "composition-parity";
RunSection("composition-parity matrix", () =>
{
    FeatureFreeCompositionParityTests.Run(generator, full, workerCount);
});
SelfTestGroupCatalog.Current = "armor-seams";
ArmorSeamReliabilityTests.Run(generator, full);
SelfTestGroupCatalog.Current = "ship-composition";
ShipGenerationServiceTests.Run();
SelfTestGroupCatalog.Current = "decoration-codec";
DecorationCodecTests.Run();
SelfTestGroupCatalog.Current = "native-extension-contract";
NativeSlopeExtensionContractTests.Run();
SelfTestGroupCatalog.Current = "handmade-decoration-evidence";
HandmadeDecorationEvidenceTests.Run();
SelfTestGroupCatalog.Current = "vertical-slope-extension";
VerticalSlopeExtensionTests.Run();
SelfTestGroupCatalog.Current = "horizontal-slope-extension";
HorizontalSlopeExtensionTests.Run(catalog);
SelfTestGroupCatalog.Current = "resolved-slope-refinement";
ResolvedSlopeRefinementTests.Run(catalog);
SelfTestGroupCatalog.Current = "deco-slope-selection";
DecoSlopeSelectionTests.Run(catalog);
SelfTestGroupCatalog.Current = "historical-authoring";
HistoricalEnvelopeAuthoringTests.Run();
SelfTestGroupCatalog.Current = "project-persistence";
ProjectPersistenceTests.Run(full);
SelfTestGroupCatalog.Current = "editor-state";
EditorStateTests.Run();
SelfTestGroupCatalog.Current = "workspace-shell";
WorkspaceShellTests.Run();
SelfTestGroupCatalog.Current = "barbette-workspace";
BarbetteWorkspaceTests.Run();
SelfTestGroupCatalog.Current = "deco-slope-selection";
DecoSlopeSelectionTests.RunUiChoices();
SelfTestGroupCatalog.Current = "internal-structure-editor";
InternalStructureEditorTests.Run(full);
SelfTestGroupCatalog.Current = "superstructure";
SuperstructureTests.Run(generator, full);
SelfTestGroupCatalog.Current = "frozen-product-surface";
FrozenProductSurfaceTests.Run(generator, catalog);
SelfTestGroupCatalog.Current = "stabilization-team-c";
Stabilization2TeamCTests.Run(generator, catalog);
SelfTestGroupCatalog.Current = "sketchbook-native-audit";
RunSection("sketchbook native audit", () =>
{
    SketchbookNativeAuditTests.Run(generator, catalog, full);
});
SelfTestGroupCatalog.Current = "quality";
EditorPresentationTests.Run();
PreviewMotionTests.Run(generator);
PreviewSceneInvarianceTests.Run(generator, catalog);
PreviewSceneModeTests.Run(generator, catalog);
SmoothingQualityValidatorTests.Run();
SurfaceCoverageTests.Run();
SurfaceFairnessAdversarialTests.Run();
TruncationSafetyTests.Run();
ManualCorrectionRegressionTests.Run(generator);

var deckless = generator.Generate(HullParameters.Default with { DeckArmor = null });
Assert(deckless.BlockCount < defaultHull.BlockCount, "Deckless hull did not remove its top interior.");
Assert(HullGeometryValidator.Validate(deckless).Count == 0, "Deckless hull failed geometry validation.");

var layeredParameters = HullParameters.Default with
{
    HullArmor = new ArmorLayout([MaterialKind.Metal, MaterialKind.HeavyArmor, MaterialKind.Rubber]),
    DeckArmor = new ArmorLayout([MaterialKind.Wood, MaterialKind.Lead]),
    Beamify = false,
};
Assert(MainWindow.CreateBlueprintName(layeredParameters) == "HF_Dolphin_100x21x12",
    "Armor and construction details leaked into the generated name.");
Assert(MainWindow.CreateBlueprintName(WithShapeFullness(
        HullParameters.Default,
        bow: 0.13,
        stern: -0.22,
        body: 0.47)) == "HF_Custom_100x21x12",
    "Custom shape values leaked into the generated name.");
Assert(layeredParameters.HullArmor.Equals(
    new ArmorLayout([MaterialKind.Metal, MaterialKind.HeavyArmor, MaterialKind.Rubber])),
    "Equivalent armor stacks must have value equality.");
var layeredHull = generator.Generate(layeredParameters);
var layeredMaterials = CellMaterials(layeredHull);
Assert(HullGeometryValidator.Validate(layeredHull).Count == 0, "Layered hull failed geometry validation.");
foreach (var material in new[] { MaterialKind.Metal, MaterialKind.HeavyArmor, MaterialKind.Rubber, MaterialKind.Wood, MaterialKind.Lead })
    Assert(layeredMaterials.Values.Contains(material), $"Layered hull did not place its {material} layer.");
Assert(layeredMaterials.Where(pair => pair.Key.Y == layeredHull.MaxY &&
       (pair.Key.X == layeredHull.MinX || pair.Key.X == layeredHull.MaxX))
       .All(pair => pair.Value == MaterialKind.Metal), "Deck material replaced hull armor at the outer rim.");
Assert(layeredMaterials.Where(pair => pair.Key.Y == layeredHull.MaxY)
       .All(pair => pair.Value is MaterialKind.Metal or MaterialKind.Wood),
    "An inward hull or lower deck layer leaked onto the top surface.");

var layeredBeamHull = generator.Generate(layeredParameters with { Beamify = true });
Assert(CellMaterials(layeredBeamHull).OrderBy(pair => pair.Key).SequenceEqual(
       layeredMaterials.OrderBy(pair => pair.Key)), "Beamifying changed layered cell materials.");
Assert(layeredBeamHull.BlockCount < layeredHull.BlockCount, "Layered armor was not reduced by beamification.");

var airPoleParameters = HullParameters.Default with
{
    HullArmor = new ArmorLayout([
        new ArmorLayer(MaterialKind.Metal),
        ArmorLayer.Air,
        new ArmorLayer(MaterialKind.HeavyArmor, usePoles: true),
    ]),
    DeckArmor = new ArmorLayout([
        new ArmorLayer(MaterialKind.Metal),
        ArmorLayer.Air,
        new ArmorLayer(MaterialKind.LightweightAlloy, usePoles: true),
    ]),
    Beamify = true,
};
Assert(MainWindow.CreateBlueprintName(airPoleParameters) == "HF_Dolphin_100x21x12",
    "Air and pole armor choices leaked into the generated name.");
var airPoleHull = generator.Generate(airPoleParameters);
Assert(HullGeometryValidator.Validate(airPoleHull).Count == 0, "Air-gapped pole armor failed geometry validation.");
Assert(airPoleHull.Blocks.Any(block => block.Shape is BlockShape.Pole1 or BlockShape.Pole2 or BlockShape.Pole3 or BlockShape.Pole4),
    "A pole-enabled armor layout produced no poles.");
Assert(airPoleHull.Blocks.Where(block => block.Material is MaterialKind.HeavyArmor or MaterialKind.LightweightAlloy)
    .All(block => block.Shape is BlockShape.Pole1 or BlockShape.Pole2 or BlockShape.Pole3 or BlockShape.Pole4),
    "A pole-enabled armor layer retained a full-width block or beam.");
var solidThreeLayerHull = generator.Generate(airPoleParameters with
{
    HullArmor = new ArmorLayout([MaterialKind.Metal, MaterialKind.Rubber, MaterialKind.HeavyArmor]),
    DeckArmor = new ArmorLayout([MaterialKind.Metal, MaterialKind.Rubber, MaterialKind.LightweightAlloy]),
    Beamify = false,
});
Assert(airPoleHull.OccupiedCellCount < solidThreeLayerHull.OccupiedCellCount,
    "Air armor layers did not leave their one-metre depth empty.");
var debugPoleHull = generator.Generate(airPoleParameters with { Beamify = false });
Assert(debugPoleHull.Blocks.Where(block => block.Material is MaterialKind.HeavyArmor or MaterialKind.LightweightAlloy)
    .All(block => block.Shape == BlockShape.Cube),
    "Single-block debug mode emitted pole parts instead of source cubes.");
Assert((HullParameters.Default with
    {
        HullArmor = new ArmorLayout([ArmorLayer.Air, new ArmorLayer(MaterialKind.Metal)]),
    }).Validate().Any(error => error.Contains("outer hull armor layer", StringComparison.OrdinalIgnoreCase)),
    "An exposed air layer was accepted as the hull surface.");

var openLayeredHull = generator.Generate(layeredParameters with { DeckArmor = null });
Assert(openLayeredHull.Blocks.All(block => block.Material is not MaterialKind.Wood and not MaterialKind.Lead),
    "An open layered hull retained deck materials.");
Assert(CellMaterials(openLayeredHull).Keys.Count(cell => cell.Y == openLayeredHull.MaxY) <
       layeredMaterials.Keys.Count(cell => cell.Y == layeredHull.MaxY),
    "Hull armor closed the top opening while growing inward.");

var excessiveLayersFailed = false;
try
{
    generator.Generate(HullParameters.Default with
    {
        HullArmor = new ArmorLayout(Enumerable.Repeat(MaterialKind.Metal, HullParameters.Default.Height + HullParameters.Default.Width)),
    });
}
catch (HullGenerationException)
{
    excessiveLayersFailed = true;
}
Assert(excessiveLayersFailed, "An armor layout that consumed the cavity was accepted.");

var evenWidthParameters = HullParameters.Default with { Width = 18 };
Assert(!evenWidthParameters.HasSingleBlockCenterline, "Even widths must be identified as having a between-block symmetry plane.");
var evenWidthHull = generator.Generate(evenWidthParameters);
Assert(HullGeometryValidator.Validate(evenWidthHull).Count == 0, "Even-width hulls must remain valid when explicitly requested.");

foreach (var bow in new[] { -0.9, 0.9 })
foreach (var stern in new[] { -0.9, 0.9 })
foreach (var crossSection in new[] { -0.9, 0.9 })
{
    var hull = generator.Generate(WithShapeFullness(HullParameters.Default with
    {
        Length = 64,
        Width = 18,
        Height = 10,
    }, bow, stern, crossSection));
    Assert(HullGeometryValidator.Validate(hull).Count == 0, "An extreme shape combination failed geometry validation.");
}

GeneratedHull? largeHull = null;
double largeGenerationMs = 0;
double sliderMaximumMs = 0;
if (full)
{
    var generationTimer = Stopwatch.StartNew();
    largeHull = generator.Generate(HullParameters.Default with { Length = 300, Width = 60, Height = 30 });
    generationTimer.Stop();
    largeGenerationMs = generationTimer.Elapsed.TotalMilliseconds;
    Assert(largeHull.OccupiedLength == 300, "300m hull length did not match the request.");
    Assert(HullGeometryValidator.Validate(largeHull).Count == 0, "300m hull failed geometry validation.");
    Assert(generationTimer.Elapsed < TimeSpan.FromSeconds(2), $"300m hull generation took {largeGenerationMs:N0} ms.");

    var sliderMaximumTimer = Stopwatch.StartNew();
    var sliderMaximumHull = generator.Generate(HullParameters.Default with { Length = 500, Width = 250, Height = 100 });
    sliderMaximumTimer.Stop();
    sliderMaximumMs = sliderMaximumTimer.Elapsed.TotalMilliseconds;
    Assert(sliderMaximumHull.OccupiedLength == 500 && sliderMaximumHull.OccupiedWidth == 250 && sliderMaximumHull.OccupiedHeight == 100,
        "A hull at the full dimension-slider envelope did not retain its requested size.");
    Assert(HullGeometryValidator.Validate(sliderMaximumHull).Count == 0,
        "A hull at the full dimension-slider envelope failed geometry validation.");
}

// The 24-entry rotation table is shared by the corner resolver and the beam
// footprint expansion, so pin its algebra rather than only its call sites.
var seenAxes = new HashSet<(int, int, int, int, int, int)>();
for (var rotation = 0; rotation <= 23; rotation++)
{
    var axes = BlockRotations.GetRotationAxes(rotation);
    Assert(Dot(axes.Forward, axes.Up) == 0, $"Rotation {rotation} has non-perpendicular forward and up axes.");
    Assert(IsUnit(axes.Forward) && IsUnit(axes.Up), $"Rotation {rotation} has a non-unit axis.");
    Assert(IsUnit(axes.Right) && Dot(axes.Right, axes.Forward) == 0 && Dot(axes.Right, axes.Up) == 0, $"Rotation {rotation} has an invalid right axis.");
    Assert(axes.Right == AxisDirection.Cross(axes.Up, axes.Forward), $"Rotation {rotation} changed the right-handed cross-product convention.");
    Assert(seenAxes.Add((axes.Forward.X, axes.Forward.Y, axes.Forward.Z, axes.Up.X, axes.Up.Y, axes.Up.Z)), $"Rotation {rotation} duplicates another orientation.");
}
Assert(BlockRotations.GetRotationAxes(0) == new RotationAxes(new AxisDirection(0, 0, 1), new AxisDirection(0, 1, 0)), "Rotation 0 must be forward +Z, up +Y.");
Assert(BlockRotations.GetRotationAxes(16) == new RotationAxes(new AxisDirection(0, 0, 1), new AxisDirection(1, 0, 0)), "Rotation 16 must be forward +Z, up +X.");
Assert(!BlockRotations.IsValid(-1) && !BlockRotations.IsValid(24) && BlockRotations.IsValid(23), "The valid rotation range must stay 0 through 23.");

// Beam run splitting: fewest pieces possible, and never a stray 1 m cube beside a beam.
for (var runLength = 1; runLength <= 64; runLength++)
{
    var pieces = new List<int>();
    BeamOptimizer.AppendRunLengths(runLength, pieces);
    Assert(pieces.Sum() == runLength, $"A run of {runLength} split into {pieces.Sum()} cells.");
    Assert(pieces.Count == (runLength + 3) / 4, $"A run of {runLength} used {pieces.Count} pieces instead of {(runLength + 3) / 4}.");
    Assert(runLength == 1 || pieces.All(length => length is >= 2 and <= 4), $"A run of {runLength} produced a piece the game has no beam for.");
    // Stern to bow order, so the long beams end up forward.
    Assert(pieces.SequenceEqual(pieces.Order()), $"A run of {runLength} was not split in stern-to-bow order.");
}

var fifteenMetreRun = new List<int>();
BeamOptimizer.AppendRunLengths(15, fifteenMetreRun);
Assert(fifteenMetreRun.SequenceEqual([3, 4, 4, 4]), $"A 15 m run split into {string.Join(" + ", fifteenMetreRun)} instead of 3 + 4 + 4 + 4.");

var beamParameters = HullParameters.Default with { Beamify = true };
var beamHull = generator.Generate(beamParameters);
Assert(HullGeometryValidator.Validate(beamHull).Count == 0, "Beamified default hull failed geometry validation.");
Assert(beamHull.OccupiedCellCount == singleBlockHull.OccupiedCellCount, "Beamifying changed how many lattice cells the hull occupies.");
Assert(beamHull.BlockCount < singleBlockHull.BlockCount, "Beamifying did not reduce the block count.");
Assert(beamHull.Blocks.All(block => block.CellLength == 1 || block.Rotation == 0 ||
       (block.Shape is BlockShape.Beam2 or BlockShape.Beam3 or BlockShape.Beam4 &&
        block.Rotation is 1 or 3 or 8)),
    "A native member used an unsupported multi-axis beam rotation.");
Assert(beamHull.Blocks.Any(block => block.Shape == BlockShape.Beam4), "The default hull produced no 4 m beams.");
// Bow bias, checked on the hull rather than only on the split function. A column can
// hold several runs separated by fitted shapes or by gaps in the shell, and a forward
// run is free to be shorter than one behind it, so the pieces of each run are
// reconstructed by contiguity before their lengths are compared.
var checkedRuns = 0;
foreach (var column in beamHull.Blocks.GroupBy(block => (block.X, block.Y)))
{
    var run = new List<int>();
    var runEndZ = int.MinValue;
    foreach (var block in column.OrderBy(block => block.Z))
    {
        var isMergeable = block.Shape == BlockShape.Cube ||
            block.CellLength > 1 && block.Rotation == 0;
        if (!isMergeable || block.Z != runEndZ)
        {
            AssertRunIsBowBiased(run, column.Key.X, column.Key.Y);
            run.Clear();
        }

        if (!isMergeable)
        {
            runEndZ = int.MinValue;
            continue;
        }

        run.Add(block.CellLength);
        runEndZ = block.Z + block.CellLength;
    }

    AssertRunIsBowBiased(run, column.Key.X, column.Key.Y);
}

Assert(checkedRuns > 0, "No merged runs were found to check for bow bias.");

void AssertRunIsBowBiased(List<int> lengths, int x, int y)
{
    if (lengths.Count == 0)
        return;
    checkedRuns++;
    Assert(lengths.SequenceEqual(lengths.Order()), $"A run in column ({x}, {y}) placed a longer beam aft of a shorter one: {string.Join(" + ", lengths)}.");
    Assert(lengths.Sum() >= 1 && lengths.Count == (lengths.Sum() + 3) / 4, $"A run in column ({x}, {y}) used {lengths.Count} pieces for {lengths.Sum()} m.");
}
Assert(beamHull.MinX == singleBlockHull.MinX && beamHull.MaxX == singleBlockHull.MaxX &&
       beamHull.MinY == singleBlockHull.MinY && beamHull.MaxY == singleBlockHull.MaxY &&
       beamHull.MinZ == singleBlockHull.MinZ && beamHull.MaxZ == singleBlockHull.MaxZ, "Beamifying changed the hull bounds.");
Assert(beamHull.Blocks.SelectMany(block => block.OccupiedCells).ToHashSet()
           .SetEquals(singleBlockHull.Blocks.SelectMany(block => block.OccupiedCells)), "Beamifying changed the occupied cell set.");

// Per-layer construction: beam slopes keep longitudinal footprints, mirror their
// handed item across the hull, and alternate across neighboring surface rows.
foreach (var construction in new[]
         {
             ArmorConstruction.BeamSlopeUp,
             ArmorConstruction.BeamSlopeDown,
             ArmorConstruction.BeamSlopeSpike,
         })
{
    var source = (from x in new[] { -2, 2 }
                  from y in new[] { 0, 1 }
                  from z in Enumerable.Range(0, 5)
                  select new BlockPlacement(BlockShape.Cube, MaterialKind.Metal, x, y, z, 0)
                  {
                      Construction = construction,
                      ArmorRegion = ArmorRegion.Side,
                  }).ToArray();
    var patterned = BeamOptimizer.Merge(source);
    Assert(patterned.SelectMany(block => block.OccupiedCells).ToHashSet()
               .SetEquals(source.Select(block => block.Position)), $"{construction} changed occupied cells.");
    Assert(patterned.All(block => block.CellLength is 2 or 3 &&
                                  BlockShapeMetadata.Get(block.Shape).Family == StructuralFamily.BeamSlope),
        $"{construction} did not use native 2–4 m beam slopes.");
    foreach (var block in patterned.Where(block => block.X > 0))
    {
        var mirror = patterned.Single(candidate => candidate.X == -block.X && candidate.Y == block.Y && candidate.Z == block.Z);
        Assert(BlockShapeMetadata.MirrorShape(block.Shape) == mirror.Shape && mirror.Rotation == block.Rotation,
            $"{construction} did not use the mirrored beam-slope partner.");
    }
    if (construction == ArmorConstruction.BeamSlopeSpike)
    {
        var rows = patterned.Where(block => block.X > 0 && block.Z == 0).OrderBy(block => block.Y).ToArray();
        Assert(rows.Length == 2 && rows[0].Shape != rows[1].Shape,
            "Spike construction did not alternate neighboring side rows.");
    }
}
var shortSlopeSource = new[]
{
    new BlockPlacement(BlockShape.Cube, MaterialKind.Metal, -1, 0, 0, 0)
        { Construction = ArmorConstruction.BeamSlopeUp, ArmorRegion = ArmorRegion.Side },
    new BlockPlacement(BlockShape.Cube, MaterialKind.Metal, 1, 0, 0, 0)
        { Construction = ArmorConstruction.BeamSlopeUp, ArmorRegion = ArmorRegion.Side },
};
Assert(BeamOptimizer.Merge(shortSlopeSource).All(block => block.Shape == BlockShape.Cube),
    "A one-metre beam-slope run did not fall back to a full block.");
Assert(new ArmorLayer(MaterialKind.Metal, usePoles: true).Construction == ArmorConstruction.Pole &&
       new ArmorLayer(null, ArmorConstruction.BeamSlopeSpike).Construction == ArmorConstruction.Solid,
    "Armor-layer compatibility or air-gap construction normalization failed.");

foreach (var construction in Enum.GetValues<ArmorConstruction>())
{
    var constructionLayout = new ArmorLayout([new ArmorLayer(MaterialKind.Metal, construction)]);
    var constructionHull = generator.Generate(beamParameters with
    {
        HullArmor = constructionLayout,
        BottomArmor = constructionLayout,
        DeckArmor = constructionLayout,
    });
    Assert(HullGeometryValidator.Validate(constructionHull).Count == 0,
        $"{construction} armor failed whole-hull validation.");
    if (construction is ArmorConstruction.BeamSlopeUp or ArmorConstruction.BeamSlopeDown or ArmorConstruction.BeamSlopeSpike)
        Assert(constructionHull.Blocks.Any(block => BlockShapeMetadata.Get(block.Shape).Family == StructuralFamily.BeamSlope),
            $"{construction} produced no beam slopes.");
}

Assert(HullGeometryValidator.Validate(generator.Generate(beamParameters with { DeckArmor = null })).Count == 0, "Beamified deckless hull failed geometry validation.");
Assert(HullGeometryValidator.Validate(generator.Generate(beamParameters with { Width = 18 })).Count == 0, "Beamified even-width hull failed geometry validation.");
foreach (var bow in new[] { -0.9, 0.9 })
foreach (var stern in new[] { -0.9, 0.9 })
foreach (var crossSection in new[] { -0.9, 0.9 })
{
    var extremeBeamHull = generator.Generate(WithShapeFullness(beamParameters with
    {
        Length = 64,
        Width = 18,
        Height = 10,
    }, bow, stern, crossSection));
    Assert(HullGeometryValidator.Validate(extremeBeamHull).Count == 0, "An extreme beamified shape combination failed geometry validation.");
}

double beamLargeMs = 0;
if (full)
{
    var beamTimer = Stopwatch.StartNew();
    var largeBeamHull = generator.Generate(beamParameters with { Length = 300, Width = 60, Height = 30 });
    beamTimer.Stop();
    beamLargeMs = beamTimer.Elapsed.TotalMilliseconds;
    Assert(largeBeamHull.OccupiedCellCount == largeHull!.OccupiedCellCount, "The beamified 300m hull covers a different number of cells.");
    Assert(beamTimer.Elapsed < TimeSpan.FromSeconds(2), $"Beamified 300m hull generation took {beamLargeMs:N0} ms.");
}

// The editor startup default must not fragment straight exterior runs into one-metre
// cubes around shell candidates the exporter will flatten anyway.
BeamRunEligibilityTests.Run(generator, catalog);

// --- Vertical slope fill ---------------------------------------------------
SelfTestGroupCatalog.Current = "slope-fills";
var smallVfillHull = VerticalSlopeFillTests.Run(generator, full);
var smallHfillHull = HorizontalSlopeFillTests.Run(generator, full);
var (smallXfillHull, bidirectionalXfillFixture) = CrossSectionSlopeFillTests.Run(generator, full);
var smallCombinedFillHull = CombinedSlopeFillTests.Run(generator, full);
var smallHybridFillHull = HybridSlopeFillTests.Run(generator, full);
var correctedFillHulls = CorrectedSlopeFillTests.Run(generator);
var invalidRotationSlope = smallXfillHull.Blocks.First(block =>
    block.Origin == BlockOrigin.Smoothing && block.CellLength > 1);
var invalidRotationHull = smallXfillHull with
{
    Blocks = smallXfillHull.Blocks.Select(block => block == invalidRotationSlope
            ? block with { Rotation = 24 }
            : block)
        .ToArray(),
};
Assert(HullGeometryValidator.Validate(invalidRotationHull)
        .Any(error => error.Contains("invalid rotation 24", StringComparison.Ordinal)),
    "The validator accepted a rotation outside the game's 24 native orientations.");
// Multi-cell slope footprints expand along the rotation's forward axis, exactly
// like beams, so pin CellLength and representative occupied-cell directions.
Assert(new BlockPlacement(BlockShape.Slope4, MaterialKind.Metal, 0, 0, 0, 0).CellLength == 4, "Slope4 must occupy four cells.");
Assert(new BlockPlacement(BlockShape.Slope2, MaterialKind.Metal, 0, 0, 0, 0).CellLength == 2, "Slope2 must occupy two cells.");
Assert(new BlockPlacement(BlockShape.Slope4, MaterialKind.Metal, 5, 6, 7, 1).OccupiedCells
        .SequenceEqual([(5, 6, 7), (6, 6, 7), (7, 6, 7), (8, 6, 7)]), "A rotation-1 native slope must extend along +X.");
Assert(new BlockPlacement(BlockShape.Slope4, MaterialKind.Metal, 5, 6, 7, 12).OccupiedCells
        .SequenceEqual([(5, 6, 7), (5, 6, 8), (5, 6, 9), (5, 6, 10)]), "A rotation-12 slope must extend along +Z.");
Assert(new BlockPlacement(BlockShape.Slope3, MaterialKind.Metal, 5, 6, 7, 14).OccupiedCells
        .SequenceEqual([(5, 6, 7), (5, 6, 6), (5, 6, 5)]), "A rotation-14 slope must extend along -Z.");
Assert(new BlockPlacement(BlockShape.Slope2, MaterialKind.Metal, 5, 6, 7, 4).OccupiedCells
        .SequenceEqual([(5, 6, 7), (5, 5, 7)]), "A rotation-4 slope must extend along -Y.");
Assert(new BlockPlacement(BlockShape.Slope2, MaterialKind.Metal, 5, 6, 7, 6).OccupiedCells
        .SequenceEqual([(5, 6, 7), (5, 5, 7)]), "A rotation-6 slope must extend along -Y.");

// The captured ground-truth reference hull (250x25x21, Bp54, deck, beamified).
var vfillParameters = HullParameters.Default with
{
    Length = 250, Width = 25, Height = 21,
    BowFullness = 0.54, SternFullness = 0, CrossSectionCurve = 0.35,
    Beamify = true, Smoothing = SmoothingMethod.VerticalSlopeFill,
    Shape = null,
};
var vfillHull = generator.GenerateLegacyRegression(vfillParameters);
var vfillBase = generator.GenerateLegacyRegression(vfillParameters with { Smoothing = SmoothingMethod.None });
Assert(HullGeometryValidator.Validate(vfillHull).Count == 0, "Vertical-fill hull failed geometry validation.");
Assert(vfillHull.MinX == vfillBase.MinX && vfillHull.MaxX == vfillBase.MaxX &&
       vfillHull.MinY == vfillBase.MinY && vfillHull.MaxY == vfillBase.MaxY &&
       vfillHull.MinZ == vfillBase.MinZ && vfillHull.MaxZ == vfillBase.MaxZ, "Vertical fill changed the hull bounds.");

var vfillBaseCells = vfillBase.Blocks.SelectMany(block => block.OccupiedCells).ToHashSet();
var vfillSlopes = vfillHull.Blocks.Where(block => block.Origin == BlockOrigin.Smoothing).ToArray();
var vfillSlopeCells = vfillSlopes.SelectMany(block => block.OccupiedCells).ToList();
Assert(vfillSlopes.Length > 0, "Vertical fill added no slopes to the reference hull.");
Assert(vfillHull.Blocks.Where(block => block.Origin == BlockOrigin.Shell).SequenceEqual(vfillBase.Blocks),
    "Vertical fill altered a shell or beam placement instead of only adding slopes.");
Assert(vfillSlopes.All(block => block.Shape is BlockShape.Slope1 or BlockShape.Slope2 or BlockShape.Slope3 or BlockShape.Slope4),
    "Vertical fill emitted a non-slope shape.");
Assert(vfillSlopes.All(block => block.Rotation is 4 or 6 or 12 or 14), "Vertical fill used a rotation other than 4/6/12/14.");
Assert(vfillSlopes.Any(block => block.Rotation is 12 or 4) && vfillSlopes.Any(block => block.Rotation is 14 or 6),
    "Vertical fill produced no leading/trailing split on the reference hull.");
Assert(vfillSlopeCells.Count == vfillSlopeCells.Distinct().Count(), "Vertical fill slopes overlap each other.");
Assert(vfillSlopeCells.All(cell => !vfillBaseCells.Contains(cell)), "A vertical-fill slope overlaps the base hull.");
Assert(vfillSlopeCells.All(cell =>
        cell.X >= vfillHull.MinX && cell.X <= vfillHull.MaxX &&
        cell.Y >= vfillHull.MinY && cell.Y <= vfillHull.MaxY &&
        cell.Z >= vfillHull.MinZ && cell.Z <= vfillHull.MaxZ), "A vertical-fill slope left the declared bounds.");
Assert(vfillSlopeCells.All(cell => new[]
    {
        (cell.X - 1, cell.Y, cell.Z), (cell.X + 1, cell.Y, cell.Z),
        (cell.X, cell.Y - 1, cell.Z), (cell.X, cell.Y + 1, cell.Z),
        (cell.X, cell.Y, cell.Z - 1), (cell.X, cell.Y, cell.Z + 1),
    }.Any(vfillBaseCells.Contains)), "A vertical-fill slope is not face-adjacent to the hull.");
Assert(vfillHull.OccupiedCellCount == vfillBase.OccupiedCellCount + vfillSlopeCells.Count, "Vertical fill is not purely additive.");

// X-mirror symmetry of the added slopes: every port slope cell has its reflection.
var vfillSlopeSet = vfillSlopeCells.ToHashSet();
Assert(vfillSlopeCells.All(cell => vfillSlopeSet.Contains((vfillHull.MinX + vfillHull.MaxX - cell.X, cell.Y, cell.Z))),
    "Vertical fill is not port/starboard symmetric.");

// Handedness: bow-half longitudinal slopes descend toward the bow (rotation 12),
// stern-half ones toward the stern (rotation 14). The pivot is near the widest
// station, which for this hull is well aft of centre, so both must appear.
var vfillMidZ = (vfillHull.MinZ + vfillHull.MaxZ) / 2;
var longitudinalSlopes = vfillSlopes.Where(block => block.Rotation is 12 or 14).ToArray();
Assert(longitudinalSlopes.Where(block => block.Rotation == 12).All(block => block.Z > vfillHull.MinZ) &&
       longitudinalSlopes.Any(block => block.Rotation == 12 && block.Z > vfillMidZ), "No forward-descending slopes in the bow.");
Assert(longitudinalSlopes.Any(block => block.Rotation == 14 && block.Z < vfillMidZ), "No aft-descending slopes in the stern.");

Assert(generator.Generate(HullParameters.Default with { Smoothing = SmoothingMethod.VerticalSlopeFill }).Blocks
        .SequenceEqual(defaultHull.Blocks) == false, "Vertical fill did nothing to the default hull.");
Assert(generator.Generate(HullParameters.Default with { Smoothing = SmoothingMethod.None }).Blocks
        .SequenceEqual(defaultHull.Blocks), "SmoothingMethod.None must be a no-op.");
Assert(HullGeometryValidator.Validate(generator.GenerateLegacyRegression(vfillParameters with { DeckArmor = null })).Count == 0,
    "Deckless vertical-fill hull failed geometry validation.");
Assert(HullGeometryValidator.Validate(generator.GenerateLegacyRegression(vfillParameters with
    {
        HullArmor = ArmorLayout.Single(MaterialKind.Wood),
        DeckArmor = ArmorLayout.Single(MaterialKind.Wood),
        Beamify = false,
    })).Count == 0,
    "Non-Metal deckless-style vertical-fill hull failed geometry validation.");

double vfillLargeMs = 0;
if (full)
{
    var vfillTimer = Stopwatch.StartNew();
    var largeVfillHull = generator.GenerateLegacyRegression(vfillParameters with { Length = 300, Width = 60, Height = 30 });
    vfillTimer.Stop();
    vfillLargeMs = vfillTimer.Elapsed.TotalMilliseconds;
    Assert(HullGeometryValidator.Validate(largeVfillHull).Count == 0, "300m vertical-fill hull failed geometry validation.");
    Assert(vfillTimer.Elapsed < TimeSpan.FromSeconds(2), $"300m vertical-fill hull generation took {vfillLargeMs:N0} ms.");
}

foreach (var material in Enum.GetValues<MaterialKind>())
{
    Assert(catalog.Resolve(material, BlockShape.Cube).Guid != Guid.Empty, $"{material} base block could not be resolved.");
    Assert(!catalog.Resolve(material, BlockShape.Slope1).IsFallback, $"{material} 1m slope could not be resolved from the installed catalog.");
    foreach (var slope in new[] { BlockShape.Slope2, BlockShape.Slope3, BlockShape.Slope4 })
        Assert(!catalog.Resolve(material, slope).IsFallback, $"{material} {slope} could not be resolved from the installed catalog.");
    Assert(!catalog.Resolve(material, BlockShape.CornerLeft).IsFallback, $"{material} left 1m corner could not be resolved from the installed catalog.");
    Assert(!catalog.Resolve(material, BlockShape.CornerRight).IsFallback, $"{material} right 1m corner could not be resolved from the installed catalog.");

    var cubeCost = catalog.Resolve(material, BlockShape.Cube).MaterialCost;
    foreach (var (beamShape, beamLength) in new[] { (BlockShape.Beam2, 2), (BlockShape.Beam3, 3), (BlockShape.Beam4, 4) })
    {
        var beam = catalog.Resolve(material, beamShape);
        Assert(!beam.IsFallback, $"{material} {beamLength}m beam could not be resolved from the installed catalog.");
        Assert(beam.Guid != Guid.Empty, $"{material} {beamLength}m beam resolved to an empty GUID.");
        // The catalog scales beam cost from the cube baseline by CostWeightHealthScaling.
        // That only holds because Load reads Items before ItemDup; pin it here, because
        // the dependency lives in folder enumeration order and is invisible in the code.
        Assert(Math.Abs(beam.MaterialCost - beamLength * cubeCost) < 1e-9, $"{material} {beamLength}m beam does not cost {beamLength} times its cube.");
    }
    foreach (var (poleShape, poleLength) in new[]
             {
                 (BlockShape.Pole1, 1), (BlockShape.Pole2, 2),
                 (BlockShape.Pole3, 3), (BlockShape.Pole4, 4),
             })
    {
        var pole = catalog.Resolve(material, poleShape);
        Assert(!pole.IsFallback, $"{material} {poleLength}m pole could not be resolved from the installed catalog.");
        Assert(pole.Guid != Guid.Empty, $"{material} {poleLength}m pole resolved to an empty GUID.");
    }
}
foreach (var shape in new[]
         {
             BlockShape.BeamSlope2, BlockShape.BeamSlope3, BlockShape.BeamSlope4,
             BlockShape.BeamSlopeMirrored2, BlockShape.BeamSlopeMirrored3, BlockShape.BeamSlopeMirrored4,
         })
    Assert(!catalog.Resolve(MaterialKind.Metal, shape).IsFallback, $"Metal {shape} could not be resolved from the installed catalog.");

var tempDirectory = Path.Combine(Path.GetTempPath(), $"FtdHullGeneratorSelfTest-{Guid.NewGuid():N}");
try
{
    var result = new BlueprintExporter().Export(singleBlockHull, catalog, tempDirectory, "Self test hull");
    Assert(File.Exists(result.FilePath), "Blueprint export did not create a file.");
    Assert(result.GenerationParametersPath is null, "Normal exports must not create a generation-parameter sidecar.");
    Assert(!Path.GetFileName(result.FilePath).Contains(' '), "Exported blueprint filenames must not contain spaces.");
    using var blueprint = JsonDocument.Parse(File.ReadAllText(result.FilePath));
    var root = blueprint.RootElement;
    Assert(!root.GetProperty("Name").GetString()!.Contains(' '), "The blueprint name stored in JSON must not contain spaces.");
    var craft = root.GetProperty("Blueprint");
    Assert(craft.GetProperty("BLP").GetArrayLength() == singleBlockHull.BlockCount, "Exported position count was wrong.");
    Assert(craft.GetProperty("BLR").GetArrayLength() == singleBlockHull.BlockCount, "Exported rotation count was wrong.");
    Assert(craft.GetProperty("BlockIds").GetArrayLength() == singleBlockHull.BlockCount, "Exported item count was wrong.");
    Assert(craft.GetProperty("COL").GetArrayLength() == 32, "Exported color palette did not have 32 entries.");
    Assert(craft.GetProperty("CSI").GetArrayLength() == 80, "Exported construct info did not have 80 entries.");
    Assert(craft.GetProperty("BlockCount").GetInt32() == singleBlockHull.BlockCount, "Exported BlockCount was wrong.");
    Assert(craft.GetProperty("PersistentSubObjectIndex").GetInt32() == -1, "Persistent sub-object index did not use the neutral value.");
    Assert(craft.GetProperty("PersistentBlockIndex").GetInt32() == -1, "Persistent block index did not use the neutral value.");
    Assert(craft.GetProperty("SerialisedInfo").GetProperty("IsEmpty").GetBoolean(), "SerialisedInfo was not emitted as empty.");
    Assert(!craft.GetProperty("AuthorDetails").GetProperty("Valid").GetBoolean(), "Generated author details must remain invalid.");
    Assert(craft.GetProperty("BlockData").GetString() == string.Empty, "Neutral BlockData was not emitted as an empty byte payload.");
    Assert(craft.GetProperty("VehicleData").ValueKind == JsonValueKind.Null, "VehicleData must remain null when no game-owned vehicle state exists.");
    var expectedFittedReplacements = singleBlockHull.Blocks.Count(block => block.Shape != BlockShape.Cube);
    Assert(result.ShapeFallbackCount == expectedFittedReplacements, "Candidate fitted shapes were not conservatively replaced during export.");
    Assert(craft.GetProperty("BlockIds").EnumerateArray().Select(value => value.GetInt32()).Distinct().Count() == 1, "Recovery export contained a non-cube block mapping.");
    Assert(craft.GetProperty("BLR").EnumerateArray().All(value => value.GetInt32() == 0), "Recovery export contained a fitted-block rotation.");
    Assert(root.GetProperty("ItemDictionary").EnumerateObject().Count() == 2, "Recovery export item dictionary must contain only the vehicle and selected material cube.");
    Assert(root.GetProperty("ItemDictionary").GetProperty(craft.GetProperty("ItemNumber").GetInt32().ToString()).GetString() == FtdBlockCatalog.ConstructableVehicleGuid.ToString(), "Vehicle item GUID was missing from the item dictionary.");

    var parameterExport = new BlueprintExporter().Export(
        layeredHull,
        catalog,
        tempDirectory,
        "Self test generation parameters",
        recordGenerationParameters: true);
    Assert(parameterExport.GenerationParametersPath is { } parameterPath && File.Exists(parameterPath),
        "Debug export did not create its generation-parameter sidecar.");
    using (var parameterDocument = JsonDocument.Parse(File.ReadAllText(parameterExport.GenerationParametersPath!)))
    {
        var parameterRoot = parameterDocument.RootElement;
        Assert(parameterRoot.GetProperty("Format").GetString() == "HullForge.GenerationParameters",
            "Generation-parameter sidecar used the wrong format identifier.");
        Assert(parameterRoot.GetProperty("FormatVersion").GetInt32() == 1,
            "Generation-parameter sidecar used the wrong format version.");
        Assert(parameterRoot.GetProperty("BlueprintFile").GetString() == Path.GetFileName(parameterExport.FilePath),
            "Generation-parameter sidecar did not identify its blueprint.");
        Assert(parameterRoot.GetProperty("GameVersion").GetString() == catalog.GameVersion,
            "Generation-parameter sidecar did not record the catalog version.");

        var recorded = parameterRoot.GetProperty("Parameters");
        Assert(recorded.GetProperty("Length").GetInt32() == layeredParameters.Length &&
               recorded.GetProperty("Width").GetInt32() == layeredParameters.Width &&
               recorded.GetProperty("Height").GetInt32() == layeredParameters.Height &&
               recorded.GetProperty("Beamify").GetBoolean() == layeredParameters.Beamify,
            "Generation-parameter sidecar did not preserve the scalar generation inputs.");
        Assert(recorded.GetProperty("HullArmor").GetArrayLength() == layeredParameters.HullArmor.Thickness &&
               recorded.GetProperty("HullArmor")[1].GetProperty("Material").GetString() == nameof(MaterialKind.HeavyArmor),
            "Generation-parameter sidecar did not preserve the ordered armor stack.");
        Assert(recorded.GetProperty("DeckArmor").GetArrayLength() == layeredParameters.DeckArmor!.Thickness,
            "Generation-parameter sidecar did not preserve the deck armor stack.");
        Assert(recorded.GetProperty("BottomArmor").ValueKind == JsonValueKind.Null &&
               recorded.GetProperty("EffectiveBottomArmor").GetArrayLength() == layeredParameters.EffectiveBottomArmor.Thickness,
            "Generation-parameter sidecar did not preserve the bottom-armor fallback.");
        Assert(recorded.GetProperty("Shape").GetProperty("Body").GetProperty("Style").GetString() == nameof(BodyStyle.Rounded) &&
               recorded.GetProperty("EffectiveShape").GetProperty("Bow").GetProperty("EntranceLengthPercent").GetInt32() ==
               layeredParameters.EffectiveShape.Bow.EntranceLengthPercent,
            "Generation-parameter sidecar did not preserve the complete shape settings.");
        Assert(recorded.GetProperty("Bulb").ValueKind == JsonValueKind.Null &&
               recorded.GetProperty("EffectiveBulb").GetProperty("LengthPercent").GetInt32() == BulbSettings.Default.LengthPercent,
            "Generation-parameter sidecar did not preserve the bulb fallback.");
        Assert(recorded.GetProperty("Superstructure").ValueKind == JsonValueKind.Null &&
               !recorded.GetProperty("EffectiveSuperstructure").GetProperty("Enabled").GetBoolean(),
            "Generation-parameter sidecar did not preserve the superstructure fallback.");
    }

    var beamResult = new BlueprintExporter().Export(beamHull, catalog, tempDirectory, "Self test beam hull");
    Assert(beamResult.BlockCount < result.BlockCount, "The beamified export did not contain fewer blocks.");
    Assert(beamResult.BeamCount > 0, "The beamified export contained no beams.");
    Assert(beamResult.OccupiedCellCount == result.BlockCount, "The beamified export covers a different number of metres.");
    Assert(Math.Abs(beamResult.MaterialCost - result.MaterialCost) < 1e-6, "Beamifying changed the exported material cost.");

    using var beamBlueprint = JsonDocument.Parse(File.ReadAllText(beamResult.FilePath));
    var beamRoot = beamBlueprint.RootElement;
    var beamCraft = beamRoot.GetProperty("Blueprint");
    Assert(beamCraft.GetProperty("BLP").GetArrayLength() == beamResult.BlockCount, "Beamified position count did not match the reported block count.");
    Assert(beamCraft.GetProperty("BLR").GetArrayLength() == beamResult.BlockCount, "Beamified rotation count did not match the reported block count.");
    Assert(beamCraft.GetProperty("BlockIds").GetArrayLength() == beamResult.BlockCount, "Beamified item count did not match the reported block count.");
    Assert(beamCraft.GetProperty("BLR").EnumerateArray().All(value => value.GetInt32() is 0 or 1 or 3 or 8),
        "A beamified export emitted an unsupported solid-beam rotation.");
    Assert(beamCraft.GetProperty("MinCords").GetString() == craft.GetProperty("MinCords").GetString(), "Beamifying changed the exported minimum bounds.");
    Assert(beamCraft.GetProperty("MaxCords").GetString() == craft.GetProperty("MaxCords").GetString(), "Beamifying changed the exported maximum bounds.");
    Assert(Math.Abs(beamRoot.GetProperty("SavedMaterialCost").GetDouble() - root.GetProperty("SavedMaterialCost").GetDouble()) < 1e-6, "Beamifying changed SavedMaterialCost in the written file.");

    var beamSlopeLayout = new ArmorLayout([new ArmorLayer(MaterialKind.Metal, ArmorConstruction.BeamSlopeSpike)]);
    var beamSlopeHull = generator.Generate(beamParameters with
    {
        HullArmor = beamSlopeLayout,
        BottomArmor = beamSlopeLayout,
        DeckArmor = beamSlopeLayout,
    });
    var beamSlopeResult = new BlueprintExporter().Export(beamSlopeHull, catalog, tempDirectory, "Self test beam slope hull");
    Assert(beamSlopeResult.BeamSlopeCount > 0 && beamSlopeResult.ShapeFallbackCount == 0,
        "Beam-slope export did not report native beam slopes without fallbacks.");
    using var beamSlopeBlueprint = JsonDocument.Parse(File.ReadAllText(beamSlopeResult.FilePath));
    // Every beam-slope member runs along Z: the upward ones at rotation 0, the downward ones
    // rolled 180° about that axis at rotation 12. Nothing else is a legal armor rotation.
    var beamSlopeRotations = beamSlopeBlueprint.RootElement.GetProperty("Blueprint").GetProperty("BLR")
        .EnumerateArray().Select(value => value.GetInt32()).ToArray();
    Assert(beamSlopeRotations.All(rotation => rotation is 0 or 12) && beamSlopeRotations.Contains(12),
        "Longitudinal beam-slope armor exported a rotation other than 0 or the downward roll 12.");

    var layeredResult = new BlueprintExporter().Export(layeredBeamHull, catalog, tempDirectory, "Self test layered hull");
    using var layeredBlueprint = JsonDocument.Parse(File.ReadAllText(layeredResult.FilePath));
    var layeredGuids = layeredBlueprint.RootElement.GetProperty("ItemDictionary").EnumerateObject()
        .Select(entry => Guid.Parse(entry.Value.GetString()!)).ToHashSet();
    foreach (var material in layeredParameters.HullArmor.Layers.Concat(layeredParameters.DeckArmor!.Layers)
                 .Select(layer => layer.Material).OfType<MaterialKind>().Distinct())
    {
        Assert(catalog.Blocks.Any(block => block.Material == material && layeredGuids.Contains(block.Guid)),
            $"Layered export item dictionary omitted {material}.");
    }
    Assert(Math.Abs(layeredBlueprint.RootElement.GetProperty("SavedMaterialCost").GetDouble() - layeredResult.MaterialCost) < 1e-6,
        "Layered export SavedMaterialCost did not match its resolved mixed-material blocks.");

    var airPoleResult = new BlueprintExporter().Export(airPoleHull, catalog, tempDirectory, "Self test air pole hull");
    Assert(airPoleResult.PoleCount > 0, "The air-gapped pole export reported no poles.");
    Assert(airPoleResult.ShapeFallbackCount == airPoleHull.Blocks.Count(block =>
        block.Origin == BlockOrigin.Shell && block.Shape is not BlockShape.Cube and
            not BlockShape.Beam2 and not BlockShape.Beam3 and not BlockShape.Beam4 and
            not BlockShape.Pole1 and not BlockShape.Pole2 and not BlockShape.Pole3 and not BlockShape.Pole4),
        "Native pole catalog entries unexpectedly fell back to cubes.");
    using var airPoleBlueprint = JsonDocument.Parse(File.ReadAllText(airPoleResult.FilePath));
    var airPoleGuids = airPoleBlueprint.RootElement.GetProperty("ItemDictionary").EnumerateObject()
        .Select(entry => Guid.Parse(entry.Value.GetString()!)).ToHashSet();
    Assert(catalog.Blocks.Where(block => block.Shape is BlockShape.Pole1 or BlockShape.Pole2 or BlockShape.Pole3 or BlockShape.Pole4)
        .Any(block => airPoleGuids.Contains(block.Guid)), "The exported item dictionary contains no native pole GUID.");

    // Expand the written file back into lattice cells through its item dictionary. This
    // is the assertion that would catch a wrong footprint direction, a missing beam
    // GUID, or an off-by-one split: the file must describe exactly the cube hull.
    var shapesByGuid = catalog.Blocks.ToDictionary(block => block.Guid, block => block.Shape);
    var itemGuids = beamRoot.GetProperty("ItemDictionary").EnumerateObject()
        .ToDictionary(entry => int.Parse(entry.Name), entry => Guid.Parse(entry.Value.GetString()!));
    var beamIds = beamCraft.GetProperty("BlockIds").EnumerateArray().Select(value => value.GetInt32()).ToArray();
    var beamPositions = beamCraft.GetProperty("BLP").EnumerateArray().Select(value => value.GetString()!).ToArray();
    var writtenRotations = beamCraft.GetProperty("BLR").EnumerateArray()
        .Select(value => value.GetInt32()).ToArray();
    var expandedCells = new List<(int X, int Y, int Z)>();
    for (var index = 0; index < beamPositions.Length; index++)
    {
        var parts = beamPositions[index].Split(',');
        var anchor = (X: int.Parse(parts[0]), Y: int.Parse(parts[1]), Z: int.Parse(parts[2]));
        var shape = shapesByGuid[itemGuids[beamIds[index]]];
        var footprint = new BlockPlacement(shape, beamHull.Parameters.SurfaceMaterial,
            anchor.X, anchor.Y, anchor.Z, writtenRotations[index]);
        foreach (var cell in footprint.OccupiedCells)
            expandedCells.Add(cell);
    }

    Assert(expandedCells.Count == singleBlockHull.OccupiedCellCount, $"The beamified file expanded to {expandedCells.Count} cells instead of {singleBlockHull.OccupiedCellCount}.");
    Assert(expandedCells.Distinct().Count() == expandedCells.Count, "The beamified file contained overlapping block footprints.");
    Assert(expandedCells.ToHashSet().SetEquals(singleBlockHull.Blocks.SelectMany(block => block.OccupiedCells)), "The beamified file did not describe the same hull as the cube export.");

    // Vertical-fill export: slopes ship as real items at their real rotations, and
    // expanding the whole file back through the catalog must reproduce the base
    // hull's cells plus exactly the added slope cells, with nothing overlapping.
    var vfillExport = new BlueprintExporter().Export(vfillHull, catalog, tempDirectory, "Self test vfill hull");
    SlopeFillTestSupport.VerifyExport(smallVfillHull, catalog, tempDirectory, 86);
    SlopeFillTestSupport.VerifyExport(smallHfillHull, catalog, tempDirectory, 108);
    SlopeFillTestSupport.VerifyExport(smallXfillHull, catalog, tempDirectory, 230);
    SlopeFillTestSupport.VerifyExport(bidirectionalXfillFixture, catalog, tempDirectory, 4);
    SlopeFillTestSupport.VerifyExport(smallCombinedFillHull, catalog, tempDirectory, 190);
    SlopeFillTestSupport.VerifyExport(smallHybridFillHull, catalog, tempDirectory,
        smallHybridFillHull.Blocks.Count(block => block.Origin == BlockOrigin.Smoothing));
    foreach (var correctedHull in correctedFillHulls)
        SlopeFillTestSupport.VerifyExport(correctedHull, catalog, tempDirectory,
            correctedHull.Blocks.Count(block => block.Origin == BlockOrigin.Smoothing));
    var superstructureHull = generator.Generate(HullParameters.Default with
    {
        Superstructure = new(true, SuperstructureStyle.FrenchHotel, 4, MaterialKind.LightweightAlloy,
            SuperstructureSmoothingMethod.HorizontalSlopeFill),
    });
    var superstructureExport = new BlueprintExporter().Export(superstructureHull, catalog, tempDirectory,
        MainWindow.CreateBlueprintName(superstructureHull.Parameters));
    Assert(superstructureExport.BlockCount == superstructureHull.BlockCount &&
           superstructureExport.OccupiedCellCount == superstructureHull.OccupiedCellCount,
        "Combined superstructure export changed its placement or occupied-cell count.");
    Assert(superstructureExport.SlopeCount >= superstructureHull.Blocks.Count(block =>
            block.Origin == BlockOrigin.SuperstructureSmoothing),
        "Combined superstructure export omitted horizontal-fill slopes.");
    using (var superstructureBlueprint = JsonDocument.Parse(File.ReadAllText(superstructureExport.FilePath)))
    {
        var superstructureCraft = superstructureBlueprint.RootElement.GetProperty("Blueprint");
        Assert(superstructureCraft.GetProperty("MinCords").GetString() ==
               $"{superstructureHull.MinX},{superstructureHull.MinY},{superstructureHull.MinZ}" &&
               superstructureCraft.GetProperty("MaxCords").GetString() ==
               $"{superstructureHull.MaxX},{superstructureHull.MaxY},{superstructureHull.MaxZ}",
            "Combined superstructure export wrote incorrect bounds.");
    }
    Assert(vfillExport.SlopeCount == vfillSlopes.Length, "The vertical-fill export slope count did not match the hull.");
    Assert(vfillExport.BlockCount == vfillBase.BlockCount + vfillSlopes.Length, "The vertical-fill export did not add one entry per slope.");
    using var vfillBlueprint = JsonDocument.Parse(File.ReadAllText(vfillExport.FilePath));
    var vfillRoot = vfillBlueprint.RootElement;
    var vfillCraft = vfillRoot.GetProperty("Blueprint");
    var vfillRotations = vfillCraft.GetProperty("BLR").EnumerateArray().Select(value => value.GetInt32()).ToArray();
    Assert(vfillRotations.Length == vfillExport.BlockCount, "Vertical-fill BLR length did not match the block count.");
    Assert(vfillRotations.Contains(12) && vfillRotations.Contains(14), "The vertical-fill export emitted no leading and trailing longitudinal slopes.");
    Assert(vfillRotations.All(rotation => rotation is 0 or 1 or 3 or 4 or 6 or 8 or 12 or 14), "The vertical-fill export emitted an unexpected rotation.");
    Assert(vfillCraft.GetProperty("MinCords").GetString() == $"{vfillBase.MinX},{vfillBase.MinY},{vfillBase.MinZ}" &&
           vfillCraft.GetProperty("MaxCords").GetString() == $"{vfillBase.MaxX},{vfillBase.MaxY},{vfillBase.MaxZ}",
        "Vertical fill changed the exported bounds.");

    var vfillItemGuids = vfillRoot.GetProperty("ItemDictionary").EnumerateObject()
        .ToDictionary(entry => int.Parse(entry.Name), entry => Guid.Parse(entry.Value.GetString()!));
    Assert(catalog.Blocks.Where(block => block.Shape is BlockShape.Slope2 or BlockShape.Slope3 or BlockShape.Slope4 && block.Material == vfillHull.Parameters.SurfaceMaterial)
        .Any(block => vfillItemGuids.ContainsValue(block.Guid)), "The vertical-fill item dictionary is missing a multi-metre slope GUID.");

    var vfillIds = vfillCraft.GetProperty("BlockIds").EnumerateArray().Select(value => value.GetInt32()).ToArray();
    var vfillPositions = vfillCraft.GetProperty("BLP").EnumerateArray().Select(value => value.GetString()!).ToArray();
    var vfillExpanded = new List<(int X, int Y, int Z)>();
    for (var index = 0; index < vfillPositions.Length; index++)
    {
        var parts = vfillPositions[index].Split(',');
        var shape = shapesByGuid[vfillItemGuids[vfillIds[index]]];
        var footprint = new BlockPlacement(shape, vfillHull.Parameters.SurfaceMaterial,
            int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]), vfillRotations[index]);
        vfillExpanded.AddRange(footprint.OccupiedCells);
    }

    Assert(vfillExpanded.Distinct().Count() == vfillExpanded.Count, "The vertical-fill file contained overlapping footprints.");
    var vfillExpected = vfillBase.Blocks.SelectMany(block => block.OccupiedCells).Concat(vfillSlopeCells).ToHashSet();
    Assert(vfillExpanded.ToHashSet().SetEquals(vfillExpected), "The vertical-fill file did not describe the base hull plus its slopes.");
}
finally
{
    if (Directory.Exists(tempDirectory))
        Directory.Delete(tempDirectory, recursive: true);
}


// The preview and the exporter must agree on which placements keep their fitted shape,
// otherwise the preview would show geometry the blueprint does not carry. The cube hull
// carries the shell classifier's fitted candidates; beamification absorbs the ones it
// flattens into the run that covers them, so check the agreement on the cube hull.
Assert(singleBlockHull.Blocks.Any(block => block.Origin == BlockOrigin.Shell && block.CellLength == 1 && block.Shape != BlockShape.Cube),
    "The cube hull no longer contains one-metre fitted shell candidates to check.");
Assert(singleBlockHull.Blocks.Where(block => block.Origin == BlockOrigin.Shell && block.CellLength == 1).All(block => !block.KeepsFittedShape),
    "A one-metre shell placement claimed the exporter keeps its fitted shape.");
Assert(beamHull.Blocks.Where(block => block.CellLength > 1).All(block => block.KeepsFittedShape),
    "A multi-cell part did not keep its fitted shape.");
Assert(vfillHull.Blocks.Any(block => block.Origin == BlockOrigin.Smoothing),
    "The vertical-fill hull no longer contains smoothing slopes to check.");
Assert(vfillHull.Blocks.Where(block => block.Origin == BlockOrigin.Smoothing).All(block => block.KeepsFittedShape),
    "A smoothing slope did not keep its fitted shape.");

// Armor depth is what lets the preview hide internal armor by default and style it
// apart from the shell when asked for. Depth zero is the exposed layer, and every
// deeper layer carries the metre it was placed at, for hull and deck stacks alike.
Assert(defaultHull.Blocks.All(block => block.ArmorDepth == 0 && !block.IsInternalArmor),
    "A single-layer hull reported internal armor.");
Assert(layeredHull.Blocks.All(block => block.ArmorDepth >= 0 && block.ArmorDepth < 3),
    "A layered hull placement carried a depth outside its armor stack.");
Assert(layeredHull.Blocks.Where(block => block.Material == MaterialKind.Metal).All(block => block.ArmorDepth == 0),
    "The exposed hull layer was not at depth zero.");
Assert(layeredHull.Blocks.Where(block => block.Material == MaterialKind.HeavyArmor).All(block => block.ArmorDepth == 1),
    "The second hull layer was not at depth one.");
Assert(layeredHull.Blocks.Where(block => block.Material == MaterialKind.Rubber).All(block => block.ArmorDepth == 2),
    "The third hull layer was not at depth two.");
Assert(layeredHull.Blocks.Where(block => block.Material == MaterialKind.Wood).All(block => block.ArmorDepth == 0),
    "The top deck layer was not at depth zero.");
Assert(layeredHull.Blocks.Where(block => block.Material == MaterialKind.Lead).All(block => block.ArmorDepth == 1),
    "The second deck layer was not at depth one.");
Assert(vfillHull.Blocks.Where(block => block.Origin == BlockOrigin.Smoothing).All(block => block.ArmorDepth == 0),
    "A smoothing slope was marked as internal armor.");

// Beam merging is keyed by material alone, so a member may run from the exposed
// taper into the layer behind it; it takes the shallowest depth so the preview
// still draws it as shell, and merging never changes which cells are internal.
var depthBeamHull = generator.Generate(layeredParameters with { Beamify = true });
Assert(HullGeometryValidator.Validate(depthBeamHull).Count == 0, "Beamified layered hull failed geometry validation.");
Assert(depthBeamHull.Blocks.Any(block => block.IsInternalArmor && block.CellLength > 1),
    "The beamified layered hull no longer contains internal beams to check.");
var layeredCubeDepths = layeredHull.Blocks.ToDictionary(block => block.Position, block => block.ArmorDepth);
foreach (var member in depthBeamHull.Blocks)
{
    var cellDepths = member.OccupiedCells.Select(cell => layeredCubeDepths[cell]).ToArray();
    Assert(member.ArmorDepth == cellDepths.Min(),
        $"A merged member at {member.Position} carried depth {member.ArmorDepth} but its cells reach depth {cellDepths.Min()}.");
}

// The preview leaves internal armor out unless asked for, and a cutaway keeps exactly
// the cells on the near side of its plane, shortening beams rather than dropping them.
var shellOnly = HullPreviewControl.SelectDrawnPlacements(depthBeamHull, false, CutawayPlane.None, 0.5);
Assert(shellOnly.All(block => !block.IsInternalArmor) &&
       shellOnly.Count == depthBeamHull.Blocks.Count(block => !block.IsInternalArmor),
    "The default preview selection did not hide exactly the internal armor.");
Assert(HullPreviewControl.SelectDrawnPlacements(depthBeamHull, true, CutawayPlane.None, 0.5)
        .SequenceEqual(depthBeamHull.Blocks),
    "Rendering internal armor with no cutaway did not draw the whole placement list.");
foreach (var plane in new[] { CutawayPlane.Station, CutawayPlane.Centreline, CutawayPlane.Waterline })
{
    foreach (var fraction in new[] { 0.0, 0.37, 0.5, 1.0 })
    {
        var sectioned = HullPreviewControl.SelectDrawnPlacements(depthBeamHull, true, plane, fraction);
        var drawnCells = sectioned.SelectMany(block => block.OccupiedCells).ToArray();
        Assert(drawnCells.Length == drawnCells.Distinct().Count(),
            $"The {plane} cutaway at {fraction} drew overlapping parts.");
        var allCells = depthBeamHull.Blocks.SelectMany(block => block.OccupiedCells).ToArray();
        var (minimum, maximum) = plane switch
        {
            CutawayPlane.Station => (depthBeamHull.MinZ, depthBeamHull.MaxZ),
            CutawayPlane.Centreline => (depthBeamHull.MinX, depthBeamHull.MaxX),
            _ => (depthBeamHull.MinY, depthBeamHull.MaxY),
        };
        var limit = minimum + (int)Math.Round(fraction * (maximum - minimum));
        int Coordinate((int X, int Y, int Z) cell) => plane switch
        {
            CutawayPlane.Station => cell.Z,
            CutawayPlane.Centreline => cell.X,
            _ => cell.Y,
        };
        var expectedKept = allCells.Where(cell => Coordinate(cell) <= limit).ToHashSet();
        // Slopes are kept whole by their anchor, so the drawn set may exceed the plane by
        // slope cells only; every kept cell must be drawn and no beam cell may cross.
        Assert(expectedKept.IsSubsetOf(drawnCells),
            $"The {plane} cutaway at {fraction} dropped cells on the kept side.");
        Assert(sectioned.Where(block => block.Shape is not (BlockShape.Slope2 or BlockShape.Slope3 or BlockShape.Slope4))
                .SelectMany(block => block.OccupiedCells).All(cell => Coordinate(cell) <= limit),
            $"The {plane} cutaway at {fraction} drew a non-slope cell beyond the plane.");
        Assert(fraction < 1.0 || sectioned.SequenceEqual(depthBeamHull.Blocks),
            $"The {plane} cutaway at 1.0 did not draw the whole hull.");
    }
}
// Internal armor is drawn without being asked while the hull stays within the cell
// limit, and left out past it until the debug option forces it. The limit is checked
// against a fabricated oversize hull so the test does not have to generate one.
Assert(HullPreviewControl.DrawsInternalArmorAutomatically(depthBeamHull),
    "A default-size layered hull was not within the automatic internal armor limit.");
Assert(depthBeamHull.InternalArmorCellCount == depthBeamHull.Blocks.Where(block => block.IsInternalArmor).Sum(block => block.CellLength),
    "InternalArmorCellCount did not sum the internal placements' cells.");
var oversizeBlocks = Enumerable.Range(0, HullPreviewControl.AutomaticInternalArmorCellLimit / 4 + 1)
    .Select(index => new BlockPlacement(BlockShape.Beam4, MaterialKind.Metal, 0, 0, index * 4, 0) { ArmorDepth = 1 })
    .Prepend(new BlockPlacement(BlockShape.Cube, MaterialKind.Metal, 1, 0, 0, 0))
    .ToArray();
var oversizeHull = depthBeamHull with { Blocks = oversizeBlocks };
Assert(oversizeHull.InternalArmorCellCount == HullPreviewControl.AutomaticInternalArmorCellLimit + 4,
    "The fabricated oversize hull did not exceed the limit by one beam.");
Assert(!HullPreviewControl.DrawsInternalArmorAutomatically(oversizeHull),
    "A hull past the internal armor limit was still drawn automatically.");
var atLimitHull = depthBeamHull with { Blocks = oversizeBlocks.Take(oversizeBlocks.Length - 1).ToArray() };
Assert(atLimitHull.InternalArmorCellCount == HullPreviewControl.AutomaticInternalArmorCellLimit &&
       HullPreviewControl.DrawsInternalArmorAutomatically(atLimitHull),
    "A hull exactly at the internal armor limit was not drawn automatically.");
// The control itself is a WPF element, so it is exercised on a single-threaded apartment.
Exception? previewFailure = null;
var previewThread = new Thread(() =>
{
    try
    {
        var previewControl = new HullPreviewControl();
        previewControl.SetHull(oversizeHull);
        Assert(!previewControl.InternalArmorDrawn, "The preview drew an oversize hull's interior without being asked.");
        previewControl.ShowInternalArmor = true;
        Assert(previewControl.InternalArmorDrawn, "The debug option did not force an oversize hull's interior on.");
        previewControl.ShowInternalArmor = false;
        previewControl.SetHull(depthBeamHull);
        Assert(previewControl.InternalArmorDrawn, "The preview left out a small hull's interior.");

        // The edit highlight draws the settled finish, animated from the edit red, so the
        // separation lines are already in place while the fade runs and the frame the
        // highlight expires on is the frame the settled rebuild draws.
        var highlightModel = HullPreviewControl.BuildEditHighlightModel(depthBeamHull.Blocks);
        var highlightKeys = depthBeamHull.Blocks.Select(block => (block.Material, block.ArmorDepth)).Distinct().ToArray();
        var highlightMaterials = highlightModel.Children.OfType<GeometryModel3D>()
            .Select(model => model.Material).Distinct().ToArray();
        Assert(highlightMaterials.Length == highlightKeys.Length,
            "The edit highlight no longer builds one face finish per material and depth.");
        Assert(highlightKeys.Any(key => key.ArmorDepth > 0),
            "The hull used to check the edit highlight no longer contains internal armor.");
        foreach (var key in highlightKeys)
        {
            var settled = FaceSignature(HullPreviewControl.CreateFaceMaterial(key.Material, key.ArmorDepth));
            var highlighted = highlightMaterials.FirstOrDefault(material => FaceSignature(material) == settled);
            Assert(highlighted is not null,
                $"The edit highlight for {key.Material} at depth {key.ArmorDepth} does not carry the lines the settled face draws.");
            Assert(FaceBrushesAnimated(highlighted!),
                $"The edit highlight for {key.Material} at depth {key.ArmorDepth} does not fade its lines in.");
        }

        static (Color Fill, Color? Hatch, Color Seam) FaceSignature(Material material)
        {
            var drawing = (DrawingGroup)((DrawingBrush)((DiffuseMaterial)((MaterialGroup)material).Children[0]).Brush).Drawing;
            return (
                SettledFill((SolidColorBrush)((GeometryDrawing)drawing.Children[0]).Brush),
                drawing.Children.Count == 3
                    ? SettledFill((SolidColorBrush)((Pen)((GeometryDrawing)drawing.Children[1]).Pen).Brush)
                    : null,
                SettledFill((SolidColorBrush)((Pen)((GeometryDrawing)drawing.Children[^1]).Pen).Brush));
        }

        // The colour a face brush shows once its fade is over. Reading the animation's base
        // value rather than its colour is what proves the settled frame needs no rebuild.
        static Color SettledFill(SolidColorBrush brush) =>
            (Color)brush.GetAnimationBaseValue(SolidColorBrush.ColorProperty);

        static bool FaceBrushesAnimated(Material material)
        {
            var drawing = (DrawingGroup)((DrawingBrush)((DiffuseMaterial)((MaterialGroup)material).Children[0]).Brush).Drawing;
            return drawing.Children.Cast<GeometryDrawing>().All(child =>
                (child.Brush is null or SolidColorBrush { HasAnimatedProperties: true }) &&
                (child.Pen?.Brush is null or SolidColorBrush { HasAnimatedProperties: true }));
        }
    }
    catch (Exception exception)
    {
        previewFailure = exception;
    }
});
previewThread.SetApartmentState(ApartmentState.STA);
previewThread.Start();
previewThread.Join();
if (previewFailure is not null)
    throw previewFailure;

// Bow and stern styles. The defaults are the original ends, which the fixture oracles
// above already pin bit-for-bit; these checks cover the presets, the style sweep, and
// the profile cut's invariants.
SelfTestGroupCatalog.Current = "presets-and-styles";
Assert(HullParameters.Default.BowStyle == BowStyle.Pointed && HullParameters.Default.SternStyle == SternStyle.Transom,
    "The default hull must use the original pointed bow and flat transom.");
Assert(HullShapePreset.All.Count == 16 && HullShapePreset.All.Select(preset => preset.Name).Distinct().Count() == 16,
    "The stable animal roster must hold all sixteen presets with distinct names.");
Assert(HullShapePreset.All.All(preset => preset.Shape is not null && !string.IsNullOrWhiteSpace(preset.Description)),
    "Every animal must define a complete Shape V2 bundle and a user-facing profile description.");
Assert(Enum.GetValues<BodyStyle>().Where(style => style != BodyStyle.Custom)
        .All(style => HullShapePreset.All.Any(preset => preset.EffectiveShape.Body.Style == style)),
    "The animal roster must demonstrate every named body style.");
Assert(Enum.GetValues<BowStyle>().All(style => HullShapePreset.All.Any(preset => preset.Bow == style)) &&
       Enum.GetValues<SternStyle>().All(style => HullShapePreset.All.Any(preset => preset.Stern == style)),
    "The animal roster must demonstrate every bow and stern style.");
Assert(HullShapePreset.All.Count(preset => preset.HasBulb) == 4,
    "The updated animal roster must include four distinct bulb profiles.");
Assert(HullShapePreset.All.All(preset =>
        preset.BowFullness == preset.EffectiveShape.Bow.Fullness &&
        preset.SternFullness == preset.EffectiveShape.Stern.Fullness &&
        preset.CrossSectionCurve == preset.EffectiveShape.Body.Fullness),
    "A preset's legacy scalar adapter drifted from its Shape V2 bundle.");
Assert(HullShapePreset.All
        .Select(preset => (preset.Bow, preset.Stern, preset.EffectiveShape, preset.HasBulb,
            Bulb: preset.HasBulb ? preset.EffectiveBulb : default(BulbSettings)))
        .Distinct()
        .Count() == HullShapePreset.All.Count,
    "Two animal names resolve to the same complete hull profile.");
Assert(HullShapePreset.Match(HullParameters.Default)?.Name == "Dolphin",
    "The default hull no longer matches the Dolphin preset.");
Assert(HullShapePreset.Match(WithShapeFullness(HullParameters.Default, 0.13, -0.22, 0.47)) is null,
    "A new preset collides with the pinned Custom slider triple.");
Assert(HullShapePreset.Match(HullParameters.Default with { BowStyle = BowStyle.Raked }) is null,
    "Changing only a style still matched an animal.");
var animalGeometrySignatures = new HashSet<string>(StringComparer.Ordinal);
var presetSizes = full
    ? new[] { (Length: 100, Width: 21, Height: 12), (Length: 40, Width: 15, Height: 11) }
    : new[] { (Length: 100, Width: 21, Height: 12) };
foreach (var preset in HullShapePreset.All)
{
    foreach (var (length, width, height) in presetSizes)
    {
        var presetParameters = preset.Apply(HullParameters.Default with { Length = length, Width = width, Height = height });
        Assert(presetParameters.Validate().Count == 0, $"{preset.Name} parameters failed validation at {length}x{width}x{height}.");
        var presetHull = generator.Generate(presetParameters);
        var presetErrors = HullGeometryValidator.Validate(presetHull);
        Assert(presetErrors.Count == 0, $"{preset.Name} at {length}x{width}x{height} failed geometry validation: {string.Join("; ", presetErrors)}");
        Assert(presetHull.OccupiedLength == length && presetHull.OccupiedWidth == width &&
               presetHull.OccupiedHeight == presetParameters.OverallHeight,
            $"{preset.Name} at {length}x{width}x{height} did not fill its declared extents.");
        if (length == 100 && width == 21 && height == 12)
        {
            var signature = string.Join('|', presetHull.Blocks
                .SelectMany(block => block.OccupiedCells)
                .Distinct()
                .OrderBy(cell => cell.Z)
                .ThenBy(cell => cell.Y)
                .ThenBy(cell => cell.X));
            Assert(animalGeometrySignatures.Add(signature),
                $"{preset.Name} discretized to the same standard-size geometry as an earlier animal.");
        }
        Assert(MainWindow.CreateBlueprintName(presetParameters) == $"HF_{preset.Name}_{length}x{width}x{height}",
            $"{preset.Name} did not name its blueprint after itself.");
        var presetShape = presetParameters.EffectiveShape;
        Assert(MainWindow.CreateBlueprintName(presetParameters with
               {
                   BowFullness = presetShape.Bow.Fullness + 0.01,
                   Shape = presetShape with
                   {
                       Bow = presetShape.Bow with { Fullness = presetShape.Bow.Fullness + 0.01 },
                   },
               }).StartsWith("HF_Custom_", StringComparison.Ordinal),
            $"A nudged {preset.Name} still carried the animal name.");
    }
}
Assert(animalGeometrySignatures.Count == HullShapePreset.All.Count,
    "The standard-size animal roster did not produce sixteen distinct hull geometries.");
Assert(HullShapePreset.FindByName("barracuda")?.Name == "Barracuda", "Preset lookup by name must ignore case.");

// Every style pairing generates a valid hull across the contour extremes, and every
// smoothing method stays additive on it.
if (full)
{
    var styleVariants = 0;
    foreach (var bowStyle in Enum.GetValues<BowStyle>())
    foreach (var sternStyle in Enum.GetValues<SternStyle>())
    foreach (var styleBulb in new[] { false, true })
    foreach (var styleWidth in new[] { 15, 18 })
    foreach (var styleDeck in new[] { false, true })
    foreach (var styleBow in new[] { -0.9, 0.9 })
    foreach (var styleStern in new[] { -0.9, 0.9 })
    {
        var baseStyleShape = HullParameters.Default.EffectiveShape;
        var styleParameters = HullParameters.Default with
        {
            Length = 64, Width = styleWidth, Height = 10,
            BowStyle = bowStyle, SternStyle = sternStyle, HasBulb = styleBulb,
            BowFullness = styleBow, SternFullness = styleStern,
            Shape = baseStyleShape with
            {
                Bow = baseStyleShape.Bow with { Fullness = styleBow },
                Stern = baseStyleShape.Stern with { Fullness = styleStern },
            },
            HullArmor = ArmorLayout.Single(MaterialKind.Wood),
            DeckArmor = styleDeck ? ArmorLayout.Single(MaterialKind.Wood) : null,
        };
        GeneratedHull styleBase;
        try
        {
            styleBase = generator.Generate(styleParameters);
        }
        catch (HullGenerationException exception)
        {
            throw new InvalidOperationException($"{bowStyle}/{sternStyle} bulb={styleBulb} w{styleWidth} deck={styleDeck} bow={styleBow} stern={styleStern} failed: {string.Join("; ", exception.Errors)}", exception);
        }
        Assert(HullGeometryValidator.Validate(styleBase).Count == 0, $"{bowStyle}/{sternStyle} base failed geometry validation.");
        foreach (var styleMethod in SelfTestProfiles.ProductSmoothingMethods)
            SlopeFillTestSupport.VerifySmoothing(styleBase, generator.Generate(styleParameters with { Smoothing = styleMethod }), styleMethod);
        styleVariants++;
    }
    Assert(styleVariants == 1152, $"The style sweep covered {styleVariants} variants instead of 1152.");
}
else
{
    var styleVariants = 0;
    var baseStyleShape = HullParameters.Default.EffectiveShape;
    foreach (var bowStyle in Enum.GetValues<BowStyle>())
    foreach (var sternStyle in Enum.GetValues<SternStyle>())
    {
        var styleParameters = HullParameters.Default with
        {
            Length = 64,
            Width = 18,
            Height = 10,
            BowStyle = bowStyle,
            SternStyle = sternStyle,
            Shape = baseStyleShape with
            {
                Bow = baseStyleShape.Bow with { Fullness = -0.9 },
                Stern = baseStyleShape.Stern with { Fullness = 0.9 },
            },
            HullArmor = ArmorLayout.Single(MaterialKind.Wood),
        };
        var styleBase = generator.Generate(styleParameters);
        Assert(HullGeometryValidator.Validate(styleBase).Count == 0,
            $"{bowStyle}/{sternStyle} representative style failed geometry validation.");
        styleVariants++;
    }
    Assert(styleVariants == 36, $"The representative style smoke covered {styleVariants} pairings instead of 36.");
    Console.WriteLine("Style smoke: all 36 bow/stern pairings passed.");
}

var styleBasis = HullParameters.Default with
{
    Length = 80, Width = 21, Height = 12, Smoothing = SmoothingMethod.None,
    Shape = HullShapeSettings.Default,
};
var axeHull = generator.Generate(styleBasis with { BowStyle = BowStyle.Axe });
var axeCells = axeHull.Blocks.SelectMany(block => block.OccupiedCells).ToHashSet();
var axeTip = axeCells.Where(cell => cell.Z == axeHull.MaxZ).ToArray();
Assert(axeTip.Min(cell => cell.Y) == axeHull.MinY && axeTip.Max(cell => cell.Y) < axeHull.MaxY &&
       axeCells.Any(cell => cell.Z < axeHull.MaxZ && cell.Y == axeHull.MaxY),
    "The axe bow does not place its lower prow ahead of the reverse upper stem.");

var rakedComparison = generator.Generate(styleBasis with { BowStyle = BowStyle.Raked });
var clipperComparison = generator.Generate(styleBasis with { BowStyle = BowStyle.Clipper });
var rakedFloors = rakedComparison.Blocks.SelectMany(block => block.OccupiedCells)
    .GroupBy(cell => cell.Z).ToDictionary(group => group.Key, group => group.Min(cell => cell.Y));
var clipperFloors = clipperComparison.Blocks.SelectMany(block => block.OccupiedCells)
    .GroupBy(cell => cell.Z).ToDictionary(group => group.Key, group => group.Min(cell => cell.Y));
Assert(clipperFloors.Any(pair => pair.Value > 0 && pair.Value < rakedFloors[pair.Key]),
    "The clipper forefoot did not produce a longer hollow curve than the linear raked bow.");

var cruiserHull = generator.Generate(styleBasis with { SternStyle = SternStyle.Cruiser });
var fantailHull = generator.Generate(styleBasis with { SternStyle = SternStyle.Fantail });
int EndWidth(GeneratedHull hull) => hull.Blocks.SelectMany(block => block.OccupiedCells)
    .Where(cell => cell.Z == hull.MinZ && cell.Y == hull.MinY + 1)
    .Select(cell => cell.X).Distinct().Count();
Assert(EndWidth(fantailHull) > EndWidth(cruiserHull),
    "The fantail stern is not broader than the cruiser stern at its end plane.");

// Profile-cut invariants: every station keeps its deck row and the row beneath it, the
// floor rises monotonically toward each cut end and is zero at the widest station, and
// the overhang's underside is armor rather than a hole.
var cutParameters = HullParameters.Default with { Length = 60, Width = 17, Height = 12, BowStyle = BowStyle.Raked, SternStyle = SternStyle.Counter };
var cutHull = generator.Generate(cutParameters);
var cutCells = cutHull.Blocks.SelectMany(block => block.OccupiedCells).ToHashSet();
var floorByStation = Enumerable.Range(cutHull.MinZ, cutHull.OccupiedLength)
    .ToDictionary(z => z, z => cutCells.Where(cell => cell.Z == z).Min(cell => cell.Y));
Assert(floorByStation.Values.All(floor => floor <= cutHull.MaxY - 1) &&
       Enumerable.Range(cutHull.MinZ, cutHull.OccupiedLength).All(z => cutCells.Any(cell => cell.Z == z && cell.Y == cutHull.MaxY) &&
                                                                    cutCells.Any(cell => cell.Z == z && cell.Y == cutHull.MaxY - 1)),
    "A profile-cut station lost its deck row or the row beneath it.");
Assert(floorByStation[cutHull.MinZ] > cutHull.MinY && floorByStation[cutHull.MaxZ] > cutHull.MinY,
    "The raked bow and counter stern did not lift the keel at the end planes.");
var widestZ = floorByStation.Where(pair => pair.Value == cutHull.MinY).Select(pair => pair.Key).ToArray();
Assert(widestZ.Length > 0 && widestZ.Min() < 0 && widestZ.Max() > 0, "The keel row vanished amidships.");
for (var z = cutHull.MinZ + 1; z <= widestZ.Min(); z++)
    Assert(floorByStation[z] <= floorByStation[z - 1], $"The counter stern floor rose toward midships at z={z}.");
for (var z = widestZ.Max(); z < cutHull.MaxZ; z++)
    Assert(floorByStation[z] <= floorByStation[z + 1], $"The raked bow floor fell toward the stem at z={z}.");
foreach (var (z, floor) in floorByStation)
{
    Assert(!cutCells.Any(cell => cell.Z == z && cell.Y < floor), $"A cell sat below the floor at z={z}.");
    // The lowest surviving row at a cut station is the overhang's underside, so all of
    // it must be placed as armor: a hollow there would be an opening.
    if (floor > cutHull.MinY)
    {
        var undersideWidth = cutCells.Where(cell => cell.Z == z && cell.Y == floor).Select(cell => cell.X).ToArray();
        Assert(undersideWidth.Length == undersideWidth.Max() - undersideWidth.Min() + 1,
            $"The overhang underside at z={z} has a gap.");
    }
}
Assert(cutCells.All(cell => cutCells.Contains((cutHull.MinX + cutHull.MaxX - cell.X, cell.Y, cell.Z))),
    "A profile-cut hull is not mirror-symmetric.");
// A three-metre hull has one removable row, so the cut can lift the keel by at most one
// row and the deck plus one row still survive everywhere.
var lowCut = generator.Generate(cutParameters with { Height = 3 });
var lowCells = lowCut.Blocks.SelectMany(block => block.OccupiedCells).ToHashSet();
Assert(Enumerable.Range(lowCut.MinZ, lowCut.OccupiedLength).All(z => lowCells.Where(cell => cell.Z == z).Min(cell => cell.Y) <= lowCut.MinY + 1) &&
       lowCells.Any(cell => cell.Y == lowCut.MinY) && lowCells.Any(cell => cell.Z == lowCut.MaxZ && cell.Y == lowCut.MinY + 1),
    "A three-metre hull's profile cut must remove at most one row and keep two rows at the ends.");

// Bulbous bow: a toggle any bow style can carry. The stem still reaches its own end
// plane with a deck row, the bulb alone continues to MaxZ below the deck, the offsets
// move it fore/aft and up, and the bulb proportions take part in preset matching only
// while the bulb is on.
SelfTestGroupCatalog.Current = "bulbs-and-profiles";
var bulbParameters = HullParameters.Default with { Length = 80, Width = 19, Height = 12, HasBulb = true, Bulb = new BulbSettings(10, 40, 0, 0) };
Assert(bulbParameters.Validate().Count == 0, "Default-range bulb settings failed validation.");
Assert((bulbParameters with { Bulb = new BulbSettings(1, 40, 0, 0) }).Validate().Count == 1 &&
       (bulbParameters with { HasBulb = false, Bulb = new BulbSettings(1, 40, 0, 0) }).Validate().Count == 0,
    "Bulb ranges must be enforced only while the bulb is on.");
var bulbHull = generator.Generate(bulbParameters);
Assert(HullGeometryValidator.Validate(bulbHull).Count == 0, "The bulbous hull failed geometry validation.");
Assert(bulbHull.OccupiedLength == 80 && bulbHull.OccupiedWidth == 19 && bulbHull.OccupiedHeight == 12,
    "The bulbous hull did not fill its declared extents.");
var bulbCells = bulbHull.Blocks.SelectMany(block => block.OccupiedCells).ToHashSet();
var stemZ = bulbCells.Where(cell => cell.Y == bulbHull.MaxY).Max(cell => cell.Z);
var bulbStations = bulbHull.MaxZ - stemZ;
Assert(bulbStations >= 3 && bulbStations <= 4, $"A 10% bulb centred on the stem of an 80 m hull should project about 4 m, not {bulbStations}.");
var bulbTop = bulbCells.Where(cell => cell.Z > stemZ).Max(cell => cell.Y);
var bulbBottom = bulbCells.Where(cell => cell.Z > stemZ).Min(cell => cell.Y);
Assert(bulbBottom == bulbHull.MinY && bulbTop >= bulbHull.MinY + 6 && bulbTop <= bulbHull.MinY + 7,
    $"A 40%-of-19 m bulb with no rise should sit on the keel and top out around row 7, not rows {bulbBottom - bulbHull.MinY}..{bulbTop - bulbHull.MinY}.");
Assert(bulbCells.Any(cell => cell.Z == bulbHull.MaxZ), "The bulb did not reach the end plane.");
Assert(!bulbCells.Any(cell => cell.Z > stemZ && cell.Y > bulbTop), "Cells appeared above the bulb ahead of the stem.");
Assert(Enumerable.Range(bulbHull.MinZ, stemZ - bulbHull.MinZ + 1).All(z => bulbCells.Any(cell => cell.Z == z && cell.Y == bulbHull.MaxY)),
    "A stem station lost its deck row.");
Assert(bulbCells.All(cell => bulbCells.Contains((bulbHull.MinX + bulbHull.MaxX - cell.X, cell.Y, cell.Z))),
    "The bulbous hull is not mirror-symmetric.");
foreach (var bulbMethod in SelfTestProfiles.ProductSmoothingMethods)
    SlopeFillTestSupport.VerifySmoothing(bulbHull, generator.Generate(bulbParameters with { Smoothing = bulbMethod }), bulbMethod);

// Offsets: a rise lifts the bulb off the keel; a forward offset projects further; an
// aft offset tucks the whole bulb into the forefoot so the stem itself reaches the end
// plane while the keel rows near it grow wider than the plain hull's.
var risenCells = generator.Generate(bulbParameters with { Bulb = new BulbSettings(10, 40, 0, 25) }).Blocks.SelectMany(block => block.OccupiedCells).ToHashSet();
var risenStemZ = risenCells.Where(cell => cell.Y == bulbHull.MaxY).Max(cell => cell.Z);
Assert(risenCells.Where(cell => cell.Z > risenStemZ).Min(cell => cell.Y) >= bulbHull.MinY + 2,
    "A 25% rise did not lift the bulb off the keel.");
var forwardCells = generator.Generate(bulbParameters with { Bulb = new BulbSettings(10, 40, 5, 0) }).Blocks.SelectMany(block => block.OccupiedCells).ToHashSet();
Assert(bulbHull.MaxZ - forwardCells.Where(cell => cell.Y == bulbHull.MaxY).Max(cell => cell.Z) > bulbStations,
    "A forward offset did not project the bulb further past the stem.");
var tuckedParameters = bulbParameters with { Bulb = new BulbSettings(10, 40, -10, 0) };
var tuckedCells = generator.Generate(tuckedParameters).Blocks.SelectMany(block => block.OccupiedCells).ToHashSet();
var plainCells = generator.Generate(tuckedParameters with { HasBulb = false }).Blocks.SelectMany(block => block.OccupiedCells).ToHashSet();
Assert(tuckedCells.Any(cell => cell.Z == bulbHull.MaxZ && cell.Y == bulbHull.MaxY), "A tucked bulb still shortened the stem.");
// Widening the forefoot moves the shell outward, so the plain shell is not a subset of
// the tucked one; what holds is that every new cell sits low and forward.
Assert(tuckedCells.Count > plainCells.Count &&
       tuckedCells.Except(plainCells).All(cell => cell.Y <= bulbHull.MinY + 8 && cell.Z > 0),
    "A tucked bulb must only add cells low in the forefoot.");
// A -10% offset on an 80 m hull centres the bulb 8 m behind the stem; the keel row is
// all exterior, so its armor count is the row's full width.
Assert(tuckedCells.Count(cell => cell.Y == bulbHull.MinY && cell.Z == bulbHull.MaxZ - 8) >
       plainCells.Count(cell => cell.Y == bulbHull.MinY && cell.Z == bulbHull.MaxZ - 8),
    "A tucked bulb did not widen the keel row near the stem.");
foreach (var styledBow in Enum.GetValues<BowStyle>())
{
    var styledBulb = generator.Generate(bulbParameters with { BowStyle = styledBow, SternStyle = SternStyle.Canoe });
    Assert(HullGeometryValidator.Validate(styledBulb).Count == 0, $"A {styledBow} bow with a bulb failed validation.");
    Assert(styledBulb.Blocks.SelectMany(block => block.OccupiedCells).Any(cell => cell.Z == styledBulb.MaxZ && cell.Y < styledBulb.MaxY - 2),
        $"A {styledBow} bow lost its bulb.");
}

// A raised forefoot may leave open space above a bulb. This fixed regression case
// intentionally keeps the original raised Orca bulb values, so preset tuning does
// not rewrite the geometry invariant being tested: the bulb remains connected by
// fairing aft into the hull, while the open gap above it proves that no vertical
// hanger was inserted beneath the bow.
var orcaPreset = HullShapePreset.FindByName("Orca")!;
Assert(orcaPreset.EffectiveShape.Bow == new BowShapeSettings(0.50, 0.30, 25) &&
       orcaPreset.EffectiveBulb == new BulbSettings(16, 40, -4, 0),
    "Orca preset bow and bulb defaults changed unexpectedly.");
var orcaParameters = orcaPreset.Apply(
    HullParameters.Default with { Length = 96, Width = 25, Height = 15, Beamify = false })
    with
    {
        Shape = orcaPreset.EffectiveShape with
        {
            Bow = new BowShapeSettings(0.55, 0.25, 34),
        },
        Bulb = new BulbSettings(10, 40, -2, 6),
    };
var orcaHull = generator.Generate(orcaParameters);
var orcaCells = orcaHull.Blocks.SelectMany(block => block.OccupiedCells).ToHashSet();
var orcaCentreX = (orcaHull.MinX + orcaHull.MaxX) / 2;
var orcaRadius = orcaParameters.Width * 0.5 * orcaParameters.EffectiveBulb.WidthPercent / 100.0;
var orcaAttachmentY = (int)Math.Round(
    (orcaParameters.Height - 1) * orcaParameters.EffectiveBulb.RisePercent / 100.0 + orcaRadius - 0.5,
    MidpointRounding.AwayFromZero);
var orcaHasOpenGapAboveBulb = Enumerable.Range(orcaHull.MinZ, orcaHull.OccupiedLength)
    .Where(z => z > orcaHull.MaxZ - orcaParameters.Length / 3)
    .Any(z =>
        orcaCells.Contains((orcaCentreX, orcaAttachmentY, z)) &&
        !orcaCells.Contains((orcaCentreX, orcaAttachmentY + 1, z)) &&
        Enumerable.Range(orcaAttachmentY + 2, orcaHull.MaxY - orcaAttachmentY - 1)
            .Any(y => orcaCells.Contains((orcaCentreX, y, z))));
Assert(HullGeometryValidator.Validate(orcaHull).Count == 0 && orcaHasOpenGapAboveBulb,
    "Orca's bulb must connect aft into the hull without a vertical hanger to the forefoot.");

var narwhal = HullShapePreset.FindByName("Narwhal")!;
Assert(narwhal.HasBulb && HullShapePreset.Match(narwhal.Apply(HullParameters.Default))?.Name == "Narwhal", "Narwhal did not match itself.");
Assert(HullShapePreset.Match(narwhal.Apply(HullParameters.Default) with { Bulb = new BulbSettings(5, 25, 0, 0) }) is null,
    "Changing the bulb proportions still matched Narwhal.");
Assert(HullShapePreset.Match(narwhal.Apply(HullParameters.Default) with { HasBulb = false }) is null,
    "Switching Narwhal's bulb off still matched Narwhal.");
Assert(HullShapePreset.Match(HullParameters.Default with { Bulb = new BulbSettings(5, 25, 0, 0) })?.Name == "Dolphin",
    "A stowed bulb setting must not affect a preset match while the bulb is off.");
// Too little height for a bulb leaves the plain bow; the setting is simply ignored.
var lowBulb = generator.Generate(bulbParameters with { Height = 3 });
var lowPointed = generator.Generate(bulbParameters with { Height = 3, HasBulb = false });
Assert(lowBulb.Blocks.SelectMany(block => block.OccupiedCells).ToHashSet()
        .SetEquals(lowPointed.Blocks.SelectMany(block => block.OccupiedCells)),
    "A three-metre hull cannot carry a bulb and must match the plain hull.");

SelfTestGroupCatalog.Current = "shape-v2";
RunSection("shape-v2 matrix", () =>
{
    ShapeV2Tests.Run(generator, catalog, full, workerCount);
});
RunSection("shape-v2 experience", () =>
{
    ShapeV2ExperienceTests.Run(generator, catalog, full);
});
SelfTestGroupCatalog.Current = "historical-library";
RunSection("historical library", () =>
{
    HistoricalLibraryTests.Run(generator, catalog, full);
});
SelfTestGroupCatalog.Current = "alternate-naval-sketchbook";
RunSection("alternate naval sketchbook", () =>
{
    AlternateNavalSketchbookTests.Run(generator, catalog, full);
});
SelfTestGroupCatalog.Current = "sketchbook-product-surface";
RunSection("sketchbook product surface", () =>
{
    SketchbookProductSurfaceTests.Run(generator, catalog, full);
});
SelfTestGroupCatalog.Current = "release-candidate";
RunSection("release candidate integration", () =>
{
    ReleaseCandidateIntegrationTests.Run(generator, catalog);
});
if (full)
{
    SelfTestGroupCatalog.Current = "automatic-baseline";
    AutomaticSmoothingBaselineTests.Run();
}
SelfTestGroupCatalog.Current = "editor";
EditorRevisionTests.Run(generator, catalog, full);
SelfTestGroupCatalog.Current = "slope-fills";
DescendingFillTests.Run(generator, catalog, full);
SelfTestGroupCatalog.Current = "envelopes";

var internalColours = Enum.GetValues<MaterialKind>().Select(HullPreviewControl.InternalArmorColor).ToArray();
Assert(internalColours.Distinct().Count() == internalColours.Length,
    "Two materials share an internal armor colour.");

// The preview draws each placement's real envelope, so the envelopes are pinned against
// the shapes measured read-only from the installed Core_Structural meshes. A signed
// volume computed from the emitted polygons also proves every face is wound outward:
// one reversed face would subtract instead of add.
var expectedVolumes = new Dictionary<BlockShape, double>
{
    [BlockShape.Cube] = 1,
    [BlockShape.Beam2] = 2,
    [BlockShape.Beam3] = 3,
    [BlockShape.Beam4] = 4,
    [BlockShape.Pole1] = 16 * 0.25 * Math.Tan(Math.PI / 16),
    [BlockShape.Pole2] = 2 * 16 * 0.25 * Math.Tan(Math.PI / 16),
    [BlockShape.Pole3] = 3 * 16 * 0.25 * Math.Tan(Math.PI / 16),
    [BlockShape.Pole4] = 4 * 16 * 0.25 * Math.Tan(Math.PI / 16),
    [BlockShape.Slope1] = 0.5,
    [BlockShape.Slope2] = 1,
    [BlockShape.Slope3] = 1.5,
    [BlockShape.Slope4] = 2,
    [BlockShape.CornerLeft] = 1 / 6d,
    [BlockShape.CornerLeft2] = 2 / 6d,
    [BlockShape.CornerLeft3] = 3 / 6d,
    [BlockShape.CornerLeft4] = 4 / 6d,
    [BlockShape.CornerRight] = 1 / 6d,
    [BlockShape.CornerRight2] = 2 / 6d,
    [BlockShape.CornerRight3] = 3 / 6d,
    [BlockShape.CornerRight4] = 4 / 6d,
    [BlockShape.Corner] = 1 / 6d,
    [BlockShape.InverseCornerLeft] = 5 / 6d,
    [BlockShape.InverseCornerLeft2] = 10 / 6d,
    [BlockShape.InverseCornerLeft3] = 15 / 6d,
    [BlockShape.InverseCornerLeft4] = 20 / 6d,
    [BlockShape.InverseCornerRight] = 5 / 6d,
    [BlockShape.InverseCornerRight2] = 10 / 6d,
    [BlockShape.InverseCornerRight3] = 15 / 6d,
    [BlockShape.InverseCornerRight4] = 20 / 6d,
    [BlockShape.InverseCorner] = 5 / 6d,
};

// Volumes measured independently from the installed native envelopes. Transition
// boundary meshes contain tiny import seams; the analytic envelopes normalize the
// lattice landmarks and retain the exact rational solid volumes below.
foreach (var (names, volume) in new (string Names, double Volume)[]
{
    ("BeamSlope2 BeamSlopeMirrored2", 1),
    ("BeamSlope3 BeamSlopeMirrored3", 1.5),
    ("BeamSlope4 BeamSlopeMirrored4", 2),
    ("SquareCornerLeft SquareCornerRight", 1 / 3d),
    ("SquareCornerLeft2 SquareCornerRight2 SquareBackedCornerLeft2 SquareBackedCornerRight2", 2 / 3d),
    ("SquareCornerLeft3 SquareCornerRight3 SquareBackedCornerLeft3 SquareBackedCornerRight3", 1),
    ("SquareCornerLeft4 SquareCornerRight4 SquareBackedCornerLeft4 SquareBackedCornerRight4", 4 / 3d),
    ("SlopeTransitionLeft12 SlopeTransitionRight12", 5 / 6d),
    ("SlopeTransitionLeft13 SlopeTransitionRight13", 7 / 6d),
    ("SlopeTransitionLeft14 SlopeTransitionRight14", 9 / 6d),
    ("SlopeTransitionLeft23 SlopeTransitionRight23", 8 / 6d),
    ("SlopeTransitionLeft24 SlopeTransitionRight24", 10 / 6d),
    ("SlopeTransitionLeft34 SlopeTransitionRight34", 11 / 6d),
    ("InverseTransitionLeft12 InverseTransitionRight12", 8 / 6d),
    ("InverseTransitionLeft13 InverseTransitionRight13", 13 / 6d),
    ("InverseTransitionLeft14 InverseTransitionRight14", 18 / 6d),
    ("InverseTransitionLeft23 InverseTransitionRight23", 11 / 6d),
    ("InverseTransitionLeft24 InverseTransitionRight24", 16 / 6d),
    ("InverseTransitionLeft34 InverseTransitionRight34", 14 / 6d),
})
foreach (var name in names.Split(' '))
    expectedVolumes.Add(Enum.Parse<BlockShape>(name), volume);

foreach (var shape in Enum.GetValues<BlockShape>())
{
    var length = new BlockPlacement(shape, MaterialKind.Metal, 0, 0, 0, 0).CellLength;
    var faces = BlockEnvelope.Faces(shape);
    Assert(faces.Count > 0, $"{shape} produced no envelope faces.");
    Assert(Math.Abs(EnvelopeVolume(shape) - expectedVolumes[shape]) < 1e-6,
        $"{shape} envelope volume was {EnvelopeVolume(shape):0.######}, not {expectedVolumes[shape]:0.######}.");

    foreach (var face in faces)
    {
        Assert(face.Points.Count >= 3, $"{shape} emitted a degenerate envelope face.");
        Assert(face.Points.Count == face.TextureCoordinates.Count,
            $"{shape} emitted a face whose texture coordinates do not match its points.");
        foreach (var point in face.Points)
        {
            Assert(point.X >= -0.5 - 1e-9 && point.X <= 0.5 + 1e-9 &&
                   point.Y >= -0.5 - 1e-9 && point.Y <= 0.5 + 1e-9 &&
                   point.Z >= -0.5 - 1e-9 && point.Z <= length - 0.5 + 1e-9,
                $"{shape} emitted a point outside its {length} m footprint.");
        }
        foreach (var texture in face.TextureCoordinates)
        {
            Assert(texture.X >= -1e-9 && texture.X <= 1 + 1e-9 && texture.Y >= -1e-9 && texture.Y <= 1 + 1e-9,
                $"{shape} emitted a texture coordinate outside the material brush.");
        }
        if (face.Boundary is { } boundary)
        {
            Assert(boundary.CellIndex >= 0 && boundary.CellIndex < length,
                $"{shape} named a boundary outside its footprint.");
            Assert(IsUnit(boundary.Direction), $"{shape} named a boundary that is not an axis face.");
        }
        else
        {
            Assert(!face.FillsCellFace, $"{shape} claimed a cut face fills a cell boundary.");
        }
    }
}

// A filled boundary square is what lets a neighbouring part drop the face behind it, so the
// count per shape is stated rather than inferred: full blocks close every cell face, a slope
// closes its floor plus the back of its anchor, a triangle corner closes nothing because each
// of its axis faces is only half a square, and an inverted corner closes the three squares
// away from its cut.
var expectedFilledFaces = new Dictionary<BlockShape, int>
{
    [BlockShape.Cube] = 6,
    [BlockShape.Beam2] = 10,
    [BlockShape.Beam3] = 14,
    [BlockShape.Beam4] = 18,
    [BlockShape.Pole1] = 0,
    [BlockShape.Pole2] = 0,
    [BlockShape.Pole3] = 0,
    [BlockShape.Pole4] = 0,
    [BlockShape.Slope1] = 2,
    [BlockShape.Slope2] = 3,
    [BlockShape.Slope3] = 4,
    [BlockShape.Slope4] = 5,
    [BlockShape.CornerLeft] = 0,
    [BlockShape.CornerLeft2] = 0,
    [BlockShape.CornerLeft3] = 0,
    [BlockShape.CornerLeft4] = 0,
    [BlockShape.CornerRight] = 0,
    [BlockShape.CornerRight2] = 0,
    [BlockShape.CornerRight3] = 0,
    [BlockShape.CornerRight4] = 0,
    [BlockShape.Corner] = 0,
    [BlockShape.InverseCornerLeft] = 3,
    [BlockShape.InverseCornerLeft2] = 5,
    [BlockShape.InverseCornerLeft3] = 7,
    [BlockShape.InverseCornerLeft4] = 9,
    [BlockShape.InverseCornerRight] = 3,
    [BlockShape.InverseCornerRight2] = 5,
    [BlockShape.InverseCornerRight3] = 7,
    [BlockShape.InverseCornerRight4] = 9,
    [BlockShape.InverseCorner] = 3,
};

foreach (var (shape, expected) in expectedFilledFaces)
{
    var filled = BlockEnvelope.Faces(shape).Count(face => face.FillsCellFace);
    Assert(filled == expected, $"{shape} filled {filled} cell faces, not {expected}.");
    var distinct = BlockEnvelope.Faces(shape)
        .Where(face => face.FillsCellFace)
        .Select(face => face.Boundary!.Value)
        .Distinct()
        .Count();
    Assert(distinct == expected, $"{shape} filled the same cell boundary more than once.");
}

// The measured envelopes themselves. Wood down slope 1m has wedge corners at
// (+-0.5,-0.5,-0.5), (+-0.5,-0.5,+0.5) and (+-0.5,+0.5,-0.5); Wood triangle corner left 1m
// is the tetrahedron whose solid vertex is where local -X, -Y and -Z meet after the
// game's OBJ X reflection; the right mesh
// mirrors it in X; and the inverted corners are a cube less the tetrahedron at the
// opposing vertex.
Assert(EnvelopeCorners(BlockShape.Slope1).SetEquals(
    [(0.5, -0.5, -0.5), (-0.5, -0.5, -0.5), (0.5, -0.5, 0.5), (-0.5, -0.5, 0.5), (0.5, 0.5, -0.5), (-0.5, 0.5, -0.5)]),
    "The 1 m wedge envelope did not match the installed down slope mesh.");
// Multi-cell side walls are split at every cell boundary so each piece can be culled
// against the neighbour it abuts, which adds vertices along the split lines. The measured
// corners must therefore be present rather than exclusive, and no vertex may sit outside
// the mesh's own wedge or box.
Assert(EnvelopeCorners(BlockShape.Slope4).IsSupersetOf(
    [(0.5, -0.5, -0.5), (-0.5, -0.5, -0.5), (0.5, -0.5, 3.5), (-0.5, -0.5, 3.5), (0.5, 0.5, -0.5), (-0.5, 0.5, -0.5)]),
    "The 4 m wedge envelope is missing corners of the installed down slope mesh.");
Assert(EnvelopeCorners(BlockShape.Slope4).All(corner => 4 * corner.Y + corner.Z <= 1.5 + 1e-9),
    "The 4 m wedge envelope has a vertex above its descending face.");
Assert(EnvelopeCorners(BlockShape.Beam4).IsSupersetOf(
    [(0.5, -0.5, -0.5), (-0.5, -0.5, -0.5), (0.5, 0.5, -0.5), (-0.5, 0.5, -0.5),
     (0.5, -0.5, 3.5), (-0.5, -0.5, 3.5), (0.5, 0.5, 3.5), (-0.5, 0.5, 3.5)]),
    "The 4 m beam envelope is missing corners of the box spanning its four cells.");
Assert(EnvelopeCorners(BlockShape.CornerLeft).SetEquals(
    [(-0.5, -0.5, -0.5), (-0.5, -0.5, 0.5), (-0.5, 0.5, -0.5), (0.5, -0.5, -0.5)]),
    "The left triangle corner envelope did not match the installed mesh.");
Assert(EnvelopeCorners(BlockShape.CornerRight).SetEquals(
    [(-0.5, -0.5, -0.5), (0.5, -0.5, -0.5), (0.5, -0.5, 0.5), (0.5, 0.5, -0.5)]),
    "The right triangle corner envelope did not mirror the left mesh in X.");
Assert(!EnvelopeCorners(BlockShape.InverseCornerLeft).Contains((0.5, 0.5, 0.5)) &&
    EnvelopeCorners(BlockShape.InverseCornerLeft).Count == 7,
    "The left inverted corner envelope did not cut the opposing vertex.");
Assert(!EnvelopeCorners(BlockShape.InverseCornerRight).Contains((-0.5, 0.5, 0.5)) &&
    EnvelopeCorners(BlockShape.InverseCornerRight).Count == 7,
    "The right inverted corner envelope did not cut the opposing vertex.");

var performanceSummary = full
    ? $" 300m geometry: {largeGenerationMs:N0} ms, slider maximum: {sliderMaximumMs:N0} ms, " +
      $"beamified {beamLargeMs:N0} ms, vertical fill {vfillLargeMs:N0} ms."
    : string.Empty;
Console.WriteLine(
    $"Hull Forge {(full ? "full" : "fast")} self-test passed.{performanceSummary} " +
    $"Default hull {singleBlockHull.BlockCount:N0} blocks -> {beamHull.BlockCount:N0} beamified. " +
    $"Reference hull {vfillBase.BlockCount:N0} blocks -> {vfillHull.BlockCount:N0} with {vfillSlopes.Length:N0} vertical-fill slopes.");
}

if (runExperimental)
{
    SelfTestGroupCatalog.Current = "experimental-construction";
    ExperimentalConstructionTests.Run(context.Generator, context.Catalog);
}
}
catch (Exception exception)
{
    if (exception is AggregateException aggregate)
    {
        foreach (var failure in aggregate.Flatten().InnerExceptions)
            Console.Error.WriteLine($"Self-test failed in '{SelfTestGroupCatalog.Current}': {failure.Message}");
    }
    else
    {
        Console.Error.WriteLine($"Self-test failed in '{SelfTestGroupCatalog.Current}': {exception.GetBaseException().Message}");
    }

    return 1;
}

return 0;

static HullParameters WithShapeFullness(
    HullParameters parameters,
    double bow,
    double stern,
    double body)
{
    var shape = parameters.EffectiveShape;
    return parameters with
    {
        BowFullness = bow,
        SternFullness = stern,
        CrossSectionCurve = body,
        Shape = shape with
        {
            Bow = shape.Bow with { Fullness = bow },
            Body = shape.Body with { Style = BodyStyle.Custom, Fullness = body },
            Stern = shape.Stern with { Fullness = stern },
        },
    };
}

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static Dictionary<(int X, int Y, int Z), MaterialKind> CellMaterials(GeneratedHull hull)
{
    var materials = new Dictionary<(int X, int Y, int Z), MaterialKind>();
    foreach (var block in hull.Blocks)
    foreach (var cell in block.OccupiedCells)
        Assert(materials.TryAdd(cell, block.Material), $"Layered block footprints overlap at {cell}.");
    return materials;
}

static int Dot(AxisDirection left, AxisDirection right) => left.X * right.X + left.Y * right.Y + left.Z * right.Z;

static bool IsUnit(AxisDirection direction) => Dot(direction, direction) == 1;

static double EnvelopeVolume(BlockShape shape)
{
    var volume = 0d;
    foreach (var face in BlockEnvelope.Faces(shape))
    {
        for (var index = 1; index + 1 < face.Points.Count; index++)
        {
            var first = (Vector3D)face.Points[0];
            var second = (Vector3D)face.Points[index];
            var third = (Vector3D)face.Points[index + 1];
            volume += Vector3D.DotProduct(first, Vector3D.CrossProduct(second, third)) / 6;
        }
    }
    return volume;
}

static HashSet<(double X, double Y, double Z)> EnvelopeCorners(BlockShape shape) =>
    BlockEnvelope.Faces(shape)
        .SelectMany(face => face.Points)
        .Select(point => (Math.Round(point.X, 6), Math.Round(point.Y, 6), Math.Round(point.Z, 6)))
        .ToHashSet();
