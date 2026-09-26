using FtdHullGenerator.Domain;
using FtdHullGenerator.Geometry;
using static SlopeFillTestSupport;

internal static class SurfacePatchMetricTests
{
    public static void Run()
    {
        var slope = new BlockPlacement(BlockShape.Slope1, MaterialKind.Metal, 0, 0, 0, 16);
        var metric = SurfacePatchMetrics.Prepare([slope]);
        HashSet<(int X, int Y, int Z)> twoCubes = [(0, 0, 0), (0, 1, 0)];
        HashSet<(int X, int Y, int Z)> onlyNeighbor = [(0, 1, 0)];

        // Independent full-union areas: two cubes sharing a square have 10 m².
        // The native wedge has 3+sqrt(2) m²; with the unchanged neighbor cube,
        // the half-square contact removes 1 m² total, leaving 8+sqrt(2) m².
        // The uncovered half of the neighbor face MUST remain in the new area.
        var replacement = metric.Compare(twoCubes.Contains);
        Close(replacement.SurfaceImprovement, 2 - Math.Sqrt(2),
            "Replacing a cube beside a half-square cap miscounted the residual exposed cap.");
        Close(replacement.StepImprovement, 1,
            "Replacing the cube incorrectly counted transverse/longitudinal cap steps.");

        // Reuse the same prepared geometry with different *original* occupancy.
        // An outward addition starts from the lone neighbor's 6 m², not the
        // hypothetical 10 m² of a filled footprint. It makes this surface worse.
        var outward = metric.Compare(onlyNeighbor.Contains);
        Close(outward.SurfaceImprovement, -(2 + Math.Sqrt(2)),
            "An absent outward footprint was scored as though it were an original full cube.");
        Close(outward.StepImprovement, -1,
            "An outward slope addition incorrectly claimed to remove existing steps.");
        Require(outward.SurfaceImprovement < 0 && replacement.SurfaceImprovement > 0,
            "The metric did not distinguish outward growth from replacement construction.");

        // A cube in a flat side wall has backing at -X and all four transverse
        // neighbors. Cutting it into a wedge exposes those neighbors' residual
        // faces. The complete six-cube union has area 26; after the cut its area
        // is 27+sqrt(2). Minimizing just the fitted wedge's area would reward a dent.
        HashSet<(int X, int Y, int Z)> wall =
            [(0, 0, 0), (-1, 0, 0), (0, -1, 0), (0, 1, 0), (0, 0, -1), (0, 0, 1)];
        var dent = metric.Compare(wall.Contains);
        Close(dent.SurfaceImprovement, -(1 + Math.Sqrt(2)),
            "A diagonal dent in a flat wall did not count the newly exposed collar faces.");
        Close(dent.StepImprovement, -2,
            "A flat-wall dent incorrectly improved the step score.");

        var offset = (X: 137, Y: 29, Z: -119);
        var translated = SurfacePatchMetrics.Prepare([slope with { X = offset.X, Y = offset.Y, Z = offset.Z }]);
        var translatedWall = wall.Select(cell => (cell.X + offset.X, cell.Y + offset.Y, cell.Z + offset.Z)).ToHashSet();
        Close(translated.Compare(translatedWall.Contains).SurfaceImprovement, dent.SurfaceImprovement,
            "Surface scoring changed when the same patch moved away from the origin.");

        var rejectedOverlap = false;
        try { SurfacePatchMetrics.Prepare([slope, slope]); }
        catch (ArgumentException) { rejectedOverlap = true; }
        Require(rejectedOverlap, "The surface metric accepted overlapping fitted footprints.");
        Console.WriteLine("Surface metric: residual caps, actual outward occupancy, flat-wall dents, translation, and overlap rejection passed.");
    }

    private static void Close(double actual, double expected, string message) =>
        Require(Math.Abs(actual - expected) < 1e-5, $"{message} Actual {actual:G10}; expected {expected:G10}.");
}
