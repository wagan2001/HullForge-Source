using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Components;
using FtdHullGenerator.Domain.Composition;
using FtdHullGenerator.Domain.Design;
using FtdHullGenerator.Geometry;
using FtdHullGenerator.Geometry.Components;

/// <summary>
/// Independent BAR01 oracles for the frozen clear-volume barbette. The small expected masks are
/// literal sets, not values derived by the production rasterizer; hull cases inspect only the public
/// F02 context. Vertical expectations are hand-computed from the reference deck plane.
/// </summary>
internal static class BarbetteGeneratorTests
{
    public static void Run()
    {
        VerifyClearMaskAndArmorInvariance();
        VerifyNeckSizesAndConcentricSquares();
        VerifyRoofCoversCavityExceptNeckShaft();
        VerifyFloatingVerticalPlacement();
        VerifyIndependentArmorStacks();
        VerifyBottomFloorDirectionality();
        VerifyUsefulDiametersAndDepths();
        VerifyStableRepeatedGeneration();
        VerifyEvenWidthCenterlineDiagnostics();
        VerifyBudgetsAndCancellation();
        VerifyHullContextBridge();

        Console.WriteLine(
            "Barbette core: clear-volume masks, clear diameter/depth invariance, floating vertical " +
            "placement, independent side/roof/bottom/neck stacks, flat protected floor directionality, " +
            "1/3/5 m concentric square necks, even-width centerline diagnostics, budgets, cancellation " +
            "and hull-context neck openings passed.");
    }

    private static void VerifyClearMaskAndArmorInvariance()
    {
        var definition = Definition("clear", clear: 3, depth: 3, side: 1);
        var measured = BarbetteGenerator.Measure(definition, DesignMeasure.Zero, DesignMeasure.Zero);
        Require(measured.IsValid, Details(measured.Diagnostics));
        var measurement = measured.Measurement!;

        var expectedClear = Rect(-1, 1, -1, 1);
        var expectedSide = Rect(-2, 2, -2, 2);
        expectedSide.ExceptWith(expectedClear);
        Require(Set(measurement.ClearCells).SetEquals(expectedClear),
            "The block-centered 3 m clear disk must clear the literal 3x3 fixture.");
        Require(Set(measurement.SideArmorCells).SetEquals(expectedSide),
            "One metre of side armor around the 3x3 clear disk must be the literal 5x5-minus-3x3 ring.");
        Require(measurement.RealizedClearWidth.Metres == 3 && measurement.RealizedClearLength.Metres == 3,
            "The realized clear bounds must use the measured voxel faces.");
        Require(measurement.RealizedExteriorWidth.Metres == 5 && measurement.RealizedExteriorLength.Metres == 5,
            "The derived exterior footprint must include the side armor, not an entered outside diameter.");
        Require(measurement.ClearLongitudinalHalfExtent.Metres == 1.5 &&
                measurement.LongitudinalHalfExtent.Metres == 2.5,
            "The clear and exterior longitudinal half-extents must both be measured for the ruler.");
        Require(IsFaceConnected(Set(measurement.SideArmorCells)),
            "The side armor plan must form one face-connected ring.");
        Require(IsSymmetric(Set(measurement.ClearCells), 0, 0) &&
                IsSymmetric(Set(measurement.SideArmorCells), 0, 0),
            "The clear and side masks must retain four-axis centerline symmetry.");

        var thickSide = BarbetteGenerator.Measure(definition, DesignMeasure.Zero, DesignMeasure.Zero);
        Require(thickSide.IsValid, Details(thickSide.Diagnostics));
        Require(thickSide.Measurement!.RealizedClearWidth == measurement.RealizedClearWidth &&
                thickSide.Measurement.RealizedClearLength == measurement.RealizedClearLength,
            "Re-measuring must not change the clear diameter.");

        var twoLayerSide = definition with
        {
            SideArmor = new ArmorLayout([MaterialKind.Metal, MaterialKind.HeavyArmor]),
        };
        var thicker = BarbetteGenerator.Measure(twoLayerSide, DesignMeasure.Zero, DesignMeasure.Zero);
        Require(thicker.IsValid, Details(thicker.Diagnostics));
        Require(thicker.Measurement!.RealizedExteriorWidth.Metres == 7,
            "Two side layers must grow the derived exterior footprint to 7 m.");
        Require(thicker.Measurement.RealizedClearWidth == measurement.RealizedClearWidth,
            "A thicker side stack must leave the clear diameter invariant.");

        // The frozen product boundary accepts only whole odd clear diameters, so a fractional
        // request is a domain ClearDiameter error (BAR009), not a conservatively rasterized bore.
        var fractional = BarbetteGenerator.Measure(
            Definition("fractional", clear: 2.5, depth: 2, side: 1),
            DesignMeasure.Zero, DesignMeasure.Zero);
        Require(!fractional.IsValid && fractional.Measurement is null &&
                Has(fractional.Diagnostics, DesignDiagnosticCodes.BarbetteClearDiameterInvalid),
            "A fractional clear diameter must fail closed with BAR009, not be rounded.");
    }

    private static void VerifyNeckSizesAndConcentricSquares()
    {
        foreach (var (size, expectedClear, expectedExterior) in new[]
                 {
                     (1, 1, 9),
                     (3, 9, 25),
                     (5, 25, 49),
                 })
        {
            var definition = Definition($"neck-{size}", clear: 7, depth: 2, side: 1, neckSize: size);
            var measured = BarbetteGenerator.Measure(definition, DesignMeasure.Zero, DesignMeasure.Zero);
            Require(measured.IsValid, Details(measured.Diagnostics));
            var measurement = measured.Measurement!;
            Require(measurement.NeckClearCells.Count == expectedClear,
                $"A {size} m neck must expose exactly {expectedClear} clear shaft cells.");
            Require(measurement.NeckArmorCells.Count == expectedExterior - expectedClear,
                $"A {size} m neck with one armor layer must expose {expectedExterior - expectedClear} armor cells.");
            Require(measurement.RealizedNeckClearWidth.Metres == size &&
                    measurement.RealizedNeckClearLength.Metres == size,
                $"The {size} m neck clear bounds must be exactly {size} m square.");
            Require(measurement.RealizedNeckExteriorWidth.Metres == size + 2 &&
                    measurement.RealizedNeckExteriorLength.Metres == size + 2,
                $"The {size} m neck armor must be exactly square externally.");
            Require(IsSymmetric(Set(measurement.NeckClearCells), 0, 0) &&
                    IsSymmetric(Set(measurement.NeckArmorCells), 0, 0),
                $"The {size} m neck must be concentric with the main cavity.");
        }

        var unsupported = Definition("bad-neck", clear: 3, depth: 2, side: 1, neckSize: 2);
        Require(Has(unsupported.Validate(), DesignDiagnosticCodes.BarbetteNeckSizeUnsupported),
            "A 2 m neck must be rejected: 2.0 supports exactly 1 m, 3 m or 5 m.");

        // A neck wider than the clear cavity must still pass cleanly through every roof layer.
        var wideNeck = Definition("wide-neck", clear: 3, depth: 2, side: 1, roof: 1, bottom: 1,
            neck: 1, neckSize: 5);
        var wide = BarbetteGenerator.Generate(wideNeck, DesignMeasure.Zero, DesignMeasure.Zero, 11, 11);
        Require(wide.IsValid, Details(wide.Diagnostics));
        var neckSet = Set(wide.Measurement!.NeckClearCells);
        Require(!wide.Solids.Any(intent => intent.Role == BarbetteArmorRole.Roof &&
                neckSet.Contains(new BarbettePlanCell(intent.Cell.X, intent.Cell.Z))),
            "A 5 m neck shaft must pass through every roof layer even when it is wider than the clear cavity.");
        Require(wide.RequiredVoids.Any(intent => intent.Cell.Y == wide.Layout!.RoofBottomY &&
                neckSet.Contains(new BarbettePlanCell(intent.Cell.X, intent.Cell.Z))),
            "The roof-band neck shaft must be reserved as protected void.");    }

    /// <summary>
    /// The realized roof must cover the complete horizontal exterior footprint minus only the square
    /// neck-clear shaft. The circular protected clear cavity is below the roof and must never punch a
    /// roof opening. This case uses a neck narrower than the clear cavity so the old clear-cell skip
    /// produced a literal hole.
    /// </summary>
    private static void VerifyRoofCoversCavityExceptNeckShaft()
    {
        const int referenceDeckY = 15;
        var definition = Definition("roof-cover", clear: 5, depth: 2, side: 1, roof: 2, bottom: 1,
            neck: 1, neckSize: 1);
        var result = BarbetteGenerator.Generate(definition, DesignMeasure.Zero, DesignMeasure.Zero,
            referenceDeckY, referenceDeckY);
        Require(result.IsValid, Details(result.Diagnostics));

        var measurement = result.Measurement!;
        var layout = result.Layout!;
        var clear = Set(measurement.ClearCells);
        var neckClear = Set(measurement.NeckClearCells);
        var exterior = Set(measurement.ClearCells.Concat(measurement.SideArmorCells));
        Require(measurement.SideArmorCells.Count > 0,
            "The fixture must expose a side ring so the exterior is larger than the clear cavity.");
        Require(clear.Count > neckClear.Count,
            "The fixture must use a neck narrower than the clear cavity.");
        var expected = new HashSet<BarbettePlanCell>(exterior);
        expected.ExceptWith(neckClear);

        for (var layer = 0; layer < layout.RoofThicknessMetres; layer++)
        {
            var y = layout.RoofBottomY + layer;
            var roof = result.Solids
                .Where(intent => intent.Role == BarbetteArmorRole.Roof && intent.Cell.Y == y)
                .Select(intent => new BarbettePlanCell(intent.Cell.X, intent.Cell.Z)).ToHashSet();
            Require(roof.SetEquals(expected),
                $"Roof layer {layer} must cover the complete realized exterior footprint minus the neck shaft.");
            Require(roof.Intersect(clear).Count() == clear.Count - neckClear.Count,
                "The circular protected clear cavity must be covered by roof armor, not left open.");
            Require(!roof.Overlaps(neckClear),
                "The square neck-clear shaft must stay open through every roof layer.");
        }

        Require(result.Solids.Where(intent => intent.Role == BarbetteArmorRole.Roof && intent.LayerIndex == 0)
                .All(intent => intent.Material == MaterialKind.Metal) &&
            result.Solids.Where(intent => intent.Role == BarbetteArmorRole.Roof && intent.LayerIndex == 1)
                .All(intent => intent.Material == MaterialKind.HeavyArmor),
            "Multilayer roof armor must preserve its independent layer materials and order.");

        var neckShaftVoids = result.RequiredVoids.Count(intent =>
            neckClear.Contains(new BarbettePlanCell(intent.Cell.X, intent.Cell.Z)) &&
            intent.Cell.Y >= layout.RoofBottomY && intent.Cell.Y <= layout.RoofTopY);
        Require(neckShaftVoids == neckClear.Count * layout.RoofThicknessMetres,
            "The neck shaft must be reserved as protected void through every roof layer.");
    }

    private static void VerifyFloatingVerticalPlacement()
    {
        var definition = Definition("floating", clear: 3, depth: 3, side: 1, roof: 1, bottom: 1);
        const int referenceDeckY = 11;

        var flush = BarbetteGenerator.Generate(definition, DesignMeasure.Zero, DesignMeasure.Zero,
            referenceDeckY);
        Require(flush.IsValid, Details(flush.Diagnostics));
        var layout = flush.Layout!;
        Require(layout.ReferenceDeckY == referenceDeckY && layout.TopOffsetMetres == 0,
            "A default barbette must record the ship-wide reference deck plane and a zero top offset.");
        Require(layout.RoofTopY == 10 && layout.RoofBottomY == 10,
            "Zero offset must put the roof top immediately beneath the reference deck skin.");
        Require(layout.ClearTopY == 9 && layout.FloorTopY == 6 &&
                layout.RealizedClearDepthMetres == 3,
            "Clear depth must be measured from the roof underside to the flat protected floor top.");
        Require(layout.NeckBottomY == 11 && layout.NeckTopY == 11,
            "The default neck must reach the reference deck plane.");

        var loweredDefinition = definition with { TopOffsetMetres = 2 };
        var loweredResult = BarbetteGenerator.Generate(loweredDefinition, DesignMeasure.Zero,
            DesignMeasure.Zero, referenceDeckY, 11);
        Require(loweredResult.IsValid, Details(loweredResult.Diagnostics));
        var loweredLayout = loweredResult.Layout!;
        Require(loweredLayout.RoofTopY == 8 && loweredLayout.ClearTopY == 7 && loweredLayout.FloorTopY == 4,
            "A two-metre top offset must lower the roof, cavity and floor together.");
        Require(loweredLayout.RealizedClearDepthMetres == 3,
            "A top offset must not change the realized clear depth.");
        Require(loweredLayout.NeckBottomY == 9 && loweredLayout.NeckTopY == 11,
            "A lowered barbette must extend its neck up to the same reference plane.");

        Require(flush.Solids.All(intent => intent.Cell.Y >= loweredLayout.FloorTopY),
            "A floating barbette must place all armor at or above its protected floor.");
        Require(flush.Solids.Any(intent => intent.Role == BarbetteArmorRole.Roof && intent.Cell.Y == 10) &&
                !flush.Solids.Any(intent => intent.Role == BarbetteArmorRole.Roof && intent.Cell.Y <= 9),
            "Roof armor must remain independent and above the clear cavity.");
    }

    private static void VerifyIndependentArmorStacks()
    {
        const int referenceDeckY = 11;
        var baseline = Definition("independent", clear: 3, depth: 3, side: 1, roof: 1, bottom: 1,
            neck: 1, neckSize: 1);
        var result = BarbetteGenerator.Generate(baseline, DesignMeasure.Zero, DesignMeasure.Zero,
            referenceDeckY, 11);
        Require(result.IsValid, Details(result.Diagnostics));
        var layout = result.Layout!;
        Require(result.Solids.Any(intent => intent.Role == BarbetteArmorRole.Side) &&
                result.Solids.Any(intent => intent.Role == BarbetteArmorRole.Roof) &&
                result.Solids.Any(intent => intent.Role == BarbetteArmorRole.Bottom) &&
                result.Solids.Any(intent => intent.Role == BarbetteArmorRole.Neck),
            "All four armor systems must be independently present.");

        var sideChanged = BarbetteGenerator.Generate(
            baseline with { SideArmor = new ArmorLayout([MaterialKind.Metal, MaterialKind.HeavyArmor]) },
            DesignMeasure.Zero, DesignMeasure.Zero, referenceDeckY, 11);
        Require(sideChanged.IsValid, Details(sideChanged.Diagnostics));
        Require(sideChanged.Measurement!.RealizedClearWidth == result.Measurement!.RealizedClearWidth &&
                sideChanged.Layout!.RealizedClearDepthMetres == layout.RealizedClearDepthMetres,
            "Side armor must not change the clear volume.");

        var roofChanged = BarbetteGenerator.Generate(
            baseline with { RoofArmor = new ArmorLayout([MaterialKind.Wood, MaterialKind.Lead]) },
            DesignMeasure.Zero, DesignMeasure.Zero, referenceDeckY, 11);
        Require(roofChanged.IsValid, Details(roofChanged.Diagnostics));
        Require(roofChanged.Layout!.RealizedClearDepthMetres == layout.RealizedClearDepthMetres,
            "Roof thickness must never consume the reported clear internal depth.");
        Require(roofChanged.Solids.Where(intent => intent.Role == BarbetteArmorRole.Roof)
                .Select(intent => intent.LayerIndex).Distinct().Order().SequenceEqual([0, 1]),
            "A two-layer roof must expose both independent layers.");
        Require(roofChanged.Solids.Where(intent => intent.Role == BarbetteArmorRole.Roof)
                .All(intent => intent.LayerIndex == 1 ? intent.Material == MaterialKind.Lead
                    : intent.Material == MaterialKind.Wood),
            "Each roof layer must retain its own material.");

        var bottomChanged = BarbetteGenerator.Generate(
            baseline with { BottomArmor = new ArmorLayout([MaterialKind.Metal, MaterialKind.Stone]) },
            DesignMeasure.Zero, DesignMeasure.Zero, referenceDeckY, 11);
        Require(bottomChanged.IsValid, Details(bottomChanged.Diagnostics));
        Require(bottomChanged.Layout!.RealizedClearDepthMetres == layout.RealizedClearDepthMetres,
            "Bottom thickness must never consume the reported clear internal depth.");
        Require(bottomChanged.Solids.Where(intent => intent.Role == BarbetteArmorRole.Bottom)
                .All(intent => intent.Cell.Y <= bottomChanged.Layout.FloorTopY),
            "Bottom armor must grow only downward from the protected floor.");

        var neckChanged = BarbetteGenerator.Generate(
            baseline with { NeckArmor = new ArmorLayout([MaterialKind.Metal, MaterialKind.Rubber]) },
            DesignMeasure.Zero, DesignMeasure.Zero, referenceDeckY, 11);
        Require(neckChanged.IsValid, Details(neckChanged.Diagnostics));
        Require(neckChanged.Solids.Where(intent => intent.Role == BarbetteArmorRole.Neck)
                .All(intent => intent.Cell.Y >= neckChanged.Layout!.NeckBottomY),
            "Neck armor must span only the neck band above the roof.");
        Require(neckChanged.Measurement!.RealizedNeckExteriorWidth.Metres == 5,
            "Two neck layers around a 1 m shaft must be exactly 5 m square externally.");
    }

    private static void VerifyBottomFloorDirectionality()
    {
        const int referenceDeckY = 11;
        var definition = Definition("floor", clear: 5, depth: 4, side: 1, bottom: 3);
        var result = BarbetteGenerator.Generate(definition, DesignMeasure.Zero, DesignMeasure.Zero,
            referenceDeckY, 11);
        Require(result.IsValid, Details(result.Diagnostics));
        var layout = result.Layout!;
        Require(layout.RealizedClearDepthMetres == 4,
            "The protected clear depth must be the requested four metres.");
        Require(result.Solids.Where(intent => intent.Role == BarbetteArmorRole.Bottom)
                .All(intent => intent.Cell.Y <= layout.FloorTopY),
            "No bottom-armor cell may rise above the protected floor top.");
        Require(result.Solids.Where(intent => intent.Role == BarbetteArmorRole.Bottom)
                .Select(intent => intent.Cell.Y).Distinct().Order().SequenceEqual(
                    Enumerable.Range(layout.BottomArmorBottomY, layout.BottomThicknessMetres)),
            "Bottom armor must fill exactly the requested downward layers.");

        var clear = Set(result.Measurement!.ClearCells);
        var bottomInClear = result.Solids
            .Where(intent => intent.Role == BarbetteArmorRole.Bottom &&
                             clear.Contains(new BarbettePlanCell(intent.Cell.X, intent.Cell.Z)))
            .ToArray();
        Require(bottomInClear.Any() && bottomInClear.Max(intent => intent.Cell.Y) == layout.FloorTopY,
            "Inside the clear plan the highest bottom-armor cell must be the single flat protected floor.");
        Require(bottomInClear.All(intent => intent.Cell.Y <= layout.FloorTopY),
            "No bottom armor may rise into the protected clear depth.");

        var cavityVoids = result.RequiredVoids
            .Where(intent => clear.Contains(new BarbettePlanCell(intent.Cell.X, intent.Cell.Z)) &&
                             intent.Cell.Y >= layout.ClearVolumeMinY &&
                             intent.Cell.Y <= layout.ClearVolumeMaxY)
            .ToArray();
        Require(cavityVoids.Length == clear.Count * layout.RealizedClearDepthMetres,
            "Every clear column must reserve its complete protected clear depth as void.");
        Require(!result.Solids.Any(intent =>
                clear.Contains(new BarbettePlanCell(intent.Cell.X, intent.Cell.Z)) &&
                intent.Cell.Y >= layout.ClearVolumeMinY && intent.Cell.Y <= layout.ClearVolumeMaxY),
            "No armor may fill upward into the protected clear depth.");
        Require(cavityVoids.Select(intent => intent.Cell.Y).Distinct().Count() == 4,
            "The flat protected floor must give every clear column the same four clear layers.");
    }

    private static void VerifyUsefulDiametersAndDepths()
    {
        const int referenceDeckY = 15;
        foreach (var diameter in new[] { 3.0, 5.0, 7.0, 9.0 })
        foreach (var depth in new[] { 1, 3, 6 })
        {
            var definition = Definition($"d{diameter}-c{depth}", clear: diameter, depth: depth,
                side: 1, roof: 1, bottom: 1, neck: diameter >= 5 ? 5 : 3);
            var result = BarbetteGenerator.Generate(definition, DesignMeasure.Zero, DesignMeasure.Zero,
                referenceDeckY, referenceDeckY);
            Require(result.IsValid, $"d{diameter} c{depth}: {Details(result.Diagnostics)}");
            Require(result.Layout!.RealizedClearDepthMetres == depth,
                $"A {depth} m clear-depth request must realize exactly {depth} m of clear cavity.");
            Require(result.Measurement!.RealizedClearWidth.Metres == diameter,
                $"A {diameter} m clear-diameter request must realize exactly {diameter} m of clear width.");
            Require(result.RequiredVoids.Any() && result.Solids.Any(),
                $"A {diameter} m x {depth} m barbette must produce both protected voids and armor.");
        }
    }

    private static void VerifyStableRepeatedGeneration()
    {
        const int referenceDeckY = 11;
        var definition = Definition("stable", clear: 5, depth: 3, side: 2, roof: 1, bottom: 2, neck: 3);
        var first = BarbetteGenerator.Generate(definition, DesignMeasure.Zero, DesignMeasure.Zero,
            referenceDeckY, 13);
        var second = BarbetteGenerator.Generate(definition, DesignMeasure.Zero, DesignMeasure.Zero,
            referenceDeckY, 13);
        Require(first.IsValid && second.IsValid, Details(first.Diagnostics));
        Require(first.Solids.SequenceEqual(second.Solids) &&
                first.RequiredVoids.SequenceEqual(second.RequiredVoids) &&
                first.AuthorizedDeckCuts.SequenceEqual(second.AuthorizedDeckCuts),
            "Equal barbette requests must emit byte-identical ordered intent.");
        Require(first.Solids.Select(intent => (intent.Cell.Z, intent.Cell.Y, intent.Cell.X))
                .SequenceEqual(first.Solids.OrderBy(intent => intent.Cell.Z)
                    .ThenBy(intent => intent.Cell.Y).ThenBy(intent => intent.Cell.X)
                    .Select(intent => (intent.Cell.Z, intent.Cell.Y, intent.Cell.X))),
            "Solid intent must retain stable Z-then-Y-then-X ordering.");
    }

    private static void VerifyEvenWidthCenterlineDiagnostics()
    {
        var half = DesignMeasure.FromTwiceMetres(1);
        var definition = Definition("even", clear: 3, depth: 2, side: 1);
        var measured = BarbetteGenerator.Measure(definition, half, half);
        Require(!measured.IsValid && measured.Measurement is not null &&
                Has(measured.Diagnostics, BarbetteDiagnosticCodes.EvenWidthCenterline),
            "Even-width centerline intent must be diagnosed, not shifted.");
        Require(measured.Measurement!.ClearCells.Count > 0,
            "The representable circular clear mask must still be retained for diagnostics.");

        var generated = BarbetteGenerator.Generate(definition, half, half, 11, 11);
        Require(!generated.IsValid && generated.Solids.Count == 0 && generated.RequiredVoids.Count == 0 &&
                Has(generated.Diagnostics, BarbetteDiagnosticCodes.EvenWidthCenterline),
            "Even-width centerline intent must reject physical generation without leaking geometry.");

        var mixed = BarbetteGenerator.Measure(definition, DesignMeasure.Zero, half);
        Require(!mixed.IsValid && Has(mixed.Diagnostics, BarbetteDiagnosticCodes.CenterParityMismatch),
            "A mixed whole/half center must be diagnosed rather than rounded.");
    }

    private static void VerifyBudgetsAndCancellation()
    {
        var definition = Definition("budget", clear: 5, depth: 2, side: 1);
        var planBudget = BarbetteGenerator.Measure(definition, DesignMeasure.Zero, DesignMeasure.Zero,
            new BarbetteGenerationLimits(MaxPlanCells: 20, MaxOutputIntents: 1_000));
        Require(!planBudget.IsValid && Has(planBudget.Diagnostics, BarbetteDiagnosticCodes.PlanBudgetExceeded),
            "The planar budget must fail before building an oversized mask.");

        var invalidBudget = BarbetteGenerator.Measure(definition, DesignMeasure.Zero, DesignMeasure.Zero,
            new BarbetteGenerationLimits(0, 1));
        Require(!invalidBudget.IsValid && Has(invalidBudget.Diagnostics, BarbetteDiagnosticCodes.InvalidBudget),
            "Non-positive budgets must fail closed.");

        var outputBudget = BarbetteGenerator.Generate(definition, DesignMeasure.Zero, DesignMeasure.Zero,
            11, 11, new BarbetteGenerationLimits(MaxPlanCells: 10_000, MaxOutputIntents: 5));
        Require(!outputBudget.IsValid && outputBudget.Solids.Count == 0 &&
                Has(outputBudget.Diagnostics, BarbetteDiagnosticCodes.OutputBudgetExceeded),
            "An output-budget failure must expose no partial intent.");

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var observed = false;
        try
        {
            _ = BarbetteGenerator.Generate(definition, DesignMeasure.Zero, DesignMeasure.Zero,
                11, 11, cancellationToken: cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            observed = true;
        }

        Require(observed, "A pre-cancelled request must throw before any result can escape.");
    }

    private static void VerifyHullContextBridge()
    {
        var context = HullGenerator.CreateContext(HullParameters.Default);
        Require(context.Lattice.HasCenterColumn && context.ReferenceDeckY == HullParameters.Default.Height - 1,
            "The supported product fixture must expose a real centre column and a ship-wide reference deck.");

        var definition = Definition("bridge", clear: 3, depth: 3, side: 1, roof: 1, bottom: 1,
            neck: 1, neckSize: 3);
        var result = BarbetteGenerator.Generate(definition, context.CenterPlaneX,
            DesignMeasure.FromCellAnchor(0), context);
        Require(result.IsValid, Details(result.Diagnostics));
        var layout = result.Layout!;
        Require(layout.RoofTopY == context.ReferenceDeckY - 1,
            "Zero offset against a flat reference deck must place the roof immediately beneath the deck skin.");
        Require(result.AuthorizedDeckCuts.Count == 25 &&
                result.AuthorizedDeckCuts.All(cut =>
                    context.TryGetArmor(cut.Cell.X, cut.Cell.Y, cut.Cell.Z, out var armor) &&
                    armor.Region == ArmorRegion.Deck && armor.Material == cut.Material &&
                    !context.IsProtectedShellCell(cut.Cell.X, cut.Cell.Y, cut.Cell.Z)),
            $"The 5x5 neck footprint must cut exactly the deck-owned structural armor; got " +
            $"{result.AuthorizedDeckCuts.Count}. {Details(result.Diagnostics)}");
        Require(result.RequiredVoids.Any(intent => intent.Cell.Y == context.ReferenceDeckY) &&
                result.Solids.Any(intent => intent.Role == BarbetteArmorRole.Neck),
            "The bridge must emit the protected neck shaft and its independent square armor.");
        Require(result.ClearVolumeBounds is { } bounds &&
                bounds.MinZ < bounds.MaxZ && bounds.MinY < bounds.MaxY,
            "The protected clear-volume envelope must be exposed for later ownership work.");

        var repeated = BarbetteGenerator.Generate(definition, context.CenterPlaneX,
            DesignMeasure.FromCellAnchor(0), context);
        Require(repeated.Solids.SequenceEqual(result.Solids) &&
                repeated.RequiredVoids.SequenceEqual(result.RequiredVoids) &&
                repeated.AuthorizedDeckCuts.SequenceEqual(result.AuthorizedDeckCuts),
            "Equal supported requests must emit identical ordered intent.");

        var offCenter = BarbetteGenerator.Generate(definition,
            context.CenterPlaneX + DesignMeasure.FromMetres(1), DesignMeasure.FromCellAnchor(0), context);
        Require(!offCenter.IsValid && Has(offCenter.Diagnostics, BarbetteDiagnosticCodes.CenterlineMismatch) &&
                offCenter.Solids.Count == 0,
            "A barbette off the evaluated hull center plane must reject without leaking geometry.");

        var evenContext = HullGenerator.CreateContext(HullParameters.Default with { Width = 20 });
        var even = BarbetteGenerator.Generate(definition, evenContext.CenterPlaneX,
            DesignMeasure.FromTwiceMetres(1), evenContext);
        Require(!even.IsValid && even.Measurement is null && even.Solids.Count == 0 &&
                Has(even.Diagnostics, BarbetteDiagnosticCodes.OddHullWidthRequired),
            "An even evaluated hull must reject centerline barbette generation atomically.");

        // Local deck rise must lengthen the neck, never lift the main barbette above the reference plane.
        var raisedParameters = HullParameters.Default with
        {
            Length = 72,
            Shape = HullShapeSettings.Default with { Profile = new HullProfileSettings(4, 2, 3, 1) },
        };
        var raisedContext = HullGenerator.CreateContext(raisedParameters);
        int? raisedZ = null;
        for (var z = raisedContext.MinZ + 4; z <= raisedContext.MaxZ - 4 && raisedZ is null; z++)
            if (raisedContext.DeckYAt(z) > raisedContext.ReferenceDeckY)
                raisedZ = z;

        Require(raisedZ is not null, "The raised-profile fixture must expose a locally raised deck station.");
        var raised = BarbetteGenerator.Generate(definition, raisedContext.CenterPlaneX,
            DesignMeasure.FromCellAnchor(raisedZ!.Value), raisedContext);
        Require(raised.IsValid, Details(raised.Diagnostics));
        Require(raised.Layout!.RoofTopY == raisedContext.ReferenceDeckY - 1,
            "A locally raised deck must not move the main barbette roof above the ship-wide reference plane.");
        Require(raised.Layout.NeckTopY == raisedContext.DeckYAt(raisedZ.Value),
            "The neck must extend up to the highest local deck elevation across its footprint.");
    }

    private static BarbetteDefinition Definition(
        string id,
        double clear,
        int depth,
        int side,
        int roof = 1,
        int bottom = 1,
        int neck = 1,
        int neckSize = 3) => BarbetteDefinition.Create(
        id,
        $"node-{id}",
        DesignMeasure.FromMetres(clear),
        depth,
        topOffsetMetres: 0,
        neckClearSizeMetres: neckSize,
        sideArmor: Stack(side),
        roofArmor: Stack(roof),
        bottomArmor: Stack(bottom),
        neckArmor: Stack(neck));

    private static ArmorLayout Stack(int thickness)
    {
        var layers = new List<MaterialKind>();
        for (var index = 0; index < thickness; index++)
            layers.Add(index == thickness - 1 ? MaterialKind.HeavyArmor : MaterialKind.Metal);
        return new ArmorLayout(layers);
    }

    private static HashSet<BarbettePlanCell> Rect(int minX, int maxX, int minZ, int maxZ)
    {
        var result = new HashSet<BarbettePlanCell>();
        for (var z = minZ; z <= maxZ; z++)
            for (var x = minX; x <= maxX; x++)
                result.Add(new BarbettePlanCell(x, z));
        return result;
    }

    private static HashSet<BarbettePlanCell> Set(IEnumerable<BarbettePlanCell> cells) => [.. cells];

    private static bool IsFaceConnected(HashSet<BarbettePlanCell> cells)
    {
        var queue = new Queue<BarbettePlanCell>();
        var reached = new HashSet<BarbettePlanCell>();
        var first = cells.First();
        queue.Enqueue(first);
        reached.Add(first);
        while (queue.Count > 0)
        {
            var cell = queue.Dequeue();
            foreach (var next in new[]
                     {
                         new BarbettePlanCell(cell.X - 1, cell.Z),
                         new BarbettePlanCell(cell.X + 1, cell.Z),
                         new BarbettePlanCell(cell.X, cell.Z - 1),
                         new BarbettePlanCell(cell.X, cell.Z + 1),
                     })
            {
                if (cells.Contains(next) && reached.Add(next))
                    queue.Enqueue(next);
            }
        }

        return reached.Count == cells.Count;
    }

    private static bool IsSymmetric(HashSet<BarbettePlanCell> cells, int centerXTwice, int centerZTwice) =>
        cells.All(cell =>
            cells.Contains(new BarbettePlanCell(centerXTwice - cell.X, cell.Z)) &&
            cells.Contains(new BarbettePlanCell(cell.X, centerZTwice - cell.Z)) &&
            cells.Contains(new BarbettePlanCell(
                (centerXTwice + centerZTwice) / 2 - cell.Z,
                (centerZTwice - centerXTwice) / 2 + cell.X)));

    private static bool Has(IEnumerable<DesignDiagnostic> diagnostics, string code) =>
        diagnostics.Any(diagnostic => diagnostic.Code == code);

    private static string Details(IEnumerable<DesignDiagnostic> diagnostics) =>
        string.Join(" | ", diagnostics.Select(diagnostic => diagnostic.ToString()));

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
