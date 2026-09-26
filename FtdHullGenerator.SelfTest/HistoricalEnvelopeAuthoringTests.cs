using System.Numerics;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FtdHullGenerator.Domain.Components;
using FtdHullGenerator.Domain.Design;
using FtdHullGenerator.Domain.Layout;
using FtdHullGenerator.Domain.Projects;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Composition;
using FtdHullGenerator.Domain.Historical;
using FtdHullGenerator.Geometry;
using FtdHullGenerator.Geometry.Composition;
using FtdHullGenerator.Geometry.HullSources;
using FtdHullGenerator.Infrastructure;

internal static class HistoricalEnvelopeAuthoringTests
{
    public static void Run()
    {
        VerifyBoundedObjImportAndAxes();
        VerifyTopologyAndNumericRejection();
        VerifyEnvelopeSamplingUnsupportedRowsAndSymmetry();
        VerifyShapeV2FitAndCancellation();
        VerifyProfileAndBulbFitWithAnalyticOracles();
        VerifyFidelityAndPackagingRemainIndependent();
        VerifyValidatedFallbackComposition();
        VerifyEvidenceBackedHistoricalDatums();
        VerifyFiniteOutcomesAndBoundedReports();
        VerifyHistoricalAuthoringProcessSafety();
        Console.WriteLine("Historical authoring: bounded OBJ, axes, topology, rows, independent fit, " +
                          "datum-aware fallback, finite outcomes, bounded reports, CLI atomicity and rights passed.");
    }

    private static void VerifyBoundedObjImportAndAxes()
    {
        var path = WriteTempObj(BoxObj("hull", -4, 4, -2, 2, -6, 6));
        try
        {
            var imported = HistoricalObjImporter.Import(path,
                HistoricalObjImportOptions.Metres("hull"));
            Require(imported.Success && imported.Mesh is not null,
                $"A closed labelled box must import: {string.Join("; ", imported.Diagnostics)}");
            var importedMesh = imported.Mesh!;
            Require(importedMesh.Bounds == new HullEnvelopeBounds(-4, 4, -2, 2, -6, 6),
                "Identity units/axes must preserve the source bounds.");
            Require(imported.Topology is { BoundaryEdgeCount: 0, NonManifoldEdgeCount: 0,
                    InconsistentWindingEdgeCount: 0, ConnectedComponentCount: 1 } &&
                    imported.Topology.SignedVolumeCubicMetres > 0,
                "The synthetic hull must be a closed outward-oriented manifold.");

            var swapped = HistoricalObjImporter.Import(path, new HistoricalObjImportOptions(
                0.5,
                new ObjAxisMapping(SignedSourceAxis.PositiveZ,
                    SignedSourceAxis.PositiveY, SignedSourceAxis.NegativeX),
                new HashSet<string>(["hull"], StringComparer.Ordinal)));
            Require(swapped.Success && swapped.Mesh!.Bounds.Length == 4 && swapped.Mesh.Bounds.Beam == 6,
                "A right-handed signed permutation and metre scale must be applied explicitly.");

            var reflected = HistoricalObjImporter.Import(path, new HistoricalObjImportOptions(
                1, new ObjAxisMapping(SignedSourceAxis.NegativeX,
                    SignedSourceAxis.PositiveY, SignedSourceAxis.PositiveZ),
                new HashSet<string>(["hull"], StringComparer.Ordinal)));
            Require(reflected.Success && reflected.Topology!.SignedVolumeCubicMetres > 0,
                "A reflected signed-axis map must reverse face indices exactly once and retain outward winding.");

            var duplicateAxis = HistoricalObjImporter.Import(path, new HistoricalObjImportOptions(
                1,
                new ObjAxisMapping(SignedSourceAxis.PositiveX,
                    SignedSourceAxis.NegativeX, SignedSourceAxis.PositiveZ),
                new HashSet<string>(["hull"], StringComparer.Ordinal)));
            Require(!duplicateAxis.Success && duplicateAxis.Diagnostics.Any(diagnostic =>
                    diagnostic.Code == HistoricalDiagnosticCodes.UnitsOrAxesInvalid),
                "Duplicate absolute axes must be rejected as a non-bijective map.");

            var wrongGroup = HistoricalObjImporter.Import(path,
                HistoricalObjImportOptions.Metres("superstructure"));
            Require(!wrongGroup.Success && wrongGroup.Diagnostics.Any(diagnostic =>
                    diagnostic.Code == HistoricalDiagnosticCodes.HullGroupInvalid),
                "The importer must not guess a hull from unselected groups.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void VerifyTopologyAndNumericRejection()
    {
        var openObj = BoxObj("hull", -1, 1, -1, 1, -1, 1)
            .Replace($"f 5 6 7{Environment.NewLine}f 5 7 8{Environment.NewLine}",
                string.Empty, StringComparison.Ordinal);
        var openPath = WriteTempObj(openObj);
        var nanPath = WriteTempObj("g hull\nv NaN 0 0\nv 0 0 0\nv 0 1 0\nf 1 2 3\n");
        var infinityPath = WriteTempObj("g hull\nv Infinity 0 0\nv 0 0 0\nv 0 1 0\nf 1 2 3\n");
        var scaleOverflowPath = WriteTempObj(BoxObj("hull", -3e33, 3e33, -2e33, 2e33, -4e33, 4e33));
        var indexPath = WriteTempObj("g hull\nv 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 2147483647\n");
        var negativePath = WriteTempObj(BoxObjNegativeIndices());
        var quadPath = WriteTempObj(BoxObjQuads(nonPlanar: false));
        var nonPlanarQuadPath = WriteTempObj(BoxObjQuads(nonPlanar: true));
        var crossedQuadPath = WriteTempObj(BoxObjQuads(nonPlanar: false)
            .Replace("f 1 4 3 2", "f 1 3 4 2", StringComparison.Ordinal));
        var inwardPath = WriteTempObj(ReverseWinding(BoxObj("hull", -1, 1, -1, 1, -1, 1)));
        var nonManifoldPath = WriteTempObj(BoxObj("hull", -1, 1, -1, 1, -1, 1) + "f 1 4 3\n");
        var disconnectedPath = WriteTempObj(DisconnectedBoxesObj());
        try
        {
            var open = HistoricalObjImporter.Import(openPath, HistoricalObjImportOptions.Metres("hull"));
            Require(!open.Success && open.Diagnostics.Any(diagnostic =>
                    diagnostic.Code == HistoricalDiagnosticCodes.TopologyOpen),
                "An open hull must be reported, not silently repaired.");
            var nan = HistoricalObjImporter.Import(nanPath, HistoricalObjImportOptions.Metres("hull"));
            Require(!nan.Success && nan.Diagnostics.Any(diagnostic =>
                    diagnostic.Code == HistoricalDiagnosticCodes.ObjSyntaxInvalid),
                "NaN input must be rejected.");
            Require(!HistoricalObjImporter.Import(infinityPath,
                    HistoricalObjImportOptions.Metres("hull")).Success,
                "Infinity input must be rejected.");
            Require(HistoricalObjImporter.Import(scaleOverflowPath,
                    new HistoricalObjImportOptions(100_000, ObjAxisMapping.Identity,
                        new HashSet<string>(["hull"], StringComparer.Ordinal))).Diagnostics.Any(diagnostic =>
                        diagnostic.Code == HistoricalDiagnosticCodes.UnitsOrAxesInvalid),
                "Finite coordinates whose unit transform overflows must be rejected.");
            var index = HistoricalObjImporter.Import(indexPath, HistoricalObjImportOptions.Metres("hull"));
            Require(!index.Success && index.Diagnostics.Any(diagnostic =>
                    diagnostic.Code == HistoricalDiagnosticCodes.ObjSyntaxInvalid),
                "Out-of-range face indices must be rejected deterministically.");
            var overBudget = HistoricalObjImporter.Import(openPath,
                HistoricalObjImportOptions.Metres("hull") with { MaxVertices = 4 });
            Require(!overBudget.Success && overBudget.Diagnostics.Any(diagnostic =>
                    diagnostic.Code == HistoricalDiagnosticCodes.InputLimitExceeded),
                "An OBJ exceeding the configured vertex budget must be rejected.");
            Require(HistoricalObjImporter.Import(negativePath,
                    HistoricalObjImportOptions.Metres("hull")).Success,
                "Valid already-parsed negative OBJ indices must import deterministically.");
            Require(HistoricalObjImporter.Import(quadPath,
                    HistoricalObjImportOptions.Metres("hull")).Success,
                "A planar, convex, simply ordered quadrilateral hull must triangulate.");
            Require(!HistoricalObjImporter.Import(nonPlanarQuadPath,
                    HistoricalObjImportOptions.Metres("hull")).Success,
                "A non-planar quadrilateral must be rejected before triangulation.");
            Require(!HistoricalObjImporter.Import(crossedQuadPath,
                    HistoricalObjImportOptions.Metres("hull")).Success,
                "A non-simple quadrilateral must be rejected before triangulation.");
            Require(HistoricalObjImporter.Import(inwardPath,
                    HistoricalObjImportOptions.Metres("hull")).Diagnostics.Any(diagnostic =>
                        diagnostic.Code == HistoricalDiagnosticCodes.WindingInvalid),
                "An inward closed mesh must be rejected.");
            Require(HistoricalObjImporter.Import(nonManifoldPath,
                    HistoricalObjImportOptions.Metres("hull")).Diagnostics.Any(diagnostic =>
                        diagnostic.Code == HistoricalDiagnosticCodes.TopologyNonManifold),
                "A non-manifold edge must be rejected.");
            Require(HistoricalObjImporter.Import(disconnectedPath,
                    HistoricalObjImportOptions.Metres("hull")).Diagnostics.Any(diagnostic =>
                        diagnostic.Code == HistoricalDiagnosticCodes.TopologyDisconnected),
                "Disconnected closed hull groups must be rejected.");
        }
        finally
        {
            File.Delete(openPath);
            File.Delete(nanPath);
            File.Delete(infinityPath);
            File.Delete(scaleOverflowPath);
            File.Delete(indexPath);
            File.Delete(negativePath);
            File.Delete(quadPath);
            File.Delete(nonPlanarQuadPath);
            File.Delete(crossedQuadPath);
            File.Delete(inwardPath);
            File.Delete(nonManifoldPath);
            File.Delete(disconnectedPath);
        }
    }

    private static void VerifyEnvelopeSamplingUnsupportedRowsAndSymmetry()
    {
        var path = WriteTempObj(BoxObj("hull", -4, 4, -2, 2, -6, 6));
        try
        {
            var mesh = HistoricalObjImporter.Import(path,
                HistoricalObjImportOptions.Metres("hull")).Mesh!;
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            RequireThrowsCancellation(() => HistoricalObjImporter.Import(path,
                    HistoricalObjImportOptions.Metres("hull"), cancelled.Token),
                "A cancelled import must not return a partial mesh.");
            RequireThrowsCancellation(() => HistoricalEnvelopeSampler.Sample(mesh,
                    new HistoricalEnvelopeSamplingOptions(6, 4), cancelled.Token),
                "A cancelled sampler must not return a partial envelope.");
            var sampled = HistoricalEnvelopeSampler.Sample(mesh,
                new HistoricalEnvelopeSamplingOptions(6, 4));
            Require(sampled.Success && sampled.Envelope is not null,
                $"A box must sample as one interval per row: {string.Join("; ", sampled.Diagnostics)}");
            var envelope = sampled.Envelope!;
            var status = envelope.QueryNormalized(0.5, 0.5, out var interval);
            Require(status == HullEnvelopeQueryStatus.Available &&
                    Math.Abs(interval.MinX + 4) < 1e-6 && Math.Abs(interval.MaxX - 4) < 1e-6,
                "A sampled box row must reconstruct its metre-space interval.");
            Require(envelope.QueryNormalized(-0.1, 0.5, out _) ==
                    HullEnvelopeQueryStatus.OutsideSampleDomain,
                "Out-of-domain queries must not clamp into invented coverage.");

            var twoBoxes = CombineBoxes((-4, -2), (2, 4));
            var unsupported = HistoricalEnvelopeSampler.Sample(twoBoxes,
                new HistoricalEnvelopeSamplingOptions(4, 2));
            Require(!unsupported.Success && unsupported.Envelope is not null &&
                    unsupported.Envelope.UnsupportedRows.Count == 8 &&
                    unsupported.Envelope.QueryNormalized(0.5, 0.5, out _) ==
                    HullEnvelopeQueryStatus.UnsupportedMultipleIntervals,
                "Disjoint row intervals must be retained as unsupported and never bridged.");

            var sheared = ShearedBoxMesh();
            var rejected = HistoricalEnvelopeSampler.Sample(sheared,
                new HistoricalEnvelopeSamplingOptions(4, 2,
                    HistoricalSymmetryPolicy.RejectAsymmetry, SymmetryToleranceMetres: 0.01));
            Require(!rejected.Success && rejected.Diagnostics.Any(diagnostic =>
                    diagnostic.Code == HistoricalDiagnosticCodes.SymmetryRejected && diagnostic.IsError),
                "Asymmetric rows must be rejected unless normalization is explicitly selected.");
            var normalized = HistoricalEnvelopeSampler.Sample(sheared,
                new HistoricalEnvelopeSamplingOptions(4, 2,
                    HistoricalSymmetryPolicy.NormalizeByOuterUnion, SymmetryToleranceMetres: 0.01));
            Require(normalized.Success && normalized.Envelope is { AsymmetryNormalizedRowCount: > 0 } &&
                    normalized.Diagnostics.Any(diagnostic =>
                        diagnostic.Code == HistoricalDiagnosticCodes.SymmetryRejected && !diagnostic.IsError),
                "Explicit outer-union normalization must succeed with a disclosed fidelity warning.");

            var overBudget = HistoricalEnvelopeSampler.Sample(mesh,
                new HistoricalEnvelopeSamplingOptions(3, 2, MaxTriangleTests: 1));
            Require(!overBudget.Success && overBudget.Diagnostics.Any(diagnostic =>
                    diagnostic.Code == HistoricalDiagnosticCodes.InputLimitExceeded),
                "Envelope sampling must stop before exceeding its triangle-test budget.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void VerifyShapeV2FitAndCancellation()
    {
        var parameters = HullParameters.Default with
        {
            Length = 24,
            Width = 11,
            Height = 8,
            Beamify = false,
            Smoothing = SmoothingMethod.None,
        };
        var source = RegionalShapeV2EnvelopeSource.Sample(parameters, 12, 8).Snapshot;
        var metadata = MetadataFor(source, parameters.Height);
        var fit = ShapeV2HullSourceFitter.Fit(source, metadata, parameters,
            new ShapeV2FitOptions(MaxEvaluations: 1));
        Require(fit.Accepted && fit.Parameters is { Length: 24, Width: 11, Height: 8 } &&
                fit.SourceFit.MaximumErrorMetres < 1e-9,
            "An exact Shape V2 sampled source must fit without changing its dimensions or shape.");

        var overBudget = ShapeV2HullSourceFitter.Fit(source, metadata, parameters,
            new ShapeV2FitOptions(MaxCandidateVoxelCells: 100));
        Require(!overBudget.Accepted && overBudget.Diagnostics.Any(diagnostic =>
                diagnostic.Code == HistoricalDiagnosticCodes.InputLimitExceeded),
            "Shape V2 fitting must reject a candidate before exceeding its voxel-cell budget.");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancellationObserved = false;
        try
        {
            ShapeV2HullSourceFitter.Fit(source, metadata, parameters,
                cancellationToken: cancelled.Token);
        }
        catch (OperationCanceledException)
        {
            cancellationObserved = true;
        }
        Require(cancellationObserved, "A cancelled fit must not return a partial candidate.");
    }

    private static void VerifyFidelityAndPackagingRemainIndependent()
    {
        var source = RegionalShapeV2EnvelopeSource.Sample(
            HullParameters.Default with { Length = 12, Width = 7, Height = 6, Beamify = false }, 6, 4).Snapshot;
        var fitMetadata = MetadataFor(source, 6);
        var fit = ShapeV2HullSourceFitter.Fit(source, fitMetadata,
            HullParameters.Default with { Length = 12, Width = 7, Height = 6, Beamify = false },
            new ShapeV2FitOptions(MaxEvaluations: 1));
        var smoothing = new HistoricalFidelityMetric(HistoricalFidelityStageStatus.Passed,
            0, 0, 0, 12, 1, "Native smoothing placement boundary change");
        var blocked = new HistoricalPackagingStatus(HistoricalRightsDisposition.RightsBlocked,
            HistoricalDimensionEvidence.Verified, "unresolved", null,
            DerivativesPermitted: false, RedistributionPermitted: false,
            HumanRightsReviewComplete: false);
        var blockedReport = HistoricalFidelityReports.FromFit(fit, source, smoothing, blocked,
            HistoricalAccuracyLabel.SourceFaithful);
        Require(blockedReport.GeometryAccepted && !blockedReport.CanUseNamedPreset &&
                HistoricalAuthoringOutcomes.ClassifyRepresentability(
                    shapeFitAccepted: true, fidelity: blockedReport) ==
                HistoricalAuthoringOutcome.SourceOrRightsBlocked &&
                HistoricalAuthoringOutcome.SourceOrRightsBlocked.ExitCode() == 5,
            "Passing geometry must not override a rights-blocked packaging status.");

        var unverifiedDimensions = blocked with
        {
            Rights = HistoricalRightsDisposition.ClearedForDerivedRedistribution,
            Dimensions = HistoricalDimensionEvidence.Unverified,
            LicenseIdentifier = "CC0-1.0",
            DerivativesPermitted = true,
            RedistributionPermitted = true,
            HumanRightsReviewComplete = true,
        };
        var dimensionReport = blockedReport with { Packaging = unverifiedDimensions };
        Require(!dimensionReport.CanUseNamedPreset,
            "Unverified roster dimensions must not become a usable named preset.");

        var cleared = unverifiedDimensions with { Dimensions = HistoricalDimensionEvidence.Verified };
        var clearedReport = blockedReport with { Packaging = cleared };
        Require(clearedReport.GeometryAccepted && !clearedReport.CanUseNamedPreset &&
                !clearedReport.RuntimeRepresentationSupported &&
                HistoricalAuthoringOutcomes.ClassifyRepresentability(
                    shapeFitAccepted: true, fidelity: clearedReport) ==
                HistoricalAuthoringOutcome.UnsupportedByCurrentEnvelopeRepresentation,
            "Shape V2 fit evidence must remain a report-only unsupported outcome until fitted parameters are runtime-bound.");
        var packagedSource = CopyWithTransform(source);
        var metadata = new HistoricalEnvelopeMetadata("synthetic-box", "1", packagedSource.SourceContentHash,
            "Synthetic authored fixture", "Synthetic fixture", "not historical",
            HistoricalConstructionStatus.Built, new HistoricalSourceTransform(1, ObjAxisMapping.Identity),
            WaterlineMetres: 0, DeckDatumMetres: 5.5, KeelDatumMetres: -0.5);
        clearedReport = clearedReport with { SourceContentHash = packagedSource.SourceContentHash };
        var shapePackageRejected = false;
        try
        {
            HistoricalEnvelopeAsset.CreateForPackaging(metadata, packagedSource, clearedReport);
        }
        catch (InvalidOperationException)
        {
            shapePackageRejected = true;
        }
        Require(shapePackageRejected,
            "A Shape V2 fit report must not become a runtime asset without serialized fitted parameters.");
    }

    private static void VerifyProfileAndBulbFitWithAnalyticOracles()
    {
        var profileSource = AnalyticEnvelope(20, 9, -0.5, 7.5, "analytic-raised-profile",
            (z, y) =>
            {
                var end = Math.Max(Math.Max(0, (0.25 - z) / 0.25), Math.Max(0, (z - 0.75) / 0.25));
                var floor = z > 0.75 ? -0.5 + 2 * (z - 0.75) / 0.25 : -0.5;
                var deck = 5.5 + 2 * end;
                return y >= floor && y <= deck ? 4.5 : (double?)null;
            });
        var profileMetadata = new HistoricalEnvelopeMetadata("analytic-profile", "1",
            profileSource.SourceContentHash, "Independent raised sheer/keel oracle", "Synthetic",
            "not historical", HistoricalConstructionStatus.Built, profileSource.SourceTransform!,
            0, 5.5, -0.5);
        var profileTemplate = HullParameters.Default with
            { Length = 20, Width = 9, Height = 6, Beamify = false };
        var profileBaseline = ShapeV2HullSourceFitter.Fit(profileSource, profileMetadata,
            profileTemplate, new ShapeV2FitOptions(MaxEvaluations: 1));
        var profileFit = ShapeV2HullSourceFitter.Fit(profileSource, profileMetadata,
            profileTemplate,
            new ShapeV2FitOptions(MaxEvaluations: 400));
        var expectedProfile = new HullProfileSettings(1, 2, 1, 0);
        Require(profileFit.Parameters?.EffectiveShape.Profile == expectedProfile &&
                profileBaseline.SourceFit.MeanErrorMetres > 2.5 &&
                profileFit.SourceFit.MeanErrorMetres < 0.4 &&
                profileBaseline.SourceFit.MeanErrorMetres - profileFit.SourceFit.MeanErrorMetres > 2 &&
                Math.Abs(profileFit.SourceFit.MaximumErrorMetres - 4.5) < 1e-9 &&
                profileFit.SourceFit.AcceptanceThresholdMetres == 1 &&
                profileFit.SourceFit.Status == HistoricalFidelityStageStatus.Failed,
            $"Independent raised-sheer/keel oracle expected {expectedProfile}, >2 m mean improvement, " +
            $"and honest 4.5 m max-threshold failure; got {profileFit.Parameters?.EffectiveShape.Profile}, " +
            $"{profileBaseline.SourceFit.MeanErrorMetres:0.###}->{profileFit.SourceFit.MeanErrorMetres:0.###}, " +
            $"max {profileFit.SourceFit.MaximumErrorMetres:0.###}.");

        var bulbSource = AnalyticEnvelope(24, 11, -2.5, 5.5, "analytic-bulb",
            (z, y) =>
            {
                if (y >= -0.5)
                {
                    var bowTaper = z <= 0.7 ? 1 : Math.Max(0.15, 1 - (z - 0.7) / 0.3);
                    return 5.5 * bowTaper;
                }
                var dz = (z - 0.88) / 0.16;
                var dy = (y + 1.4) / 1.2;
                var radius = 1 - dz * dz - dy * dy;
                return radius > 0 ? 3.0 * Math.Sqrt(radius) : (double?)null;
            });
        var bulbMetadata = new HistoricalEnvelopeMetadata("analytic-bulb", "1",
            bulbSource.SourceContentHash, "Independent ellipsoid-union bulb oracle", "Synthetic",
            "not historical", HistoricalConstructionStatus.Built, bulbSource.SourceTransform!,
            0, 5.5, -0.5);
        var wrongBulb = BulbSettings.Default;
        var bulbTemplate = HullParameters.Default with { Length = 24, Width = 11, Height = 6,
            Beamify = false, HasBulb = true, Bulb = wrongBulb };
        var bulbBaseline = ShapeV2HullSourceFitter.Fit(bulbSource, bulbMetadata, bulbTemplate,
            new ShapeV2FitOptions(MaxEvaluations: 1, IncludeBulb: true));
        var bulbFit = ShapeV2HullSourceFitter.Fit(bulbSource, bulbMetadata,
            bulbTemplate,
            new ShapeV2FitOptions(MaxEvaluations: 500, IncludeBulb: true));
        var expectedBulb = new BulbSettings(8, 35, 0, -50);
        Require(bulbFit.Parameters is { HasBulb: true } &&
                bulbFit.Parameters.EffectiveBulb == expectedBulb &&
                bulbBaseline.SourceFit.MeanErrorMetres > 2.5 &&
                bulbFit.SourceFit.MeanErrorMetres < 0.35 &&
                bulbBaseline.SourceFit.MeanErrorMetres - bulbFit.SourceFit.MeanErrorMetres > 2 &&
                Math.Abs(bulbFit.SourceFit.MaximumErrorMetres - 5.5) < 1e-9 &&
                bulbFit.SourceFit.AcceptanceThresholdMetres == 1 &&
                bulbFit.SourceFit.Status == HistoricalFidelityStageStatus.Failed,
            $"Independent lower-bow ellipsoid oracle expected {expectedBulb}, >2 m mean improvement, " +
            $"and honest 5.5 m max-threshold failure; got {bulbFit.Parameters?.EffectiveBulb}, " +
            $"{bulbBaseline.SourceFit.MeanErrorMetres:0.###}->{bulbFit.SourceFit.MeanErrorMetres:0.###}, " +
            $"max {bulbFit.SourceFit.MaximumErrorMetres:0.###}.");
    }

    private static SampledHullEnvelope AnalyticEnvelope(
        int length, int width, double minY, double maxY, string hash,
        Func<double, double, double?> halfBreadth)
    {
        const int stations = 24;
        const int rows = 16;
        var z = Enumerable.Range(0, stations).Select(index => (index + 0.5) / stations).ToArray();
        var y = Enumerable.Range(0, rows).Select(index => (index + 0.5) / rows).ToArray();
        var intervals = new HullEnvelopeInterval?[stations, rows];
        for (var station = 0; station < stations; station++)
        for (var row = 0; row < rows; row++)
        {
            var worldY = minY + y[row] * (maxY - minY);
            if (halfBreadth(z[station], worldY) is not { } half || half <= 0)
                continue;
            intervals[station, row] = new HullEnvelopeInterval(
                0.5 - half / width, 0.5 + half / width);
        }
        return new SampledHullEnvelope(new HullEnvelopeBounds(-width / 2.0, width / 2.0,
                minY, maxY, -length / 2.0, length / 2.0), z, y, intervals, [], 0, [], hash,
            new HistoricalSourceTransform(1, ObjAxisMapping.Identity));
    }

    private static void VerifyValidatedFallbackComposition()
    {
        foreach (var width in new[] { 7, 6 })
        {
            var source = AnalyticBoxEnvelope(6, width, 5, $"analytic-box-{width}");
            var parameters = HullParameters.Default with
            {
                Length = 6,
                Width = width,
                Height = 5,
                HullArmor = new ArmorLayout([new ArmorLayer(MaterialKind.Metal), ArmorLayer.Air]),
                BottomArmor = ArmorLayout.Single(MaterialKind.Wood),
                DeckArmor = ArmorLayout.Single(MaterialKind.LightweightAlloy),
                Beamify = false,
                Smoothing = SmoothingMethod.None,
                Superstructure = null,
            };
            var metadata = new HistoricalEnvelopeMetadata($"box-{width}", "1", source.SourceContentHash,
                "Independent analytic box", "Synthetic", "not historical",
                HistoricalConstructionStatus.Built, source.SourceTransform!, 0, 4.5, -0.5);
            var cleared = new HistoricalPackagingStatus(
                HistoricalRightsDisposition.ClearedForDerivedRedistribution,
                HistoricalDimensionEvidence.Verified, "CC0-1.0", null,
                DerivativesPermitted: true, RedistributionPermitted: true,
                HumanRightsReviewComplete: true);
            var exact = new HistoricalFidelityMetric(HistoricalFidelityStageStatus.Passed,
                0, 0, 0, 10, 1, "Independent analytic source-section comparison");
            var smoothing = HistoricalFidelityMetric.NotMeasured("Native smoothing change",
                "No native smoothing was requested or applied.");
            var authored = HistoricalEnvelopeCompositionAdapter.EvaluateForAuthoring(
                source, metadata, parameters);
            var report = HistoricalFidelityReports.FromSampledFallback(source, exact,
                authored.VoxelQuantization, authored.Validation, smoothing, cleared,
                HistoricalAccuracyLabel.Approximate);
            var asset = HistoricalEnvelopeAsset.CreateForPackaging(metadata, source, report);
            var path = Path.Combine(Path.GetTempPath(), $"HullForge-H02-asset-{Guid.NewGuid():N}.json");
            try
            {
                File.WriteAllText(path, asset.ToJson(), new UTF8Encoding(false));
                var loaded = HistoricalEnvelopeAssetLoader.Load(path);
                Require(loaded.Success, string.Join("; ", loaded.Diagnostics));
                Require(!HistoricalEnvelopeAssetLoader.Load(path, maxBytes: 32).Success,
                    "The runtime loader must reject an asset beyond its byte budget.");
                Require(!HistoricalEnvelopeAssetLoader.Validate(asset with { SchemaVersion = 1 }).Success,
                    "The runtime loader must reject an unsupported envelope schema version.");
                Require(!HistoricalEnvelopeAssetLoader.Validate(asset with
                    { AssetContentHash = new string('0', 64) }).Success,
                    "The runtime loader must reject a package whose normalized content hash was altered.");
                Require(!HistoricalEnvelopeAssetLoader.Validate(asset with
                    { Rows = asset.Rows.Concat([asset.Rows[0]]).ToArray() }).Success,
                    "The runtime loader must reject duplicated/non-rectangular rows.");
                var excessiveThresholdFidelity = report with
                {
                    VoxelQuantization = report.VoxelQuantization with
                        { AcceptanceThresholdMetres = 1.25 },
                };
                var excessiveThresholdAsset = asset with { Fidelity = excessiveThresholdFidelity };
                excessiveThresholdAsset = excessiveThresholdAsset with
                {
                    AssetContentHash = HistoricalEnvelopeAsset.ComputeContentHash(
                        excessiveThresholdAsset.Metadata, excessiveThresholdAsset.SourceBoundsMetres,
                        excessiveThresholdAsset.Rows, excessiveThresholdAsset.Fidelity),
                };
                Require(!HistoricalEnvelopeAssetLoader.Validate(excessiveThresholdAsset).Success,
                    "The loader must reject a voxel threshold outside the runtime evaluator's range.");
                var context = HistoricalEnvelopeCompositionAdapter.CreateContext(loaded.Loaded!, parameters);
                var repeatedContext = HistoricalEnvelopeCompositionAdapter.CreateContext(loaded.Loaded!, parameters);
                Require(report.GeometryAccepted && report.FallbackValidation is
                    { UsableCavityCellCount: > 0, FaceConnected: true, ContextHullBoundsMatch: true } &&
                    report.VoxelQuantization.Measurement.Contains("X/Y/Z", StringComparison.Ordinal),
                    "Fallback fidelity must describe the exact connected adapter output, cavity and three-axis error.");
                Require(context.MinX == authored.Hull.MinX && context.MaxX == authored.Hull.MaxX &&
                        context.MinY == authored.Hull.MinY && context.MaxY == authored.Hull.MaxY &&
                        context.MinZ == authored.Hull.MinZ && context.MaxZ == authored.Hull.MaxZ,
                    "Common context and materialized hull must share tightened occupied bounds.");
                Require(context.MirrorSum == (width % 2 == 0 ? -1 : 0),
                    "Historical voxelization must preserve odd/even center-plane parity.");
                Require(context.RoleAt(0, 2, 0) == HullCellRole.Cavity &&
                        context.RoleAt(0, 4, 0) == HullCellRole.DeckArmor &&
                        context.RoleAt(0, 0, 0) == HullCellRole.BottomArmor &&
                        context.IsReservedArmorAir((-(width / 2) + width - 1) - 1, 2, 0) &&
                        context.IsProtectedShellCell(-(width / 2), 2, 0),
                    "Fallback context must preserve cavity, deck, bottom and reserved armor-air intent.");
                var minX = -(width / 2);
                var maxX = minX + width - 1;
                var minZ = -(parameters.Length / 2);
                var maxZ = minZ + parameters.Length - 1;
                for (var z = minZ; z <= maxZ; z++)
                for (var y = 0; y < parameters.Height; y++)
                for (var x = minX; x <= maxX; x++)
                {
                    if (x != minX && x != maxX && y != 0 && y != parameters.Height - 1 &&
                        z != minZ && z != maxZ)
                        continue;
                    Require(context.TryGetArmor(x, y, z, out var boundary) &&
                            boundary.IsStructuralArmor,
                        $"Fallback box shell opened at boundary ({x},{y},{z}).");
                }
                Require(context.EnumerateArmor().SequenceEqual(repeatedContext.EnumerateArmor()),
                    "Historical armor cell ownership must be deterministic.");
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                RequireThrowsCancellation(() => HistoricalEnvelopeCompositionAdapter.CreateContext(
                        loaded.Loaded!, parameters, cancellationToken: cancelled.Token),
                    "Historical fallback cancellation must return no partial context.");
                var bounded = false;
                try
                {
                    HistoricalEnvelopeCompositionAdapter.CreateContext(loaded.Loaded!, parameters,
                        maxPhysicalCells: 10);
                }
                catch (HullGenerationException)
                {
                    bounded = true;
                }
                Require(bounded, "Historical fallback must enforce its voxel-domain budget before allocation.");

                var document = ShipDocument.CreateNew("Historical box", parameters, $"hist-{width}") with
                {
                    Source = HullSource.Historical(metadata.AssetId, metadata.AssetVersion,
                        asset.AssetContentHash),
                };
                var result = new ShipGenerationService().GenerateHistorical(
                    document, 41 + width, EmptyCatalog(), loaded.Loaded!);
                Require(result.IsValid && result.Snapshot!.Hull.Blocks.Count > 0 &&
                        result.Snapshot.Hull.Blocks.All(block => block.Origin != BlockOrigin.Smoothing),
                    $"Validated sampled fallback must compose/export as conservative armor cubes: " +
                    string.Join("; ", result.Diagnostics));

                var forgedLoaded = new LoadedHistoricalEnvelopeAsset(
                    excessiveThresholdAsset, source);
                var forgedDocument = document with
                {
                    Source = HullSource.Historical(metadata.AssetId, metadata.AssetVersion,
                        excessiveThresholdAsset.AssetContentHash),
                };
                var forgedResult = new ShipGenerationService().GenerateHistorical(
                    forgedDocument, 1, EmptyCatalog(), forgedLoaded);
                Require(!forgedResult.IsValid && forgedResult.Diagnostics.Any(diagnostic =>
                        diagnostic.Code == ShipCompositionDiagnosticCodes.UnsupportedHullSource),
                    "Even a forged in-memory package with an excessive threshold must fail " +
                    "diagnostically rather than escape an ArgumentOutOfRangeException.");

                var mismatched = document with { Source = HullSource.Historical(
                    metadata.AssetId, metadata.AssetVersion, "wrong-hash") };
                Require(!new ShipGenerationService().GenerateHistorical(
                        mismatched, 1, EmptyCatalog(), loaded.Loaded!).IsValid,
                    "Historical composition must reject a loaded package not named by the document hash.");
                var smoothingRequest = document with
                    { Hull = document.Hull with { Smoothing = SmoothingMethod.VerticalSlopeFill } };
                var smoothingResult = new ShipGenerationService().GenerateHistorical(
                    smoothingRequest, 1, EmptyCatalog(), loaded.Loaded!);
                Require(!smoothingResult.IsValid && smoothingResult.Diagnostics.Any(diagnostic =>
                        diagnostic.Code == ShipCompositionDiagnosticCodes.HistoricalSmoothingUnavailable &&
                        diagnostic.IsError),
                    "Historical fallback must reject a non-None smoothing request instead of downgrading it.");
            }
            finally
            {
                File.Delete(path);
            }
        }

        var unscaledIntervals = new HullEnvelopeInterval?[6, 5];
        for (var station = 0; station < 6; station++)
        for (var row = 0; row < 5; row++)
            unscaledIntervals[station, row] = new HullEnvelopeInterval(
                (-2.55 + 3.2) / 6.4, (2.55 + 3.2) / 6.4);
        var unscaledSource = new SampledHullEnvelope(
            new HullEnvelopeBounds(-3.2, 3.2, -0.5, 4.5, -3, 3),
            Enumerable.Range(0, 6).Select(index => (index + 0.5) / 6).ToArray(),
            Enumerable.Range(0, 5).Select(index => (index + 0.5) / 5).ToArray(),
            unscaledIntervals, [], 0, [], "analytic-unscaled",
            new HistoricalSourceTransform(1, ObjAxisMapping.Identity));
        var unscaledMetadata = new HistoricalEnvelopeMetadata("unscaled", "1",
            unscaledSource.SourceContentHash, "Physical-width quantization fixture", "Synthetic",
            "not historical", HistoricalConstructionStatus.Built, unscaledSource.SourceTransform!,
            0, 4.5, -0.5);
        var unscaledPassed = new HistoricalFidelityMetric(HistoricalFidelityStageStatus.Passed,
            0, 0, 0, 10, 1, "Independent synthetic comparison");
        var unscaledRights = new HistoricalPackagingStatus(
            HistoricalRightsDisposition.ClearedForDerivedRedistribution,
            HistoricalDimensionEvidence.Verified, "CC0-1.0", null, true, true, true);
        var unscaledParameters = HullParameters.Default with { Length = 6, Width = 6, Height = 5,
            HullArmor = ArmorLayout.Single(MaterialKind.Metal),
            BottomArmor = ArmorLayout.Single(MaterialKind.Metal),
            DeckArmor = ArmorLayout.Single(MaterialKind.Metal), Beamify = false,
            Smoothing = SmoothingMethod.None, Superstructure = null };
        var unscaledEvaluation = HistoricalEnvelopeCompositionAdapter.EvaluateForAuthoring(
            unscaledSource, unscaledMetadata, unscaledParameters);
        var unscaledReport = HistoricalFidelityReports.FromSampledFallback(unscaledSource,
            unscaledPassed, unscaledEvaluation.VoxelQuantization, unscaledEvaluation.Validation,
            HistoricalFidelityMetric.NotMeasured("Native smoothing change"), unscaledRights,
            HistoricalAccuracyLabel.Approximate);
        var unscaledLoaded = HistoricalEnvelopeAssetLoader.Validate(
            HistoricalEnvelopeAsset.CreateForPackaging(
                unscaledMetadata, unscaledSource, unscaledReport)).Loaded!;
        var unscaledContext = HistoricalEnvelopeCompositionAdapter.CreateContext(
            unscaledLoaded, unscaledParameters);
        Require(unscaledContext.IsOccupied(-3, 2, 0) && unscaledContext.IsOccupied(2, 2, 0),
            "Fallback voxelization must quantize physical source boundaries without silently rescaling beam.");

        var disconnected = AnalyticBoxEnvelope(6, 7, 5, "analytic-disconnected");
        var disconnectedIntervals = new HullEnvelopeInterval?[disconnected.StationCount, disconnected.RowCount];
        for (var station = 0; station < disconnected.StationCount; station++)
        for (var row = 0; row < disconnected.RowCount; row++)
            disconnectedIntervals[station, row] = station == disconnected.StationCount / 2
                ? null : disconnected.GetNormalizedInterval(station, row);
        var disconnectedSource = new SampledHullEnvelope(disconnected.Bounds,
            Enumerable.Range(0, disconnected.StationCount).Select(disconnected.GetNormalizedZ).ToArray(),
            Enumerable.Range(0, disconnected.RowCount).Select(disconnected.GetNormalizedY).ToArray(),
            disconnectedIntervals, [], 0, [], disconnected.SourceContentHash, disconnected.SourceTransform);
        var disconnectedMetadata = new HistoricalEnvelopeMetadata("disconnected", "1",
            disconnectedSource.SourceContentHash, "Disconnected analytic fixture", "Synthetic",
            "not historical", HistoricalConstructionStatus.Built, disconnectedSource.SourceTransform!,
            0, 4.5, -0.5);
        var disconnectedRejected = false;
        try
        {
            HistoricalEnvelopeCompositionAdapter.EvaluateForAuthoring(
                disconnectedSource, disconnectedMetadata,
                HullParameters.Default with { Length = 6, Width = 7, Height = 5,
                    Beamify = false, Smoothing = SmoothingMethod.None, Superstructure = null });
        }
        catch (HullGenerationException)
        {
            disconnectedRejected = true;
        }
        Require(disconnectedRejected,
            "A face-disconnected sampled fallback must be rejected before composition.");

        var shallow = AnalyticBoxEnvelope(6, 5, 2, "analytic-no-cavity");
        var shallowMetadata = new HistoricalEnvelopeMetadata("no-cavity", "1",
            shallow.SourceContentHash, "No-cavity analytic fixture", "Synthetic",
            "not historical", HistoricalConstructionStatus.Built, shallow.SourceTransform!,
            0, 1.5, -0.5);
        var noCavityRejected = false;
        try
        {
            HistoricalEnvelopeCompositionAdapter.EvaluateForAuthoring(
                shallow, shallowMetadata,
                HullParameters.Default with { Length = 6, Width = 5, Height = 2,
                    HullArmor = ArmorLayout.Single(MaterialKind.Metal),
                    BottomArmor = ArmorLayout.Single(MaterialKind.Metal),
                    DeckArmor = ArmorLayout.Single(MaterialKind.Metal),
                    Beamify = false, Smoothing = SmoothingMethod.None, Superstructure = null });
        }
        catch (HullGenerationException)
        {
            noCavityRejected = true;
        }
        Require(noCavityRejected,
            "A sampled fallback whose armor consumes every usable cavity cell must fail closed.");
    }

    private static void VerifyEvidenceBackedHistoricalDatums()
    {
        const int stationCount = 10;
        const int rowCount = 9;
        var z = Enumerable.Range(0, stationCount)
            .Select(index => (index + 0.5) / stationCount).ToArray();
        var y = Enumerable.Range(0, rowCount)
            .Select(index => (index + 0.5) / rowCount).ToArray();
        var intervals = new HullEnvelopeInterval?[stationCount, rowCount];
        for (var station = 0; station < stationCount; station++)
        for (var row = 0; row < rowCount; row++)
        {
            // Baseline body crosses the declared 4.5 m deck datum. Station 8 carries both a
            // raised local deck and the bulb root; station 9 is a bulb-only forward extremum.
            var body = station <= 7 && row is >= 2 and <= 6;
            var raisedAndRoot = station == 8;
            var bulbTip = station == 9 && row <= 1;
            if (body || raisedAndRoot || bulbTip)
                intervals[station, row] = new HullEnvelopeInterval(0, 1);
        }

        var source = new SampledHullEnvelope(
            new HullEnvelopeBounds(-3.5, 3.5, -2.5, 6.5, -5, 5),
            z, y, intervals, [], 0, [], "analytic-datum-bulb",
            new HistoricalSourceTransform(1, ObjAxisMapping.Identity));
        var metadata = new HistoricalEnvelopeMetadata("datum-bulb", "1",
            source.SourceContentHash, "Raised-deck and bulb datum oracle", "Synthetic",
            "not historical", HistoricalConstructionStatus.Built, source.SourceTransform!,
            WaterlineMetres: 1.5, DeckDatumMetres: 4.5, KeelDatumMetres: -0.5);
        var parameters = HullParameters.Default with
        {
            Length = 10,
            Width = 7,
            Height = 5,
            HullArmor = ArmorLayout.Single(MaterialKind.Metal),
            BottomArmor = ArmorLayout.Single(MaterialKind.Wood),
            DeckArmor = ArmorLayout.Single(MaterialKind.LightweightAlloy),
            Beamify = false,
            Smoothing = SmoothingMethod.None,
            Superstructure = null,
        };
        var authored = HistoricalEnvelopeCompositionAdapter.EvaluateForAuthoring(
            source, metadata, parameters);
        Require(authored.Context.MinY == -2 && authored.Context.MaxY == 6 &&
                authored.Context.MinZ == -5 && authored.Context.MaxZ == 4,
            "Bulb and raised-sheer extrema must remain in tightened occupied bounds.");
        Require(authored.Context.DeckYAt(3) == 6 && authored.Context.DeckYAt(4) == int.MinValue &&
                authored.Context.FloorYAt(3) == -2,
            "A raised station crossing the declared datum must retain its local deck, while a " +
            "bulb-only extremum must not become deck support.");
        Require(authored.Context.EnumerateArmor().Where(intent =>
                    intent.Region == ArmorRegion.Deck && intent.IsStructuralArmor)
                .Max(intent => intent.Cell.Z) == 3 &&
                !authored.Context.EnumerateArmor().Any(intent =>
                    intent.Region == ArmorRegion.Deck && intent.Cell.Z == 4),
            "Structural deck classification must stop at the evidence-backed deck interval, not the bulb tip.");
        Require(authored.VoxelQuantization.Status == HistoricalFidelityStageStatus.Passed &&
                authored.VoxelQuantization.Measurement.Contains("declared datums", StringComparison.Ordinal),
            "Voxel fidelity must compare declared datums independently of local/global extrema.");

        var direct = new HistoricalFidelityMetric(HistoricalFidelityStageStatus.Passed,
            0, 0, 0, 20, 1, "Independent direct analytic boundary comparison");
        var cleared = new HistoricalPackagingStatus(
            HistoricalRightsDisposition.ClearedForDerivedRedistribution,
            HistoricalDimensionEvidence.Verified, "CC0-1.0", null, true, true, true);
        var fidelity = HistoricalFidelityReports.FromSampledFallback(source, direct,
            authored.VoxelQuantization, authored.Validation,
            HistoricalFidelityMetric.NotMeasured("Native smoothing change"), cleared,
            HistoricalAccuracyLabel.Approximate);
        var asset = HistoricalEnvelopeAsset.CreateForPackaging(metadata, source, fidelity);
        var loaded = HistoricalEnvelopeAssetLoader.Validate(asset).Loaded!;
        var definition = BarbetteDefinition.Create("datum-ring", "datum-node",
            DesignMeasure.FromMetres(1), 1, neckClearSizeMetres: 1);
        var document = ShipDocument.CreateNew("Historical datum", parameters, "h02-datum") with
        {
            Source = HullSource.Historical(metadata.AssetId, metadata.AssetVersion,
                asset.AssetContentHash),
            Datum = new LayoutDatum(DesignMeasure.FromMetres(3.5), DesignMeasure.Zero),
            Arrangement = new Arrangement(
                [ArrangementNode.Create("datum-node", ArrangementNodeKind.Barbette,
                    definition.Id, DesignMeasure.FromMetres(1.5))], [], [],
                DesignMeasure.FromMetres(2), DesignMeasure.FromMetres(4),
                ArrangementAnchorKind.BowDatum, ArrangementResizePolicy.PreserveAbsolute),
            Barbettes = [definition],
        };
        var generated = new ShipGenerationService().GenerateHistorical(
            document, 1, EmptyCatalog(), loaded);
        Require(generated.IsValid && generated.Snapshot!.Arrangement.SupportedRulerEnd ==
                DesignMeasure.FromMetres(9),
            "LayoutBowDatum must resolve from the forward structural deck face at 3.5 m, not " +
            $"the bulb/global occupied face at 4.5 m: {string.Join("; ", generated.Diagnostics)}");
        var bulbDatum = document with
        {
            Datum = new LayoutDatum(DesignMeasure.FromMetres(4.5), DesignMeasure.Zero),
        };
        var rejected = new ShipGenerationService().GenerateHistorical(
            bulbDatum, 1, EmptyCatalog(), loaded);
        Require(!rejected.IsValid && rejected.Diagnostics.Any(diagnostic =>
                diagnostic.Code == ShipCompositionDiagnosticCodes.LayoutDatumUnsupported &&
                diagnostic.Realized == DesignMeasure.FromMetres(3.5)),
            "A bulb tip must never be accepted as the historical arrangement bow datum.");
    }

    private static void VerifyFiniteOutcomesAndBoundedReports()
    {
        var source = AnalyticBoxEnvelope(60, 21, 10, "large-fallback-summary");
        var metadata = new HistoricalEnvelopeMetadata("large-summary", "1",
            source.SourceContentHash, "Large bounded fallback summary", "Synthetic",
            "not historical", HistoricalConstructionStatus.Built, source.SourceTransform!,
            0, 9.5, -0.5);
        var parameters = HullParameters.Default with
        {
            Length = 60, Width = 21, Height = 10,
            HullArmor = ArmorLayout.Single(MaterialKind.Metal),
            BottomArmor = ArmorLayout.Single(MaterialKind.Metal),
            DeckArmor = ArmorLayout.Single(MaterialKind.Metal),
            Beamify = false, Smoothing = SmoothingMethod.None, Superstructure = null,
        };
        var evaluation = HistoricalEnvelopeCompositionAdapter.EvaluateForAuthoring(
            source, metadata, parameters);
        Require(evaluation.Hull.Blocks.Count > 1_000,
            "The bounded-report oracle must exercise a materially large fallback hull.");
        var direct = new HistoricalFidelityMetric(HistoricalFidelityStageStatus.Passed,
            0, 0, 0, 20, 1, "Independent direct analytic comparison");
        var cleared = new HistoricalPackagingStatus(
            HistoricalRightsDisposition.ClearedForDerivedRedistribution,
            HistoricalDimensionEvidence.Verified, "CC0-1.0", null, true, true, true);
        var fidelity = HistoricalFidelityReports.FromSampledFallback(source, direct,
            evaluation.VoxelQuantization, evaluation.Validation,
            HistoricalFidelityMetric.NotMeasured("Native smoothing change"), cleared,
            HistoricalAccuracyLabel.Approximate);
        Require(HistoricalAuthoringOutcomes.ClassifyRepresentability(false, fidelity) ==
                HistoricalAuthoringOutcome.RepresentableWithDocumentedApproximation &&
                HistoricalAuthoringOutcome.RepresentableWithDocumentedApproximation.ExitCode() == 0 &&
                HistoricalAuthoringOutcomes.ClassifyRepresentability(false,
                    fidelity with { RequestedAccuracy = HistoricalAccuracyLabel.SourceFaithful }) ==
                HistoricalAuthoringOutcome.Representable &&
                HistoricalAuthoringOutcome.InvalidInput.ExitCode() == 2 &&
                HistoricalAuthoringOutcome.UnsupportedByCurrentEnvelopeRepresentation.ExitCode() == 4,
            "The five finite authoring outcomes must retain stable classification and exit behavior.");

        var report = new HistoricalAuthoringReport(
            HistoricalAuthoringReport.CurrentSchemaVersion,
            HistoricalAuthoringOutcome.RepresentableWithDocumentedApproximation,
            "complete", metadata, source.SourceContentHash, null,
            new HistoricalSamplingReportSummary(source.StationCount, source.RowCount, 0, 0),
            null, new HistoricalFallbackReportSummary(
                evaluation.VoxelQuantization, evaluation.Validation), fidelity, []);
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        var bytes = HistoricalAuthoringReportSerializer.Serialize(report, options);
        var json = Encoding.UTF8.GetString(bytes);
        Require(bytes.Length < HistoricalAuthoringReportSerializer.DefaultMaxReportBytes &&
                json.Contains("\"outcome\": \"representable with documented approximation\"",
                    StringComparison.Ordinal) &&
                !json.Contains("\"blocks\"", StringComparison.OrdinalIgnoreCase) &&
                !json.Contains("occupiedCells", StringComparison.OrdinalIgnoreCase) &&
                !json.Contains("generatedHull", StringComparison.OrdinalIgnoreCase),
            $"A large fallback report must serialize only its bounded summary and exact typed outcome: " +
            json[..Math.Min(json.Length, 240)]);
        foreach (var (typed, serialized) in new Dictionary<HistoricalAuthoringOutcome, string>
                 {
                     [HistoricalAuthoringOutcome.Representable] = "representable",
                     [HistoricalAuthoringOutcome.RepresentableWithDocumentedApproximation] =
                         "representable with documented approximation",
                     [HistoricalAuthoringOutcome.UnsupportedByCurrentEnvelopeRepresentation] =
                         "unsupported by current envelope representation",
                     [HistoricalAuthoringOutcome.SourceOrRightsBlocked] = "source/rights blocked",
                     [HistoricalAuthoringOutcome.InvalidInput] = "invalid input",
                 })
        {
            using var parsed = JsonDocument.Parse(HistoricalAuthoringReportSerializer.Serialize(
                report with { Outcome = typed }, options));
            Require(parsed.RootElement.GetProperty("outcome").GetString() == serialized,
                $"Finite authoring outcome {typed} did not serialize as '{serialized}'.");
        }

        var oversized = report with { Diagnostics = [new string('x', 2 * 1024 * 1024)] };
        var capped = false;
        try
        {
            HistoricalAuthoringReportSerializer.Serialize(oversized, options);
        }
        catch (InvalidDataException)
        {
            capped = true;
        }
        Require(capped, "Report serialization must fail before exceeding its one-MiB output cap.");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        RequireThrowsCancellation(() => HistoricalAuthoringReportSerializer.Serialize(
                report, options, cancellationToken: cancelled.Token),
            "Cancelled report serialization must emit no partial payload.");
        var cancelledPath = Path.Combine(Path.GetTempPath(),
            $"HullForge-H02-cancelled-report-{Guid.NewGuid():N}.json");
        File.WriteAllText(cancelledPath, "sentinel", new UTF8Encoding(false));
        try
        {
            RequireThrowsCancellation(() => HistoricalAuthoringReportSerializer.WriteAtomic(
                    cancelledPath, report, options, out _, cancellationToken: cancelled.Token),
                "Cancelled atomic report output must emit no partial file.");
            Require(File.ReadAllText(cancelledPath) == "sentinel" &&
                !Directory.EnumerateFiles(Path.GetDirectoryName(cancelledPath)!,
                    $".{Path.GetFileName(cancelledPath)}.*.tmp").Any(),
                "Cancelled report output must preserve the prior file and clean its temporary file.");
        }
        finally
        {
            File.Delete(cancelledPath);
        }
    }

    private static SampledHullEnvelope AnalyticBoxEnvelope(
        int length, int width, int height, string hash)
    {
        var z = Enumerable.Range(0, 6).Select(index => (index + 0.5) / 6).ToArray();
        var y = Enumerable.Range(0, 5).Select(index => (index + 0.5) / 5).ToArray();
        var intervals = new HullEnvelopeInterval?[z.Length, y.Length];
        for (var station = 0; station < z.Length; station++)
        for (var row = 0; row < y.Length; row++)
            intervals[station, row] = new HullEnvelopeInterval(0, 1);
        return new SampledHullEnvelope(
            new HullEnvelopeBounds(-width / 2.0, width / 2.0, -0.5, height - 0.5,
                -length / 2.0, length / 2.0),
            z, y, intervals, [], 0, [], hash,
            new HistoricalSourceTransform(1, ObjAxisMapping.Identity));
    }

    private static void VerifyHistoricalAuthoringProcessSafety()
    {
        var repository = FindRepositoryRoot();
        var configuration =
#if DEBUG
            "Debug";
#else
            "Release";
#endif
        var tool = Path.Combine(repository, "FtdHullGenerator.SelfTest", "Tools", "HistoricalAuthoring", "bin",
            configuration, "net8.0-windows", "HistoricalAuthoring.dll");
        Require(File.Exists(tool),
            "HistoricalAuthoring must be built by the solution before process-level H02 tests run.");
        var root = Path.Combine(Path.GetTempPath(), $"HullForge-H02-CLI-{Guid.NewGuid():N}");
        var real = Path.Combine(root, "real");
        var link = Path.Combine(root, "link");
        Directory.CreateDirectory(real);
        var source = Path.Combine(real, "box.obj");
        File.WriteAllText(source, BoxObjQuads(nonPlanar: false), new UTF8Encoding(false));
        try
        {
            var request = Path.Combine(root, "request.json");
            var reportPath = Path.Combine(root, "report.json");
            File.WriteAllText(reportPath, "sentinel-existing-report", new UTF8Encoding(false));
            WriteCliRequest(request, source, reportPath);
            var normal = RunProcess(tool, request);
            Require(normal.ExitCode == 4 && File.Exists(reportPath) &&
                    !File.ReadAllText(reportPath).Contains("sentinel", StringComparison.Ordinal) &&
                    !Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories).Any(),
                $"CLI controlled fit outcome must atomically replace one report without temporary files: {normal}.");
            using (var report = JsonDocument.Parse(File.ReadAllText(reportPath)))
            {
                Require(report.RootElement.GetProperty("outcome").GetString() ==
                        "unsupported by current envelope representation" &&
                        !report.RootElement.TryGetProperty("hull", out _) &&
                        !File.ReadAllText(reportPath).Contains("occupiedCells",
                            StringComparison.OrdinalIgnoreCase),
                    "CLI unsupported output must use the exact finite outcome and bounded summary.");
                var transform = report.RootElement.GetProperty("metadata").GetProperty("sourceTransform");
                Require(transform.GetProperty("metresPerSourceUnit").GetDouble() == 1 &&
                        transform.GetProperty("axisMapping").GetProperty("starboard").GetString() == "PositiveX",
                    "CLI report provenance must contain the transform actually applied by the importer.");
            }

            var invalidSource = Path.Combine(root, "invalid.obj");
            File.WriteAllText(invalidSource, "g hull\nv NaN 0 0\n", new UTF8Encoding(false));
            var invalidRequest = Path.Combine(root, "invalid-request.json");
            var invalidReport = Path.Combine(root, "invalid-report.json");
            WriteCliRequest(invalidRequest, invalidSource, invalidReport);
            Require(RunProcess(tool, invalidRequest).ExitCode == 2 &&
                    ReadOutcome(invalidReport) == "invalid input",
                "CLI invalid source input must terminate with the typed invalid-input outcome and exit 2.");

            var fallbackSource = Path.Combine(root, "fallback.obj");
            File.WriteAllText(fallbackSource,
                BoxObj("hull", -3.5, 3.5, -0.5, 4.5, -3, 3), new UTF8Encoding(false));
            var fallbackParameters = HullParameters.Default with
            {
                Length = 6, Width = 7, Height = 5,
                HullArmor = ArmorLayout.Single(MaterialKind.Metal),
                BottomArmor = ArmorLayout.Single(MaterialKind.Metal),
                DeckArmor = ArmorLayout.Single(MaterialKind.Metal),
                Beamify = false, Smoothing = SmoothingMethod.None, Superstructure = null,
            };
            var cleared = new HistoricalPackagingStatus(
                HistoricalRightsDisposition.ClearedForDerivedRedistribution,
                HistoricalDimensionEvidence.Verified, "CC0-1.0", null, true, true, true);
            var direct = new HistoricalFidelityMetric(HistoricalFidelityStageStatus.Passed,
                0, 0, 0, 24, 1, "Independent direct process fixture comparison");
            var fallbackMetadata = new HistoricalEnvelopeMetadata("cli-fallback", "1", "replaced",
                "Synthetic fallback fixture", "Synthetic", "not historical",
                HistoricalConstructionStatus.Built,
                new HistoricalSourceTransform(1, ObjAxisMapping.Identity), 0, 4.5, -0.5);
            var faithfulRequest = Path.Combine(root, "faithful-request.json");
            var faithfulReport = Path.Combine(root, "faithful-report.json");
            var faithfulAsset = Path.Combine(root, "faithful.asset.json");
            WriteCliRequest(faithfulRequest, fallbackSource, faithfulReport, faithfulAsset,
                new ShapeV2FitOptions(MaxEvaluations: 1, SourceFitAcceptanceMetres: 0,
                    VoxelAcceptanceMetres: 0.5), fallbackMetadata, cleared, fallbackParameters,
                direct, HistoricalAccuracyLabel.SourceFaithful);
            var faithful = RunProcess(tool, faithfulRequest);
            Require(faithful.ExitCode == 0 && File.Exists(faithfulAsset) &&
                    ReadOutcome(faithfulReport) == "representable",
                $"CLI exact fallback must emit representable/exit 0 and one loadable asset: {faithful}.");
            var faithfulLoaded = HistoricalEnvelopeAssetLoader.Load(faithfulAsset);
            var authoredAsset = JsonSerializer.Deserialize<HistoricalEnvelopeAsset>(
                File.ReadAllText(faithfulAsset), HistoricalEnvelopeAsset.JsonOptions())!;
            var invalidRows = authoredAsset.Rows.Where(row =>
                    row.NormalizedMinX is { } min && row.NormalizedMaxX is { } max &&
                    (!double.IsFinite(min) || !double.IsFinite(max) || min < 0 || max > 1 || min > max))
                .Take(3).ToArray();
            Require(faithfulLoaded.Success,
                "The process-authored representable fallback asset must pass the runtime loader: " +
                string.Join("; ", faithfulLoaded.Diagnostics) + " rows=" +
                string.Join(" | ", invalidRows.Select(row => row.ToString())));

            var blockedRequest = Path.Combine(root, "blocked-request.json");
            var blockedReport = Path.Combine(root, "blocked-report.json");
            var rightsBlocked = cleared with
            {
                Rights = HistoricalRightsDisposition.RightsBlocked,
                Dimensions = HistoricalDimensionEvidence.Verified,
                LicenseIdentifier = null,
                DerivativesPermitted = false,
                RedistributionPermitted = false,
                HumanRightsReviewComplete = true,
            };
            WriteCliRequest(blockedRequest, fallbackSource, blockedReport, null,
                new ShapeV2FitOptions(MaxEvaluations: 1, SourceFitAcceptanceMetres: 0,
                    VoxelAcceptanceMetres: 0.5), fallbackMetadata, rightsBlocked,
                fallbackParameters, direct, HistoricalAccuracyLabel.SourceFaithful);
            var blockedProcess = RunProcess(tool, blockedRequest);
            Require(blockedProcess.ExitCode == 5 &&
                    ReadOutcome(blockedReport) == "source/rights blocked",
                $"CLI accepted geometry with blocked rights must emit the blocked outcome/exit 5: " +
                blockedProcess);

            var approximateRequest = Path.Combine(root, "approximate-request.json");
            var approximateReport = Path.Combine(root, "approximate-report.json");
            WriteCliRequest(approximateRequest, fallbackSource, approximateReport, null,
                new ShapeV2FitOptions(MaxEvaluations: 1, SourceFitAcceptanceMetres: 0,
                    VoxelAcceptanceMetres: 0.5), fallbackMetadata, cleared, fallbackParameters,
                direct, HistoricalAccuracyLabel.Approximate);
            var approximate = RunProcess(tool, approximateRequest);
            Require(approximate.ExitCode == 0 &&
                    ReadOutcome(approximateReport) ==
                    "representable with documented approximation",
                $"CLI approximate fallback must emit its exact documented outcome/exit 0: {approximate}.");

            var directAlias = Path.Combine(root, "direct-alias.json");
            WriteCliRequest(directAlias, source, directAlias);
            var directHash = SHA256.HashData(File.ReadAllBytes(directAlias));
            Require(RunProcess(tool, directAlias).ExitCode == 2 &&
                    directHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(directAlias))),
                "CLI must reject a request/output alias without replacing its request JSON.");

            var junction = RunProcess("cmd.exe", "/d", "/c", "mklink", "/J", link, real);
            Require(junction.ExitCode == 0 && Directory.Exists(link),
                $"CLI symlink test could not create its isolated junction: {junction}.");
            var linkedRequest = Path.Combine(real, "linked-request.json");
            WriteCliRequest(linkedRequest, source, Path.Combine(link, "linked-request.json"));
            var linkedHash = SHA256.HashData(File.ReadAllBytes(linkedRequest));
            Require(RunProcess(tool, linkedRequest).ExitCode == 2 &&
                    linkedHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(linkedRequest))),
                "CLI must reject a symlink-equivalent request/output alias without changing the request.");

            var sourceAliasRequest = Path.Combine(root, "source-alias.json");
            WriteCliRequest(sourceAliasRequest, source, Path.Combine(link, "box.obj"));
            var sourceHash = SHA256.HashData(File.ReadAllBytes(source));
            Require(RunProcess(tool, sourceAliasRequest).ExitCode == 2 &&
                    sourceHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(source))),
                "CLI must reject a symlink-equivalent source/output alias without changing the source.");

            var oversized = Path.Combine(root, "oversized.json");
            File.WriteAllText(oversized, new string(' ', 1024 * 1024 + 1), new UTF8Encoding(false));
            Require(RunProcess(tool, oversized).ExitCode == 2,
                "CLI must reject request JSON larger than its one-MiB read cap.");
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link, recursive: false);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void WriteCliRequest(
        string requestPath,
        string sourcePath,
        string reportPath,
        string? assetPath = null,
        ShapeV2FitOptions? fit = null,
        HistoricalEnvelopeMetadata? metadata = null,
        HistoricalPackagingStatus? packaging = null,
        HullParameters? fallbackParameters = null,
        HistoricalFidelityMetric? sampledFallbackSourceFit = null,
        HistoricalAccuracyLabel requestedAccuracy = HistoricalAccuracyLabel.Approximate)
    {
        var request = new
        {
            sourceObj = sourcePath,
            outputReport = reportPath,
            outputAsset = assetPath,
            metresPerSourceUnit = 1,
            axisMapping = ObjAxisMapping.Identity,
            includedHullGroups = new[] { "hull" },
            sampling = new { stationCount = 6, rowCount = 5 },
            fit = fit ?? new ShapeV2FitOptions(MaxEvaluations: 1),
            metadata = metadata ?? new HistoricalEnvelopeMetadata("cli-synthetic", "1", "replaced",
                "Synthetic process fixture", "Synthetic", "not historical",
                HistoricalConstructionStatus.Built,
                new HistoricalSourceTransform(99,
                    new ObjAxisMapping(SignedSourceAxis.NegativeX,
                        SignedSourceAxis.PositiveY, SignedSourceAxis.PositiveZ)),
                0, 1, -1),
            packaging = packaging ?? new HistoricalPackagingStatus(
                HistoricalRightsDisposition.RightsBlocked,
                HistoricalDimensionEvidence.Unverified, null, null, false, false, false),
            fallbackParameters,
            sampledFallbackSourceFit,
            requestedAccuracy,
        };
        var options = new JsonSerializerOptions { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        File.WriteAllText(requestPath, JsonSerializer.Serialize(request, options), new UTF8Encoding(false));
    }

    private static string? ReadOutcome(string reportPath)
    {
        using var report = JsonDocument.Parse(File.ReadAllText(reportPath));
        return report.RootElement.GetProperty("outcome").GetString();
    }

    private static ProcessResult RunProcess(string fileName, params string[] arguments)
    {
        var managedAssembly = fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        var executable = managedAssembly ? "dotnet" : fileName;
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        if (managedAssembly) process.StartInfo.ArgumentList.Add(fileName);
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException($"Process-level H02 test timed out: {fileName}.");
        }
        Task.WaitAll(stdout, stderr);
        return new ProcessResult(process.ExitCode, stdout.Result.Trim(), stderr.Result.Trim());
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "FtdHullGenerator.sln")))
                return directory.FullName;
        throw new InvalidOperationException("Could not locate the repository root for H02 process tests.");
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

    // The empty catalog is immutable and treated as read-only across the process, so one
    // load avoids re-scanning the same empty temp tree for every authoring assertion.
    private static readonly Lazy<FtdBlockCatalog> CachedEmptyCatalog = new(LoadEmptyCatalog);

    private static FtdBlockCatalog EmptyCatalog()
    {
        return CachedEmptyCatalog.Value;
    }

    private static FtdBlockCatalog LoadEmptyCatalog()
    {
        var root = Path.Combine(Path.GetTempPath(), "HullForge-H02-EmptyCatalog");
        Directory.CreateDirectory(Path.Combine(root, "From_The_Depths_Data", "StreamingAssets"));
        return FtdBlockCatalog.Load(root);
    }

    private static HistoricalEnvelopeMetadata MetadataFor(SampledHullEnvelope source, int height) => new(
        "synthetic-fit", "1", source.SourceContentHash, "Independent synthetic fixture",
        "Synthetic", "not historical", HistoricalConstructionStatus.Built,
        new HistoricalSourceTransform(1, ObjAxisMapping.Identity),
        WaterlineMetres: 0, DeckDatumMetres: height - 0.5, KeelDatumMetres: -0.5);

    private static SampledHullEnvelope CopyWithTransform(SampledHullEnvelope source)
    {
        var intervals = new HullEnvelopeInterval?[source.StationCount, source.RowCount];
        var z = new double[source.StationCount];
        var y = new double[source.RowCount];
        for (var station = 0; station < source.StationCount; station++)
        {
            z[station] = source.GetNormalizedZ(station);
            for (var row = 0; row < source.RowCount; row++)
                intervals[station, row] = source.GetNormalizedInterval(station, row);
        }
        for (var row = 0; row < source.RowCount; row++)
            y[row] = source.GetNormalizedY(row);
        return new SampledHullEnvelope(source.Bounds, z, y, intervals, [], 0, [],
            source.SourceContentHash, new HistoricalSourceTransform(1, ObjAxisMapping.Identity));
    }

    private static HistoricalMesh CombineBoxes((double Min, double Max) left, (double Min, double Max) right)
    {
        var vertices = new List<Vector3>();
        var triangles = new List<HistoricalTriangle>();
        AddBox(left.Min, left.Max);
        AddBox(right.Min, right.Max);
        return new HistoricalMesh(vertices, triangles,
            new HullEnvelopeBounds(left.Min, right.Max, -1, 1, -2, 2), "synthetic-two-boxes", ["hull"],
            new HistoricalSourceTransform(1, ObjAxisMapping.Identity));

        void AddBox(double minX, double maxX)
        {
            var offset = vertices.Count;
            vertices.AddRange(BoxVertices(minX, maxX, -1, 1, -2, 2));
            triangles.AddRange(BoxTriangles(offset).Select(t => t with { Group = "hull" }));
        }
    }

    private static HistoricalMesh ShearedBoxMesh()
    {
        var vertices = BoxVertices(-1, 1, -1, 1, -2, 2);
        for (var index = 0; index < vertices.Length; index++)
        {
            if (vertices[index].Y > 0)
                vertices[index].X += 1;
        }
        return new HistoricalMesh(vertices, BoxTriangles(0),
            new HullEnvelopeBounds(-1, 2, -1, 1, -2, 2), "synthetic-sheared-box", ["hull"],
            new HistoricalSourceTransform(1, ObjAxisMapping.Identity));
    }

    private static string BoxObj(string group, double minX, double maxX, double minY, double maxY,
        double minZ, double maxZ)
    {
        var builder = new StringBuilder().AppendLine($"g {group}");
        foreach (var vertex in BoxVertices(minX, maxX, minY, maxY, minZ, maxZ))
            builder.AppendLine(FormattableString.Invariant($"v {vertex.X} {vertex.Y} {vertex.Z}"));
        foreach (var triangle in BoxTriangles(0))
            builder.AppendLine($"f {triangle.A + 1} {triangle.B + 1} {triangle.C + 1}");
        return builder.ToString();
    }

    private static string BoxObjNegativeIndices()
    {
        var builder = new StringBuilder().AppendLine("g hull");
        foreach (var vertex in BoxVertices(-1, 1, -1, 1, -1, 1))
            builder.AppendLine(FormattableString.Invariant($"v {vertex.X} {vertex.Y} {vertex.Z}"));
        foreach (var triangle in BoxTriangles(0))
            builder.AppendLine($"f {triangle.A - 8} {triangle.B - 8} {triangle.C - 8}");
        return builder.ToString();
    }

    private static string BoxObjQuads(bool nonPlanar)
    {
        var vertices = BoxVertices(-1, 1, -1, 1, -1, 1);
        if (nonPlanar) vertices[6].X += 0.25f;
        var builder = new StringBuilder().AppendLine("g hull");
        foreach (var vertex in vertices)
            builder.AppendLine(FormattableString.Invariant($"v {vertex.X} {vertex.Y} {vertex.Z}"));
        foreach (var face in new[] { "1 4 3 2", "5 6 7 8", "1 2 6 5",
                     "4 8 7 3", "1 5 8 4", "2 3 7 6" })
            builder.AppendLine($"f {face}");
        return builder.ToString();
    }

    private static string ReverseWinding(string obj)
    {
        var builder = new StringBuilder();
        foreach (var line in obj.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith("f ", StringComparison.Ordinal))
                builder.AppendLine(line);
            else
                builder.AppendLine("f " + string.Join(' ', line[2..].Split(' ').Reverse()));
        }
        return builder.ToString();
    }

    private static string DisconnectedBoxesObj()
    {
        var builder = new StringBuilder().AppendLine("g hull");
        foreach (var vertex in BoxVertices(-4, -2, -1, 1, -1, 1)
                     .Concat(BoxVertices(2, 4, -1, 1, -1, 1)))
            builder.AppendLine(FormattableString.Invariant($"v {vertex.X} {vertex.Y} {vertex.Z}"));
        foreach (var triangle in BoxTriangles(0).Concat(BoxTriangles(8)))
            builder.AppendLine($"f {triangle.A + 1} {triangle.B + 1} {triangle.C + 1}");
        return builder.ToString();
    }

    private static Vector3[] BoxVertices(double minX, double maxX, double minY, double maxY,
        double minZ, double maxZ) =>
    [
        new((float)minX, (float)minY, (float)minZ), new((float)maxX, (float)minY, (float)minZ),
        new((float)maxX, (float)maxY, (float)minZ), new((float)minX, (float)maxY, (float)minZ),
        new((float)minX, (float)minY, (float)maxZ), new((float)maxX, (float)minY, (float)maxZ),
        new((float)maxX, (float)maxY, (float)maxZ), new((float)minX, (float)maxY, (float)maxZ),
    ];

    private static HistoricalTriangle[] BoxTriangles(int offset) =>
    [
        new(offset + 0, offset + 3, offset + 2, ""), new(offset + 0, offset + 2, offset + 1, ""),
        new(offset + 4, offset + 5, offset + 6, ""), new(offset + 4, offset + 6, offset + 7, ""),
        new(offset + 0, offset + 1, offset + 5, ""), new(offset + 0, offset + 5, offset + 4, ""),
        new(offset + 3, offset + 7, offset + 6, ""), new(offset + 3, offset + 6, offset + 2, ""),
        new(offset + 0, offset + 4, offset + 7, ""), new(offset + 0, offset + 7, offset + 3, ""),
        new(offset + 1, offset + 2, offset + 6, ""), new(offset + 1, offset + 6, offset + 5, ""),
    ];

    private static string WriteTempObj(string contents)
    {
        var path = Path.Combine(Path.GetTempPath(), $"HullForge-H02-{Guid.NewGuid():N}.obj");
        File.WriteAllText(path, contents, new UTF8Encoding(false));
        return path;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void RequireThrowsCancellation(Action action, string message)
    {
        try
        {
            action();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }
}
