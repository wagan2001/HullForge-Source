using System.Collections.Immutable;
using System.Text.Json;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Components;
using FtdHullGenerator.Domain.Composition;
using FtdHullGenerator.Domain.Design;
using FtdHullGenerator.Domain.Layout;
using FtdHullGenerator.Domain.Projects;
using FtdHullGenerator.Domain.Superstructures;
using FtdHullGenerator.Geometry;
using FtdHullGenerator.Geometry.Composition;
using FtdHullGenerator.Geometry.Layout;
using FtdHullGenerator.Infrastructure;
using FtdHullGenerator.Serialization;

/// <summary>
/// Independent I01 integration oracles. Expected apertures and intersections are enumerated from
/// small literal masks; feature-free parity compares expanded physical cells, not packing order.
/// </summary>
internal static class ShipGenerationServiceTests
{
    public static void Run()
    {
        VerifyFeatureFreeParityAndCatalogSnapshot();
        VerifyUnavailableRefinementFailsClosed();
        VerifySupportedDeckDatumAndRulerInterval();
        VerifyInstalledCatalogNativeSmoothingParity();
        VerifyFinalCatalogSmoothingBlockersDiscardWholePlacements();
        VerifyLayeredBeamCutsWellExclusionAndProvenance();
        VerifyDifferentMaterialInternalsYieldToBarbette();
        VerifyAsymmetricComponentMaterialIsNotTreatedAsBareHull();
        VerifyAsymmetricSpanExactGapAndWellSeparation();
        VerifyEvenWidthBarbetteFailsClosed();
        VerifyRootHandleConstraintRejection();
        VerifyCancellationAndPhysicalBudget();
        VerifyCurrentRevisionExportRoundTrip();

        Console.WriteLine(
            "Ship composition: measured arrangement, layered packed-deck cuts/remnants, required-well " +
            "exclusions, component provenance/compartments, module conflicts, feature-free parity, " +
            "odd-width centerline-barbette enforcement, " +
            "installed-catalog native smoothing/blockers, catalog snapshot, cancellation/budgets " +
            "and current-revision export passed.");
    }

    private static void VerifyFeatureFreeParityAndCatalogSnapshot()
    {
        var parameters = HullParameters.Default with
        {
            Smoothing = SmoothingMethod.HorizontalSlopeFill,
        };
        var document = ShipDocument.FromLegacyParameters(parameters, "Parity", "i01-parity");
        var expected = new HullGenerator().Generate(parameters);
        var result = new ShipGenerationService().Generate(document, 7, Catalog());
        Require(result.IsValid, Describe(result.Diagnostics));
        var snapshot = result.Snapshot!;

        Require(CellMaterials(expected).SetEquals(CellMaterials(snapshot.Hull)),
            "Feature-free composition changed expanded occupied cells or materials.");
        Require(snapshot.Hull.SourceRevision == 7 && snapshot.Hull.SourceDocumentId == document.DocumentId,
            "The resolved hull did not retain its immutable document/revision identity.");
        Require(snapshot.Hull.CellProvenance.Count == snapshot.Hull.OccupiedCellCount &&
                snapshot.Hull.CellProvenance.Select(item => item.Cell).Distinct().Count() ==
                snapshot.Hull.OccupiedCellCount,
            "Every feature-free occupied metre needs exactly one cell-level provenance record.");
        Require(snapshot.Hull.Blocks.All(block => block.Shape == BlockShape.Cube),
            "The empty test catalog must be resolved into the exact conservative cube preview snapshot.");
    }

    private static void VerifyInstalledCatalogNativeSmoothingParity()
    {
        var catalog = InstalledCatalog();
        foreach (var method in new[]
                 {
                     SmoothingMethod.VerticalSlopeFill,
                     SmoothingMethod.HorizontalSlopeFill,
                     SmoothingMethod.HybridSlopeFill,
                 })
        {
            var parameters = HullParameters.Default with
            {
                Length = 40,
                Width = 15,
                Height = 10,
                Smoothing = method,
                HybridFillOffset = 3,
                Superstructure = null,
            };
            var expected = new HullGenerator().Generate(parameters);
            var document = ShipDocument.FromLegacyParameters(parameters,
                $"Native {method}", $"i01-native-{(int)method}");
            var result = new ShipGenerationService().Generate(document, 8, catalog);
            Require(result.IsValid, Describe(result.Diagnostics));

            var expectedNative = NativeSmoothing(expected);
            var actualNative = NativeSmoothing(result.Snapshot!.Hull);
            Require(expectedNative.Count > 0 && expectedNative.SequenceEqual(actualNative),
                $"{method}: installed-catalog composition changed a native smoothing shape/length, " +
                "anchor, rotation, material or origin.");
        }
    }

    private static void VerifySupportedDeckDatumAndRulerInterval()
    {
        var centered = BarbetteDocument(includeInternals: false);
        var centeredResult = new ShipGenerationService().Generate(centered, 9, Catalog());
        Require(centeredResult.IsValid &&
                centeredResult.Snapshot!.Arrangement.SupportedRulerEnd == DesignMeasure.FromMetres(91) &&
                centeredResult.Snapshot.Arrangement.Nodes.Single().WorldZCenter == DesignMeasure.Zero,
            "The centered default hull did not use its exact 40.5..-50.5 supported deck interval. " +
            Describe(centeredResult.Diagnostics));

        var defaultDatum = centered with { Datum = LayoutDatum.Origin };
        var defaultRejected = new ShipGenerationService().Generate(defaultDatum, 9, Catalog());
        Require(!defaultRejected.IsValid && defaultRejected.Snapshot is null &&
                defaultRejected.Diagnostics.Any(diagnostic =>
                    diagnostic.Code == ShipCompositionDiagnosticCodes.LayoutDatumUnsupported &&
                    diagnostic.Requested == DesignMeasure.Zero &&
                    diagnostic.Realized == DesignMeasure.FromMetres(40.5)),
            "A persisted HullOrigin/default-zero datum must not masquerade as the forward supported deck edge.");

        var sternOverrun = centered with
        {
            Arrangement = centered.Arrangement with { BowMargin = DesignMeasure.FromMetres(88) },
        };
        var overrunRejected = new ShipGenerationService().Generate(sternOverrun, 9, Catalog());
        Require(!overrunRejected.IsValid && overrunRejected.Snapshot is null &&
                overrunRejected.Diagnostics.Any(diagnostic =>
                    diagnostic.Code == ArrangementDiagnosticCodes.ChainExceedsSupportedLength &&
                    diagnostic.Realized == DesignMeasure.FromMetres(91)),
            "The centered hull ruler still treated unsupported space aft of -50.5 m as usable deck.");

        var shortHull = centered.Hull with { Length = 40 };
        var custom = centered with
        {
            Hull = shortHull,
            Datum = new LayoutDatum(DesignMeasure.FromMetres(15.5), DesignMeasure.Zero),
            Arrangement = centered.Arrangement with { BowMargin = DesignMeasure.FromMetres(13) },
        };
        var customResult = new ShipGenerationService().Generate(custom, 10, Catalog());
        Require(customResult.IsValid &&
                customResult.Snapshot!.Arrangement.SupportedRulerEnd == DesignMeasure.FromMetres(36) &&
                customResult.Snapshot.Arrangement.Nodes.Single().WorldZCenter == DesignMeasure.Zero,
            "The custom 40 m hull did not derive its exact 15.5..-20.5 supported deck interval.");

        var staleCustom = custom with { Datum = centered.Datum };
        var staleRejected = new ShipGenerationService().Generate(staleCustom, 10, Catalog());
        Require(!staleRejected.IsValid && staleRejected.Diagnostics.Any(diagnostic =>
                diagnostic.Code == ShipCompositionDiagnosticCodes.LayoutDatumUnsupported &&
                diagnostic.Realized == DesignMeasure.FromMetres(15.5)),
            "A datum copied from a differently sized hull was not rejected against local deck support.");
    }

    private static void VerifyFinalCatalogSmoothingBlockersDiscardWholePlacements()
    {
        var componentBlocked = new BlockPlacement(BlockShape.Slope4, MaterialKind.Metal, 0, 0, 0, 0)
        {
            Origin = BlockOrigin.Smoothing,
        };
        var wellBlocked = new BlockPlacement(BlockShape.Slope3, MaterialKind.Metal, 2, 0, 0, 0)
        {
            Origin = BlockOrigin.Smoothing,
        };
        var retained = new BlockPlacement(BlockShape.Slope2, MaterialKind.Metal, 4, 0, 0, 0)
        {
            Origin = BlockOrigin.Smoothing,
        };
        var source = new GeneratedHull(HullParameters.Default,
            [componentBlocked, wellBlocked, retained], 0, 4, 0, 0, 0, 3);
        var componentBlockers = new HashSet<HullCell> { new(0, 0, 2) };
        var wellBlockers = new HashSet<HullCell> { new(2, 0, 1) };
        var blockers = componentBlockers.Concat(wellBlockers).ToHashSet();

        var (resolved, fallbackCount) = ShipGenerationService.ResolveCatalog(
            source, InstalledCatalog(), CancellationToken.None, blockers);
        Require(fallbackCount == 0 && resolved.Blocks.SequenceEqual(new[] { retained }),
            "Final installed-catalog resolution did not discard each complete native smoothing " +
            "placement intersecting a component/well blocker, or truncated one into replacement cubes.");
        Require(resolved.Blocks.SelectMany(block => block.OccupiedCells).All(cell =>
                !blockers.Contains(new HullCell(cell.X, cell.Y, cell.Z))),
            "A component/well blocker was reoccupied during final catalog materialization.");
    }

    private static void VerifyLayeredBeamCutsWellExclusionAndProvenance()
    {
        var document = BarbetteDocument(includeInternals: true);
        document = document with
        {
            Hull = document.Hull with
            {
                DeckArmor = new ArmorLayout([
                    new ArmorLayer(MaterialKind.Wood),
                    new ArmorLayer(MaterialKind.Lead, ArmorConstruction.Pole),
                    new ArmorLayer(MaterialKind.LightweightAlloy, ArmorConstruction.BeamSlopeUp),
                ]),
            },
        };
        var result = new ShipGenerationService().Generate(document, 11, InstalledCatalog());
        Require(result.IsValid, Describe(result.Diagnostics));
        var snapshot = result.Snapshot!;
        var barbette = snapshot.Barbettes.Single();
        Require(barbette.AuthorizedDeckCuts.Count == 75,
            $"The literal 5x5 neck through three deck layers needs 75 cuts, not {barbette.AuthorizedDeckCuts.Count}.");

        var occupied = snapshot.Hull.Blocks.SelectMany(block => block.OccupiedCells)
            .Select(cell => new HullCell(cell.X, cell.Y, cell.Z)).ToHashSet();
        var materialAt = snapshot.Hull.Blocks.SelectMany(block => block.OccupiedCells.Select(cell =>
                (Cell: new HullCell(cell.X, cell.Y, cell.Z), block.Material)))
            .ToDictionary(item => item.Cell, item => item.Material);
        var cuts = barbette.AuthorizedDeckCuts.Select(intent => intent.Cell).ToHashSet();
        var voids = barbette.RequiredVoids.Select(intent => intent.Cell).ToHashSet();
        Require(!occupied.Overlaps(voids),
            "A packed deck remnant, internal wall or component reoccupied a required clear-volume cell.");
        var neckSolids = barbette.Solids.Select(intent => intent.Cell).ToHashSet();
        Require(cuts.All(cut => !occupied.Contains(cut) || neckSolids.Contains(cut)),
            "An authorized deck cut must be empty or refilled by the barbette neck armor, never left to another owner.");

        foreach (var layerY in cuts.Select(cell => cell.Y).Distinct())
        {
            Require(occupied.Contains(new HullCell(0, layerY, -4)) &&
                    occupied.Contains(new HullCell(0, layerY, 4)),
                "Cutting the center neck footprint did not retain independently enumerated deck cells around it.");
        }
        var packedDeck = FeatureFreeHullCompositionAdapter.Compose(snapshot.HullContext).Blocks
            .Where(block => block.CellLength > 1 && block.Origin == BlockOrigin.Shell &&
                            block.OccupiedCells.Any(cell => cuts.Contains(
                                new HullCell(cell.X, cell.Y, cell.Z))))
            .ToArray();
        Require(packedDeck.Length > 0 && packedDeck.Select(block => block.Material).Distinct().Count() == 3,
            "The layered aperture fixture did not intersect native packed members in all three deck materials.");
        Require(packedDeck.All(block => block.OccupiedCells
                .Select(cell => new HullCell(cell.X, cell.Y, cell.Z)).Where(cell => !cuts.Contains(cell))
                .All(cell => materialAt.TryGetValue(cell, out var material) && material == block.Material)),
            "Unpacking a cut deck member lost or rematerialized a compatible survivor cell.");
        var lowerPoleMembers = packedDeck.Where(block => block.Material == MaterialKind.Lead &&
            BlockShapeMetadata.Get(block.Shape).Family == StructuralFamily.Pole).ToArray();
        var finalOwnerAt = snapshot.Hull.Blocks.SelectMany(block => block.OccupiedCells.Select(cell =>
                (Cell: new HullCell(cell.X, cell.Y, cell.Z), Block: block)))
            .ToDictionary(item => item.Cell, item => item.Block);
        Require(lowerPoleMembers.Length > 0 &&
                lowerPoleMembers.SelectMany(block => block.OccupiedCells)
                    .Select(cell => new HullCell(cell.X, cell.Y, cell.Z))
                    .Where(cell => !cuts.Contains(cell)).All(cell =>
                        finalOwnerAt.TryGetValue(cell, out var survivor) && survivor.UsePoles &&
                        survivor.Construction == ArmorConstruction.Pole &&
                        BlockShapeMetadata.Get(survivor.Shape).Family == StructuralFamily.Pole),
            "A lower Lead Pole cut survivor lost its pole construction/family during rematerialization.");
        var deeperSlopeMembers = packedDeck.Where(block => block.Material == MaterialKind.LightweightAlloy &&
            BlockShapeMetadata.Get(block.Shape).Family == StructuralFamily.BeamSlope).ToArray();
        Require(deeperSlopeMembers.Length > 0 &&
                deeperSlopeMembers.SelectMany(block => block.OccupiedCells)
                    .Select(cell => new HullCell(cell.X, cell.Y, cell.Z))
                    .Where(cell => !cuts.Contains(cell)).All(cell =>
                        finalOwnerAt.TryGetValue(cell, out var survivor) &&
                        survivor.Material == MaterialKind.LightweightAlloy &&
                        (BlockShapeMetadata.Get(survivor.Shape).Family == StructuralFamily.BeamSlope
                            ? survivor.Construction == ArmorConstruction.BeamSlopeUp
                            : survivor.Shape == BlockShape.Cube)),
            "A deeper LightweightAlloy beam-slope cut survivor lost its material or degraded into an " +
            "incompatible shape during rematerialization.");
        Require(snapshot.Internals.Cells.Length > 0 && snapshot.Internals.Planes.Any(plane =>
                plane.RequiredVoidCellCount > 0),
            "The center bulkhead did not report and yield to the protected barbette well.");

        // INT01: experimental internals route around the complete resolved barbette footprint, not
        // just its protected clear volume, so no internal cell may coincide with barbette armor or
        // an authorized aperture.
        var barbetteFootprint = snapshot.Barbettes.SelectMany(item =>
                item.Solids.Select(solid => solid.Cell)
                    .Concat(item.RequiredVoids.Select(voidIntent => voidIntent.Cell))
                    .Concat(item.AuthorizedDeckCuts.Select(cut => cut.Cell)))
            .ToHashSet();
        Require(snapshot.Internals.Cells.All(intent => !barbetteFootprint.Contains(intent.Cell)),
            "An experimental internal cell invaded the resolved barbette armor/shaft/aperture footprint.");
        Require(snapshot.Internals.IsValid,
            "Intentional internal compartments must not trigger the bare-hull single-cavity rule.");

        var repeated = new ShipGenerationService().Generate(document, 11, InstalledCatalog());
        Require(repeated.IsValid && snapshot.Hull.Blocks.SequenceEqual(repeated.Snapshot!.Hull.Blocks) &&
                snapshot.Hull.CellProvenance.Select(ProvenanceKey).SequenceEqual(
                    repeated.Snapshot.Hull.CellProvenance.Select(ProvenanceKey)),
            "Equal document/revision/catalog inputs did not produce deterministic placements and provenance.");
    }

    private static void VerifyAsymmetricSpanExactGapAndWellSeparation()
    {
        var document = AsymmetricSuperstructureDocument();

        var result = new ShipGenerationService().Generate(document, 12, Catalog());
        Require(result.IsValid, Describe(result.Diagnostics));
        var snapshot = result.Snapshot!;
        var span = snapshot.Arrangement.FindNode("span")!;
        var root = span.Handles.Single(handle => handle.TargetId == "tower-root");
        var superCells = snapshot.Superstructures.Single().Cells.Select(intent => intent.Cell).ToArray();
        Require(span.OuterHalfExtent == DesignMeasure.FromMetres(3.5) &&
                span.WorldZCenter == DesignMeasure.FromMetres(-11) &&
                root.WorldZPosition == DesignMeasure.FromMetres(-10) &&
                snapshot.Arrangement.Gaps.Single().RealizedClearGap == DesignMeasure.FromMetres(5),
            "The asymmetric S01 footprint center offset did not preserve its exact solved interval/root/gap.");
        Require(superCells.Min(cell => cell.Z) == -14 && superCells.Max(cell => cell.Z) == -8,
            "The asymmetric module union shifted outside its solved [-14.5, -7.5] m interval.");
        Require(DesignMeasure.FromTwiceMetres(superCells.Min(cell => cell.Z) * 2 - 1) ==
                    span.WorldZCenter - span.OuterHalfExtent &&
                DesignMeasure.FromTwiceMetres(superCells.Max(cell => cell.Z) * 2 + 1) ==
                    span.WorldZCenter + span.OuterHalfExtent,
            "The asymmetric physical union did not exactly occupy the measured solved interval.");
        var barbetteCells = snapshot.Barbettes.Single().Solids.Select(intent => intent.Cell).ToArray();
        Require(barbetteCells.Min(cell => cell.Z) - superCells.Max(cell => cell.Z) - 1 == 5,
            "The five-metre arrangement gap was not the exact physical edge-to-edge clearance.");
        var well = snapshot.RequiredVoids.SelectMany(exclusion => exclusion.Cells).ToHashSet();
        Require(!well.Overlaps(superCells),
            "A correctly measured five-metre component gap still allowed the modular shell into the barbette well.");
    }

    private static void VerifyRootHandleConstraintRejection()
    {
        var valid = AsymmetricSuperstructureDocument();
        var validSpan = valid.Arrangement.FindNode("span")!;
        var validRoot = valid.Superstructure.TowerRoots.Single();

        var disagreeing = valid with
        {
            Superstructure = valid.Superstructure with
            {
                TowerRoots = [validRoot with { OffsetFromSpanCenter = DesignMeasure.Zero }],
            },
        };
        RequireRootConstraintRejection(disagreeing,
            "Different persisted handle/root offsets were accepted or silently reconciled.");

        var wrongMeasuredCenter = valid with
        {
            Arrangement = valid.Arrangement with
            {
                Nodes = valid.Arrangement.Nodes.Select(node => node.Id == "span"
                    ? node with
                    {
                        Handles = [node.Handles.Single() with { OffsetFromNodeCenter = DesignMeasure.Zero }],
                    }
                    : node).ToImmutableArray(),
            },
            Superstructure = valid.Superstructure with
            {
                TowerRoots = [validRoot with { OffsetFromSpanCenter = DesignMeasure.Zero }],
            },
        };
        RequireRootConstraintRejection(wrongMeasuredCenter,
            "A matching root/handle that displaced the measured physical union was accepted.");

        var missing = valid with
        {
            Arrangement = valid.Arrangement with
            {
                Nodes = valid.Arrangement.Nodes.Select(node => node.Id == "span"
                    ? node with { Handles = [] }
                    : node).ToImmutableArray(),
            },
        };
        RequireRootConstraintRejection(missing,
            "A declared superstructure root without an arrangement handle was accepted.");

        var duplicate = valid with
        {
            Arrangement = valid.Arrangement with
            {
                Nodes = valid.Arrangement.Nodes.Select(node => node.Id == "span"
                    ? node with
                    {
                        Handles =
                        [
                            validSpan.Handles.Single(),
                            new ArrangementHandle("tower-root-alias", "Duplicate tower root",
                                DesignMeasure.FromMetres(-1), "tower-root"),
                        ],
                    }
                    : node).ToImmutableArray(),
            },
        };
        RequireRootConstraintRejection(duplicate,
            "Two arrangement handles targeting one superstructure root were accepted.");
    }

    private static void RequireRootConstraintRejection(ShipDocument document, string message)
    {
        var result = new ShipGenerationService().Generate(document, 121, Catalog());
        Require(!result.IsValid && result.Snapshot is null && result.Diagnostics.Any(diagnostic =>
                diagnostic.Code == ShipCompositionDiagnosticCodes.RootHandleConstraintMismatch &&
                diagnostic.NodeId == "span" && diagnostic.AffectedBounds is not null),
            message + " " + Describe(result.Diagnostics));
    }

    private static void VerifyAsymmetricComponentMaterialIsNotTreatedAsBareHull()
    {
        var parameters = HullParameters.Default with
        {
            Smoothing = SmoothingMethod.None,
            Superstructure = SuperstructureSettings.Default with { Enabled = false },
        };
        var node = ArrangementNode.Create("span", ArrangementNodeKind.SuperstructureSpan, "tower",
            DesignMeasure.FromMetres(1),
            new ArrangementHandle("tower-root", "Tower root", DesignMeasure.Zero, "tower-root"));
        var module = new SuperstructureBoxModule("offset-box", DesignMeasure.Zero,
            DesignMeasure.FromMetres(2), DesignMeasure.FromMetres(5), DesignMeasure.FromMetres(5),
            2, 1, 1, MaterialKind.Stone);
        var document = ShipDocument.CreateNew("Asymmetric", parameters, "i01-asymmetric") with
        {
            Datum = new LayoutDatum(DesignMeasure.FromMetres(40.5), DesignMeasure.Zero),
            Arrangement = new Arrangement([node], [], [], DesignMeasure.FromMetres(38), DesignMeasure.Zero,
                ArrangementAnchorKind.BowDatum, ArrangementResizePolicy.PreserveAbsolute),
            Superstructure = new SuperstructureLayout(true,
                [new SuperstructureLayer("layer-1", 1, [module])],
                [new SuperstructureTowerRoot("tower-root", "span", DesignMeasure.Zero)],
                LegacySuperstructureSettings.None),
        };

        var result = new ShipGenerationService().Generate(document, 13, Catalog());
        Require(result.IsValid, Describe(result.Diagnostics));
        var modular = result.Snapshot!.Hull.CellProvenance.Where(item => item.Owners.Any(owner =>
            owner.Role == PhysicalCellRole.ModularSuperstructure)).ToArray();
        Require(modular.Length > 0 && modular.All(item => item.Material == MaterialKind.Stone),
            "An off-centre modular material absent from hull armor layouts lost its physical provenance.");
        var allCells = result.Snapshot.Hull.Blocks.SelectMany(block => block.OccupiedCells)
            .Select(cell => new HullCell(cell.X, cell.Y, cell.Z)).ToHashSet();
        Require(modular.Any(item => !allCells.Contains(new HullCell(-item.Cell.X, item.Cell.Y, item.Cell.Z))),
            "The asymmetric modular fixture did not exercise the composition-specific topology path.");
    }

    private static void VerifyDifferentMaterialInternalsYieldToBarbette()
    {
        var document = BarbetteDocument(includeInternals: true);
        var families = document.Internals.Families.Select(family =>
                family.Family == InternalPlaneFamily.LongitudinalBulkhead
                    ? family with { Material = MaterialKind.Lead }
                    : family)
            .ToImmutableArray();
        document = document with { Internals = document.Internals with { Families = families } };

        // INT01: the experimental family yields to the resolved barbette footprint. Before the
        // reconciliation pass a different-material junction rejected the whole required revision
        // with CMP105, so an experimental setting could block a valid barbette composition.
        var result = new ShipGenerationService().Generate(document, 14, Catalog());
        Require(result.IsValid && result.Snapshot is not null,
            "A different-material internal family must yield to the barbette footprint, not block " +
            "required composition: " + Describe(result.Diagnostics));
        var snapshot = result.Snapshot!;
        var footprint = snapshot.Barbettes.SelectMany(item => item.Solids.Select(solid => solid.Cell))
            .ToHashSet();
        Require(snapshot.Internals.Cells.All(intent => !footprint.Contains(intent.Cell)),
            "A different-material internal family still invaded the barbette armor footprint.");
        Require(snapshot.Internals.Cells.Any(intent => intent.Material == MaterialKind.Lead),
            "The Lead internal family material was silently downgraded instead of being routed around the barbette.");
        var occupied = snapshot.Hull.Blocks.SelectMany(block => block.OccupiedCells)
            .Select(cell => new HullCell(cell.X, cell.Y, cell.Z)).ToHashSet();
        Require(footprint.All(occupied.Contains),
            "Yielding internals removed required barbette armor from the resolved physical plan.");
        Require(snapshot.Hull.CellProvenance.Where(item => footprint.Contains(item.Cell))
                .All(item => item.Owners.Any(owner => owner.Role == PhysicalCellRole.Barbette)),
            "A barbette armor cell lost its barbette ownership after experimental internals yielded.");
    }

    private static void VerifyCancellationAndPhysicalBudget()
    {
        var document = ShipDocument.FromLegacyParameters(HullParameters.Default, "Bounds", "i01-bounds");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var threw = false;
        try
        {
            new ShipGenerationService().Generate(document, 1, Catalog(), cancellationToken: cancelled.Token);
        }
        catch (OperationCanceledException)
        {
            threw = true;
        }
        Require(threw, "A pre-cancelled revision must expose no partial composition result.");

        var limited = new ShipGenerationService().Generate(document, 2, Catalog(),
            new ShipGenerationOptions { MaxPhysicalCells = 1 });
        Require(!limited.IsValid && limited.Snapshot is null && limited.Diagnostics.Any(diagnostic =>
                diagnostic.Code == ShipCompositionDiagnosticCodes.PhysicalBudgetExceeded),
            "The physical-cell cap must reject atomically before returning a snapshot.");

        var duplicateNode = BarbetteDocument(includeInternals: false);
        duplicateNode = duplicateNode with
        {
            Barbettes = duplicateNode.Barbettes.Add(duplicateNode.Barbettes[0] with { Id = "barbette-2" }),
        };
        var duplicateRejected = new ShipGenerationService().Generate(duplicateNode, 3, Catalog());
        Require(!duplicateRejected.IsValid && duplicateRejected.Snapshot is null &&
                duplicateRejected.Diagnostics.Any(diagnostic =>
                    diagnostic.Code == ShipCompositionDiagnosticCodes.ComponentNodeMismatch),
            "Two component definitions claiming one arrangement node must reject diagnostically, not throw.");
    }

    private static void VerifyCurrentRevisionExportRoundTrip()
    {
        var document = ShipDocument.FromLegacyParameters(HullParameters.Default with
        {
            Smoothing = SmoothingMethod.None,
        }, "Export", "i01-export");
        var result = new ShipGenerationService().Generate(document, 23, InstalledCatalog());
        Require(result.IsValid, Describe(result.Diagnostics));
        var snapshot = result.Snapshot!;
        var root = Path.Combine(Path.GetTempPath(), $"HullForge-I01-{Guid.NewGuid():N}");
        var staleRoot = root + "-stale";
        try
        {
            var staleRejected = false;
            try
            {
                new BlueprintExporter().Export(snapshot, document, 24, staleRoot, "stale");
            }
            catch (InvalidOperationException)
            {
                staleRejected = true;
            }
            Require(staleRejected && !Directory.Exists(staleRoot),
                "Export must reject a stale revision before creating output state.");

            var wrongDocumentRejected = false;
            try
            {
                var wrongDocument = document with { DocumentId = "i01-other-document" };
                new BlueprintExporter().Export(snapshot, wrongDocument, 23, staleRoot, "wrong-document");
            }
            catch (InvalidOperationException)
            {
                wrongDocumentRejected = true;
            }
            Require(wrongDocumentRejected && !Directory.Exists(staleRoot),
                "Export must reject a resolved snapshot from another document even at the same revision.");

            var swappedCatalog = snapshot with { Catalog = Catalog() };
            Require(swappedCatalog.Catalog.GameVersion == snapshot.Catalog.GameVersion,
                "The swapped-catalog regression requires two distinct catalogs claiming the same game version.");
            var swappedRejected = false;
            try
            {
                new BlueprintExporter().Export(swappedCatalog, document, 23, staleRoot, "swapped-catalog");
            }
            catch (InvalidOperationException)
            {
                swappedRejected = true;
            }
            Require(swappedRejected && !Directory.Exists(staleRoot),
                "A same-version empty catalog swap must be rejected before it can expand the resolved native beams.");

            var export = new BlueprintExporter().Export(snapshot, document, 23, root, "current");
            Require(export.BlockCount == snapshot.Hull.BlockCount &&
                    export.OccupiedCellCount == snapshot.Hull.OccupiedCellCount,
                "Current-revision export did not consume the resolved snapshot placement list.");
            using var json = JsonDocument.Parse(File.ReadAllText(export.FilePath));
            var blueprint = json.RootElement.GetProperty("Blueprint");
            var positions = blueprint.GetProperty("BLP").EnumerateArray()
                .Select(item => item.GetString()).ToArray();
            var rotations = blueprint.GetProperty("BLR").EnumerateArray()
                .Select(item => item.GetInt32()).ToArray();
            Require(blueprint.GetProperty("BlockCount").GetInt32() == snapshot.Hull.BlockCount &&
                    positions.SequenceEqual(snapshot.Hull.Blocks.Select(block =>
                        $"{block.X},{block.Y},{block.Z}")) &&
                    rotations.SequenceEqual(snapshot.Hull.Blocks.Select(block => block.Rotation)),
                "The native export round trip changed the resolved snapshot placement list.");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            if (Directory.Exists(staleRoot)) Directory.Delete(staleRoot, true);
        }
    }

    private static ShipDocument AsymmetricSuperstructureDocument()
    {
        var baseDocument = BarbetteDocument(includeInternals: false);
        var barbetteNode = baseDocument.Arrangement.Nodes.Single();
        var requiredRootOffset = DesignMeasure.FromMetres(-1);
        var spanNode = ArrangementNode.Create("span", ArrangementNodeKind.SuperstructureSpan, "tower",
            DesignMeasure.FromMetres(1),
            new ArrangementHandle("tower-root", "Tower root", requiredRootOffset, "tower-root"));
        var arrangement = new Arrangement(
            [barbetteNode, spanNode],
            [new ArrangementGap("gap", ArrangementMeasure.ClearEdgeGap, DesignMeasure.FromMetres(5))],
            [], DesignMeasure.FromMetres(38), DesignMeasure.Zero,
            ArrangementAnchorKind.BowDatum, ArrangementResizePolicy.PreserveAbsolute);
        var modules = new SuperstructureLayout(true,
            [new SuperstructureLayer("layer-1", 1,
            [
                new SuperstructureBoxModule("main", DesignMeasure.Zero, DesignMeasure.Zero,
                    DesignMeasure.FromMetres(5), DesignMeasure.FromMetres(3), 1),
                new SuperstructureBoxModule("aft", DesignMeasure.FromMetres(3), DesignMeasure.Zero,
                    DesignMeasure.FromMetres(3), DesignMeasure.FromMetres(3), 1),
            ])],
            [new SuperstructureTowerRoot("tower-root", "span", requiredRootOffset)],
            LegacySuperstructureSettings.None);
        return baseDocument with
        {
            Arrangement = arrangement,
            Superstructure = modules,
        };
    }

    private static void VerifyUnavailableRefinementFailsClosed()
    {
        var source = ShipDocument.CreateNew("Unavailable refinement", HullParameters.Default);
        var stale = source with
        {
            Smoothing = source.Smoothing with
            {
                Refinement = DecorativeRefinementKind.HorizontalRuns,
                RefinementMaxRunMetres = 8,
            },
        };

        var result = new ShipGenerationService().Generate(stale, 2, Catalog());
        Require(!result.IsValid && result.Snapshot is null &&
                result.Diagnostics.Any(diagnostic =>
                    diagnostic.Code == DesignDiagnosticCodes.DecorativeRefinementUnavailable),
            "An unavailable refinement request must block generation instead of being silently ignored.");
        Require(stale.Smoothing.Refinement == DecorativeRefinementKind.HorizontalRuns &&
                stale.Smoothing.RefinementMaxRunMetres == 8,
            "Fail-closed generation must not mutate persisted refinement intent.");
    }

    private static void VerifyEvenWidthBarbetteFailsClosed()
    {
        var odd = BarbetteDocument(includeInternals: false);
        var staleEven = odd with { Hull = odd.Hull with { Width = 20 } };

        var result = new ShipGenerationService().Generate(staleEven, 31, Catalog());
        Require(!result.IsValid && result.Snapshot is null &&
                result.Diagnostics.Any(diagnostic =>
                    diagnostic.Code == DesignDiagnosticCodes.BarbetteOddHullWidthRequired &&
                    diagnostic.Message == "Centerline barbettes require an odd hull width.") &&
                staleEven.Barbettes.SequenceEqual(odd.Barbettes),
            "A stale odd-to-even edit must preserve barbette intent but block physical generation.");
    }

    private static ShipDocument BarbetteDocument(bool includeInternals)
    {
        var parameters = HullParameters.Default with
        {
            DeckArmor = new ArmorLayout([
                new ArmorLayer(MaterialKind.Wood),
                new ArmorLayer(MaterialKind.Lead, ArmorConstruction.Pole),
            ]),
            Beamify = true,
            Smoothing = SmoothingMethod.None,
            Superstructure = SuperstructureSettings.Default with { Enabled = false },
        };
        var definition = BarbetteDefinition.Create("barbette", "ring",
            DesignMeasure.FromMetres(3), 3, neckClearSizeMetres: 3);
        var arrangement = new Arrangement(
            [ArrangementNode.Create("ring", ArrangementNodeKind.Barbette, "barbette",
                DesignMeasure.FromMetres(1))],
            [], [], DesignMeasure.FromMetres(38), DesignMeasure.Zero,
            ArrangementAnchorKind.BowDatum, ArrangementResizePolicy.PreserveAbsolute);
        var internals = includeInternals
            ? Structure(new InternalStructureFamily(InternalPlaneFamily.LongitudinalBulkhead,
                true, 1, MaterialKind.Metal, DesignMeasure.FromMetres(6), 1,
                DesignMeasure.Zero, true)
            {
                Datum = InternalPlaneDatum.CenterPlane,
                Direction = InternalRepeatDirection.Both,
                IncludeCentralPlane = true,
            })
            : InternalStructure.Disabled;
        return ShipDocument.CreateNew("Barbette", parameters, "i01-barbette") with
        {
            Arrangement = arrangement,
            Datum = new LayoutDatum(DesignMeasure.FromMetres(40.5), DesignMeasure.Zero),
            Internals = internals,
            Barbettes = [definition],
        };
    }

    private static InternalStructure Structure(params InternalStructureFamily[] enabled)
    {
        var byFamily = enabled.ToDictionary(family => family.Family);
        return new InternalStructure(Enum.GetValues<InternalPlaneFamily>()
            .Select(family => byFamily.TryGetValue(family, out var settings)
                ? settings
                : InternalStructureFamily.Disabled(family)).ToImmutableArray());
    }

    private static FtdBlockCatalog Catalog()
    {
        var root = Path.Combine(Path.GetTempPath(), "HullForge-I01-EmptyCatalog");
        Directory.CreateDirectory(Path.Combine(root, "From_The_Depths_Data", "StreamingAssets"));
        return FtdBlockCatalog.Load(root);
    }

    private static FtdBlockCatalog InstalledCatalog()
    {
        var gameDirectory = FtdInstallationLocator.FindInstalledGame() ??
                            throw new InvalidOperationException(
                                "From The Depths is required for the installed-catalog smoothing oracle.");
        return FtdBlockCatalog.Load(gameDirectory);
    }

    private static IReadOnlyList<string> NativeSmoothing(GeneratedHull hull) => hull.Blocks
        .Where(block => block.Origin is BlockOrigin.Smoothing or BlockOrigin.SuperstructureSmoothing)
        .Select(block =>
        {
            var metadata = BlockShapeMetadata.Get(block.Shape);
            return $"{block.Shape}|{metadata.Family}|{metadata.Length}|{metadata.Mirrored}|" +
                   $"{block.Material}|{block.X},{block.Y},{block.Z}|{block.Rotation}|{block.Origin}";
        })
        .Order(StringComparer.Ordinal)
        .ToArray();

    private static HashSet<string> CellMaterials(GeneratedHull hull) => hull.Blocks
        .SelectMany(block => block.OccupiedCells.Select(cell =>
            $"{cell.X},{cell.Y},{cell.Z}|{block.Material}"))
        .ToHashSet(StringComparer.Ordinal);

    private static string ProvenanceKey(PhysicalCellProvenance item) =>
        $"{item.Cell.X},{item.Cell.Y},{item.Cell.Z}|{item.Material}|" +
        string.Join(",", item.Owners.Select(owner => $"{owner.Role}:{owner.OwnerId}"));

    private static string Describe(IEnumerable<DesignDiagnostic> diagnostics) =>
        string.Join(" | ", diagnostics.Select(diagnostic => diagnostic.ToString()));

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
