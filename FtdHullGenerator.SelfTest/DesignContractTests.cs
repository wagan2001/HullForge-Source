using System.Collections.Immutable;
using System.Text.Json;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Components;
using FtdHullGenerator.Domain.Design;
using FtdHullGenerator.Domain.Layout;
using FtdHullGenerator.Domain.Projects;
using FtdHullGenerator.Domain.Superstructures;
using FtdHullGenerator.Geometry.Components;

/// <summary>
/// Independent expectations for the frozen V2 contracts. Every expected number below is written
/// out by hand rather than derived from the production helper it is checking.
/// </summary>
internal static class DesignContractTests
{
    public static void Run()
    {
        VerifyDesignMeasure();
        VerifyCenterlineLattice();
        VerifyDesignSpan();
        VerifyDocumentDefaultsAndLegacyMeaning();
        VerifyLinkedGapArithmetic();
        VerifyCountsAndIdentifiers();
        VerifyArrangementDiagnostics();
        VerifyBarbetteAndSuperstructureDiagnostics();
        VerifyBarbetteRealizedExtent();
        VerifyExtensionsAndStyleProvenance();
        VerifySchemaVersionGate();
        VerifyReviewFixes();

        Console.WriteLine(
            "Design contracts: twice-metre arithmetic, centre parity, linked gaps, document defaults, " +
            "legacy smoothing identity, count limits, arrangement/component diagnostics and schema gating passed.");
    }

    private static void VerifyDesignMeasure()
    {
        Require(DesignMeasure.FromMetres(3).TwiceMetres == 6, "Three metres must be six twice-metre units.");
        Require(DesignMeasure.FromCellAnchor(-4).TwiceMetres == -8, "Cell anchor -4 must map to -8 twice-metres.");
        Require(DesignMeasure.FromMetres(2.5).TwiceMetres == 5, "Two and a half metres must be five twice-metre units.");
        Require(DesignMeasure.FromTwiceMetres(7).Metres == 3.5, "Seven twice-metre units must be 3.5 m.");
        Require(!DesignMeasure.FromTwiceMetres(7).IsWholeMetre, "A half-metre value is not a whole metre.");
        Require(DesignMeasure.FromTwiceMetres(7).FloorMetres == 3, "3.5 m must floor to 3 m.");
        Require(DesignMeasure.FromTwiceMetres(-3).FloorMetres == -2, "-1.5 m must floor to -2 m, not -1 m.");
        Require(DesignMeasure.FromMetres(5).TryHalve()!.Value.TwiceMetres == 5, "5 m must halve exactly to 2.5 m.");
        Require(DesignMeasure.FromTwiceMetres(7).TryHalve() is null, "A half-metre must not halve exactly.");

        var half = DesignMeasure.FromTwiceMetres(1);
        var sum = half + half + half + half;
        Require(sum.TwiceMetres == 4 && sum.Metres == 2, "Four half-metres must sum to exactly two metres.");
        Require((DesignMeasure.FromMetres(6) * 2).Metres == 12, "Scaling must stay exact.");
    }

    private static void VerifyCenterlineLattice()
    {
        // Odd width 21 on columns -10..10: the mirror plane falls on column 0.
        var odd = new CenterlineLattice(-10, 10);
        Require(odd.Width == 21, "Columns -10..10 span 21 cells.");
        Require(odd.MirrorSum == 0, "Columns -10..10 mirror about the sum 0.");
        Require(odd.CenterPlane.Metres == 0, "The odd-width centre plane is at x = 0.");
        Require(odd.HasCenterColumn, "An odd-width hull has a centre column.");
        Require(odd.IsThicknessRepresentable(1) && odd.IsThicknessRepresentable(3) && odd.IsThicknessRepresentable(5),
            "An odd-width hull represents odd centred slabs.");
        Require(!odd.IsThicknessRepresentable(2), "An odd-width hull cannot centre an even slab.");
        Require(odd.FirstColumnFor(3) == -1, "A three-cell centred slab must start at column -1.");
        Require(odd.FirstColumnFor(5) == -2, "A five-cell centred slab must start at column -2.");
        Require(odd.MirrorColumn(-1) == 1 && odd.MirrorColumn(4) == -4, "Columns must mirror about the real plane.");
        Require(odd.SuggestCompatibleThickness(2) == 1, "An even request must be offered the thinner odd neighbour.");

        // Even width 20 on columns -10..9: the mirror plane falls between -1 and 0.
        var even = new CenterlineLattice(-10, 9);
        Require(even.Width == 20, "Columns -10..9 span 20 cells.");
        Require(even.MirrorSum == -1, "Columns -10..9 mirror about the sum -1.");
        Require(even.CenterPlane.Metres == -0.5, "The even-width centre plane is half-integral.");
        Require(!even.HasCenterColumn, "An even-width hull has no centre column.");
        Require(!even.IsThicknessRepresentable(1), "An even-width hull cannot centre a single-cell wall.");
        Require(even.IsThicknessRepresentable(2), "An even-width hull centres an even slab.");
        Require(even.FirstColumnFor(2) == -1, "A two-cell centred slab on this lattice starts at column -1.");
        Require(even.MirrorColumn(-1) == 0 && even.MirrorColumn(0) == -1, "The pair -1/0 is the symmetric pair.");
        Require(even.SuggestCompatibleThickness(1) == 2, "A one-cell request must be offered the two-cell slab.");

        // A two-cell slab is the one that actually straddles the plane: -1.5 m to 0.5 m around -0.5 m.
        var slabCentre = (DesignMeasure.FromCellAnchor(-1) + DesignMeasure.FromCellAnchor(0)) * 1;
        Require(slabCentre.TwiceMetres == -2 && slabCentre.TryHalve()!.Value.Metres == -0.5,
            "The two-cell slab's centre must equal the lattice's centre plane.");
    }

    private static void VerifyDesignSpan()
    {
        var span = new DesignSpan(DesignMeasure.FromMetres(10), DesignMeasure.FromMetres(23));
        Require(span.Length.Metres == 13, "Span [10, 23] is 13 m long.");
        Require(span.Contains(DesignMeasure.FromMetres(23)), "A span includes its end.");
        Require(span.Overlaps(new DesignSpan(DesignMeasure.FromMetres(20), DesignMeasure.FromMetres(30))),
            "Touching-by-overlap spans must be reported.");
        Require(!span.Overlaps(new DesignSpan(DesignMeasure.FromMetres(23), DesignMeasure.FromMetres(30))),
            "Spans that merely abut must not be reported as overlapping.");
        Require(span.Shift(DesignMeasure.FromMetres(5)).Start.Metres == 15, "Shifting must move both ends.");
    }

    private static void VerifyDocumentDefaultsAndLegacyMeaning()
    {
        var document = ShipDocument.CreateNew("Test hull", HullParameters.Default);
        Require(document.SchemaVersion == 1, "The first V2 schema is version 1.");
        Require(document.Validate().HasErrors() == false,
            $"A new default document must validate. {document.Validate().Summary()}");
        Require(document.Internals.RequestedPlaneCount == 0, "New documents must not request internal planes.");
        Require(document.Barbettes.Length == 0, "New documents must not contain barbettes.");
        Require(!document.Superstructure.Enabled, "Modular superstructure must start disabled.");
        Require(document.Smoothing.Refinement == DecorativeRefinementKind.None,
            "Decorative refinement must start disabled.");
        Require(document.Extensions is null, "A new document must not carry extension payloads.");

        foreach (var method in Enum.GetValues<SmoothingMethod>())
        {
            var legacy = HullParameters.Default with { Smoothing = method };
            var adapted = ShipDocument.FromLegacyParameters(legacy, "Legacy");
            Require(adapted.Smoothing.NativeMethod == method,
                $"Adapting legacy parameters changed {method} to {adapted.Smoothing.NativeMethod}.");
            Require(adapted.Hull.Smoothing == method, "The wrapped parameters must keep the original method.");
            Require(adapted.Validate().HasErrors() == false, $"Adapting {method} produced an invalid document.");
        }

        Require((int)SmoothingMethod.CombinedSlopeFill == 4 && (int)SmoothingMethod.HybridSlopeFill == 5,
            "Combined must stay 4 and the offset Hybrid must stay 5.");
        var combined = ShipDocument.FromLegacyParameters(
            HullParameters.Default with { Smoothing = SmoothingMethod.CombinedSlopeFill }, "Combined");
        Require(!combined.Smoothing.HasDecorativeRefinement,
            "Adapting a legacy Combined document must not silently enable the new refinement.");

        var unavailable = document with
        {
            Smoothing = new SmoothingSettings(
                document.Smoothing.NativeMethod,
                document.Smoothing.AlgorithmVersion,
                DecorativeRefinementKind.VerticalAndHorizontalRuns,
                12),
        };
        Require(unavailable.Validate().Any(diagnostic =>
                diagnostic.Code == DesignDiagnosticCodes.DecorativeRefinementUnavailable &&
                diagnostic.IsError),
            "Legacy automatic-refinement intent must remain an error and must not become an explicit selection.");
    }

    private static void VerifyLinkedGapArithmetic()
    {
        // Three rings sharing one named 5 m clear gap, with unequal outer half-extents.
        var named = new NamedArrangementGap("gun-pair-gap", "Gun pair gap",
            ArrangementMeasure.ClearEdgeGap, DesignMeasure.FromMetres(5));
        var nodeA = ArrangementNode.Create("ring-a", ArrangementNodeKind.Barbette, "barbette-a",
            DesignMeasure.FromTwiceMetres(13), new ArrangementHandle("ring-a-root", "Root", DesignMeasure.Zero));
        var nodeB = ArrangementNode.Create("ring-b", ArrangementNodeKind.Barbette, "barbette-b",
            DesignMeasure.FromMetres(8));
        var nodeC = ArrangementNode.Create("ring-c", ArrangementNodeKind.Barbette, "barbette-c",
            DesignMeasure.FromTwiceMetres(13));
        var gaps = new[]
        {
            new ArrangementGap("gap-ab", ArrangementMeasure.ClearEdgeGap, DesignMeasure.Zero, named.Id),
            new ArrangementGap("gap-bc", ArrangementMeasure.ClearEdgeGap, DesignMeasure.Zero, named.Id),
        };
        var arrangement = new Arrangement([nodeA, nodeB, nodeC], [.. gaps], [named],
            DesignMeasure.FromMetres(10), DesignMeasure.FromMetres(10),
            ArrangementAnchorKind.BowDatum, ArrangementResizePolicy.PreserveAbsolute, "gap-ab");
        Require(!arrangement.Validate().HasErrors(), "The linked-gap chain must validate.");

        foreach (var gap in arrangement.Gaps)
            Require(arrangement.ResolveGapValue(gap).Metres == 5,
                "Both pair gaps must resolve through the shared variable to 5 m.");

        // Centre distance for half-extents a, b and a clear edge gap g is a + g + b.
        var a = nodeA.OuterHalfExtent.Metres;
        var b = nodeB.OuterHalfExtent.Metres;
        var c = nodeC.OuterHalfExtent.Metres;
        Require(a == 6.5 && b == 8, "The fixture's realized half-extents must be 6.5 m and 8 m.");
        Require(a + arrangement.ResolveGapValue(gaps[0]).Metres + b == 19.5,
            "Unequal rings must still produce the requested realized clear gap.");
        Require(b + arrangement.ResolveGapValue(gaps[1]).Metres + c == 19.5,
            "Both linked segments must produce the same realized centre distance for equal neighbours.");

        // Changing the named value moves both ends together: 5 m -> 7 m adds 2 m to each centre distance.
        var widened = arrangement with
        {
            NamedGaps = [named with { Value = DesignMeasure.FromMetres(7) }],
        };
        Require(widened.Gaps.All(gap => widened.ResolveGapValue(gap).Metres == 7),
            "Editing the named gap must update every linked segment.");
        Require(a + widened.ResolveGapValue(widened.Gaps[0]).Metres + b == 21.5,
            "The widened chain must move by exactly the named delta.");
    }

    private static void VerifyCountsAndIdentifiers()
    {
        var barbetteNode = ArrangementNode.Create("ring", ArrangementNodeKind.Barbette, "b", DesignMeasure.FromMetres(6));
        var tooMany = Enumerable.Range(0, DesignLimits.MaxBarbettesPerDocument + 1)
            .Select(index => BarbetteDefinition.Create(
                $"barbette-{index}", "ring", DesignMeasure.FromMetres(9), 3))
            .ToArray();
        var crowded = ShipDocument.CreateNew("Crowded", HullParameters.Default) with
        {
            Arrangement = new Arrangement([barbetteNode], [], [], DesignMeasure.FromMetres(10),
                DesignMeasure.FromMetres(10), ArrangementAnchorKind.BowDatum,
                ArrangementResizePolicy.PreserveAbsolute, null),
            Barbettes = [.. tooMany],
        };
        Require(crowded.Validate().Any(diagnostic => diagnostic.Code == DesignDiagnosticCodes.CountLimitExceeded),
            "Exceeding the barbette cap must be reported.");

        var tooManyPlanes = ShipDocument.CreateNew("Planes", HullParameters.Default) with
        {
            Internals = new InternalStructure([
                new InternalStructureFamily(InternalPlaneFamily.LongitudinalBulkhead, true, 1,
                    MaterialKind.Metal, DesignMeasure.FromMetres(4),
                    DesignLimits.MaxInternalPlanesPerDocument + 1, DesignMeasure.Zero, true),
                InternalStructureFamily.Disabled(InternalPlaneFamily.InternalDeck),
                InternalStructureFamily.Disabled(InternalPlaneFamily.TransverseBulkhead),
            ]),
        };
        Require(tooManyPlanes.Validate().Any(diagnostic => diagnostic.Code == DesignDiagnosticCodes.CountLimitExceeded),
            "Exceeding the internal-plane cap must be reported.");

        var duplicateIds = ShipDocument.CreateNew("Duplicates", HullParameters.Default) with
        {
            Arrangement = new Arrangement(
                [
                    ArrangementNode.Create("same", ArrangementNodeKind.SuperstructureSpan, "span", DesignMeasure.FromMetres(5)),
                    ArrangementNode.Create("same", ArrangementNodeKind.SuperstructureSpan, "span", DesignMeasure.FromMetres(5)),
                ],
                [new ArrangementGap("gap", ArrangementMeasure.ClearEdgeGap, DesignMeasure.FromMetres(1))],
                [], DesignMeasure.FromMetres(1), DesignMeasure.FromMetres(1),
                ArrangementAnchorKind.BowDatum, ArrangementResizePolicy.PreserveAbsolute, null),
        };
        Require(duplicateIds.Validate().Any(diagnostic => diagnostic.Code == DesignDiagnosticCodes.IdentifierDuplicate),
            "Duplicate arrangement ids must be reported.");
    }

    private static void VerifyArrangementDiagnostics()
    {
        var nodes = new[]
        {
            ArrangementNode.Create("a", ArrangementNodeKind.Barbette, "ba", DesignMeasure.FromMetres(5)),
            ArrangementNode.Create("b", ArrangementNodeKind.Barbette, "bb", DesignMeasure.FromMetres(5)),
        };
        var missingNamedGap = new Arrangement([.. nodes],
            [new ArrangementGap("gap", ArrangementMeasure.ClearEdgeGap, DesignMeasure.Zero, "not-declared")],
            [], DesignMeasure.FromMetres(1), DesignMeasure.FromMetres(1),
            ArrangementAnchorKind.BowDatum, ArrangementResizePolicy.PreserveAbsolute, null);
        Require(Codes(missingNamedGap.Validate()).Contains(DesignDiagnosticCodes.ArrangementUnknownNamedGap),
            "An undeclared named gap must be reported.");

        var mismatched = new Arrangement([.. nodes],
            [new ArrangementGap("gap", ArrangementMeasure.CenterPitch, DesignMeasure.Zero, "shared")],
            [new NamedArrangementGap("shared", "Shared", ArrangementMeasure.ClearEdgeGap, DesignMeasure.FromMetres(5))],
            DesignMeasure.FromMetres(1), DesignMeasure.FromMetres(1),
            ArrangementAnchorKind.BowDatum, ArrangementResizePolicy.PreserveAbsolute, null);
        Require(Codes(mismatched.Validate()).Contains(DesignDiagnosticCodes.ArrangementMeasureMismatch),
            "Sharing a variable of a different measurement kind must be reported.");

        var negative = new Arrangement([.. nodes],
            [new ArrangementGap("gap", ArrangementMeasure.ClearEdgeGap, DesignMeasure.FromMetres(-1))],
            [], DesignMeasure.FromMetres(1), DesignMeasure.FromMetres(1),
            ArrangementAnchorKind.BowDatum, ArrangementResizePolicy.PreserveAbsolute, null);
        Require(Codes(negative.Validate()).Contains(DesignDiagnosticCodes.ArrangementNegativeGap),
            "A negative gap must be reported.");

        var wrongGapCount = new Arrangement([.. nodes], [], [], DesignMeasure.FromMetres(1), DesignMeasure.FromMetres(1),
            ArrangementAnchorKind.BowDatum, ArrangementResizePolicy.PreserveAbsolute, null);
        Require(Codes(wrongGapCount.Validate()).Contains(DesignDiagnosticCodes.ArrangementGapCountMismatch),
            "Two nodes must declare exactly one gap.");

        var escapingChild = new Arrangement(
            [ArrangementNode.Create("span", ArrangementNodeKind.SuperstructureSpan, "s", DesignMeasure.FromMetres(5),
                new ArrangementHandle("tower", "Tower", DesignMeasure.FromMetres(7)))],
            [], [], DesignMeasure.FromMetres(1), DesignMeasure.FromMetres(1),
            ArrangementAnchorKind.BowDatum, ArrangementResizePolicy.PreserveAbsolute, null);
        Require(Codes(escapingChild.Validate()).Contains(DesignDiagnosticCodes.ArrangementChildOutsideParent),
            "A child handle outside its span must be reported.");
    }

    private static void VerifyBarbetteAndSuperstructureDiagnostics()
    {
        var node = ArrangementNode.Create("ring", ArrangementNodeKind.Barbette, "b", DesignMeasure.FromMetres(6));
        var arrangement = new Arrangement([node], [], [], DesignMeasure.Zero, DesignMeasure.Zero,
            ArrangementAnchorKind.BowDatum, ArrangementResizePolicy.PreserveAbsolute, null);
        var document = ShipDocument.CreateNew("Barbette", HullParameters.Default) with
        {
            Arrangement = arrangement,
            Barbettes = [BarbetteDefinition.Create("barbette", "ring", DesignMeasure.FromMetres(9), 3)],
        };
        Require(!document.Validate().HasErrors(),
            "A frozen clear-volume barbette with default armor must validate.");

        var zeroDiameter = document with
        {
            Barbettes = [BarbetteDefinition.Create("barbette", "ring", DesignMeasure.Zero, 3)],
        };
        Require(Codes(zeroDiameter.Validate()).Contains(DesignDiagnosticCodes.BarbetteClearDiameterInvalid),
            "A non-positive clear diameter must be reported with BAR009.");

        var zeroDepth = document with
        {
            Barbettes = [BarbetteDefinition.Create("barbette", "ring", DesignMeasure.FromMetres(9), 0)],
        };
        Require(Codes(zeroDepth.Validate()).Contains(DesignDiagnosticCodes.BarbetteClearDepthInvalid),
            "A non-positive clear depth must be reported with BAR010.");

        var negativeOffset = document with
        {
            Barbettes = [BarbetteDefinition.Create("barbette", "ring", DesignMeasure.FromMetres(9), 3,
                topOffsetMetres: -1)],
        };
        Require(Codes(negativeOffset.Validate()).Contains(DesignDiagnosticCodes.BarbetteTopOffsetInvalid),
            "A negative top offset must be reported with BAR011.");

        var badNeck = document with
        {
            Barbettes = [BarbetteDefinition.Create("barbette", "ring", DesignMeasure.FromMetres(9), 3,
                neckClearSizeMetres: 2)],
        };
        Require(Codes(badNeck.Validate()).Contains(DesignDiagnosticCodes.BarbetteNeckSizeUnsupported),
            "A neck that is not 1 m, 3 m or 5 m must be reported with BAR012.");

        var airBoundary = document with
        {
            Barbettes =
            [
                BarbetteDefinition.Create("barbette", "ring", DesignMeasure.FromMetres(9), 3,
                    sideArmor: new ArmorLayout([ArmorLayer.Air, new ArmorLayer(MaterialKind.Metal)])),
            ],
        };
        Require(Codes(airBoundary.Validate()).Contains(DesignDiagnosticCodes.BarbetteClearBoundaryNotArmor),
            "An air gap as the innermost layer against the clear volume must be reported with BAR014.");

        var unknownNode = document with
        {
            Barbettes = [BarbetteDefinition.Create("barbette", "missing", DesignMeasure.FromMetres(9), 3)],
        };
        Require(Codes(unknownNode.Validate()).Contains(DesignDiagnosticCodes.BarbetteUnknownNode),
            "A barbette attached to a missing arrangement node must be reported.");

        var evenHull = document with
        {
            Hull = document.Hull with { Width = 20 },
        };
        var evenDiagnostics = evenHull.Validate();
        Require(Codes(evenDiagnostics).Contains(DesignDiagnosticCodes.BarbetteOddHullWidthRequired) &&
                evenDiagnostics.Any(diagnostic =>
                    diagnostic.Message == "Centerline barbettes require an odd hull width.") &&
                evenHull.Barbettes.Length == 1,
            "An even-width document must remain intact but invalid while it contains a centerline barbette.");

        var towerOnBarbette = ShipDocument.CreateNew("Tower", HullParameters.Default) with
        {
            Arrangement = new Arrangement([node], [], [], DesignMeasure.Zero, DesignMeasure.Zero,
                ArrangementAnchorKind.BowDatum, ArrangementResizePolicy.PreserveAbsolute, null),
            Superstructure = new SuperstructureLayout(true, [], [
                new SuperstructureTowerRoot("tower", "ring", DesignMeasure.Zero),
            ]),
        };
        Require(Codes(towerOnBarbette.Validate()).Contains(DesignDiagnosticCodes.SuperstructureRootUnknownSpan),
            "A tower root may only attach to a superstructure span.");

        var tinyModule = ShipDocument.CreateNew("Module", HullParameters.Default) with
        {
            Superstructure = new SuperstructureLayout(true, [
                new SuperstructureLayer("layer-1", 1, [
                    new SuperstructureBoxModule("box", DesignMeasure.Zero, DesignMeasure.Zero,
                        DesignMeasure.Zero, DesignMeasure.FromMetres(4), 3),
                ]),
            ], []),
        };
        Require(Codes(tinyModule.Validate()).Contains(DesignDiagnosticCodes.SuperstructureModuleTooSmall),
            "A zero-length module must be reported.");
    }

    private static void VerifyBarbetteRealizedExtent()
    {
        // The clear volume is authoritative and the frozen product boundary accepts only whole odd
        // clear diameters, so a 7 m request realizes exactly 7 m and the exterior adds the armor.
        var barbette = BarbetteDefinition.Create("barbette", "ring", DesignMeasure.FromMetres(7), 2,
            sideArmor: new ArmorLayout([MaterialKind.Metal]));
        var measured = BarbetteGenerator.Measure(barbette, DesignMeasure.Zero, DesignMeasure.Zero);
        Require(measured.IsValid, string.Join(" | ", measured.Diagnostics));
        Require(measured.Measurement!.RealizedClearWidth.Metres == 7,
            "A 7 m clear diameter must realize exactly 7 m of clear width.");
        Require(measured.Measurement.RealizedExteriorWidth.Metres == 9,
            "The derived exterior footprint must add the side armor to the clear footprint.");

        var thicker = barbette with
        {
            SideArmor = new ArmorLayout([MaterialKind.Metal, MaterialKind.HeavyArmor]),
        };
        var thickerMeasured = BarbetteGenerator.Measure(thicker, DesignMeasure.Zero, DesignMeasure.Zero);
        Require(thickerMeasured.Measurement!.RealizedExteriorWidth.Metres == 11 &&
                thickerMeasured.Measurement.RealizedClearWidth == measured.Measurement.RealizedClearWidth,
            "Adding side armor must grow only the derived exterior extent, never the clear volume.");
    }

    private static void VerifyExtensionsAndStyleProvenance()
    {
        var oversized = ShipDocument.CreateNew("Extensions", HullParameters.Default) with
        {
            Extensions = new DocumentExtensions(ImmutableDictionary<string, string>.Empty
                .Add("future.payload", new string('x', DesignLimits.MaxDocumentExtensionBytes + 1))),
        };
        Require(Codes(oversized.Validate()).Contains(DesignDiagnosticCodes.ExtensionBudgetExceeded),
            "An oversized extension payload must be reported.");

        var badKey = ShipDocument.CreateNew("Keys", HullParameters.Default) with
        {
            Extensions = new DocumentExtensions(ImmutableDictionary<string, string>.Empty
                .Add("../escape", "value")),
        };
        Require(Codes(badKey.Validate()).Contains(DesignDiagnosticCodes.IdentifierMissing),
            "An extension key that is not a plain name must be reported.");

        var emptyProvenance = ShipDocument.CreateNew("Style", HullParameters.Default) with
        {
            AppliedStyle = new AppliedStyleProvenance("style", 1, CopiedStyleFields.Nothing),
        };
        Require(Codes(emptyProvenance.Validate()).Contains(DesignDiagnosticCodes.StyleProvenanceInvalid),
            "An applied style that copied nothing must be reported.");

        var style = new FactionStyleEntry(
            "style", 1, "Faction", null, CopiedStyleFields.Everything,
            Shape: HullShapeSettings.Default,
            HullArmor: ArmorLayout.Single(MaterialKind.Metal),
            Internals: InternalStructure.Disabled,
            NamedGaps: [],
            Superstructure: SuperstructureLayout.Disabled,
            Smoothing: SmoothingSettings.FromParameters(HullParameters.Default));
        Require(!style.Validate().HasErrors(), "A complete style entry must validate.");
        Require(style.Fields.Dimensions == false, "Dimensions must be excluded from a style by default.");
        Require(CopiedStyleFields.Nothing.CopiesAnything == false, "An empty mask must copy nothing.");
    }

    private static void VerifySchemaVersionGate()
    {
        Require(ShipDocument.IsSupportedSchemaVersion(1), "Schema 1 is supported.");
        Require(!ShipDocument.IsSupportedSchemaVersion(0), "Schema 0 is not a valid document.");
        Require(!ShipDocument.IsSupportedSchemaVersion(ShipDocument.CurrentSchemaVersion + 1),
            "A future schema must not be treated as current.");

        var future = ShipDocument.CreateNew("Future", HullParameters.Default) with { SchemaVersion = 99 };
        Require(future.RequiresReadOnlyFallback, "A future document must declare the read-only fallback.");
        Require(Codes(future.Validate()).Contains(DesignDiagnosticCodes.SchemaVersionUnsupported),
            "A future schema must produce an explicit error.");
    }

    /// <summary>
    /// Regressions for the independent review of F01. Each expectation is written out by hand and
    /// targets a defect the review found, so a regression fails for the right reason.
    /// </summary>
    private static void VerifyReviewFixes()
    {
        // A non-positional record struct silently deserializes to its default without a
        // [JsonConstructor]; that would zero every design coordinate in a saved project.
        var measure = DesignMeasure.FromTwiceMetres(7);
        var json = JsonSerializer.Serialize(measure);
        var roundTripped = JsonSerializer.Deserialize<DesignMeasure>(json);
        Require(roundTripped.TwiceMetres == 7,
            $"A design measure must survive a JSON round trip, but came back as {roundTripped.TwiceMetres}.");
        Require(roundTripped == measure, "The round-tripped measure must equal the original.");
        Require(json == """{"TwiceMetres":7}""",
            $"Derived properties must not be serialized, but the payload was {json}.");

        var datumJson = JsonSerializer.Serialize(new LayoutDatum(DesignMeasure.FromMetres(159.5),
            DesignMeasure.FromMetres(10)));
        var datum = JsonSerializer.Deserialize<LayoutDatum>(datumJson)!;
        Require(datum.LayoutBowZ.Metres == 159.5 && datum.CenterPlaneX.Metres == 10,
            "A layout datum must survive a JSON round trip with its half-metre bow datum intact.");

        // Records are only shallow-immutable unless the collections are immutable too.
        var barbette = BarbetteDefinition.Create("barbette", "ring", DesignMeasure.FromMetres(9), 3);
        var callerList = new List<BarbetteDefinition> { barbette };
        var node = ArrangementNode.Create("ring", ArrangementNodeKind.Barbette, "barbette", DesignMeasure.FromMetres(6.5));
        var immutable = ShipDocument.CreateNew("Immutable", HullParameters.Default) with
        {
            Arrangement = new Arrangement([node], [], [], DesignMeasure.Zero, DesignMeasure.Zero,
                ArrangementAnchorKind.BowDatum, ArrangementResizePolicy.PreserveAbsolute, null),
            Barbettes = [.. callerList],
        };
        callerList.Add(barbette with { Id = "barbette-2" });
        Require(immutable.Barbettes.Length == 1,
            "Mutating the caller's list must not change the document.");

        // The wrapped parameters and the smoothing section are two views of one decision.
        var divergent = ShipDocument.CreateNew("Divergent", HullParameters.Default) with
        {
            Smoothing = new SmoothingSettings(SmoothingMethod.HorizontalSlopeFill),
        };
        Require(Codes(divergent.Validate()).Contains(DesignDiagnosticCodes.SmoothingIntentDiverges),
            "A document whose smoothing section disagrees with its parameters must be reported.");
        Require(!ShipDocument.CreateNew("Agree", HullParameters.Default with
            {
                Smoothing = SmoothingMethod.HybridSlopeFill,
            }).Validate().HasErrors(),
            "An agreeing smoothing intent must still validate.");

        // Count caps must accept exactly the cap and reject the next value.
        var ring = ArrangementNode.Create("ring", ArrangementNodeKind.Barbette, "b", DesignMeasure.FromMetres(6.5));
        var capArrangement = new Arrangement([ring], [], [], DesignMeasure.Zero, DesignMeasure.Zero,
            ArrangementAnchorKind.BowDatum, ArrangementResizePolicy.PreserveAbsolute, null);
        static BarbetteDefinition AtCap(int index) => BarbetteDefinition.Create(
            $"barbette-{index}", "ring", DesignMeasure.FromMetres(9), 3);
        var atCap = ShipDocument.CreateNew("Cap", HullParameters.Default) with
        {
            Arrangement = capArrangement,
            Barbettes = [.. Enumerable.Range(0, DesignLimits.MaxBarbettesPerDocument).Select(AtCap)],
        };
        Require(!Codes(atCap.Validate()).Contains(DesignDiagnosticCodes.CountLimitExceeded),
            $"{DesignLimits.MaxBarbettesPerDocument} barbettes is exactly the cap and must be allowed.");
        var overCap = atCap with
        {
            Barbettes = [.. Enumerable.Range(0, DesignLimits.MaxBarbettesPerDocument + 1).Select(AtCap)],
        };
        Require(Codes(overCap.Validate()).Contains(DesignDiagnosticCodes.CountLimitExceeded),
            "One barbette past the cap must be rejected.");
        Require(DesignLimits.MaxBarbettesPerDocument == 32 &&
                DesignLimits.MaxInternalPlanesPerDocument == 256 &&
                DesignLimits.MaxSuperstructureBoxesPerDocument == 128,
            "The first-pass caps are part of the contract and must not drift silently.");

        // A handle exactly on the span boundary is inside it, not outside.
        var boundary = ArrangementNode.Create("span", ArrangementNodeKind.SuperstructureSpan, "s",
            DesignMeasure.FromMetres(5),
            new ArrangementHandle("edge", "Edge", DesignMeasure.FromMetres(5)));
        var onBoundary = ShipDocument.CreateNew("Boundary", HullParameters.Default) with
        {
            Arrangement = new Arrangement([boundary], [], [], DesignMeasure.Zero, DesignMeasure.Zero,
                ArrangementAnchorKind.BowDatum, ArrangementResizePolicy.PreserveAbsolute, null),
        };
        Require(!Codes(onBoundary.Validate()).Contains(DesignDiagnosticCodes.ArrangementChildOutsideParent),
            "A handle exactly on the span boundary must be accepted.");
        Require(!Codes(onBoundary.Validate()).Contains(DesignDiagnosticCodes.MeasureOutOfRange),
            "A handle on the boundary is inside the design range.");

        // The datum is persisted, validated, and defaulted for documents that predate it.
        Require(ShipDocument.CreateNew("Datum", HullParameters.Default).EffectiveDatum == LayoutDatum.Origin,
            "A new document must carry the origin datum by default.");
        var farDatum = ShipDocument.CreateNew("Far", HullParameters.Default) with
        {
            Datum = new LayoutDatum(DesignMeasure.FromTwiceMetres(DesignLimits.MaxDesignTwiceMetres + 2),
                DesignMeasure.Zero),
        };
        Require(Codes(farDatum.Validate()).Contains(DesignDiagnosticCodes.MeasureOutOfRange),
            "A bow datum outside the design range must be reported.");

        // C10 wants the requested/realized values and a suggested correction where they exist.
        var detail = ShipDocument.CreateNew("Detail", HullParameters.Default) with
        {
            Arrangement = new Arrangement([ring, ring with { Id = "ring-2" }], [], [],
                DesignMeasure.Zero, DesignMeasure.Zero, ArrangementAnchorKind.BowDatum,
                ArrangementResizePolicy.PreserveAbsolute, null),
        };
        var detailDiagnostic = detail.Validate()
            .Single(diagnostic => diagnostic.Code == DesignDiagnosticCodes.ArrangementGapCountMismatch);
        Require(detailDiagnostic.Field is not null && detailDiagnostic.SuggestedCorrection is not null,
            "A structural diagnostic must name its field and suggest a correction.");
        Require(detailDiagnostic.Requested is null && detailDiagnostic.Realized is null,
            "A count is not a length; it must not be encoded as a design measure in metres.");
        var lengthDiagnostic = farDatum.Validate()
            .Single(item => item.Code == DesignDiagnosticCodes.MeasureOutOfRange);
        Require(lengthDiagnostic.Requested is not null && lengthDiagnostic.Realized is not null,
            "A length diagnostic must carry the requested and realized measurements.");

        // The most negative coordinate must report, not throw, and an unsupported enumeration value
        // must have its own code rather than borrowing the unknown-node code.
        var extreme = ShipDocument.CreateNew("Extreme", HullParameters.Default) with
        {
            Arrangement = new Arrangement(
                [ArrangementNode.Create("ring", ArrangementNodeKind.Barbette, "b",
                    DesignMeasure.FromTwiceMetres(int.MinValue))],
                [], [], DesignMeasure.Zero, DesignMeasure.Zero,
                (ArrangementAnchorKind)7, ArrangementResizePolicy.PreserveAbsolute, null),
        };
        var extremeCodes = Codes(extreme.Validate());
        Require(extremeCodes.Contains(DesignDiagnosticCodes.MeasureOutOfRange),
            "An unrepresentable magnitude must be reported as out of range, not thrown.");
        Require(extremeCodes.Contains(DesignDiagnosticCodes.ArrangementUnsupportedEnumeration),
            "An unsupported anchor value must be reported with its own code.");
    }

    private static HashSet<string> Codes(IEnumerable<DesignDiagnostic> diagnostics) =>
        diagnostics.Select(diagnostic => diagnostic.Code).ToHashSet(StringComparer.Ordinal);

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
