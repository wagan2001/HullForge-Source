using FtdHullGenerator.Domain;
using FtdHullGenerator.Geometry;
using static SlopeFillTestSupport;

// Milestone 2 adversarial verification. Each fixture is a small synthetic hull built directly from
// placements, so the bounded-window regression, baseline classification, section/continuity evidence and
// protected-feature handling are exercised without depending on generation behaviour.
internal static class SurfaceFairnessAdversarialTests
{
    public static void Run()
    {
        ContinuousFittedSlopeStaysClean();
        ResidualLipCapIsDetectedAndLocalized();
        BowSternDefectIsDetectedByVerticalSectionEvidence();
        IdenticalJaggedBaselineIsPresentInBaseline();
        NewlyAddedToothIsIntroduced();
        AdditiveSpikeIsNotExempted();
        RemoteImprovementDoesNotHideLocalDefect();
        IntentionalChineFlareAndOpenDeckAreNotDefects();
        SamplingOffsetDoesNotReverseSupportedVerdicts();
        AmbiguousGeometryIsReportedAsUncertainty();
        ProtectedRegionIsNotAttributedToSmoothing();
        BoundedWindowOptionsHaveDocumentedDefaults();
        SectionQueryPreservesMultipleLoops();
        Console.WriteLine(
            "Surface fairness adversarial: 13 bounded-window, baseline-classification, section, offset, " +
            "uncertainty, and protected-feature cases passed.");
    }

    /// <summary>Adversarial 2: a continuous fitted slope that reduces area stays clean.</summary>
    private static void ContinuousFittedSlopeStaysClean()
    {
        var baseline = MakeHull(
            Cube(0, 0, 0), Cube(0, 0, 1), Cube(0, 0, 2), Cube(0, 1, 0), Cube(0, 1, 1));
        var candidate = MakeHull(
            Cube(0, 0, 0), Cube(0, 0, 1), Cube(0, 0, 2),
            new BlockPlacement(BlockShape.Slope2, MaterialKind.Metal, 0, 1, 0, 0));
        var report = SmoothingQualityValidator.Validate(baseline, candidate);
        Require(report.Findings.All(finding => finding.Detector != "local-regression"),
            "A continuous fitted slope was reported as a local regression.");
        Require(report.Findings.All(finding => finding.Detector != "continuity"),
            "A continuous fitted slope was reported as a continuity defect.");
    }

    /// <summary>Adversarial 3: a residual lip/cap is detected and localized to its own cells.</summary>
    private static void ResidualLipCapIsDetectedAndLocalized()
    {
        var wall = new List<BlockPlacement>();
        for (var y = 0; y <= 2; y++)
        for (var z = 0; z <= 2; z++)
            wall.Add(Cube(0, y, z));
        var baseline = MakeHull(wall);
        var candidate = MakeHull(wall.Append(Cube(1, 1, 1)));

        var report = SmoothingQualityValidator.Validate(baseline, candidate);
        var caps = report.Findings.Where(finding => finding.Code == "CONTINUITY_RESIDUAL_CAP").ToArray();
        Require(caps.Length >= 1, "A residual lip/cap produced no continuity finding.");
        Require(caps.Any(cap => cap.Cells.Contains((1, 1, 1))),
            "The residual lip/cap was not localized to its protruding cell.");
        Require(caps.All(cap => cap.Classification == BaselineClassification.Introduced),
            "A newly added residual lip/cap was not classified Introduced.");
    }

    /// <summary>
    /// Adversarial 4: a bow/stern defect a lateral X-extent check misses is found by the vertical/fore-aft
    /// section evidence. The fixture adds a fitted slope at the bow (+Z); it does not create a lateral
    /// overhang, but the Z/Y section continuity sees the short residual cap it leaves.
    /// </summary>
    private static void BowSternDefectIsDetectedByVerticalSectionEvidence()
    {
        var hull = new List<BlockPlacement>();
        for (var x = -2; x <= 2; x++)
        for (var y = 0; y <= 2; y++)
        for (var z = 0; z <= 4; z++)
            hull.Add(Cube(x, y, z));
        var baseline = MakeHull(hull);
        var candidateBlocks = new List<BlockPlacement>(hull)
        {
            new BlockPlacement(BlockShape.Slope1, MaterialKind.Metal, 0, 2, 5, 0) { Origin = BlockOrigin.Smoothing },
        };
        var candidate = MakeHull(candidateBlocks);

        var report = SmoothingQualityValidator.Validate(baseline, candidate);
        Require(report.Findings.All(finding => finding.Detector != "overhang"),
            "The fixture unexpectedly produced a lateral overhang, so it does not prove the vertical check.");
        var continuity = report.Findings.Where(finding => finding.Detector == "continuity").ToArray();
        Require(continuity.Length >= 1, "The bow/stern defect produced no continuity evidence.");
        Require(continuity.Any(finding => finding.Bounds.MaxZ >= 5),
            "The continuity evidence did not localize the defect at the bow/stern (+Z).");
    }

    /// <summary>Adversarial 5: identical jagged baseline and candidate classify as PresentInBaseline.</summary>
    private static void IdenticalJaggedBaselineIsPresentInBaseline()
    {
        var jagged = ExtentProfile([3, 4, 3, 4, 3, 4, 3]);
        var report = SmoothingQualityValidator.Validate(jagged, jagged);
        var findings = report.Findings.Where(finding => finding.Detector == "jagged").ToArray();
        Require(findings.Length == 1, $"Expected one jagged finding but saw {findings.Length}.");
        Require(findings[0].Classification == BaselineClassification.PresentInBaseline,
            $"Identical jagged hull classified as {findings[0].Classification} instead of PresentInBaseline.");
        Require(findings[0].BaselineValue == findings[0].CandidateValue,
            "PresentInBaseline evidence did not record equal baseline and candidate values.");
    }

    /// <summary>Adversarial 6: a newly added tooth classifies as Introduced (not baseline evidence).</summary>
    private static void NewlyAddedToothIsIntroduced()
    {
        var smooth = ExtentProfile([3, 3, 3, 3, 3, 3, 3]);
        var jagged = ExtentProfile([3, 4, 3, 4, 3, 4, 3]);
        var report = SmoothingQualityValidator.Validate(smooth, jagged);
        var tooth = report.Findings.Single(finding => finding.Detector == "jagged");
        Require(tooth.Classification is BaselineClassification.Introduced or BaselineClassification.Worsened,
            $"A newly added tooth classified as {tooth.Classification}.");
        Require(tooth.BaselineValue == 0, "A newly added tooth recorded a non-zero baseline value.");
    }

    /// <summary>
    /// Adversarial 7: an additive spike with no removed cells is still reported, proving the detector no
    /// longer exempts additive exposed-area growth merely because nothing was removed.
    /// </summary>
    private static void AdditiveSpikeIsNotExempted()
    {
        var wall = new List<BlockPlacement>();
        for (var y = 0; y <= 2; y++)
        for (var z = 0; z <= 2; z++)
            wall.Add(Cube(0, y, z));
        var baseline = MakeHull(wall);
        var candidate = MakeHull(wall
            .Append(Cube(1, 1, 0))
            .Append(Cube(1, 1, 1))
            .Append(Cube(1, 1, 2)));

        var report = SmoothingQualityValidator.Validate(baseline, candidate);
        var removed = report.Metrics.First(metric => metric.Name == "cells.removed").Value;
        Require(removed == 0, "The additive spike fixture unexpectedly removed baseline cells.");
        var regression = report.Findings.Where(finding => finding.Detector == "local-regression").ToArray();
        Require(regression.Length >= 1, "An additive spike with no removed cells was exempted from regression.");
        Require(regression.Any(finding => finding.Bounds.MinX >= 0 && finding.Bounds.MaxX >= 1),
            "The additive spike regression was not localized to the added lip.");
    }

    /// <summary>
    /// Adversarial 8: a small bad patch stays visible beside a larger improved patch, because the bounded
    /// window reports the worst local measurement instead of a global net.
    /// </summary>
    private static void RemoteImprovementDoesNotHideLocalDefect()
    {
        // The baseline has a large bump; the candidate removes the whole bump (a large improvement) and
        // adds one small lip cube. The whole-hull exposed area improves, yet the bounded window must still
        // report the small local defect.
        var wall = new List<BlockPlacement>();
        for (var y = 0; y <= 2; y++)
        for (var z = 0; z <= 8; z++)
            wall.Add(Cube(0, y, z));
        var baselineBlocks = new List<BlockPlacement>(wall);
        for (var y = 0; y <= 2; y++)
        for (var z = 0; z <= 6; z++)
            baselineBlocks.Add(Cube(1, y, z));
        var baseline = MakeHull(baselineBlocks);

        var candidateBlocks = new List<BlockPlacement>(wall) { Cube(1, 1, 8) };
        var candidate = MakeHull(candidateBlocks);

        var report = SmoothingQualityValidator.Validate(baseline, candidate);
        var exposedDelta = report.Metrics.First(metric => metric.Name == "surface.exposedArea.delta").Value;
        Require(exposedDelta < 0, $"The larger improved patch did not improve the whole-hull area (delta {exposedDelta}).");
        var regression = report.Findings.Where(finding => finding.Detector == "local-regression").ToArray();
        Require(regression.Length >= 1, "The small bad patch was hidden by the larger remote improvement.");
        Require(regression.Any(finding => finding.Bounds.MaxZ >= 8),
            "The regression was not localized to the small bad patch at z=8.");
    }

    /// <summary>
    /// Adversarial 9: an intentional hard chine, a flare and an open deck are not automatically defects.
    /// A hull compared with itself reports nothing, and a chine is only asserted when the baseline shows a
    /// sustained strict interior shoulder.
    /// </summary>
    private static void IntentionalChineFlareAndOpenDeckAreNotDefects()
    {
        var chine = new List<BlockPlacement>();
        for (var z = 0; z <= 4; z++)
        {
            for (var x = -1; x <= 1; x++) chine.Add(Cube(x, 0, z));
            for (var x = -3; x <= 3; x++) chine.Add(Cube(x, 1, z));
            for (var x = -2; x <= 2; x++) chine.Add(Cube(x, 2, z));
        }

        var chineHull = MakeHull(chine);
        var report = SmoothingQualityValidator.Validate(chineHull, chineHull);
        Require(report.Findings.Count == 0,
            $"An intentional chine hull compared with itself produced {report.Findings.Count} finding(s).");
        Require(report.Metrics.First(metric => metric.Name == "landmark.chineUncertain").Value == 0,
            "A sustained strict interior shoulder was not recognized as chine evidence.");

        // An open-deck hull (no top face) compared with itself is also clean.
        var openDeck = new List<BlockPlacement>();
        for (var x = -1; x <= 1; x++)
        for (var z = -1; z <= 1; z++)
        {
            if (x == 0 && z == 0) continue;
            openDeck.Add(Cube(x, 0, z));
            openDeck.Add(Cube(x, 1, z));
        }

        var openDeckHull = MakeHull(openDeck);
        var openReport = SmoothingQualityValidator.Validate(openDeckHull, openDeckHull);
        Require(openReport.Findings.Count == 0,
            $"An open-deck hull compared with itself produced {openReport.Findings.Count} finding(s).");
    }

    /// <summary>
    /// Adversarial 10: changing the sub-cell section sampling offset does not reverse the verdict on a
    /// supported fixture: a lip is detected at every offset, and a clean hull is clean at every offset.
    /// </summary>
    private static void SamplingOffsetDoesNotReverseSupportedVerdicts()
    {
        var wall = new List<BlockPlacement>();
        for (var y = 0; y <= 2; y++)
        for (var z = 0; z <= 2; z++)
            wall.Add(Cube(0, y, z));
        var baseline = MakeHull(wall);
        var candidate = MakeHull(wall.Append(Cube(1, 1, 1)));

        var atCenter = SmoothingQualityValidator.Validate(baseline, candidate, null,
            new SmoothingDiagnosticContext { SectionOffset = 0.0 });
        var atQuarter = SmoothingQualityValidator.Validate(baseline, candidate, null,
            new SmoothingDiagnosticContext { SectionOffset = 0.25 });
        var centerCaps = atCenter.Findings.Where(finding => finding.Code == "CONTINUITY_RESIDUAL_CAP").ToArray();
        var quarterCaps = atQuarter.Findings.Where(finding => finding.Code == "CONTINUITY_RESIDUAL_CAP").ToArray();
        Require(centerCaps.Length >= 1 && quarterCaps.Length >= 1,
            "The lip verdict reversed between supported sampling offsets.");
        Require(centerCaps.Any(cap => cap.Cells.Contains((1, 1, 1))) &&
                quarterCaps.Any(cap => cap.Cells.Contains((1, 1, 1))),
            "The lip was not localized to the same cell at both supported sampling offsets.");

        var clean = MakeHull(Cube(0, 0, 0), Cube(1, 0, 0));
        foreach (var offset in new[] { 0.0, 0.25 })
        {
            var cleanReport = SmoothingQualityValidator.Validate(clean, clean, null,
                new SmoothingDiagnosticContext { SectionOffset = offset });
            Require(cleanReport.Findings.All(finding => finding.Detector != "continuity"),
                $"A clean hull reported continuity findings at sampling offset {offset}.");
        }

        // The equivalent-tessellation case: one Beam4 and four cubes cover the same solid and must agree.
        var beam = MakeHull(new BlockPlacement(BlockShape.Beam4, MaterialKind.Metal, 0, 0, 0, 0));
        var cubes = MakeHull(Cube(0, 0, 0), Cube(0, 0, 1), Cube(0, 0, 2), Cube(0, 0, 3));
        var beamReport = SmoothingQualityValidator.Validate(beam, beam);
        var cubeReport = SmoothingQualityValidator.Validate(cubes, cubes);
        Require(beamReport.Findings.All(finding => finding.Detector != "continuity") &&
                cubeReport.Findings.All(finding => finding.Detector != "continuity"),
            "An equivalent-tessellation clean solid reported continuity findings.");

        // The defect fixture expressed as three Beam3 members must give the same localized verdict as the
        // nine-cube wall, so a tessellation choice does not reverse the continuity verdict.
        var beamWall = new List<BlockPlacement>
        {
            new(BlockShape.Beam3, MaterialKind.Metal, 0, 0, 0, 0),
            new(BlockShape.Beam3, MaterialKind.Metal, 0, 1, 0, 0),
            new(BlockShape.Beam3, MaterialKind.Metal, 0, 2, 0, 0),
        };
        var beamLip = SmoothingQualityValidator.Validate(
            MakeHull(beamWall), MakeHull(beamWall.Append(Cube(1, 1, 1))));
        var beamCaps = beamLip.Findings.Where(finding => finding.Code == "CONTINUITY_RESIDUAL_CAP").ToArray();
        Require(beamCaps.Length == centerCaps.Length &&
                beamCaps.All(cap => centerCaps.Any(cubeCap => cubeCap.Bounds == cap.Bounds)),
            "A beam/cube tessellation change reversed the residual-cap verdict.");
    }

    /// <summary>
    /// Adversarial 11: an ambiguous section intersection (a plane coplanar with shared faces) is reported
    /// as explicit uncertainty rather than a confident verdict.
    /// </summary>
    private static void AmbiguousGeometryIsReportedAsUncertainty()
    {
        var hull = MakeHull(Cube(0, 0, 0), Cube(1, 0, 0));
        var ambiguous = SmoothingQualityValidator.Validate(hull, hull, null,
            new SmoothingDiagnosticContext { SectionOffset = 0.5 });
        Require(ambiguous.Metrics.First(metric => metric.Name == "continuity.uncertain").Value == 1,
            "A coplanar section intersection was not flagged as uncertain.");
        Require(ambiguous.Findings.Any(finding => finding.Code == "CONTINUITY_UNCERTAIN"),
            "An ambiguous section intersection produced no explicit uncertainty finding.");

        var supported = SmoothingQualityValidator.Validate(hull, hull, null,
            new SmoothingDiagnosticContext { SectionOffset = 0.0 });
        Require(supported.Metrics.First(metric => metric.Name == "continuity.uncertain").Value == 0,
            "A supported section intersection was incorrectly flagged as uncertain.");
    }

    /// <summary>
    /// P4: cells the caller declares protected are not attributed to smoothing. The same fixture that
    /// reports a regression without a context is quiet once every evidence cell is protected.
    /// </summary>
    private static void ProtectedRegionIsNotAttributedToSmoothing()
    {
        var wall = new List<BlockPlacement>();
        for (var y = 0; y <= 2; y++)
        for (var z = 0; z <= 2; z++)
            wall.Add(Cube(0, y, z));
        var baseline = MakeHull(wall);
        var candidate = MakeHull(wall.Append(Cube(1, 1, 1)));

        var unprotected = SmoothingQualityValidator.Validate(baseline, candidate);
        Require(unprotected.Findings.Any(finding => finding.Detector == "local-regression"),
            "The protected-feature fixture did not produce a regression to suppress.");

        var context = new SmoothingDiagnosticContext
        {
            ProtectedCells = new HashSet<(int X, int Y, int Z)> { (0, 1, 1), (1, 1, 1) },
        };
        var protectedReport = SmoothingQualityValidator.Validate(baseline, candidate, null, context);
        Require(protectedReport.Findings.All(finding => finding.Detector != "local-regression"),
            "A regression whose evidence cells are all protected was still attributed to smoothing.");
        Require(protectedReport.Metrics.First(metric => metric.Name == "regression.protectedWindows").Value >= 1,
            "A protected regression window was not counted.");
    }

    /// <summary>P1: the window size and stride are documented options with the pinned defaults.</summary>
    private static void BoundedWindowOptionsHaveDocumentedDefaults()
    {
        var options = SmoothingQualityOptions.Default;
        Require(options.RegressionWindowSize == 4, "The regression window size default is not 4.");
        Require(options.RegressionWindowStride == 2, "The regression window stride default is not 2.");
        Require(options.RegressionSupportRadius == 1, "The regression support radius default is not 1.");
    }

    /// <summary>
    /// P3: the section query preserves every contour loop, not just the outermost extent. A hollow square
    /// tube cut perpendicular to X yields two closed loops (outer wall and inner hole), and a solid cube
    /// yields one.
    /// </summary>
    private static void SectionQueryPreservesMultipleLoops()
    {
        var blocks = new List<BlockPlacement>();
        for (var x = 0; x <= 2; x++)
        for (var y = -1; y <= 1; y++)
        for (var z = -1; z <= 1; z++)
            if (!(y == 0 && z == 0))
                blocks.Add(Cube(x, y, z));
        var tube = MakeHull(blocks);
        var section = new SurfaceSectionQuery(new SurfaceCoverage(tube))
            .Cut(SectionAxis.X, 1.0, SurfaceScope.HullSkin);
        Require(section.Contours.Count == 2,
            $"A hollow tube section had {section.Contours.Count} contour(s) instead of 2.");
        Require(section.Contours.All(contour => contour.IsClosed), "A tube section contour was not closed.");
        Require(section.Contours.Any(contour => contour.Points.Count == 4) &&
                section.Contours.Any(contour => contour.Points.Count > 4),
            "The tube section did not preserve both the inner hole and the outer wall loop.");
        Require(section.SamplingResolution is null, "An analytic section reported a sampling resolution.");

        var cube = MakeHull(Cube(0, 0, 0));
        var cubeSection = new SurfaceSectionQuery(new SurfaceCoverage(cube))
            .Cut(SectionAxis.X, 0.0, SurfaceScope.HullSkin);
        Require(cubeSection.Contours.Count == 1 && cubeSection.Contours[0].IsClosed,
            "A single cube did not cut into one closed loop.");
    }

    private static GeneratedHull ExtentProfile(int[] extents)
    {
        var blocks = new List<BlockPlacement>();
        for (var z = 0; z < extents.Length; z++)
        for (var x = 0; x <= extents[z]; x++)
            blocks.Add(Cube(x, 0, z));
        return MakeHull(blocks);
    }

    private static GeneratedHull MakeHull(params BlockPlacement[] blocks) =>
        MakeHull((IEnumerable<BlockPlacement>)blocks);

    private static GeneratedHull MakeHull(IEnumerable<BlockPlacement> blocks)
    {
        var list = blocks.ToArray();
        var cells = list.SelectMany(block => block.OccupiedCells).ToArray();
        Require(cells.Length > 0, "A synthetic hull fixture must contain at least one cell.");
        return new GeneratedHull(
            HullParameters.Default,
            list,
            cells.Min(cell => cell.X), cells.Max(cell => cell.X),
            cells.Min(cell => cell.Y), cells.Max(cell => cell.Y),
            cells.Min(cell => cell.Z), cells.Max(cell => cell.Z));
    }

    private static BlockPlacement Cube(int x, int y, int z) =>
        new(BlockShape.Cube, MaterialKind.Metal, x, y, z, 0);
}
