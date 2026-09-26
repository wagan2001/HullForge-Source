using FtdHullGenerator.Domain.Design;
using FtdHullGenerator.Domain.Historical;

namespace FtdHullGenerator.Geometry.HullSources;

public enum HistoricalSymmetryPolicy
{
    RejectAsymmetry = 0,
    NormalizeByOuterUnion = 1,
}

public sealed record HistoricalEnvelopeSamplingOptions(
    int StationCount = 96,
    int RowCount = 32,
    HistoricalSymmetryPolicy SymmetryPolicy = HistoricalSymmetryPolicy.RejectAsymmetry,
    double SymmetryToleranceMetres = 0.05,
    double IntersectionToleranceMetres = 1e-6,
    int MaxSampleRows = 131_072,
    int MaxTriangleTests = 100_000_000);

public sealed record HistoricalEnvelopeSamplingResult(
    SampledHullEnvelope? Envelope,
    IReadOnlyList<DesignDiagnostic> Diagnostics)
{
    public bool Success => Envelope?.IsUsable == true && !Diagnostics.Any(diagnostic => diagnostic.IsError);
}

/// <summary>
/// Samples a closed mesh with X-directed rays. Rows with more than one material interval are
/// retained only as explicit unsupported-row records; they are never collapsed into one span.
/// </summary>
public static class HistoricalEnvelopeSampler
{
    public static HistoricalEnvelopeSamplingResult Sample(
        HistoricalMesh mesh,
        HistoricalEnvelopeSamplingOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        options ??= new HistoricalEnvelopeSamplingOptions();
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = Validate(options);
        if (diagnostics.Any(diagnostic => diagnostic.IsError))
            return new HistoricalEnvelopeSamplingResult(null, diagnostics);

        var sampleRows = checked((long)options.StationCount * options.RowCount);
        var triangleTests = checked(sampleRows * mesh.Triangles.Count);
        if (sampleRows > options.MaxSampleRows || triangleTests > options.MaxTriangleTests)
        {
            diagnostics.Add(DesignDiagnostic.Error(HistoricalDiagnosticCodes.InputLimitExceeded,
                $"Envelope sampling would require {sampleRows} rows and {triangleTests} triangle tests; " +
                $"limits are {options.MaxSampleRows} and {options.MaxTriangleTests}."));
            return new HistoricalEnvelopeSamplingResult(null, Array.AsReadOnly(diagnostics.ToArray()));
        }

        var zSamples = CellCenteredSamples(options.StationCount);
        var ySamples = CellCenteredSamples(options.RowCount);
        var intervals = new HullEnvelopeInterval?[options.StationCount, options.RowCount];
        var unsupported = new List<UnsupportedEnvelopeRow>();
        var normalizedRows = 0;
        var occupiedRows = 0;
        var centerPlane = (mesh.Bounds.MinX + mesh.Bounds.MaxX) / 2;

        for (var zIndex = 0; zIndex < options.StationCount; zIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var z = Lerp(mesh.Bounds.MinZ, mesh.Bounds.MaxZ, zSamples[zIndex]);
            for (var yIndex = 0; yIndex < options.RowCount; yIndex++)
            {
                var y = Lerp(mesh.Bounds.MinY, mesh.Bounds.MaxY, ySamples[yIndex]);
                var crossings = new List<double>();
                var triangleIndex = 0;
                foreach (var triangle in mesh.Triangles)
                {
                    if ((triangleIndex++ & 4095) == 0)
                        cancellationToken.ThrowIfCancellationRequested();
                    if (TryIntersectProjectedTriangle(mesh, triangle, y, z,
                            options.IntersectionToleranceMetres, out var x))
                        crossings.Add(x);
                }

                crossings.Sort();
                Deduplicate(crossings, options.IntersectionToleranceMetres);
                if (crossings.Count == 0)
                    continue;
                if ((crossings.Count & 1) != 0)
                {
                    diagnostics.Add(DesignDiagnostic.Error(HistoricalDiagnosticCodes.TopologyOpen,
                        $"Ray sampling found an odd crossing count at station {zIndex}, row {yIndex}."));
                    continue;
                }

                var intervalCount = crossings.Count / 2;
                if (intervalCount != 1)
                {
                    unsupported.Add(new UnsupportedEnvelopeRow(
                        zIndex, yIndex, zSamples[zIndex], ySamples[yIndex], intervalCount));
                    continue;
                }

                var minX = crossings[0];
                var maxX = crossings[1];
                var port = centerPlane - minX;
                var starboard = maxX - centerPlane;
                var asymmetry = Math.Abs(port - starboard);
                if (asymmetry > options.SymmetryToleranceMetres)
                {
                    if (options.SymmetryPolicy == HistoricalSymmetryPolicy.RejectAsymmetry)
                    {
                        diagnostics.Add(new DesignDiagnostic(
                            HistoricalDiagnosticCodes.SymmetryRejected,
                            DesignSeverity.Error,
                            $"Hull row asymmetry {asymmetry:0.###} m exceeds the configured " +
                            $"{options.SymmetryToleranceMetres:0.###} m tolerance.",
                            Field: nameof(HistoricalEnvelopeSamplingOptions.SymmetryPolicy),
                            Requested: DesignMeasure.FromMetres(options.SymmetryToleranceMetres),
                            Realized: DesignMeasure.FromMetres(asymmetry),
                            SuggestedCorrection: "Select disclosed symmetry normalization or repair/reselect the source hull."));
                    }
                    else
                    {
                        var half = Math.Max(port, starboard);
                        minX = centerPlane - half;
                        maxX = centerPlane + half;
                        normalizedRows++;
                    }
                }

                // Ray/triangle arithmetic is float-backed; clamp only the normalized expression
                // to its independently measured mesh bounds so the package loader sees the same
                // closed [0,1] contract rather than a one-ulp overshoot.
                intervals[zIndex, yIndex] = new HullEnvelopeInterval(
                    Math.Clamp((minX - mesh.Bounds.MinX) / mesh.Bounds.Beam, 0, 1),
                    Math.Clamp((maxX - mesh.Bounds.MinX) / mesh.Bounds.Beam, 0, 1));
                occupiedRows++;
            }
        }

        if (unsupported.Count != 0)
            diagnostics.Add(DesignDiagnostic.Error(HistoricalDiagnosticCodes.UnsupportedRowIntervals,
                $"{unsupported.Count} sampled row(s) contain multiple disjoint intervals. " +
                "The bounded historical-envelope representation cannot encode them."));
        if (occupiedRows == 0)
            diagnostics.Add(DesignDiagnostic.Error(HistoricalDiagnosticCodes.UnsupportedRowIntervals,
                "Sampling found no supported single-interval hull rows."));
        if (normalizedRows != 0)
            diagnostics.Add(DesignDiagnostic.Warning(HistoricalDiagnosticCodes.SymmetryRejected,
                $"Symmetry normalization expanded {normalizedRows} row(s) to the outer union; asymmetry loss is disclosed."));

        var envelope = new SampledHullEnvelope(
            mesh.Bounds, zSamples, ySamples, intervals, unsupported, normalizedRows,
            diagnostics, mesh.SourceContentHash, mesh.SourceTransform);
        return new HistoricalEnvelopeSamplingResult(envelope,
            Array.AsReadOnly(diagnostics.ToArray()));
    }

    private static List<DesignDiagnostic> Validate(HistoricalEnvelopeSamplingOptions options)
    {
        var diagnostics = new List<DesignDiagnostic>();
        if (options.StationCount is < 3 or > 512 || options.RowCount is < 2 or > 256 ||
            !Enum.IsDefined(options.SymmetryPolicy) ||
            !double.IsFinite(options.SymmetryToleranceMetres) || options.SymmetryToleranceMetres < 0 ||
            options.SymmetryToleranceMetres > 10 ||
            !double.IsFinite(options.IntersectionToleranceMetres) ||
            options.IntersectionToleranceMetres is <= 0 or > 0.01 ||
            options.MaxSampleRows is < 6 or > 10_000_000 ||
            options.MaxTriangleTests is < 1 or > 2_000_000_000)
        {
            diagnostics.Add(DesignDiagnostic.Error(HistoricalDiagnosticCodes.InputLimitExceeded,
                "Historical envelope sampling options are outside their supported bounds."));
        }
        return diagnostics;
    }

    private static double[] CellCenteredSamples(int count) =>
        Enumerable.Range(0, count).Select(index => (index + 0.5) / count).ToArray();

    private static bool TryIntersectProjectedTriangle(
        HistoricalMesh mesh,
        HistoricalTriangle triangle,
        double y,
        double z,
        double tolerance,
        out double x)
    {
        var a = mesh.Vertices[triangle.A];
        var b = mesh.Vertices[triangle.B];
        var c = mesh.Vertices[triangle.C];
        var denominator = (b.Z - c.Z) * (a.Y - c.Y) + (c.Y - b.Y) * (a.Z - c.Z);
        if (Math.Abs(denominator) <= tolerance)
        {
            x = 0;
            return false;
        }

        var alpha = ((b.Z - c.Z) * (y - c.Y) + (c.Y - b.Y) * (z - c.Z)) / denominator;
        var beta = ((c.Z - a.Z) * (y - c.Y) + (a.Y - c.Y) * (z - c.Z)) / denominator;
        var gamma = 1 - alpha - beta;
        if (alpha < -tolerance || beta < -tolerance || gamma < -tolerance)
        {
            x = 0;
            return false;
        }

        x = alpha * a.X + beta * b.X + gamma * c.X;
        return double.IsFinite(x);
    }

    private static void Deduplicate(List<double> sorted, double tolerance)
    {
        if (sorted.Count < 2)
            return;
        var write = 1;
        for (var read = 1; read < sorted.Count; read++)
        {
            if (Math.Abs(sorted[read] - sorted[write - 1]) <= tolerance)
                continue;
            sorted[write++] = sorted[read];
        }
        if (write < sorted.Count)
            sorted.RemoveRange(write, sorted.Count - write);
    }

    private static double Lerp(double from, double to, double amount) => from + (to - from) * amount;
}
