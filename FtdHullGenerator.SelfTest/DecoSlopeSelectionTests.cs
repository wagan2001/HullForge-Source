using System.Collections.Immutable;
using System.Windows;
using System.Windows.Media.Media3D;
using FtdHullGenerator;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Decorations;
using FtdHullGenerator.Domain.Design;
using FtdHullGenerator.Domain.Projects;
using FtdHullGenerator.Geometry;
using FtdHullGenerator.Geometry.Composition;
using FtdHullGenerator.Geometry.Refinement;
using FtdHullGenerator.Infrastructure;
using FtdHullGenerator.Serialization;
using FtdHullGenerator.Serialization.Decorations;
using FtdHullGenerator.Serialization.Projects;
using FtdHullGenerator.UI;

/// <summary>
/// DEC01 focused checks for the normal parameter-free Deco Vertical/Deco Horizontal selections:
/// direct native parity, eligible/ineligible anchors, 5/40 m boundaries, medium and long runs,
/// shorter-donor fallback, mirrored output, serialization round trip and preview/export agreement.
/// </summary>
internal static class DecoSlopeSelectionTests
{
    public static void Run(FtdBlockCatalog catalog)
    {
        var basis = new ShipGenerationService().Generate(
            ShipDocument.CreateNew("Deco basis", HullParameters.Default with
            {
                Length = 40, Width = 15, Height = 10, Smoothing = SmoothingMethod.None,
                Superstructure = null,
            }), 1, catalog).Snapshot
            ?? throw new InvalidOperationException("Could not construct the DEC01 basis snapshot.");

        VerifyAppendOnlyIdentity();
        VerifyDirectNativeParity(catalog);
        VerifyReproducedCavityCaseParity();
        VerifyAutomaticNativeParity(catalog);
        VerifyEligibilityAndIneligibility(basis, catalog);
        VerifyBoundariesAndSelection(basis, catalog);
        VerifyShorterFallback(basis);
        VerifyMirroredOutput(basis, catalog);
        VerifySerializationRoundTrip(catalog);
        VerifyNoEligibleAnchorStaysNative(catalog);
        VerifyPreviewAndExportAgreement(catalog);
        Console.WriteLine(
            "Deco V/H selection: append-only identity, direct and composed native parity, eligible/" +
            "ineligible anchors, 5/40 m boundaries, medium/long selection, shorter-donor fallback, " +
            "mirrored output, serialization round trip and preview/export agreement passed.");
    }

    private static void VerifyAppendOnlyIdentity()
    {
        Require((int)SmoothingMethod.DecoVertical == 7 && (int)SmoothingMethod.DecoHorizontal == 8,
            "The decorative selections must append identities 7 and 8 without renumbering.");
        Require(SmoothingMethodMapping.NativeBase(SmoothingMethod.DecoVertical) == SmoothingMethod.VerticalSlopeFill &&
            SmoothingMethodMapping.NativeBase(SmoothingMethod.DecoHorizontal) == SmoothingMethod.HorizontalSlopeFill,
            "A decorative selection must generate its corresponding proven native method.");
        Require(SmoothingMethodMapping.DecorationKind(SmoothingMethod.DecoVertical) == SlopeExtensionKind.Vertical &&
            SmoothingMethodMapping.DecorationKind(SmoothingMethod.DecoHorizontal) == SlopeExtensionKind.Horizontal &&
            !SmoothingMethodMapping.IsDecorative(SmoothingMethod.HybridSlopeFill),
            "The decorative kind mapping is wrong.");
    }

    /// <summary>
    /// The two normal choices must be selectable without any decoration configuration control. This
    /// runs after the workspace-shell probe so it can reuse that probe's live WPF application.
    /// </summary>
    public static void RunUiChoices()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                // Only one Application may exist per AppDomain; reuse an existing one. This probe
                // leaves an application it creates alive for the later WPF probes, as the shell probe does.
                if (System.Windows.Application.Current is null)
                {
                    var created = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    created.InitializeComponent();
                    created.Navigating += (_, eventArgs) => eventArgs.Cancel = true;
                    FtdHullGenerator.UI.ThemeManager.Apply(FtdHullGenerator.UI.AppTheme.Workbench);
                }
                var window = new MainWindow();
                try
                {
                    var labels = window.SmoothingBox.Items.Cast<object>().Select(item => item.ToString()).ToArray();
                    Require(labels.Contains("Deco Vertical") && labels.Contains("Deco Horizontal"),
                        "The normal smoothing list does not expose the decorative selections: " + string.Join(", ", labels));
                    Require(labels.Contains("Vertical slope fill") && labels.Contains("Horizontal slope fill"),
                        "The proven native smoothing choices were removed.");
                }
                finally
                {
                    window.Close();
                }
            }
            catch (Exception error)
            {
                failure = error;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Require(thread.Join(TimeSpan.FromSeconds(30)), "The smoothing-choice UI probe timed out.");
        if (failure is not null)
            throw new InvalidOperationException("The smoothing-choice UI probe failed.", failure);
    }

    /// <summary>
    /// HF-01: Deco Horizontal is the proven native pass plus a derived visual extension, so it must
    /// inherit the corrected native Shape V2 candidate set exactly and emit no cavity-facing
    /// placement of its own.
    /// </summary>
    private static void VerifyReproducedCavityCaseParity()
    {
        var parameters = HullParameters.Default with
        {
            Length = 90, Width = 17, Height = 17,
            Shape = HullShapeSettings.Default with { Profile = new HullProfileSettings(4, 2, 3, 1) },
            Superstructure = null,
        };
        var decorated = new HullGenerator().Generate(parameters with { Smoothing = SmoothingMethod.DecoHorizontal });
        var plain = new HullGenerator().Generate(parameters with { Smoothing = SmoothingMethod.HorizontalSlopeFill });
        Require(decorated.Blocks.SequenceEqual(plain.Blocks),
            "Deco Horizontal changed the native Shape V2 placement list versus HorizontalSlopeFill.");
        Require(decorated.BlockCount == plain.BlockCount &&
                decorated.OccupiedCellCount == plain.OccupiedCellCount &&
                decorated.MinX == plain.MinX && decorated.MaxX == plain.MaxX &&
                decorated.MinY == plain.MinY && decorated.MaxY == plain.MaxY &&
                decorated.MinZ == plain.MinZ && decorated.MaxZ == plain.MaxZ,
            "Deco Horizontal changed the native Shape V2 bounds or occupied cells.");

        var context = HullGenerator.CreateContext(parameters);
        var nonExterior = decorated.Blocks
            .Where(block => block.Origin == BlockOrigin.Smoothing)
            .Where(block => block.OccupiedCells.Any(cell =>
                context.RoleAt(cell.X, cell.Y, cell.Z) != FtdHullGenerator.Domain.Composition.HullCellRole.Outside))
            .ToArray();
        Require(nonExterior.Length == 0,
            $"Deco Horizontal emitted {nonExterior.Length} cavity/armor-facing native placement(s).");
    }

    /// <summary>Deco on/off must not change the direct HullGenerator physical output at all.</summary>
    private static void VerifyDirectNativeParity(FtdBlockCatalog catalog)
    {
        foreach (var (deco, native, length, width, height) in new[]
                 {
                     (SmoothingMethod.DecoVertical, SmoothingMethod.VerticalSlopeFill, 80, 21, 12),
                     (SmoothingMethod.DecoHorizontal, SmoothingMethod.HorizontalSlopeFill, 80, 21, 12),
                 })
        {
            var parameters = HullParameters.Default with
            {
                Length = length, Width = width, Height = height,
                Smoothing = deco, Superstructure = null,
            };
            var decorated = new HullGenerator().Generate(parameters);
            var plain = new HullGenerator().Generate(parameters with { Smoothing = native });
            Require(decorated.Blocks.SequenceEqual(plain.Blocks),
                $"{deco}: direct generation changed native placements versus {native}.");
            Require(decorated.MinX == plain.MinX && decorated.MaxX == plain.MaxX &&
                decorated.MinY == plain.MinY && decorated.MaxY == plain.MaxY &&
                decorated.MinZ == plain.MinZ && decorated.MaxZ == plain.MaxZ,
                $"{deco}: direct generation changed native bounds versus {native}.");
        }
    }

    /// <summary>The composed snapshot must keep the native plan invariant and attach only visual data.</summary>
    private static void VerifyAutomaticNativeParity(FtdBlockCatalog catalog)
    {
        foreach (var (deco, native, length, width, height) in new[]
                 {
                     (SmoothingMethod.DecoVertical, SmoothingMethod.VerticalSlopeFill, 80, 21, 12),
                     (SmoothingMethod.DecoHorizontal, SmoothingMethod.HorizontalSlopeFill, 80, 21, 12),
                     (SmoothingMethod.DecoHorizontal, SmoothingMethod.HorizontalSlopeFill, 120, 21, 12),
                 })
        {
            var parameters = HullParameters.Default with
            {
                Length = length, Width = width, Height = height,
                Smoothing = deco, Superstructure = null,
            };
            var decorated = Compose(parameters, deco, catalog);
            var plain = Compose(parameters with { Smoothing = native }, native, catalog);
            var decoHull = decorated.Snapshot!.Hull;
            var plainHull = plain.Snapshot!.Hull;

            Require(decoHull.Blocks.SequenceEqual(plainHull.Blocks),
                $"{deco}: composition changed native placements versus {native}.");
            Require(decoHull.MinX == plainHull.MinX && decoHull.MaxX == plainHull.MaxX &&
                decoHull.MinY == plainHull.MinY && decoHull.MaxY == plainHull.MaxY &&
                decoHull.MinZ == plainHull.MinZ && decoHull.MaxZ == plainHull.MaxZ,
                $"{deco}: composition changed native bounds versus {native}.");
            Require(decoHull.OccupiedCellCount == plainHull.OccupiedCellCount &&
                decoHull.Blocks.SelectMany(block => block.OccupiedCells).ToHashSet()
                    .SetEquals(plainHull.Blocks.SelectMany(block => block.OccupiedCells)),
                $"{deco}: composition changed the occupied cell set versus {native}.");
            var provenanceDifference = FirstProvenanceDifference(decoHull, plainHull);
            Require(provenanceDifference is null,
                $"{deco}: composition changed cell ownership/provenance versus {native}: {provenanceDifference}");

            Require(plain.Snapshot!.Refinement is null,
                $"{native}: a physical smoothing method must not attach decorations.");
            var refinement = decorated.Snapshot!.Refinement;
            Require(refinement is { Extensions.Length: > 0 },
                $"{deco}: automatic selection attached no decorations.");
            var kind = SmoothingMethodMapping.DecorationKind(deco)!.Value;
            var anchors = decoHull.Blocks.Where(block =>
                block.Shape == BlockShape.Slope4 && block.Origin == BlockOrigin.Smoothing).ToArray();
            foreach (var extension in refinement!.Extensions)
            {
                Require(DecoSlopeRunSelector.IsSupportedRotation(kind, extension.Anchor.Placement.Rotation),
                    $"{deco}: an extension used an unsupported rotation.");
                Require(anchors.Contains(extension.Anchor.Placement),
                    $"{deco}: an extension anchor is not an exact final native Slope4 placement.");
                Require(extension.RequestedLengthMetres is >= 5 and <= 40,
                    $"{deco}: an extension length left the supported 5-40 m range.");
                Require(extension.Anchor.PartGuid == catalog.Resolve(extension.Anchor.Placement.Material,
                        BlockShape.Slope4).Guid,
                    $"{deco}: the extension lost its resolved native host identity.");
            }
            Require(refinement.Module.Records.Select(record => record.DecorationId)
                    .SequenceEqual(Enumerable.Range(0, refinement.Extensions.Length)),
                $"{deco}: decoration ids are not the deterministic contiguous order.");
            Require(refinement.Extensions.Select(extension => extension.Anchor.Placement.X)
                    .SequenceEqual(refinement.Extensions.Select(extension => extension.Anchor.Placement.X).Order()),
                $"{deco}: decoration order is not deterministic by anchor position.");

            // Only eligible anchors are extended; every other native placement is untouched.
            Require(refinement.Extensions.Length <= anchors.Length,
                $"{deco}: more extensions than native Slope4 anchors.");
            Require(refinement.Extensions.Select(extension => extension.Anchor.Placement).Distinct().Count() ==
                refinement.Extensions.Length, $"{deco}: one anchor received more than one extension.");
        }
    }

    private static void VerifyEligibilityAndIneligibility(ShipGenerationSnapshot basis, FtdBlockCatalog catalog)
    {
        var anchor = Anchor(0, 0, 10, 12);
        var support = Cube(0, 1, 14);
        var eligible = Snapshot(basis, [anchor, support]);
        var requests = Select(eligible, SlopeExtensionKind.Vertical, catalog);
        Require(requests.Length == 1 && requests[0].VisualLengthMetres == 5,
            "A supported final native Slope4 anchor with a five-metre run was not selected.");

        Reject(anchor with { ArmorDepth = 1 }, "internal-armor anchor");
        Reject(anchor with { UsePoles = true }, "pole anchor");
        Reject(anchor with { Origin = BlockOrigin.Shell }, "shell-origin anchor");
        Reject(anchor with { Material = MaterialKind.Stone }, "unsupported-material anchor");
        Reject(anchor with { Rotation = 0 }, "unsupported vertical rotation");
        Reject(anchor with { Rotation = 8 }, "descending riser rotation");
        // Overlapping final placement: the anchor no longer exclusively owns its footprint.
        Reject(anchor, "overlapping placement", Cube(0, 0, 10));
        // Outside the declared final structural bounds.
        var outside = Snapshot(basis, [anchor, support]) with
        {
            Hull = Snapshot(basis, [anchor, support]).Hull with { MinZ = anchor.Z + 1 },
        };
        Require(Select(outside, SlopeExtensionKind.Vertical, catalog).IsEmpty,
            "An anchor outside the final structural bounds was selected.");
        // A Slope3 native anchor is not a 4 m anchor and must never be extended.
        var shortAnchor = new BlockPlacement(BlockShape.Slope3, MaterialKind.Metal, 0, 0, 10, 12)
        { Origin = BlockOrigin.Smoothing };
        Require(Select(Snapshot(basis, [shortAnchor, support]), SlopeExtensionKind.Vertical, catalog).IsEmpty,
            "A native 3 m anchor was selected.");
        // A vertical anchor needs its neighbouring contour row; without it the run is unknown.
        Require(Select(Snapshot(basis, [anchor]), SlopeExtensionKind.Vertical, catalog).IsEmpty,
            "An anchor without a measurable native run was extended.");
        // A horizontal anchor must not be selected by the vertical choice and vice versa.
        var horizontal = Anchor(1, 0, 10, 18);
        var horizontalSupport = Cube(0, 0, 14);
        var horizontalSnapshot = Snapshot(basis, [horizontal, horizontalSupport]);
        Require(Select(horizontalSnapshot, SlopeExtensionKind.Vertical, catalog).IsEmpty,
            "The vertical choice selected a horizontal anchor.");
        Require(Select(horizontalSnapshot, SlopeExtensionKind.Horizontal, catalog).Length == 1,
            "The horizontal choice did not select a valid horizontal anchor.");

        void Reject(BlockPlacement candidate, string description, params BlockPlacement[] extra)
        {
            var snapshot = Snapshot(basis, [candidate, support, .. extra]);
            Require(Select(snapshot, SlopeExtensionKind.Vertical, catalog).IsEmpty,
                $"An ineligible {description} was selected.");
        }
    }

    private static void VerifyBoundariesAndSelection(ShipGenerationSnapshot basis, FtdBlockCatalog catalog)
    {
        // A native tread's run is the contour step the proven pass could only build to 4 m.
        Check(12, 5, 5, "exact 5 m");
        Check(12, 4, null, "4 m");
        Check(12, 3, null, "3 m");
        Check(12, 25, 25, "medium");
        Check(12, 39, 39, "long");
        Check(12, 40, 40, "exact 40 m");
        Check(12, 41, 40, "over 40 m");
        Check(14, 5, 5, "stern 5 m");
        Check(14, 40, 40, "stern 40 m");

        // A vertical riser spans its constant-edge run.
        var riser = Anchor(0, -3, 0, 4);
        var riserRows = Enumerable.Range(0, 5).Select(index => Cube(0, -3 - index, 5)).ToArray();
        var riserSnapshot = Snapshot(basis, [riser, .. riserRows]);
        var riserRequest = Select(riserSnapshot, SlopeExtensionKind.Vertical, catalog);
        Require(riserRequest.Length == 1 && riserRequest[0].VisualLengthMetres == 5,
            "A five-row riser run did not select 5 m.");

        // A horizontal slope reaches its inboard width step, on both hands.
        foreach (var (rotation, supportZ, expected) in new[]
                 {
                     (18, 14, 5), (19, 36, 5), (16, 14, 5), (17, 36, 5),
                 })
        {
            var z = rotation is 19 or 17 ? 40 : 10;
            var x = rotation is 16 or 17 ? 1 : -1;
            var snapshot = Snapshot(basis, [Anchor(x, 0, z, rotation), Cube(0, 0, supportZ)]);
            var selected = Select(snapshot, SlopeExtensionKind.Horizontal, catalog);
            Require(selected.Length == 1 && selected[0].VisualLengthMetres == expected,
                $"Horizontal rotation {rotation} did not select its inboard run.");
        }

        void Check(int rotation, int run, int? expected, string description)
        {
            // The native pass anchors a bow tread at Edge(y)+1 and a stern tread at Edge(y)-1.
            var z = rotation == 12 ? 10 : 40;
            var supportZ = rotation == 12 ? z + run - 1 : z - run + 1;
            var snapshot = Snapshot(basis, [Anchor(0, 0, z, rotation), Cube(0, 1, supportZ)]);
            var selected = Select(snapshot, SlopeExtensionKind.Vertical, catalog);
            Require(selected.Length == (expected is null ? 0 : 1),
                $"A {description} run selected the wrong number of extensions.");
            if (expected is { } length)
                Require(selected[0].VisualLengthMetres == length,
                    $"A {description} run selected {selected[0].VisualLengthMetres} m instead of {length} m.");
        }
    }

    private static void VerifyShorterFallback(ShipGenerationSnapshot basis)
    {
        // The host 4 m part always exists for an eligible anchor, so a 31-40 m run can never need
        // a fallback. The shorter buckets fall back to the longest installed donor.
        var run25 = Snapshot(basis, [Anchor(0, 0, 10, 12), Cube(0, 1, 34)]);
        CheckFallback(run25, 25, ["Metal down slope (4m)", "Metal down slope 2m", "Metal down slope 1m"],
            20, 2, "missing 3 m donor");
        CheckFallback(run25, 25, ["Metal down slope (4m)", "Metal down slope 1m"], 10, 1, "only 1 m donor");
        CheckFallback(run25, 25, ["Metal down slope (4m)"], null, null, "no donor");
        CheckFallback(run25, 25, ["Metal down slope (4m)", "Metal down slope 3m", "Metal down slope 2m", "Metal down slope 1m"],
            25, 3, "all donors");
        var run7 = Snapshot(basis, [Anchor(0, 0, 10, 12), Cube(0, 1, 16)]);
        CheckFallback(run7, 7, ["Metal down slope (4m)"], null, null, "no 1 m donor for a short run");
        var run40 = Snapshot(basis, [Anchor(0, 0, 10, 12), Cube(0, 1, 49)]);
        CheckFallback(run40, 40, ["Metal down slope (4m)"], 40, 4, "host doubles as the long donor");

        void CheckFallback(ShipGenerationSnapshot snapshot, int run, string[] parts, int? expectedLength,
            int? expectedMesh, string description)
        {
            var partial = PartialCatalog(parts);
            var input = snapshot with
            {
                Catalog = partial,
                Hull = snapshot.Hull with
                {
                    ResolvedCatalogVersion = partial.GameVersion,
                    ResolvedCatalogFingerprint = ShipCatalogFingerprint.Compute(partial),
                },
            };
            var selected = Select(input, SlopeExtensionKind.Vertical, partial);
            if (expectedLength is null)
            {
                Require(selected.IsEmpty, $"A {description} run ({run} m) was extended without a donor.");
                return;
            }
            Require(selected.Length == 1 && selected[0].VisualLengthMetres == expectedLength,
                $"A {description} run selected {selected.Length} request(s) instead of {expectedLength} m.");
            var plan = new VerticalSlopeExtensionPlanner().Build(input, selected);
            Require(plan.IsValid && plan.Extensions.Length == 1 &&
                plan.Extensions[0].RequestedLengthMetres == expectedLength &&
                plan.Extensions[0].SourceMeshLengthMetres == expectedMesh,
                $"A {description} run did not build the evidenced fallback donor: {string.Join(";", plan.Diagnostics)}");
        }
    }

    private static void VerifyMirroredOutput(ShipGenerationSnapshot basis, FtdBlockCatalog catalog)
    {
        // Port/starboard anchors on one symmetric synthetic contour must select corresponding runs.
        var mirrorSum = -1 + 1;
        var blocks = new List<BlockPlacement>
        {
            Anchor(-1, 0, 10, 18), Anchor(1, 0, 10, 16),
            Anchor(-1, 0, 40, 19), Anchor(1, 0, 40, 17),
            Cube(0, 0, 14), Cube(0, 0, 36),
        };
        var snapshot = Snapshot(basis, blocks.ToArray());
        var requests = Select(snapshot, SlopeExtensionKind.Horizontal, catalog);
        Require(requests.Length == 4, "The mirrored synthetic contour did not select all four anchors.");
        var plan = new HorizontalSlopeExtensionPlanner().Build(snapshot, requests);
        Require(plan.IsValid && plan.Extensions.Length == 4, "The mirrored synthetic plan failed.");
        foreach (var extension in plan.Extensions)
        {
            var mirror = plan.Extensions.Single(other =>
                other.Anchor.Placement.X == mirrorSum - extension.Anchor.Placement.X &&
                other.Anchor.Placement.Y == extension.Anchor.Placement.Y &&
                other.Anchor.Placement.Z == extension.Anchor.Placement.Z);
            Require(mirror.RequestedLengthMetres == extension.RequestedLengthMetres &&
                mirror.SourceMeshLengthMetres == extension.SourceMeshLengthMetres,
                "A mirrored anchor selected a different visual length.");
            Require(HullGeometryValidator.MirrorRotation(extension.Anchor.Placement.Rotation) ==
                mirror.Anchor.Placement.Rotation,
                "A mirrored anchor does not use the mirrored native rotation.");
            Require(extension.Position == mirror.Position &&
                extension.Scale == mirror.Scale &&
                extension.EulerAngles.Z + mirror.EulerAngles.Z == 360f,
                "A mirrored decoration transform is not the corresponding mirror.");
        }

        // On a real hull the automatic extension set stays port/starboard corresponding.
        var parameters = HullParameters.Default with
        {
            Length = 80, Width = 21, Height = 12,
            Smoothing = SmoothingMethod.DecoHorizontal, Superstructure = null,
        };
        var composed = Compose(parameters, SmoothingMethod.DecoHorizontal, catalog);
        var hullMirrorSum = composed.Snapshot!.Hull.MinX + composed.Snapshot.Hull.MaxX;
        foreach (var extension in composed.Snapshot.Refinement!.Extensions)
        {
            Require(composed.Snapshot.Refinement.Extensions.Any(other =>
                    other.Anchor.Placement.X == hullMirrorSum - extension.Anchor.Placement.X &&
                    other.Anchor.Placement.Y == extension.Anchor.Placement.Y &&
                    other.Anchor.Placement.Z == extension.Anchor.Placement.Z &&
                    other.RequestedLengthMetres == extension.RequestedLengthMetres),
                "A real-hull decorative extension lost its port/starboard corresponding partner.");
        }
    }

    private static void VerifySerializationRoundTrip(FtdBlockCatalog catalog)
    {
        var document = ShipDocument.CreateNew("Deco round trip", HullParameters.Default with
        {
            Length = 80, Width = 21, Height = 12,
            Smoothing = SmoothingMethod.DecoVertical, Superstructure = null,
        });
        var result = new ShipGenerationService().Generate(document, 4, catalog);
        Require(result.IsValid, "The Deco Vertical document did not generate.");
        Require(!document.Smoothing.HasDecorativeRefinement,
            "The normal decorative selection must not need persisted explicit intent.");
        var payload = DecorationModuleWriter.Write(result.Snapshot!.Refinement!.Module);

        var json = ProjectDocumentSerializer.Serialize(document);
        Require(json.Succeeded && json.Json!.Contains("\"DecoVertical\""),
            "The decorative selection did not serialize by its append-only name.");
        var loaded = ProjectDocumentSerializer.Deserialize(json.Json!);
        Require(loaded.Succeeded && loaded.Document!.Smoothing.NativeMethod == SmoothingMethod.DecoVertical &&
            loaded.Document.Hull.Smoothing == SmoothingMethod.DecoVertical &&
            !loaded.Document.Smoothing.HasDecorativeRefinement,
            "The decorative selection did not round-trip losslessly.");
        var loadedDocument = loaded.Document!;
        Require(!loadedDocument.Validate().Any(diagnostic => diagnostic.IsError),
            "A loaded decorative document must stay valid.");
        var regenerated = new ShipGenerationService().Generate(loadedDocument, 4, catalog);
        Require(regenerated.IsValid && DecorationModuleWriter.Write(regenerated.Snapshot!.Refinement!.Module)
                .SequenceEqual(payload),
            "A reopened decorative document regenerated a different visual payload.");
        var repeated = new ShipGenerationService().Generate(document, 4, catalog);
        Require(DecorationModuleWriter.Write(repeated.Snapshot!.Refinement!.Module).SequenceEqual(payload),
            "Repeated generation of the same decorative document is not deterministic.");

        // A saved document from before this feature must keep its exact meaning.
        var legacy = ProjectDocumentSerializer.Serialize(ShipDocument.CreateNew("Legacy",
            HullParameters.Default with { Smoothing = SmoothingMethod.HybridSlopeFill }));
        var legacyLoaded = ProjectDocumentSerializer.Deserialize(legacy.Json!);
        Require(legacyLoaded.Succeeded && legacyLoaded.Document!.Smoothing.NativeMethod == SmoothingMethod.HybridSlopeFill,
            "Adding the decorative identities renumbered an existing smoothing method.");
    }

    /// <summary>An anchor with no supported run must keep the ordinary native result and native output.</summary>
    private static void VerifyNoEligibleAnchorStaysNative(FtdBlockCatalog catalog)
    {
        var document = ShipDocument.CreateNew("Deco no anchor", HullParameters.Default with
        {
            Length = 40, Width = 15, Height = 10,
            Smoothing = SmoothingMethod.DecoVertical, Superstructure = null,
        });
        var native = new ShipGenerationService().Generate(document, 5, catalog).Snapshot!;
        var anchor = Anchor(0, 0, 10, 12);
        var support = Cube(0, 1, 13); // A four-metre step: a native Slope4 with no supported extension.
        var synthetic = native with
        {
            Refinement = null,
            Hull = native.Hull with
            {
                Blocks = [anchor, support],
                MinX = 0, MaxX = 0, MinY = 0, MaxY = 1, MinZ = 10, MaxZ = 13,
            },
        };
        var attached = ResolvedSlopeRefinement.Attach(synthetic);
        Require(attached.IsValid && attached.Snapshot!.Refinement is { Extensions.IsEmpty: true },
            "An anchor without a supported run must attach an empty visual refinement: " +
            string.Join(";", attached.Diagnostics));
        var attachedSnapshot = attached.Snapshot!;
        Require(attachedSnapshot.Hull.Blocks.SequenceEqual(synthetic.Hull.Blocks),
            "An anchor without a supported run changed the native plan.");

        var root = Path.Combine(Path.GetTempPath(), "HullForgeDecoNative-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var export = new BlueprintExporter().Export(attachedSnapshot, document, 5,
                Path.Combine(root, "native"), "deco-native", exposure: new FeatureExposurePolicy(false));
            using var blueprint = System.Text.Json.JsonDocument.Parse(File.ReadAllText(export.FilePath));
            Require(blueprint.RootElement.GetProperty("Blueprint").GetProperty("VehicleData").ValueKind ==
                System.Text.Json.JsonValueKind.Null,
                "A hull with no eligible anchor exported decoration data.");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void VerifyPreviewAndExportAgreement(FtdBlockCatalog catalog)
    {
        var document = ShipDocument.CreateNew("Deco preview/export", HullParameters.Default with
        {
            Length = 80, Width = 21, Height = 12,
            Smoothing = SmoothingMethod.DecoHorizontal, Superstructure = null,
        });
        var result = new ShipGenerationService().Generate(document, 9, catalog);
        Require(result.IsValid, "The Deco Horizontal document did not generate.");
        var snapshot = result.Snapshot!;
        var refinement = snapshot.Refinement!;

        var meshGroup = HullPreviewControl.BuildRefinementModel(refinement);
        ResolvedSlopeRefinementTests.RequireRefinementModelEquivalent(meshGroup, refinement);
        var distinctMaterials = refinement.Extensions.Select(extension => extension.Anchor.Placement.Material).Distinct().Count();
        Require(meshGroup.Children.Count <= distinctMaterials &&
                meshGroup.Children.Count < refinement.Extensions.Length,
            "Preview did not batch resolved decorations by material.");
        foreach (var model in meshGroup.Children.OfType<GeometryModel3D>())
            Require(ReferenceEquals(model.Material, model.BackMaterial),
                "A preview decoration batch lost its two-sided back material.");

        var root = Path.Combine(Path.GetTempPath(), "HullForgeDeco-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var exporter = new BlueprintExporter();
            var normalExposure = new FeatureExposurePolicy(experimentalFeaturesEnabled: false);
            // The normal parameter-free selection must export without Experimental Features.
            var exported = exporter.Export(snapshot, document, 9, Path.Combine(root, "deco"), "deco", exposure: normalExposure);
            using var decorated = System.Text.Json.JsonDocument.Parse(File.ReadAllText(exported.FilePath));
            var payload = Convert.FromBase64String(decorated.RootElement.GetProperty("Blueprint")
                .GetProperty("VehicleData").GetString()!);
            Require(payload.SequenceEqual(DecorationModuleWriter.Write(refinement.Module)),
                "Export did not use the same resolved payload the preview rendered.");

            var nativeDocument = document with
            { Hull = document.Hull with { Smoothing = SmoothingMethod.HorizontalSlopeFill },
              Smoothing = document.Smoothing with { NativeMethod = SmoothingMethod.HorizontalSlopeFill } };
            var nativeSnapshot = new ShipGenerationService().Generate(nativeDocument, 9, catalog).Snapshot!;
            var nativeExport = exporter.Export(nativeSnapshot, nativeDocument, 9, Path.Combine(root, "native"), "deco");
            using var plain = System.Text.Json.JsonDocument.Parse(File.ReadAllText(nativeExport.FilePath));
            var plainRoot = plain.RootElement.GetProperty("Blueprint");
            var decoratedRoot = decorated.RootElement.GetProperty("Blueprint");
            foreach (var property in plainRoot.EnumerateObject()
                         .Where(property => property.Name is not ("VehicleData" or "ForceId")))
                Require(property.Value.GetRawText() == decoratedRoot.GetProperty(property.Name).GetRawText(),
                    "Decorative export changed a native blueprint property: " + property.Name);

            // The legacy explicit editor keeps its experimental gate.
            var explicitDocument = document with
            {
                Smoothing = document.Smoothing with
                {
                    ExplicitRefinement = new(NativeSlopeExtensionRule.RecipeId, 1, refinement.Extensions
                        .Select(extension => new SlopeExtensionRequest(extension.Anchor.Placement, extension.RequestedLengthMetres))
                        .ToImmutableArray()),
                },
            };
            var explicitSnapshot = new ShipGenerationService().Generate(explicitDocument, 9, catalog).Snapshot!;
            Reject(() => exporter.Export(explicitSnapshot, explicitDocument, 9, Path.Combine(root, "blocked"), "blocked",
                exposure: normalExposure));
            Require(!Directory.Exists(Path.Combine(root, "blocked")),
                "The experimental explicit gate stopped applying to manual intent.");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static string? FirstProvenanceDifference(GeneratedHull left, GeneratedHull right)
    {
        if (left.CellProvenance.Count != right.CellProvenance.Count)
            return $"counts {left.CellProvenance.Count}/{right.CellProvenance.Count}";
        for (var index = 0; index < left.CellProvenance.Count; index++)
        {
            var a = left.CellProvenance[index];
            var b = right.CellProvenance[index];
            if (a.Cell != b.Cell || a.Material != b.Material || !a.Owners.SequenceEqual(b.Owners))
                return $"index {index}: ({a.Cell.X},{a.Cell.Y},{a.Cell.Z}) {a.Material} [{string.Join(",", a.Owners)}] " +
                    $"vs ({b.Cell.X},{b.Cell.Y},{b.Cell.Z}) {b.Material} [{string.Join(",", b.Owners)}]";
        }
        return null;
    }

    private static ShipGenerationResult Compose(HullParameters parameters, SmoothingMethod method,
        FtdBlockCatalog catalog)    {
        var document = ShipDocument.CreateNew($"Deco {method}", parameters with { Smoothing = method });
        var result = new ShipGenerationService().Generate(document, 2, catalog);
        Require(result.IsValid, $"{method}: {string.Join("; ", result.Diagnostics)}");
        return result;
    }

    private static ImmutableArray<SlopeExtensionRequest> Select(ShipGenerationSnapshot snapshot,
        SlopeExtensionKind kind, FtdBlockCatalog catalog)
    {
        var diagnostics = ImmutableArray.CreateBuilder<FtdHullGenerator.Domain.Design.DesignDiagnostic>();
        var requests = DecoSlopeRunSelector.Select(snapshot, kind, diagnostics);
        Require(diagnostics.Count == 0, "Automatic selection produced diagnostics: " + string.Join(";", diagnostics));
        return requests;
    }

    private static ShipGenerationSnapshot Snapshot(ShipGenerationSnapshot basis, IReadOnlyList<BlockPlacement> blocks)
    {
        var cells = blocks.SelectMany(block => block.OccupiedCells).ToArray();
        return basis with
        {
            Hull = basis.Hull with
            {
                Blocks = blocks,
                MinX = cells.Min(cell => cell.X), MaxX = cells.Max(cell => cell.X),
                MinY = cells.Min(cell => cell.Y), MaxY = cells.Max(cell => cell.Y),
                MinZ = cells.Min(cell => cell.Z), MaxZ = cells.Max(cell => cell.Z),
            },
        };
    }

    private static BlockPlacement Anchor(int x, int y, int z, int rotation) =>
        new(BlockShape.Slope4, MaterialKind.Metal, x, y, z, rotation) { Origin = BlockOrigin.Smoothing };

    private static BlockPlacement Cube(int x, int y, int z) =>
        new(BlockShape.Cube, MaterialKind.Metal, x, y, z, 0) { Origin = BlockOrigin.Shell };

    /// <summary>A synthetic install that provides exactly the named Metal slope parts.</summary>
    private static FtdBlockCatalog PartialCatalog(IReadOnlyList<string> displayNames)
    {
        var root = Path.Combine(Path.GetTempPath(), "HullForgeDecoCatalog-" + Guid.NewGuid().ToString("N"));
        var itemDup = Path.Combine(root, "From_The_Depths_Data", "StreamingAssets", "Mods",
            "Core_Structural", "ItemDup");
        Directory.CreateDirectory(itemDup);
        var index = 0;
        foreach (var displayName in displayNames)
        {
            var guid = Guid.Parse($"00000000-0000-0000-0000-{++index:D12}");
            File.WriteAllText(Path.Combine(itemDup, $"{displayName.Replace(' ', '_').Replace("(", "").Replace(")", "")}.itemduplicateandmodify"),
                $"{{\"DisplayName\":\"{displayName}\",\"ComponentId\":{{\"Guid\":\"{guid}\"}}}}");
        }
        try
        {
            return FtdBlockCatalog.Load(root);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Invalid decorative export was accepted.");
    }
}
