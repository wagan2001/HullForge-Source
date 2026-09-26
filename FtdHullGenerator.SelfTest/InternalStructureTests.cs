using System.Collections.Immutable;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Components;
using FtdHullGenerator.Domain.Composition;
using FtdHullGenerator.Domain.Design;
using FtdHullGenerator.Geometry;
using FtdHullGenerator.Geometry.Components;

/// <summary>
/// Independent B01 cell oracles. Expected centres, parity, priority and void cells are written
/// directly here; none is obtained by calling a second production plane enumerator.
/// </summary>
internal static class InternalStructureTests
{
    public static void Run()
    {
        VerifyOddAndEvenCenterParity();
        VerifyMirroredPairsAndSpacingKinds();
        VerifyOffsetsAndEndDatums();
        VerifyThreeFamilyJunctionOwnershipAndDeterminism();
        VerifyArmorAirAndCavityArePreserved();
        VerifyRequiredWellExclusion();
        VerifyCancellationAndBudgets();
        VerifyNonCubicAdjacencyIsNotClaimedAsSupport();

        Console.WriteLine(
            "Internal structure: odd/even parity, spacing/offset/end datums, mirrored planes, " +
            "three-family junction priority, cavity/armor-air preservation, well exclusions, " +
            "cancellation/budgets and conservative non-cubic support passed.");
    }

    private static void VerifyOddAndEvenCenterParity()
    {
        var oddContext = HullGenerator.CreateContext(HullParameters.Default);
        var central = Longitudinal(1, 1, includeCenter: true);
        var odd = Generate(oddContext, central);
        Require(odd.IsValid, Describe(odd));
        Require(odd.Planes.Length == 1 && odd.Cells.Length > 0,
            "An odd-width hull must realize its requested one-cell central bulkhead.");
        Require(odd.Cells.All(intent => intent.Cell.X == 0),
            "The default odd-width central bulkhead must occupy x=0 exactly.");

        var evenContext = HullGenerator.CreateContext(HullParameters.Default with { Width = 18 });
        var impossible = Generate(evenContext, central);
        Require(!impossible.IsValid && impossible.Cells.IsEmpty,
            "An odd-thickness central slab on an even-width hull must fail before rasterization.");
        Require(impossible.Diagnostics.Any(diagnostic =>
                diagnostic.Code == InternalStructureDiagnosticCodes.CenterSlabParity &&
                diagnostic.SuggestedCorrection?.Contains("thickness 2", StringComparison.Ordinal) == true),
            "The parity diagnosis must offer the nearest symmetric thickness.");

        var representable = Generate(evenContext, Longitudinal(2, 1, includeCenter: true));
        Require(representable.IsValid, Describe(representable));
        Require(representable.Cells.Select(intent => intent.Cell.X).Distinct().Order().SequenceEqual([-1, 0]),
            "The even-width two-cell central slab must straddle x=-0.5 as columns -1 and 0.");
        Require(representable.Cells.All(intent =>
                representable.Cells.Any(other => other.Cell == intent.Cell with
                {
                    X = evenContext.MirrorSum - intent.Cell.X,
                })),
            "Every central-slab cell must have its exact actual-centre mirror.");
    }

    private static void VerifyMirroredPairsAndSpacingKinds()
    {
        var context = HullGenerator.CreateContext(HullParameters.Default with { Width = 18 });
        var pitch = Longitudinal(2, 4, includeCenter: false) with
        {
            Spacing = DesignMeasure.FromMetres(2),
            SpacingKind = InternalSpacingKind.CenterPitch,
        };
        var pitched = Generate(context, pitch);
        Require(pitched.IsValid, Describe(pitched));
        Require(pitched.Planes.Select(plane => plane.RequestedCenter.TwiceMetres)
                .Order().SequenceEqual([-7, -3, 1, 5]),
            "A 2 m centre pitch around x=-0.5 must produce centres -3.5,-1.5,0.5,2.5.");

        var clear = Generate(context, pitch with
        {
            SpacingKind = InternalSpacingKind.ClearCompartmentGap,
        });
        Require(clear.IsValid, Describe(clear));
        Require(clear.Planes.Select(plane => plane.RequestedCenter.TwiceMetres)
                .Order().SequenceEqual([-13, -5, 3, 11]),
            "A 2 m clear gap plus a 2 m slab must produce a 4 m centre pitch.");

        foreach (var intent in clear.Cells)
        {
            var mirror = intent.Cell with { X = context.MirrorSum - intent.Cell.X };
            Require(clear.Cells.Any(other => other.Cell == mirror && other.Material == intent.Material),
                $"Mirrored pair is missing {mirror} for {intent.Cell}.");
        }
    }

    private static void VerifyOffsetsAndEndDatums()
    {
        var context = HullGenerator.CreateContext(HullParameters.Default);
        var deckAbove = Generate(context, Deck(DesignMeasure.FromMetres(6)));
        var deckBelow = Generate(context, Deck(DesignMeasure.FromMetres(4)));
        Require(deckAbove.IsValid && deckBelow.IsValid, Describe(deckAbove, deckBelow));
        Require(deckAbove.Cells.All(intent => intent.Cell.Y == 6) &&
                deckBelow.Cells.All(intent => intent.Cell.Y == 4),
            "Positive and negative height choices must rasterize their exact world-Y rows.");

        var fromStern = Transverse(2, DesignMeasure.FromMetres(10)) with
        {
            Datum = InternalPlaneDatum.MinimumHullExtent,
            Direction = InternalRepeatDirection.Positive,
            Spacing = DesignMeasure.FromMetres(10),
        };
        var stern = Generate(context, fromStern);
        Require(stern.IsValid, Describe(stern));
        Require(stern.Planes.Select(plane => plane.RequestedCenter.Metres).SequenceEqual(
                [context.MinZ + 10d, context.MinZ + 20d]),
            "The stern datum must advance in +Z from the base-hull minimum.");

        var fromBow = Transverse(2, DesignMeasure.FromMetres(-10)) with
        {
            Datum = InternalPlaneDatum.MaximumHullExtent,
            Direction = InternalRepeatDirection.Negative,
            Spacing = DesignMeasure.FromMetres(10),
        };
        var bow = Generate(context, fromBow);
        Require(bow.IsValid, Describe(bow));
        Require(bow.Planes.Select(plane => plane.RequestedCenter.Metres).SequenceEqual(
                [context.MaxZ - 20d, context.MaxZ - 10d]),
            "The bow datum must advance in -Z from the base-hull maximum.");

        var bounded = Deck(DesignMeasure.FromMetres(2)) with
        {
            Count = 10,
            CountMode = InternalPlaneCountMode.RepeatToBoundary,
            Spacing = DesignMeasure.FromMetres(2),
            RepetitionExtent = new DesignSpan(DesignMeasure.FromMetres(2), DesignMeasure.FromMetres(6)),
        };
        var repeated = Generate(context, bounded);
        Require(repeated.IsValid && repeated.Planes.Select(plane => plane.RequestedCenter.Metres)
                .SequenceEqual([2d, 4d, 6d]),
            "Repeat-to-boundary must realize only the three exact centres inside [2,6].");

        var fixedOutside = Generate(context, bounded with { CountMode = InternalPlaneCountMode.FixedCount });
        Require(!fixedOutside.IsValid && fixedOutside.Cells.IsEmpty &&
                fixedOutside.Diagnostics.Any(diagnostic =>
                    diagnostic.Code == InternalStructureDiagnosticCodes.FixedCountDoesNotFit),
            "The same explicit ten-plane request must report, not silently drop, out-of-extent planes.");
    }

    private static void VerifyThreeFamilyJunctionOwnershipAndDeterminism()
    {
        var context = HullGenerator.CreateContext(HullParameters.Default);
        var longitudinal = Longitudinal(1, 1, includeCenter: true) with { Material = MaterialKind.Wood };
        var deck = Deck(DesignMeasure.FromMetres(5)) with { Material = MaterialKind.Glass };
        var transverse = Transverse(1, DesignMeasure.Zero) with { Material = MaterialKind.HeavyArmor };
        var settings = Structure(longitudinal, deck, transverse);
        var result = BulkheadGenerator.Generate(context, settings);
        Require(result.IsValid, Describe(result));

        var junction = result.Cells.Single(intent => intent.Cell == new HullCell(0, 5, 0));
        Require(junction.Contributions.Length == 3,
            "The exact all-family crossing must retain all three contributing owners.");
        Require(junction.Material == MaterialKind.HeavyArmor &&
                junction.WinningFamily == InternalPlaneFamily.TransverseBulkhead,
            "Default transverse > longitudinal > deck priority must own the physical junction.");
        Require(result.Cells.Select(intent => intent.Cell).Distinct().Count() == result.Cells.Length,
            "Every internal coordinate must produce exactly one physical cell intent.");

        var reordered = BulkheadGenerator.Generate(context,
            Structure(transverse, longitudinal, deck));
        Require(result.Cells.Select(CellKey).SequenceEqual(reordered.Cells.Select(CellKey)) &&
                result.Planes.Select(PlaneKey).SequenceEqual(reordered.Planes.Select(PlaneKey)),
            "Input family order must not alter deterministic planes, cells or junction ownership.");

        var custom = BulkheadGenerator.Generate(context, settings with
        {
            JunctionPriority =
            [
                InternalPlaneFamily.InternalDeck,
                InternalPlaneFamily.LongitudinalBulkhead,
                InternalPlaneFamily.TransverseBulkhead,
            ],
        });
        Require(custom.Cells.Single(intent => intent.Cell == new HullCell(0, 5, 0)).Material == MaterialKind.Glass,
            "A stored custom internal-only priority must deterministically choose the deck material.");
    }

    private static void VerifyArmorAirAndCavityArePreserved()
    {
        var parameters = HullParameters.Default with
        {
            HullArmor = new ArmorLayout(
            [
                new ArmorLayer(MaterialKind.Metal),
                ArmorLayer.Air,
                new ArmorLayer(MaterialKind.LightweightAlloy),
            ]),
            Beamify = false,
        };
        var context = HullGenerator.CreateContext(parameters);
        var armorBefore = context.EnumerateArmor().ToArray();
        var result = BulkheadGenerator.Generate(context,
            Structure(Longitudinal(1, 1, includeCenter: true), Deck(DesignMeasure.FromMetres(5))));
        Require(result.IsValid, Describe(result));
        Require(result.Cells.All(intent => context.IsUsableCavity(intent.Cell.X, intent.Cell.Y, intent.Cell.Z)),
            "Every emitted internal cell must be in the context's actual usable cavity.");
        Require(result.Cells.All(intent => !context.IsReservedArmorAir(intent.Cell.X, intent.Cell.Y, intent.Cell.Z)),
            "No internal cell may consume intentional armor air.");
        Require(context.EnumerateArmor().SequenceEqual(armorBefore),
            "Read-only internal generation must not change armor ownership or material intent.");
        Require(armorBefore.Where(intent => intent.IsReservedAir)
                .All(intent => !result.Cells.Any(cell => cell.Cell == intent.Cell)),
            "Every independently enumerated reserved-air cell must remain absent from internals.");
    }

    private static void VerifyRequiredWellExclusion()
    {
        var context = HullGenerator.CreateContext(HullParameters.Default);
        var wellCells = (from x in Enumerable.Range(-1, 3)
                         from y in Enumerable.Range(1, 9)
                         from z in Enumerable.Range(-1, 3)
                         select new HullCell(x, y, z)).ToImmutableArray();
        var well = new RequiredVoidExclusion("barbette-well", wellCells);
        var result = BulkheadGenerator.Generate(context,
            Structure(Longitudinal(1, 1, includeCenter: true),
                Deck(DesignMeasure.FromMetres(5)), Transverse(1, DesignMeasure.Zero)),
            [well]);
        Require(result.IsValid, Describe(result));
        Require(result.Planes.Sum(plane => plane.RequiredVoidCellCount) > 0,
            "At least one requested internal plane must report clipping by the well.");
        Require(wellCells.All(cell => result.Cells.All(intent => intent.Cell != cell)),
            "Every required well cell must remain physically empty after all-family reconciliation.");
        Require(result.Cells.All(intent => !context.IsProtectedShellCell(
                intent.Cell.X, intent.Cell.Y, intent.Cell.Z)),
            "A well and internal plane may never transfer ownership from protected shell armor.");
    }

    private static void VerifyCancellationAndBudgets()
    {
        var context = HullGenerator.CreateContext(HullParameters.Default);
        var structure = Structure(Longitudinal(1, 1, includeCenter: true));
        using var source = new CancellationTokenSource();
        source.Cancel();
        var cancelled = false;
        try
        {
            BulkheadGenerator.Generate(context, structure, cancellationToken: source.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        Require(cancelled, "A pre-cancelled generation must throw and publish no partial result.");

        var budget = BulkheadGenerator.Generate(context, structure, options: new BulkheadGenerationOptions
        {
            MaxCandidateCells = 1,
        });
        Require(!budget.IsValid && budget.Cells.IsEmpty && budget.Planes.IsEmpty &&
                budget.Diagnostics.Any(diagnostic =>
                    diagnostic.Code == InternalStructureDiagnosticCodes.CandidateBudgetExceeded),
            "A candidate estimate over budget must fail before allocating physical intent.");
    }

    private static void VerifyNonCubicAdjacencyIsNotClaimedAsSupport()
    {
        var pole = new ArmorLayout([new ArmorLayer(MaterialKind.Metal, ArmorConstruction.Pole)]);
        var context = HullGenerator.CreateContext(HullParameters.Default with
        {
            HullArmor = pole,
            BottomArmor = pole,
            DeckArmor = pole,
            Beamify = false,
        });
        var result = Generate(context, Longitudinal(1, 1, includeCenter: true));
        Require(!result.IsValid && result.Cells.Length > 0 &&
                result.Diagnostics.Any(diagnostic =>
                    diagnostic.Code == InternalStructureDiagnosticCodes.UnsupportedRegion),
            "Voxel adjacency to pole armor must remain an unsupported-region diagnosis, not a physical claim.");
    }

    private static InternalStructureGenerationResult Generate(
        FtdHullGenerator.Geometry.Composition.HullBuildContext context,
        params InternalStructureFamily[] families) =>
        BulkheadGenerator.Generate(context, Structure(families));

    private static InternalStructure Structure(params InternalStructureFamily[] enabled)
    {
        var byFamily = enabled.ToDictionary(family => family.Family);
        return new InternalStructure(Enum.GetValues<InternalPlaneFamily>()
            .Select(family => byFamily.TryGetValue(family, out var settings)
                ? settings
                : InternalStructureFamily.Disabled(family))
            .ToImmutableArray());
    }

    private static InternalStructureFamily Longitudinal(int thickness, int count, bool includeCenter) =>
        new(InternalPlaneFamily.LongitudinalBulkhead, true, thickness, MaterialKind.Metal,
            DesignMeasure.FromMetres(6), count, DesignMeasure.Zero, true)
        {
            Datum = InternalPlaneDatum.CenterPlane,
            Direction = InternalRepeatDirection.Both,
            IncludeCentralPlane = includeCenter,
        };

    private static InternalStructureFamily Deck(DesignMeasure y) =>
        new(InternalPlaneFamily.InternalDeck, true, 1, MaterialKind.Metal,
            DesignMeasure.FromMetres(4), 1, y, false)
        {
            Datum = InternalPlaneDatum.HullOrigin,
            Direction = InternalRepeatDirection.Positive,
        };

    private static InternalStructureFamily Transverse(int count, DesignMeasure offset) =>
        new(InternalPlaneFamily.TransverseBulkhead, true, 1, MaterialKind.Metal,
            DesignMeasure.FromMetres(8), count, offset, false)
        {
            Datum = InternalPlaneDatum.HullOrigin,
            Direction = InternalRepeatDirection.Positive,
        };

    private static string Describe(params InternalStructureGenerationResult[] results) =>
        string.Join(" || ", results.Select(result => string.Join(" | ", result.Diagnostics)));

    private static string CellKey(InternalStructureCellIntent intent) =>
        $"{intent.Cell.X},{intent.Cell.Y},{intent.Cell.Z}|{intent.Material}|{intent.WinningOwnerId}|" +
        string.Join(",", intent.Contributions.Select(contribution =>
            $"{contribution.OwnerId}:{contribution.Family}:{contribution.Material}"));

    private static string PlaneKey(InternalPlaneRealization plane) =>
        $"{plane.OwnerId}|{plane.Family}|{plane.Material}|{plane.RequestedCenter.TwiceMetres}|" +
        $"{plane.CandidateCellCount}|{plane.RealizedCellCount}|{plane.RequiredVoidCellCount}|" +
        $"{plane.CavityClippedCellCount}|{plane.RealizedBounds}";

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
