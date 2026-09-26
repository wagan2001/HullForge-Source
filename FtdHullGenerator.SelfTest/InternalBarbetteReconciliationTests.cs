using System.Collections.Immutable;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Components;
using FtdHullGenerator.Domain.Composition;
using FtdHullGenerator.Domain.Design;
using FtdHullGenerator.Domain.Layout;
using FtdHullGenerator.Domain.Projects;
using FtdHullGenerator.Geometry;
using FtdHullGenerator.Geometry.Components;
using FtdHullGenerator.Geometry.Composition;
using FtdHullGenerator.Infrastructure;

/// <summary>
/// INT01 combined oracles: every experimental internal family must route around the complete
/// resolved barbette footprint (protected cavity, shaft, armor air, solid armor and authorized
/// apertures) without changing required barbette or hull geometry. Expected cells come from the
/// resolved snapshot and small literal masks, never from a second production plane enumerator.
/// </summary>
internal static class InternalBarbetteReconciliationTests
{
    public static void Run()
    {
        VerifyAllFamiliesRouteAroundZeroOffsetBarbette();
        VerifyFloatingBarbetteAndRoofFloorBoundaries();
        VerifyNeckTrunkAndDeckApertureStayClear();
        VerifyInternalFamilyIntersectionHasSingleOccupancy();
        VerifySymmetryAndDeterminism();
        VerifyInvalidExperimentalIntentFailsClosedWithoutMutatingBarbette();

        Console.WriteLine(
            "Internal/barbette reconciliation: all three families route around zero-offset and " +
            "floating barbettes, roof/floor boundaries, neck trunks and deck apertures, preserve " +
            "hull armor/air, keep one physical occupancy at internal junctions, stay symmetric and " +
            "deterministic, and fail closed with clear diagnostics passed.");
    }

    private static void VerifyAllFamiliesRouteAroundZeroOffsetBarbette()
    {
        var parameters = StandardParameters();
        var definition = Definition("zero", clear: 3, depth: 3, topOffset: 0);
        var internals = AllFamilies();
        var document = Document(parameters, definition, internals, "int01-zero");

        var result = new ShipGenerationService().Generate(document, 101, Catalog());
        Require(result.IsValid, Details(result.Diagnostics));
        var snapshot = result.Snapshot!;

        Require(snapshot.Internals.Planes.Select(plane => plane.Family).Distinct().Count() == 3 &&
                snapshot.Internals.Cells.Length > 0,
            "The combined fixture did not realize all three experimental internal families.");
        Require(snapshot.Internals.Planes.Any(plane => plane.RequiredVoidCellCount > 0),
            "The internal planes did not report clipping by the barbette reservation.");

        var footprint = Footprint(snapshot);
        var invasion = snapshot.Internals.Cells.FirstOrDefault(intent => footprint.Contains(intent.Cell));
        Require(invasion is null,
            "An experimental internal cell invaded the resolved barbette armor/shaft/aperture footprint " +
            $"at {invasion?.Cell}.");
        Require(snapshot.Internals.Cells.All(intent =>
                snapshot.HullContext.IsUsableCavity(intent.Cell.X, intent.Cell.Y, intent.Cell.Z)),
            "An experimental internal cell left the evaluated usable hull interior.");
        Require(snapshot.Internals.Cells.All(intent =>
                !snapshot.HullContext.IsProtectedShellCell(intent.Cell.X, intent.Cell.Y, intent.Cell.Z) &&
                !snapshot.HullContext.IsReservedArmorAir(intent.Cell.X, intent.Cell.Y, intent.Cell.Z)),
            "An experimental internal cell consumed protected hull skin or deliberate armor air.");

        AssertRequiredGeometryPreserved(snapshot, footprint, "zero-offset");
    }

    private static void VerifyFloatingBarbetteAndRoofFloorBoundaries()
    {
        var parameters = StandardParameters();
        var definition = Definition("floating", clear: 3, depth: 3, topOffset: 2);
        var document = Document(parameters, definition, AllFamilies(), "int01-floating");

        var result = new ShipGenerationService().Generate(document, 102, Catalog());
        Require(result.IsValid, Details(result.Diagnostics));
        var snapshot = result.Snapshot!;
        var layout = snapshot.Barbettes.Single().Layout!;
        Require(layout.TopOffsetMetres == 2 &&
                layout.RoofTopY == snapshot.HullContext.ReferenceDeckY - 3,
            "A floating barbette roof did not sit two metres below the ship-wide reference deck.");
        Require(layout.ClearVolumeMinY > snapshot.HullContext.FloorYAt(0) + 1,
            "The floating barbette cavity still reached the inner bottom instead of floating.");

        var footprint = Footprint(snapshot);
        Require(snapshot.Internals.Cells.All(intent => !footprint.Contains(intent.Cell)),
            "A floating barbette did not keep experimental internals out of its footprint.");

        // A deck requested exactly at the roof underside and exactly at the protected floor top
        // must still be clipped, not merged into the barbette armor.
        foreach (var (y, label) in new[]
                 {
                     (layout.RoofBottomY, "roof underside"),
                     (layout.FloorTopY, "protected floor top"),
                 })
        {
            var boundary = Document(parameters, definition,
                Structure(Deck(DesignMeasure.FromCellAnchor(y))), $"int01-boundary-{y}");
            var boundaryResult = new ShipGenerationService().Generate(boundary, 103, Catalog());
            Require(boundaryResult.IsValid, $"{label}: {Details(boundaryResult.Diagnostics)}");
            var boundarySnapshot = boundaryResult.Snapshot!;
            var boundaryFootprint = Footprint(boundarySnapshot);
            Require(boundarySnapshot.Internals.Cells.Length > 0 &&
                    boundarySnapshot.Internals.Cells.All(intent => !boundaryFootprint.Contains(intent.Cell)),
                $"A deck at the barbette {label} was not routed around the reserved boundary.");
            Require(boundarySnapshot.Internals.Planes.Any(plane => plane.RequiredVoidCellCount > 0),
                $"A deck at the barbette {label} did not report its reservation clipping.");
        }

        AssertRequiredGeometryPreserved(snapshot, footprint, "floating");
    }

    private static void VerifyNeckTrunkAndDeckApertureStayClear()
    {
        var parameters = StandardParameters() with
        {
            Length = 72,
            Shape = HullShapeSettings.Default with { Profile = new HullProfileSettings(4, 2, 3, 1) },
        };
        var context = HullGenerator.CreateContext(parameters);
        var raised = Enumerable.Range(context.MinZ, context.MaxZ - context.MinZ + 1)
            .Where(z => context.DeckYAt(z) > context.ReferenceDeckY)
            .OrderBy(z => Math.Abs(z))
            .FirstOrDefault(int.MinValue);
        Require(raised != int.MinValue, "The raised-profile fixture must expose a locally raised deck station.");

        var definition = Definition("trunk", clear: 3, depth: 3, topOffset: 0);
        var document = Document(parameters, definition, AllFamilies(), "int01-trunk",
            DesignMeasure.FromCellAnchor(raised));
        var result = new ShipGenerationService().Generate(document, 104, Catalog());
        Require(result.IsValid, Details(result.Diagnostics));
        var snapshot = result.Snapshot!;
        var generation = snapshot.Barbettes.Single();
        var layout = generation.Layout!;
        Require(layout.NeckTopY > context.ReferenceDeckY &&
                layout.NeckTopY == generation.Measurement!.NeckClearCells
                    .Concat(generation.Measurement.NeckArmorCells)
                    .Select(cell => cell.Z).Distinct().Max(context.DeckYAt),
            "The neck did not extend as a complete trunk to the highest local deck under its footprint.");

        var footprint = Footprint(snapshot);
        Require(snapshot.Internals.Cells.All(intent => !footprint.Contains(intent.Cell)),
            "A protruding neck trunk did not keep experimental internals out of its shaft and armor.");

        var shaft = snapshot.Barbettes.Single().RequiredVoids
            .Where(intent => intent.Cell.Y >= layout.RoofBottomY)
            .Select(intent => intent.Cell).ToHashSet();
        Require(shaft.Count > 0 && !snapshot.Internals.Cells.Any(intent => shaft.Contains(intent.Cell)),
            "An experimental internal cell reopened a protected neck-shaft cell.");
        var cuts = snapshot.Barbettes.Single().AuthorizedDeckCuts.Select(cut => cut.Cell).ToHashSet();
        Require(cuts.Count > 0 && !snapshot.Internals.Cells.Any(intent => cuts.Contains(intent.Cell)),
            "An experimental internal cell reopened an authorized deck/neck aperture.");

        AssertRequiredGeometryPreserved(snapshot, footprint, "neck-trunk");
    }

    private static void VerifyInternalFamilyIntersectionHasSingleOccupancy()
    {
        var parameters = StandardParameters();
        var definition = Definition("junction", clear: 3, depth: 3, topOffset: 0);
        // The crossing at (3, 5, 10) is deliberately clear of the centre barbette footprint.
        var internals = Structure(
            Longitudinal(count: 2, includeCenter: false, gapMetres: 5),
            Deck(DesignMeasure.FromMetres(5)),
            Transverse(count: 1, offset: DesignMeasure.FromMetres(10)));
        var document = Document(parameters, definition, internals, "int01-junction");

        var result = new ShipGenerationService().Generate(document, 105, Catalog());
        Require(result.IsValid, Details(result.Diagnostics));
        var snapshot = result.Snapshot!;

        var crossing = new HullCell(3, 5, 10);
        var intent = snapshot.Internals.Cells.SingleOrDefault(item => item.Cell == crossing);
        Require(intent is not null && intent.Contributions.Length == 3,
            "The all-family internal crossing did not retain all three contributing owners.");
        var crossingIntent = intent!;
        var blocksAtCrossing = snapshot.Hull.Blocks
            .Where(block => block.OccupiedCells.Any(cell =>
                cell.X == crossing.X && cell.Y == crossing.Y && cell.Z == crossing.Z)).ToArray();
        Require(blocksAtCrossing.Length == 1,
            "An internal-family intersection created duplicate physical occupancy.");
        var provenance = snapshot.Hull.CellProvenance.Single(item => item.Cell == crossing);
        Require(provenance.Owners.Count(owner => owner.Role == PhysicalCellRole.InternalStructure) >= 3 &&
                provenance.Material == crossingIntent.Material,
            "An internal-family intersection lost its reconciled ownership or material.");
        var occupied = OccupiedCells(snapshot);
        Require(occupied.Count == snapshot.Hull.Blocks.SelectMany(block => block.OccupiedCells).Count(),
            "The resolved plan contains duplicate occupancy after internal-family reconciliation.");
    }

    private static void VerifySymmetryAndDeterminism()
    {
        var parameters = StandardParameters();
        var definition = Definition("symmetric", clear: 3, depth: 3, topOffset: 0);
        var document = Document(parameters, definition, AllFamilies(), "int01-symmetric");

        var first = new ShipGenerationService().Generate(document, 106, Catalog());
        Require(first.IsValid, Details(first.Diagnostics));
        var snapshot = first.Snapshot!;
        foreach (var intent in snapshot.Internals.Cells)
        {
            var mirror = intent.Cell with { X = snapshot.HullContext.MirrorSum - intent.Cell.X };
            Require(snapshot.Internals.Cells.Any(other => other.Cell == mirror),
                $"The centre-symmetric experimental fixture is missing the mirror of {intent.Cell}.");
        }

        var second = new ShipGenerationService().Generate(document, 106, Catalog());
        Require(second.IsValid && snapshot.Hull.Blocks.SequenceEqual(second.Snapshot!.Hull.Blocks) &&
                snapshot.Hull.CellProvenance.Select(ProvenanceKey)
                    .SequenceEqual(second.Snapshot.Hull.CellProvenance.Select(ProvenanceKey)),
            "Equal document/revision/catalog inputs produced non-deterministic internals/barbette output.");
    }

    private static void VerifyInvalidExperimentalIntentFailsClosedWithoutMutatingBarbette()
    {
        var parameters = StandardParameters();
        var definition = Definition("diagnosed", clear: 3, depth: 3, topOffset: 0);
        // A fixed longitudinal count must be odd with a central plane and even without one; this
        // combination is a stable INT013 error. The experimental failure must stay diagnosed and
        // must never rewrite or partially materialize the required barbette.
        var invalid = Longitudinal(count: 2, includeCenter: true, gapMetres: 5);
        var document = Document(parameters, definition, Structure(invalid), "int01-diagnosed");

        var result = new ShipGenerationService().Generate(document, 107, Catalog());
        Require(!result.IsValid && result.Snapshot is null &&
                result.Diagnostics.Any(diagnostic =>
                    diagnostic.Code == InternalStructureDiagnosticCodes.FamilyConfigurationInvalid),
            "Invalid experimental intent was not diagnosed and failed closed: " + Details(result.Diagnostics));
        Require(document.Barbettes.SequenceEqual([definition]) &&
                document.Internals.Find(InternalPlaneFamily.LongitudinalBulkhead) == invalid,
            "A failed experimental generation mutated persisted barbette/internal intent.");
    }

    private static void AssertRequiredGeometryPreserved(
        ShipGenerationSnapshot snapshot,
        HashSet<HullCell> footprint,
        string label)
    {
        var occupied = OccupiedCells(snapshot);
        Require(snapshot.RequiredVoids.SelectMany(exclusion => exclusion.Cells).All(cell => !occupied.Contains(cell)),
            $"{label}: a required protected barbette void was occupied in the resolved plan.");
        Require(snapshot.Barbettes.SelectMany(entry => entry.Solids).All(solid => occupied.Contains(solid.Cell)),
            $"{label}: required barbette armor was dropped from the resolved plan.");
        Require(snapshot.Hull.CellProvenance.Where(item => footprint.Contains(item.Cell))
                .All(item => item.Owners.Any(owner => owner.Role == PhysicalCellRole.Barbette)),
            $"{label}: a barbette reservation cell lost its barbette ownership.");
        var cuts = snapshot.Barbettes.SelectMany(entry => entry.AuthorizedDeckCuts).Select(cut => cut.Cell)
            .ToHashSet();
        var missingArmor = snapshot.HullContext.EnumerateArmor()
            .Where(intent => intent.IsStructuralArmor && !cuts.Contains(intent.Cell))
            .Where(intent => !occupied.Contains(intent.Cell))
            .Select(intent => intent.Cell).FirstOrDefault();
        Require(missingArmor == default,
            $"{label}: required hull armor was consumed at ({missingArmor.X}, {missingArmor.Y}, {missingArmor.Z}).");
        Require(snapshot.HullContext.EnumerateArmor().Where(intent => intent.IsReservedAir)
                .All(intent => !occupied.Contains(intent.Cell)),
            $"{label}: deliberate hull armor air was filled.");
        Require(occupied.Count == snapshot.Hull.Blocks.SelectMany(block => block.OccupiedCells).Count(),
            $"{label}: the resolved plan contains duplicate physical occupancy.");
    }

    private static HashSet<HullCell> Footprint(ShipGenerationSnapshot snapshot) =>
        snapshot.Barbettes.SelectMany(entry =>
                entry.Solids.Select(solid => solid.Cell)
                    .Concat(entry.RequiredVoids.Select(voidIntent => voidIntent.Cell))
                    .Concat(entry.AuthorizedDeckCuts.Select(cut => cut.Cell)))
            .ToHashSet();

    private static HashSet<HullCell> OccupiedCells(ShipGenerationSnapshot snapshot) =>
        snapshot.Hull.Blocks.SelectMany(block => block.OccupiedCells)
            .Select(cell => new HullCell(cell.X, cell.Y, cell.Z)).ToHashSet();

    private static string ProvenanceKey(PhysicalCellProvenance item) =>
        $"{item.Cell.X},{item.Cell.Y},{item.Cell.Z}|{item.Material}|" +
        string.Join(",", item.Owners.Select(owner => $"{owner.Role}:{owner.OwnerId}"));

    private static HullParameters StandardParameters() => HullParameters.Default with
    {
        Smoothing = SmoothingMethod.None,
        Superstructure = SuperstructureSettings.Default with { Enabled = false },
    };

    private static BarbetteDefinition Definition(string id, double clear, int depth, int topOffset) =>
        BarbetteDefinition.Create($"barbette-{id}", $"node-barbette-{id}",
            DesignMeasure.FromMetres(clear), depth, topOffset, neckClearSizeMetres: 3);

    private static ShipDocument Document(
        HullParameters parameters,
        BarbetteDefinition definition,
        InternalStructure internals,
        string documentId,
        DesignMeasure centerZ = default)
    {
        var context = HullGenerator.CreateContext(parameters);
        var stations = context.EnumerateArmor()
            .Where(intent => intent.IsStructuralArmor && intent.Region == ArmorRegion.Deck)
            .Select(intent => intent.Cell.Z).Distinct().Order().ToArray();
        Require(stations.Length > 0, "The INT01 fixture needs a supported structural deck interval.");
        var bowDatum = DesignMeasure.FromTwiceMetres(checked(stations[^1] * 2 + 1));
        var measured = BarbetteGenerator.Measure(definition, context.CenterPlaneX, centerZ);
        Require(measured.Measurement is not null, Details(measured.Diagnostics));
        var half = measured.Measurement!.LongitudinalHalfExtent;
        var node = ArrangementNode.Create(definition.NodeId, ArrangementNodeKind.Barbette,
            definition.Id, half);
        var arrangement = new Arrangement([node], [], [],
            bowDatum - centerZ - half, DesignMeasure.Zero,
            ArrangementAnchorKind.BowDatum, ArrangementResizePolicy.PreserveAbsolute);
        return ShipDocument.CreateNew("INT01", parameters, documentId) with
        {
            Arrangement = arrangement,
            Datum = new LayoutDatum(bowDatum, DesignMeasure.Zero),
            Barbettes = [definition],
            Internals = internals,
        };
    }

    private static InternalStructure AllFamilies() => Structure(
        Longitudinal(count: 1, includeCenter: true, gapMetres: 6),
        Deck(DesignMeasure.FromMetres(5)),
        Transverse(count: 1, offset: DesignMeasure.Zero));

    private static InternalStructure Structure(params InternalStructureFamily[] enabled)
    {
        var byFamily = enabled.ToDictionary(family => family.Family);
        return new InternalStructure(Enum.GetValues<InternalPlaneFamily>()
            .Select(family => byFamily.TryGetValue(family, out var settings)
                ? settings
                : InternalStructureFamily.Disabled(family)).ToImmutableArray());
    }

    private static InternalStructureFamily Longitudinal(int count, bool includeCenter, int gapMetres) =>
        new(InternalPlaneFamily.LongitudinalBulkhead, true, 1, MaterialKind.Metal,
            DesignMeasure.FromMetres(gapMetres), count, DesignMeasure.Zero, true)
        {
            SpacingKind = InternalSpacingKind.ClearCompartmentGap,
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

    private static FtdBlockCatalog Catalog()
    {
        var root = Path.Combine(Path.GetTempPath(), "HullForge-INT01-EmptyCatalog");
        Directory.CreateDirectory(Path.Combine(root, "From_The_Depths_Data", "StreamingAssets"));
        return FtdBlockCatalog.Load(root);
    }

    private static string Details(IEnumerable<DesignDiagnostic> diagnostics) =>
        string.Join(" | ", diagnostics.Select(diagnostic => diagnostic.ToString()));

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
