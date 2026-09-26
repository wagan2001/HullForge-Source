using FtdHullGenerator.Domain;
using FtdHullGenerator.Geometry;
using static SlopeFillTestSupport;

// Focused, deterministic checks for the Surface Fairness Validator. Each fixture is a small synthetic
// GeneratedHull built directly from placements, so the detectors are exercised without depending on
// generation behaviour or golden fixtures.
internal static class SmoothingQualityValidatorTests
{
    public static void Run()
    {
        PlanarSlopeHasNoJaggedness();
        SustainedChineHasNoJaggedness();
        AlternatingToothIsJagged();
        RetainedOverhangIsNotReported();
        IntroducedOverhangIsReported();
        MirroredSubMinimumComponentsMergeBeforeFiltering();
        FlatWallDentIsLocalRegression();
        ExposedOnlyRemovalIsRegression();
        FittedSlopeImprovesWithoutRegression();
        MovedKeelIsLandmarkDrift();
        MirroredOverhangMergesToOneFinding();
        ShuffledPlacementsAreDeterministic();
        MultiCellBeamUsesFootprintAndFaces();
        KnownGoodAdditiveSmoothingIsClean();
        SurfaceScopeMetricsSeparateSkinFromBoundary();
        Console.WriteLine("Smoothing quality validator: 15 detector, determinism, additive-clean, and surface-scope cases passed.");
    }

    private static void PlanarSlopeHasNoJaggedness()
    {
        var hull = ExtentProfile(y: 0, [0, 0, 1, 1, 2, 2, 3, 3]);
        var report = SmoothingQualityValidator.Validate(hull, hull);
        Require(report.Findings.Count == 0,
            $"A monotone planar extent sequence produced {report.Findings.Count} finding(s).");
    }

    private static void SustainedChineHasNoJaggedness()
    {
        var hull = ExtentProfile(y: 0, [0, 1, 2, 3, 3, 2, 1, 0]);
        var report = SmoothingQualityValidator.Validate(hull, hull);
        Require(report.Findings.All(finding => finding.Detector != "jagged"),
            "A single sustained direction change was misreported as jagged.");
    }

    private static void AlternatingToothIsJagged()
    {
        var hull = ExtentProfile(y: 0, [3, 4, 3, 4, 3, 4, 3]);
        var report = SmoothingQualityValidator.Validate(hull, hull);
        var jagged = report.Findings.Where(finding => finding.Detector == "jagged").ToArray();
        Require(jagged.Length == 1, $"Expected exactly one jagged finding but saw {jagged.Length}.");
        Require(jagged[0].Code == "JAG_LONGITUDINAL", $"Unexpected jagged code {jagged[0].Code}.");
        Require(jagged[0].Bounds == new HullCellBounds(3, 4, 0, 0, 1, 5),
            $"Jagged bounds were {jagged[0].Bounds.ToDisplay()} instead of x[3..4] y[0..0] z[1..5].");
        (int X, int Y, int Z)[] expected = [(4, 0, 1), (3, 0, 2), (4, 0, 3), (3, 0, 4), (4, 0, 5)];
        Require(expected.All(cell => jagged[0].Cells.Contains(cell)),
            "Jagged localization did not include the oscillating outer contour cells.");
    }

    private static void RetainedOverhangIsNotReported()
    {
        // A three-cell ledge is at or above the reporting minimum, so this fixture genuinely exercises
        // the baseline cancellation rather than passing because the ledge is below the threshold.
        var hull = MakeHull(
            Cube(0, 0, 0), Cube(0, 0, 1),
            Cube(0, 1, 0), Cube(0, 1, 1), Cube(1, 1, 0), Cube(1, 1, 1),
            Cube(2, 1, 0), Cube(2, 1, 1), Cube(3, 1, 0), Cube(3, 1, 1));
        var report = SmoothingQualityValidator.Validate(hull, hull);
        Require(report.Findings.All(finding => finding.Detector != "overhang"),
            "An overhang present in both baseline and candidate was reported as introduced.");
    }

    private static void IntroducedOverhangIsReported()
    {
        var baseline = MakeHull(Cube(0, 0, 0), Cube(0, 0, 1), Cube(0, 1, 0), Cube(0, 1, 1));
        var candidate = MakeHull(
            Cube(0, 0, 0), Cube(0, 0, 1),
            Cube(0, 1, 0), Cube(0, 1, 1),
            Cube(0, 2, 0), Cube(0, 2, 1), Cube(1, 2, 0), Cube(1, 2, 1),
            Cube(2, 2, 0), Cube(2, 2, 1), Cube(3, 2, 0), Cube(3, 2, 1));
        var report = SmoothingQualityValidator.Validate(baseline, candidate);
        var overhang = report.Findings.Where(finding => finding.Detector == "overhang").ToArray();
        Require(overhang.Length == 1, $"Expected one introduced overhang but saw {overhang.Length}.");
        Require(overhang[0].Code == "OVERHANG_INTRODUCED", $"Unexpected overhang code {overhang[0].Code}.");
        Require(overhang[0].Bounds == new HullCellBounds(1, 3, 2, 2, 0, 1),
            $"Overhang bounds were {overhang[0].Bounds.ToDisplay()} instead of x[1..3] y[2..2] z[0..1].");
        Require(overhang[0].Cells.Count == 6,
            $"Introduced overhang listed {overhang[0].Cells.Count} evidence cells instead of 6.");
        Require(overhang[0].Cells.Contains((1, 2, 0)) && overhang[0].Cells.Contains((3, 2, 1)),
            "Introduced overhang did not list the newly protruding cells.");
        Require(overhang[0].MeasuredValue >= overhang[0].Threshold,
            $"Overhang measured {overhang[0].MeasuredValue} but reported an uncrossed threshold of {overhang[0].Threshold}.");
        RequireThresholdsCrossed(report);
    }

    private static void MirroredSubMinimumComponentsMergeBeforeFiltering()
    {
        // Each side contributes a two-cell evidence component: the row has a one-cell gap at x=+/-2,
        // so the outermost coordinate fails its inboard check and only x=+/-1 is evidence. Two cells
        // are below the three-cell reporting minimum per side, but the mirror merge yields four, so
        // exactly one finding must survive.
        var baseline = MakeHull(Cube(0, 0, 0), Cube(0, 0, 1), Cube(0, 1, 0), Cube(0, 1, 1));
        var candidateBlocks = new List<BlockPlacement>
        {
            Cube(0, 0, 0), Cube(0, 0, 1), Cube(0, 1, 0), Cube(0, 1, 1),
        };
        foreach (var z in new[] { 0, 1 })
        foreach (var x in new[] { -3, -1, 0, 1, 3 })
            candidateBlocks.Add(Cube(x, 2, z));

        var candidate = MakeHull(candidateBlocks);
        var report = SmoothingQualityValidator.Validate(baseline, candidate);
        var overhang = report.Findings.Where(finding => finding.Detector == "overhang").ToArray();
        Require(overhang.Length == 1,
            $"Expected the mirrored sub-minimum components to merge into one finding, saw {overhang.Length}.");
        Require(overhang[0].Cells.Count == 4,
            $"Merged overhang listed {overhang[0].Cells.Count} cells instead of 4.");
        Require(overhang[0].Bounds == new HullCellBounds(-1, 1, 2, 2, 0, 1),
            $"Merged overhang bounds were {overhang[0].Bounds.ToDisplay()}.");
        Require(overhang[0].Cells.Contains((1, 2, 0)) && overhang[0].Cells.Contains((-1, 2, 1)),
            "Merged overhang did not cover both mirrored evidence cells.");
        RequireThresholdsCrossed(report);
    }

    private static void FlatWallDentIsLocalRegression()
    {
        // A 3x3 flat wall of cubes; the candidate removes only the interior cell (0,1,1). The four
        // face-neighbours each gain one exposed/step square (+1), while the removed cell loses its
        // two exposed X squares (-2), so the local net exposed delta is +2 m^2 and the net step
        // delta is +4 m^2. The reported original/revised sums cover the evidence component grown by
        // its six-neighbour collar, which reaches the wall's four corner cells and therefore
        // measures 30 -> 32 m^2 exposed and 12 -> 16 m^2 step. (Measured by running, not assumed.)
        var wall = new List<BlockPlacement>();
        for (var y = 0; y <= 2; y++)
        for (var z = 0; z <= 2; z++)
            wall.Add(Cube(0, y, z));
        var baseline = MakeHull(wall);
        var candidate = MakeHull(wall.Where(block => block.Position != (0, 1, 1)));
        var report = SmoothingQualityValidator.Validate(baseline, candidate);
        var regression = report.Findings.Where(finding => finding.Detector == "local-regression").ToArray();
        Require(regression.Length == 1, $"Expected one regression patch but saw {regression.Length}.");
        Require(regression[0].Code == "REGRESSION_EXPOSED", $"Unexpected regression code {regression[0].Code}.");
        Require(Math.Abs(regression[0].MeasuredValue - 2) < 1e-9,
            $"Dent exposed net delta was {regression[0].MeasuredValue} instead of 2 m^2.");
        Require(regression[0].Bounds == new HullCellBounds(0, 0, 0, 2, 0, 2),
            $"Dent bounds were {regression[0].Bounds.ToDisplay()} instead of x[0..0] y[0..2] z[0..2].");
        Require(regression[0].Explanation.Contains("30 -> 32"),
            $"The dent finding did not report the expected original/revised exposed values: {regression[0].Explanation}");
        RequireThresholdsCrossed(report);
    }

    private static void ExposedOnlyRemovalIsRegression()
    {
        // A two-cell-thick 3x3 wall; the candidate removes the edge cell (0,0,1). The three in-plane
        // neighbours gain step faces while the removed cell's own two faces disappear, so the net
        // step delta stays below the step threshold. Exposed area still worsens and the patch removed
        // baseline material, so the exposed-only branch must report it.
        var wall = new List<BlockPlacement>();
        for (var x = 0; x <= 1; x++)
        for (var y = 0; y <= 2; y++)
        for (var z = 0; z <= 2; z++)
            wall.Add(Cube(x, y, z));
        var baseline = MakeHull(wall);
        var candidate = MakeHull(wall.Where(block => block.Position != (0, 0, 1)));
        var report = SmoothingQualityValidator.Validate(baseline, candidate);
        var regression = report.Findings.Where(finding => finding.Detector == "local-regression").ToArray();
        Require(regression.Length == 1, $"Expected one exposed-only regression patch but saw {regression.Length}.");
        Require(regression[0].Code == "REGRESSION_EXPOSED", $"Unexpected regression code {regression[0].Code}.");
        Require(regression[0].MeasuredValue >= 1,
            $"Exposed-only regression measured {regression[0].MeasuredValue} m^2, below the reporting minimum.");
        RequireThresholdsCrossed(report);
    }

    private static void FittedSlopeImprovesWithoutRegression()
    {
        // A two-cell fitted replacement: the two top cubes become one Slope2 spanning both cells, so
        // the changed component reaches RegressionMinCells and the gate is genuinely exercised.
        var baseline = MakeHull(
            Cube(0, 0, 0), Cube(0, 0, 1), Cube(0, 0, 2),
            Cube(0, 1, 0), Cube(0, 1, 1));
        var candidate = MakeHull(
            Cube(0, 0, 0), Cube(0, 0, 1), Cube(0, 0, 2),
            new BlockPlacement(BlockShape.Slope2, MaterialKind.Metal, 0, 1, 0, 0));
        var report = SmoothingQualityValidator.Validate(baseline, candidate);
        Require(report.Findings.All(finding => finding.Detector != "local-regression"),
            "A fitted wedge that reduced exposed and step area was reported as a local regression.");
    }

    private static void KnownGoodAdditiveSmoothingIsClean()
    {
        // VerticalSlopeFill is the proven additive method. On a representative hull it must add cells
        // yet report no local regression, overhang or landmark drift; otherwise the detectors are
        // punishing the intended bevel instead of a real defect.
        var generator = new HullGenerator();
        var parameters = HullParameters.Default with { Length = 64, Width = 21, Height = 12 };
        var baseline = generator.Generate(parameters with { Smoothing = SmoothingMethod.None });
        var candidate = generator.Generate(parameters with { Smoothing = SmoothingMethod.VerticalSlopeFill });
        var report = SmoothingQualityValidator.Validate(baseline, candidate);
        var added = report.Metrics.First(metric => metric.Name == "cells.added").Value;
        Require(added > 0, "The known-good additive fill added no cells; the cleanliness check would be vacuous.");
        var regression = report.Findings.Count(finding => finding.Detector == "local-regression");
        var overhang = report.Findings.Count(finding => finding.Detector == "overhang");
        var landmark = report.Findings.Count(finding => finding.Detector == "landmark-drift");
        Require(regression == 0, $"Known-good VerticalSlopeFill produced {regression} local-regression finding(s).");
        Require(overhang == 0, $"Known-good VerticalSlopeFill produced {overhang} overhang finding(s).");
        Require(landmark == 0, $"Known-good VerticalSlopeFill produced {landmark} landmark-drift finding(s).");
    }

    private static void MovedKeelIsLandmarkDrift()
    {
        var baselineBlocks = new List<BlockPlacement>();
        for (var x = 0; x <= 2; x++)
        for (var y = 0; y <= 2; y++)
        for (var z = 0; z <= 5; z++)
            baselineBlocks.Add(Cube(x, y, z));
        var baseline = MakeHull(baselineBlocks);

        var candidateBlocks = new List<BlockPlacement>(baselineBlocks);
        foreach (var z in new[] { 2, 3 })
        for (var x = 0; x <= 2; x++)
        {
            candidateBlocks.Add(Cube(x, -1, z));
            candidateBlocks.Add(Cube(x, -2, z));
        }

        var candidate = MakeHull(candidateBlocks);
        var report = SmoothingQualityValidator.Validate(baseline, candidate);
        var keel = report.Findings.FirstOrDefault(finding => finding.Code == "LANDMARK_KEEL");
        Require(keel is not null, "A keel shifted down two cells was not reported as landmark drift.");
        Require(keel!.MeasuredValue >= 2,
            $"Keel drift was {keel.MeasuredValue} cells instead of at least 2.");
    }

    private static void MirroredOverhangMergesToOneFinding()
    {
        var baseline = MakeHull(Cube(0, 0, 0), Cube(0, 0, 1), Cube(0, 1, 0), Cube(0, 1, 1));
        var candidateBlocks = new List<BlockPlacement>
        {
            Cube(0, 0, 0), Cube(0, 0, 1), Cube(0, 1, 0), Cube(0, 1, 1),
        };
        for (var z = 0; z <= 1; z++)
        for (var x = -3; x <= 3; x++)
            candidateBlocks.Add(Cube(x, 2, z));

        var candidate = MakeHull(candidateBlocks);
        var report = SmoothingQualityValidator.Validate(baseline, candidate);
        var overhang = report.Findings.Where(finding => finding.Detector == "overhang").ToArray();
        Require(overhang.Length == 1, $"Expected mirrored overhangs to merge into one, saw {overhang.Length}.");
        Require(overhang[0].Cells.Count == 12,
            $"Merged overhang listed {overhang[0].Cells.Count} cells instead of 12.");
        Require(overhang[0].Bounds == new HullCellBounds(-3, 3, 2, 2, 0, 1),
            $"Merged overhang bounds were {overhang[0].Bounds.ToDisplay()}.");
        Require(overhang[0].Cells.Contains((3, 2, 1)) && overhang[0].Cells.Contains((-3, 2, 1)),
            "Merged overhang did not cover both mirrored sides.");
    }

    private static void ShuffledPlacementsAreDeterministic()
    {
        var baseline = MakeHull(Cube(0, 0, 0), Cube(0, 0, 1), Cube(0, 1, 0), Cube(0, 1, 1));
        var candidate = MakeHull(
            Cube(0, 0, 0), Cube(0, 0, 1),
            Cube(0, 1, 0), Cube(0, 1, 1),
            Cube(0, 2, 0), Cube(0, 2, 1), Cube(1, 2, 0), Cube(1, 2, 1), Cube(2, 2, 0), Cube(2, 2, 1));
        var shuffled = MakeHull(candidate.Blocks.Reverse());
        var original = SmoothingQualityValidator.Validate(baseline, candidate);
        var reversed = SmoothingQualityValidator.Validate(baseline, shuffled);
        Require(FindingsEqual(original.Findings, reversed.Findings),
            "Reversing placement order changed the findings.");
        Require(original.Metrics.SequenceEqual(reversed.Metrics),
            "Reversing placement order changed the metrics.");
    }

    private static void MultiCellBeamUsesFootprintAndFaces()
    {
        var hull = MakeHull(new BlockPlacement(BlockShape.Beam4, MaterialKind.Metal, 0, 0, 0, 0));
        var geometry = new SurfaceQualityGeometry(hull);
        Require(geometry.Occupied.Count == 4,
            $"A Beam4 occupied {geometry.Occupied.Count} cells instead of 4.");
        Require(geometry.Occupied.Contains((0, 0, 3)) && geometry.MaxXByRow.Count == 4,
            "A Beam4's footprint did not expand across all four cells.");
        SurfaceQualityGeometry.MeasureExposedSurface(hull, out var totals);
        Require(Math.Abs(totals.Exposed - 18) < 1e-6,
            $"A Beam4 exposed area was {totals.Exposed} m^2 instead of 18.");
        Require(Math.Abs(totals.Step - 10) < 1e-6,
            $"A Beam4 step area was {totals.Step} m^2 instead of 10.");
    }

    private static void SurfaceScopeMetricsSeparateSkinFromBoundary()
    {
        // An air-gapped inner armor cube is measured on the all-boundary metric but excluded from the
        // exterior hull-skin metric, so a detector consuming MeasureExposedSurface cannot see it.
        var hull = MakeHull(
            Cube(0, 0, 0),
            new BlockPlacement(BlockShape.Cube, MaterialKind.Metal, 2, 0, 0, 0) { ArmorDepth = 1 });
        var report = SmoothingQualityValidator.Validate(hull, hull);
        double Metric(string name) => report.Metrics.First(metric => metric.Name == name).Value;

        Require(Math.Abs(Metric("surface.exposedArea.original") - 6) < 1e-6,
            $"Exterior hull-skin metric was {Metric("surface.exposedArea.original")} m^2 instead of 6.");
        Require(Math.Abs(Metric("surface.exposedArea.revised") - 6) < 1e-6,
            $"Exterior hull-skin revised metric was {Metric("surface.exposedArea.revised")} m^2 instead of 6.");
        Require(Math.Abs(Metric("surface.boundaryArea.original") - 12) < 1e-6,
            $"All-boundary metric was {Metric("surface.boundaryArea.original")} m^2 instead of 12.");
        Require(Math.Abs(Metric("surface.boundaryStepArea.original") - 8) < 1e-6,
            $"All-boundary step metric was {Metric("surface.boundaryStepArea.original")} m^2 instead of 8.");
        Require(Metric("surface.boundaryArea.original") > Metric("surface.exposedArea.original"),
            "The all-boundary metric must exceed the hull-skin metric when internal armor exists.");

        var scope = report.Metrics.First(metric => metric.Name == "surface.scope");
        Require(scope.TextValue == SurfaceCoverage.ReportScope,
            "The surface.scope metric did not carry the coverage report scope verbatim.");
        Require(report.Metrics.First(metric => metric.Name == "surface.notes.count").Value == 0,
            "A clean fixture produced unattributable-area notes.");
        Require(report.Metrics.First(metric => metric.Name == "surface.notes").TextValue is null,
            "A clean fixture reported a non-empty surface.notes payload.");

        // Detector input is the hull-skin dictionary: the inner armor cell must be absent or zero.
        var areas = new SurfaceQualityGeometry(hull).MeasureExposedSurface(out var totals);
        Require(!areas.TryGetValue((2, 0, 0), out var inner) || inner.Exposed == 0,
            "The inner armor cell leaked exterior area into the detector input.");
        Require(Math.Abs(totals.Exposed - 6) < 1e-6,
            $"MeasureExposedSurface returned {totals.Exposed} m^2 instead of the 6 m^2 hull skin.");
        var boundary = new SurfaceQualityGeometry(hull).MeasureBoundarySurface(out var boundaryTotals);
        Require(Math.Abs(boundaryTotals.Exposed - 12) < 1e-6,
            $"MeasureBoundarySurface returned {boundaryTotals.Exposed} m^2 instead of the 12 m^2 boundary.");
        Require(Math.Abs(boundary[(2, 0, 0)].Exposed - 6) < 1e-6,
            "MeasureBoundarySurface omitted the inner armor cell's boundary area.");
    }

    /// <summary>Every reported finding must have crossed the threshold it names.</summary>
    private static void RequireThresholdsCrossed(SmoothingQualityReport report)
    {
        foreach (var finding in report.Findings)
            Require(finding.MeasuredValue >= finding.Threshold,
                $"{finding.Code} measured {finding.MeasuredValue} but reported an uncrossed threshold of {finding.Threshold}.");
    }

    private static bool FindingsEqual(IReadOnlyList<SmoothingFinding> left, IReadOnlyList<SmoothingFinding> right)
    {
        if (left.Count != right.Count) return false;
        for (var index = 0; index < left.Count; index++)
        {
            var a = left[index];
            var b = right[index];
            if (a.Code != b.Code || a.Detector != b.Detector || a.Severity != b.Severity ||
                a.MeasurementName != b.MeasurementName || a.MeasuredValue != b.MeasuredValue ||
                a.Threshold != b.Threshold || a.Explanation != b.Explanation || a.Bounds != b.Bounds ||
                a.CellsTruncated != b.CellsTruncated || !a.Cells.SequenceEqual(b.Cells))
                return false;
        }

        return true;
    }

    /// <summary>Builds a single-row hull whose +X extent at each Z is the supplied profile value.</summary>
    private static GeneratedHull ExtentProfile(int y, int[] extents)
    {
        var blocks = new List<BlockPlacement>();
        for (var z = 0; z < extents.Length; z++)
        for (var x = 0; x <= extents[z]; x++)
            blocks.Add(Cube(x, y, z));
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
