using System.Diagnostics;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Components;
using FtdHullGenerator.Domain.Design;
using FtdHullGenerator.Geometry;
using FtdHullGenerator.Geometry.Components;
using FtdHullGenerator.Geometry.Composition;

/// <summary>
/// HF-06 adversarial oracles: an invalid <see cref="BarbetteGenerationResult"/> must never
/// materialize protected cells, reservations, ownership or clear-volume diagnostics even when its
/// measurement and layout are retained for diagnostics; the planar preflight must fail closed on an
/// over-budget/overflowing neck before allocating; and the direct Chebyshev-ring perimeter must
/// visit exactly the ring, in order, honoring cancellation.
/// </summary>
internal static class BarbetteSafetyTests
{
    public static void Run()
    {
        VerifyInvalidResultDoesNotExpandOwnership();
        VerifyInvalidClearVolumeHelper();
        VerifyRecordLevelInvalidBounds();
        VerifyLargeNeckBudgetFailsClosed();
        VerifyLargeNeckFailsThroughGenerator();
        VerifyGeneratedNeckMatchesDirectPerimeter();
        VerifyDirectSquarePerimeterShape();
        VerifyCancellationHonored();
        VerifyPlanningCancellationHonored();

        Console.WriteLine(
            "Barbette safety: invalid results never expand protected cells, reservations, ownership " +
            "or record-level bounds, over-budget/overflowing necks fail closed before allocation " +
            "through both the helper and the generator, generated 1/3/5 m necks match the direct " +
            "Chebyshev-ring perimeter exactly, and cancellation is honored passed.");
    }

    /// <summary>
    /// The generator's output-budget rejection retains both the measurement and the layout while
    /// reporting <c>IsValid == false</c>. The ownership pass must treat that retained metadata as
    /// diagnostic-only.
    /// </summary>
    private static void VerifyInvalidResultDoesNotExpandOwnership()
    {
        var context = HullGenerator.CreateContext(HullParameters.Default);
        var definition = Definition("invalid", clear: 3, depth: 3, side: 1, neck: 1, neckSize: 3);
        var limits = new BarbetteGenerationLimits(MaxOutputIntents: 1);

        // Fixture proof: the rejected result really does retain a non-null measurement and layout.
        var generation = BarbetteGenerator.Generate(definition, context.CenterPlaneX,
            DesignMeasure.FromCellAnchor(0), context, limits);
        Require(!generation.IsValid, "The one-intent budget must reject this barbette.");
        Require(generation.Measurement is not null && generation.Measurement.ClearCells.Count > 0,
            "The rejected result must retain a populated measurement for diagnostics.");
        Require(generation.Layout is not null,
            "The rejected result must retain its layout for diagnostics.");
        Require(Has(generation.Diagnostics, BarbetteDiagnosticCodes.OutputBudgetExceeded),
            "The fixture must exercise the output-budget rejection (BAR103).");
        Require(generation.Solids.Count == 0 && generation.RequiredVoids.Count == 0 &&
                generation.AuthorizedDeckCuts.Count == 0,
            "A rejected generation must expose no partial intent.");

        // Direct helper: an invalid result must expand no protected cell at all.
        var invalidVolume = BarbetteOwnership.ProtectedClearVolumeOf(generation);
        Require(invalidVolume.Count == 0,
            "An invalid generation with retained measurement+layout must yield an empty protected volume.");

        // One invalid entry.
        var single = BarbetteOwnership.Reconcile(
            [new BarbettePlacement(definition, context.CenterPlaneX, DesignMeasure.FromCellAnchor(0))],
            DesignMeasure.Zero, context, limits);
        Require(single.Barbettes.Count == 1 && !single.Barbettes[0].Generation.IsValid,
            "An invalid barbette must still appear in Barbettes so its diagnostics stay visible.");
        var entry = single.Barbettes[0];
        Require(entry.ClearVolumeBounds is null,
            "An invalid entry must expose no clear-volume bounds even with retained measurement+layout.");
        Require(entry.ClearLongitudinalHalfExtent == DesignMeasure.Zero,
            "An invalid entry must expose a zero clear longitudinal half-extent.");
        Require(single.Solids.Count == 0,
            "An invalid entry must not merge solids into ownership.");
        Require(!Has(single.Diagnostics, BarbetteDiagnosticCodes.ClearVolumeOverlap) &&
                !Has(single.Diagnostics, BarbetteDiagnosticCodes.ClearVolumeSeparation) &&
                !Has(single.Diagnostics, BarbetteDiagnosticCodes.ArmorPenetratesClearVolume),
            "An invalid entry must not contribute a clear volume to overlap/separation/penetration checks.");

        // Two invalid barbettes whose retained clear volumes overlap at Z=1. Before this gate the
        // retained metadata expanded into protected cells and produced a false BAR211.
        var first = Definition("invalid-a", clear: 3, depth: 3, side: 1);
        var second = Definition("invalid-b", clear: 3, depth: 3, side: 1);
        var pair = BarbetteOwnership.Reconcile(
            [
                new BarbettePlacement(first, context.CenterPlaneX, DesignMeasure.FromCellAnchor(0)),
                new BarbettePlacement(second, context.CenterPlaneX, DesignMeasure.FromCellAnchor(2)),
            ],
            DesignMeasure.FromCellAnchor(1), context, limits);
        Require(pair.Barbettes.Count == 2 && pair.Barbettes.All(item => !item.Generation.IsValid),
            "Both deliberately over-budget barbettes must stay visible and invalid.");
        Require(pair.Barbettes.All(item => item.ClearVolumeBounds is null &&
                item.ClearLongitudinalHalfExtent == DesignMeasure.Zero),
            "Every invalid entry must expose no clear volume or half-extent.");
        Require(pair.Solids.Count == 0,
            "Invalid entries must not contribute ownership solids.");
        Require(!Has(pair.Diagnostics, BarbetteDiagnosticCodes.ClearVolumeOverlap) &&
                !Has(pair.Diagnostics, BarbetteDiagnosticCodes.ClearVolumeSeparation) &&
                !Has(pair.Diagnostics, BarbetteDiagnosticCodes.ArmorPenetratesClearVolume),
            "Two invalid barbettes with overlapping retained clear volumes must not report a " +
            $"clear-volume conflict. {Details(pair.Diagnostics)}");
    }

    /// <summary>
    /// A valid generation still yields a non-empty protected volume; the invalid gate must not
    /// weaken valid-case behavior.
    /// </summary>
    private static void VerifyInvalidClearVolumeHelper()
    {
        var context = HullGenerator.CreateContext(HullParameters.Default);
        var definition = Definition("helper", clear: 3, depth: 3, side: 1, neck: 1, neckSize: 3);

        var valid = BarbetteGenerator.Generate(definition, context.CenterPlaneX,
            DesignMeasure.FromCellAnchor(0), context);
        Require(valid.IsValid, Details(valid.Diagnostics));
        var validVolume = BarbetteOwnership.ProtectedClearVolumeOf(valid);
        Require(validVolume.Count > 0,
            "A valid generation must still expand its protected clear volume.");

        var invalid = BarbetteGenerator.Generate(definition, context.CenterPlaneX,
            DesignMeasure.FromCellAnchor(0), context, new BarbetteGenerationLimits(MaxOutputIntents: 1));
        Require(!invalid.IsValid && invalid.Measurement is not null && invalid.Layout is not null,
            "The helper fixture must retain measurement+layout while invalid.");
        var invalidVolume = BarbetteOwnership.ProtectedClearVolumeOf(invalid);
        Require(invalidVolume.Count == 0 && invalidVolume.Count < validVolume.Count,
            "The invalid gate must empty a protected volume the valid case still expands.");
    }

    /// <summary>
    /// The record itself must gate its public clear-volume envelope on validity, not only on a null
    /// measurement/layout: an invalid result retains both for diagnostics and still exposes no bounds.
    /// </summary>
    private static void VerifyRecordLevelInvalidBounds()
    {
        var context = HullGenerator.CreateContext(HullParameters.Default);
        var definition = Definition("record-bounds", clear: 3, depth: 3, side: 1, neck: 1, neckSize: 3);

        var valid = BarbetteGenerator.Generate(definition, context.CenterPlaneX,
            DesignMeasure.FromCellAnchor(0), context);
        Require(valid.IsValid, Details(valid.Diagnostics));
        Require(valid.ClearVolumeBounds is not null,
            "A valid generation must expose a non-null clear-volume envelope.");

        var invalid = BarbetteGenerator.Generate(definition, context.CenterPlaneX,
            DesignMeasure.FromCellAnchor(0), context, new BarbetteGenerationLimits(MaxOutputIntents: 1));
        Require(!invalid.IsValid && invalid.Measurement is not null && invalid.Layout is not null,
            "The record-level fixture must retain measurement+layout while invalid.");
        Require(invalid.ClearVolumeBounds is null,
            "An invalid generation must expose no clear-volume envelope at the record level, even " +
            "with a retained measurement and layout.");
    }

    /// <summary>
    /// A pathological neck armor thickness must fail the checked planar preflight before any mask
    /// or ring is allocated. This deliberately does not call <c>BarbetteGenerator.Generate</c> with
    /// pathological input: that wiring is the pending Team-A integration hook.
    /// </summary>
    private static void VerifyLargeNeckBudgetFailsClosed()
    {
        const int budget = 250_000;
        var clearDiameterTwice = DesignMeasure.FromMetres(5).TwiceMetres;

        // One million metres of neck armor cannot be planned; the closed-form ring/exterior sizes
        // are computed without allocating a single ring.
        var pathological = BarbettePlanPreflight.Preflight(
            clearDiameterTwice, sideArmorThicknessMetres: 1, neckClearSizeMetres: 1,
            neckArmorThicknessMetres: 1_000_000, maxPlanCells: budget);
        Require(!pathological.IsValid,
            "A million-metre neck armor stack must fail the planar preflight.");
        Require(Has(pathological.Diagnostics, BarbetteDiagnosticCodes.PlanBudgetExceeded),
            "The over-budget neck must fail closed with BAR102.");
        Require(pathological.EstimatedPlanCells > budget,
            "The preflight must report an estimate above the explicit budget.");
        Require(pathological.NeckArmorRingCells > 0 && pathological.NeckExteriorCells > 0,
            "The preflight must report the neck structures it refused to allocate.");
        var budgetDiagnostic = pathological.Diagnostics.Single(diagnostic =>
            diagnostic.Code == BarbetteDiagnosticCodes.PlanBudgetExceeded);
        Require(budgetDiagnostic.Field == nameof(BarbetteGenerationLimits.MaxPlanCells) &&
                budgetDiagnostic.SuggestedCorrection is not null,
            "The preflight budget diagnostic must keep a structured field and correction, not a bare message.");

        // The exterior side is clear + 2 * thickness; a value that overflows int must be a
        // diagnostic, never an unhandled OverflowException.
        var overflow = BarbettePlanPreflight.Preflight(
            clearDiameterTwice, sideArmorThicknessMetres: 1, neckClearSizeMetres: 1,
            neckArmorThicknessMetres: int.MaxValue, maxPlanCells: budget);
        Require(!overflow.IsValid && Has(overflow.Diagnostics, BarbetteDiagnosticCodes.PlanBudgetExceeded),
            "Neck exterior arithmetic overflow must fail closed with BAR102, not throw.");

        var invalidBudget = BarbettePlanPreflight.Preflight(
            clearDiameterTwice, 1, 1, 1, maxPlanCells: 0);
        Require(!invalidBudget.IsValid && Has(invalidBudget.Diagnostics, BarbetteDiagnosticCodes.InvalidBudget),
            "A non-positive planar budget must fail closed with BAR101.");

        // A normal neck fits, and the exterior is the frozen clear + 2 * armor square.
        var normal = BarbettePlanPreflight.Preflight(clearDiameterTwice, 1, 3, 1,
            BarbetteGenerationLimits.Default.MaxPlanCells);
        Require(normal.IsValid, Details(normal.Diagnostics));
        Require(normal.NeckExteriorCells == 25,
            $"A 3 m clear neck with one armor layer is a 5x5 exterior square; got {normal.NeckExteriorCells}.");

        var definition = Definition("preflight", clear: 5, depth: 3, side: 1, neck: 1, neckSize: 3);
        var viaDefinition = BarbettePlanPreflight.Preflight(definition);
        Require(viaDefinition.IsValid,
            "The definition overload must pass a normal barbette against the default budget.");
        Require(viaDefinition.ScanRadius > 0,
            "The preflight must expose the scan radius the generator's bore loop consumes.");

        var overBudgetDefinition = BarbettePlanPreflight.Preflight(definition,
            new BarbetteGenerationLimits(MaxPlanCells: 1));
        Require(!overBudgetDefinition.IsValid &&
                overBudgetDefinition.Diagnostics.All(diagnostic =>
                    diagnostic.NodeId == definition.NodeId),
            "The definition overload must attribute the preflight failure to the barbette node.");
    }

    /// <summary>
    /// The authoritative generator must reject a pathological neck through the real public API,
    /// within the explicit planar budget and before any mask/ring allocation. Against the old
    /// unwired generator this call would scan O(thickness³) square interiors and hang/allocate
    /// pathologically; the preflight now returns immediately.
    /// </summary>
    private static void VerifyLargeNeckFailsThroughGenerator()
    {
        var definition = BarbetteDefinition.Create(
            "large-neck",
            "node-large-neck",
            DesignMeasure.FromMetres(5),
            clearDepthMetres: 3,
            topOffsetMetres: 0,
            neckClearSizeMetres: 3,
            neckArmor: new ArmorLayout(Enumerable.Repeat(MaterialKind.Metal, 1000)));
        var limits = new BarbetteGenerationLimits(MaxPlanCells: 250_000);

        var stopwatch = Stopwatch.StartNew();
        var generation = BarbetteGenerator.Generate(definition, DesignMeasure.Zero, DesignMeasure.Zero,
            11, 11, limits);
        stopwatch.Stop();

        Require(!generation.IsValid,
            "A thousand-layer neck armor stack must be rejected by the generator preflight.");
        Require(Has(generation.Diagnostics, BarbetteDiagnosticCodes.PlanBudgetExceeded),
            $"The generator must report BAR102 for the over-budget neck. {Details(generation.Diagnostics)}");
        Require(generation.Measurement is null && generation.Layout is null,
            "The preflight rejection must return before any measurement or layout exists.");
        Require(generation.Solids.Count == 0 && generation.RequiredVoids.Count == 0 &&
                generation.AuthorizedDeckCuts.Count == 0,
            "A preflight-rejected neck must expose no partial solids, voids or deck cuts.");
        Require(generation.ClearVolumeBounds is null,
            "A preflight-rejected generation must expose no clear-volume bounds.");
        Require(stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"The preflight rejection must return immediately; took {stopwatch.Elapsed}.");
    }

    /// <summary>
    /// A valid generated neck must be exactly the direct Chebyshev perimeter per layer, in order,
    /// for every supported 1/3/5 m opening, and repeated generation must be identical.
    /// <para>
    /// This is an equivalence/determinism pin, not the old-behavior discriminator: the retired
    /// interior scan emitted the same z-then-x ring, so this test alone would also pass against the
    /// unwired generator while the helper exists. <see cref="VerifyLargeNeckFailsThroughGenerator"/>
    /// is the test that fails against the old unwired planning path.
    /// </para>
    /// </summary>
    private static void VerifyGeneratedNeckMatchesDirectPerimeter()
    {
        foreach (var neckSize in BarbetteDefinition.SupportedNeckClearSizesMetres)
        {
            var definition = Definition($"neck-{neckSize}", clear: 5, depth: 3, side: 1, neck: 1,
                neckSize: neckSize);
            var first = BarbetteGenerator.Generate(definition, DesignMeasure.Zero, DesignMeasure.Zero,
                11, 11);
            Require(first.IsValid, Details(first.Diagnostics));
            var second = BarbetteGenerator.Generate(definition, DesignMeasure.Zero, DesignMeasure.Zero,
                11, 11);
            Require(second.IsValid, Details(second.Diagnostics));

            var clearHalf = (neckSize - 1) / 2;
            var expected = new List<BarbettePlanCell>();
            for (var layer = 0; layer < definition.NeckArmorThicknessMetres; layer++)
            {
                var radius = clearHalf + 1 + layer;
                var ring = BarbettePlanPreflight.SquarePerimeter(0, 0, radius);
                Require(ring.Count == 8 * radius,
                    $"A {neckSize} m neck layer {layer} must be a full {8 * radius}-cell ring; " +
                    $"got {ring.Count}.");
                expected.AddRange(ring);
            }

            var measurement = first.Measurement!;
            Require(measurement.NeckArmorCells.SequenceEqual(expected),
                $"A {neckSize} m neck must emit exactly the direct Chebyshev perimeter per layer in order.");
            Require(second.Measurement!.NeckArmorCells.SequenceEqual(measurement.NeckArmorCells),
                $"Repeated generation of a {neckSize} m neck must be identical.");
            Require(second.Solids.Select(intent => (intent.Cell.X, intent.Cell.Y, intent.Cell.Z))
                    .SequenceEqual(first.Solids.Select(intent =>
                        (intent.Cell.X, intent.Cell.Y, intent.Cell.Z))),
                $"Repeated generation of a {neckSize} m neck must emit identical ordered solids.");
        }
    }

    /// <summary>
    /// The direct perimeter must visit exactly the Chebyshev ring, never the square interior, in a
    /// deterministic z-then-x order.
    /// </summary>
    private static void VerifyDirectSquarePerimeterShape()
    {
        var zero = BarbettePlanPreflight.SquarePerimeter(0, 0, 0);
        Require(zero.SequenceEqual([new BarbettePlanCell(0, 0)]),
            "A zero-radius perimeter is the single center cell.");

        var one = BarbettePlanPreflight.SquarePerimeter(0, 0, 1);
        Require(one.SequenceEqual(new[]
        {
            new BarbettePlanCell(-1, -1), new BarbettePlanCell(0, -1), new BarbettePlanCell(1, -1),
            new BarbettePlanCell(-1, 0), new BarbettePlanCell(1, 0),
            new BarbettePlanCell(-1, 1), new BarbettePlanCell(0, 1), new BarbettePlanCell(1, 1),
        }), "A radius-1 ring must be the eight literal neighbor cells in z-then-x order.");

        foreach (var radius in new[] { 2, 3, 7 })
        {
            const int centerX = 4;
            const int centerZ = -5;
            var ring = BarbettePlanPreflight.SquarePerimeter(centerX, centerZ, radius);
            Require(ring.Count == 8 * radius,
                $"A radius-{radius} ring must have exactly {8 * radius} cells; got {ring.Count}.");
            Require(ring.All(cell =>
                    Math.Max(Math.Abs(cell.X - centerX), Math.Abs(cell.Z - centerZ)) == radius),
                $"Every radius-{radius} ring cell must lie exactly on the Chebyshev ring.");
            Require(!ring.Any(cell =>
                    Math.Max(Math.Abs(cell.X - centerX), Math.Abs(cell.Z - centerZ)) < radius),
                "A direct perimeter walk must never visit the square interior.");
            Require(ring.Contains(new BarbettePlanCell(centerX - radius, centerZ - radius)) &&
                    ring.Contains(new BarbettePlanCell(centerX + radius, centerZ - radius)) &&
                    ring.Contains(new BarbettePlanCell(centerX - radius, centerZ + radius)) &&
                    ring.Contains(new BarbettePlanCell(centerX + radius, centerZ + radius)),
                $"A radius-{radius} ring must contain all four corners.");
            Require(ring.Select(cell => (cell.Z, cell.X)).SequenceEqual(
                    ring.Select(cell => (cell.Z, cell.X)).Order()),
                $"A radius-{radius} ring must be emitted in deterministic z-then-x order.");
        }

        var visited = new List<BarbettePlanCell>();
        BarbettePlanPreflight.EnumerateSquarePerimeter(-2, 3, 2, visited.Add);
        Require(visited.SequenceEqual(BarbettePlanPreflight.SquarePerimeter(-2, 3, 2)),
            "EnumerateSquarePerimeter must match SquarePerimeter cell for cell and in order.");
    }

    /// <summary>
    /// The preflight and both perimeter entry points must honor a cancellation token, including one
    /// raised part-way through a large ring walk.
    /// </summary>
    private static void VerifyCancellationHonored()
    {
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            Require(Throws<OperationCanceledException>(() =>
                    BarbettePlanPreflight.SquarePerimeter(0, 0, 4, cancelled.Token)),
                "SquarePerimeter must throw for a pre-cancelled token.");
            Require(Throws<OperationCanceledException>(() =>
                    BarbettePlanPreflight.EnumerateSquarePerimeter(0, 0, 4, _ => { }, cancelled.Token)),
                "EnumerateSquarePerimeter must throw for a pre-cancelled token.");
            Require(Throws<OperationCanceledException>(() =>
                    BarbettePlanPreflight.Preflight(10, 1, 3, 1, 250_000, cancelled.Token)),
                "The planar preflight must throw for a pre-cancelled token.");
        }

        using (var midWalk = new CancellationTokenSource())
        {
            var visited = 0;
            var observed = false;
            try
            {
                BarbettePlanPreflight.EnumerateSquarePerimeter(0, 0, 1000, _ =>
                {
                    visited++;
                    if (visited == 3)
                        midWalk.Cancel();
                }, midWalk.Token);
            }
            catch (OperationCanceledException)
            {
                observed = true;
            }

            Require(observed, "A perimeter walk must honor cancellation raised mid-walk.");
            Require(visited < 8 * 1000,
                "Cancellation must stop the perimeter walk rather than let it finish.");
        }
    }

    /// <summary>
    /// The public planning entry points must honor a pre-cancelled token before any plan work. The
    /// mid-walk helper cancellation above covers the new preflight/perimeter path; the deeper
    /// per-cell checks threaded through the neck build/validation loops are statically present but
    /// not separately behaviorally observable without a deterministic in-plan cancellation hook.
    /// </summary>
    private static void VerifyPlanningCancellationHonored()
    {
        var definition = Definition("cancel-plan", clear: 5, depth: 3, side: 1, neck: 1, neckSize: 3);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Require(Throws<OperationCanceledException>(() =>
                BarbetteGenerator.Measure(definition, DesignMeasure.Zero, DesignMeasure.Zero,
                    cancellationToken: cancelled.Token)),
            "Measure must throw for a pre-cancelled token.");
        Require(Throws<OperationCanceledException>(() =>
                BarbetteGenerator.Generate(definition, DesignMeasure.Zero, DesignMeasure.Zero, 11, 11,
                    cancellationToken: cancelled.Token)),
            "Local Generate must throw for a pre-cancelled token.");

        var context = HullGenerator.CreateContext(HullParameters.Default);
        Require(Throws<OperationCanceledException>(() =>
                BarbetteGenerator.Generate(definition, context.CenterPlaneX, DesignMeasure.Zero,
                    context, cancellationToken: cancelled.Token)),
            "Hull-reconciled Generate must throw for a pre-cancelled token.");
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

    private static bool Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
            return false;
        }
        catch (TException)
        {
            return true;
        }
    }

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
