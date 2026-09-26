using System.Collections.ObjectModel;
using FtdHullGenerator.Domain.Design;
using FtdHullGenerator.Domain.Historical;

namespace FtdHullGenerator.Geometry.HullSources;

public readonly record struct HullEnvelopeBounds(
    double MinX,
    double MaxX,
    double MinY,
    double MaxY,
    double MinZ,
    double MaxZ)
{
    public double Beam => MaxX - MinX;
    public double Depth => MaxY - MinY;
    public double Length => MaxZ - MinZ;

    public bool IsValid =>
        double.IsFinite(MinX) && double.IsFinite(MaxX) && MinX < MaxX &&
        double.IsFinite(MinY) && double.IsFinite(MaxY) && MinY < MaxY &&
        double.IsFinite(MinZ) && double.IsFinite(MaxZ) && MinZ < MaxZ;
}

/// <summary>A single connected port-to-starboard interval at one sampled row.</summary>
public readonly record struct HullEnvelopeInterval(double MinX, double MaxX)
{
    public double Width => MaxX - MinX;
    public double Center => (MinX + MaxX) / 2;
}

public enum HullEnvelopeQueryStatus
{
    Available = 0,
    Empty = 1,
    OutsideSampleDomain = 2,
    UnsupportedMultipleIntervals = 3,
}

public readonly record struct UnsupportedEnvelopeRow(
    int StationIndex,
    int RowIndex,
    double NormalizedZ,
    double NormalizedY,
    int IntervalCount);

/// <summary>
/// Read-only common source seam. A query may explicitly say that a row has multiple intervals;
/// consumers must not bridge it into a fabricated solid interval.
/// </summary>
public interface IReadOnlyHullEnvelopeSource
{
    HullEnvelopeBounds Bounds { get; }
    int StationCount { get; }
    int RowCount { get; }
    IReadOnlyList<UnsupportedEnvelopeRow> UnsupportedRows { get; }

    HullEnvelopeQueryStatus QueryNormalized(
        double normalizedZ,
        double normalizedY,
        out HullEnvelopeInterval interval);
}

/// <summary>
/// Immutable normalized envelope. Z/Y sample locations and lateral intervals are normalized to
/// the source bounds; metre-space queries are reconstructed without retaining the source mesh.
/// </summary>
public sealed class SampledHullEnvelope : IReadOnlyHullEnvelopeSource
{
    private readonly double[] _z;
    private readonly double[] _y;
    private readonly HullEnvelopeInterval?[,] _intervals;
    private readonly Dictionary<(int Z, int Y), UnsupportedEnvelopeRow> _unsupported;
    private readonly ReadOnlyCollection<UnsupportedEnvelopeRow> _unsupportedOrdered;

    internal SampledHullEnvelope(
        HullEnvelopeBounds bounds,
        IReadOnlyList<double> normalizedZ,
        IReadOnlyList<double> normalizedY,
        HullEnvelopeInterval?[,] normalizedIntervals,
        IEnumerable<UnsupportedEnvelopeRow> unsupportedRows,
        int asymmetryNormalizedRowCount,
        IReadOnlyList<DesignDiagnostic> diagnostics,
        string sourceContentHash,
        HistoricalSourceTransform? sourceTransform = null)
    {
        if (!bounds.IsValid)
            throw new ArgumentOutOfRangeException(nameof(bounds));
        ArgumentNullException.ThrowIfNull(normalizedZ);
        ArgumentNullException.ThrowIfNull(normalizedY);
        ArgumentNullException.ThrowIfNull(normalizedIntervals);
        ArgumentNullException.ThrowIfNull(unsupportedRows);
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (normalizedZ.Count < 2 || normalizedY.Count < 2 ||
            normalizedIntervals.GetLength(0) != normalizedZ.Count ||
            normalizedIntervals.GetLength(1) != normalizedY.Count)
            throw new ArgumentException("The sampled-envelope grid dimensions do not agree.");

        Bounds = bounds;
        _z = normalizedZ.ToArray();
        _y = normalizedY.ToArray();
        ValidateAxis(_z, nameof(normalizedZ));
        ValidateAxis(_y, nameof(normalizedY));
        _intervals = (HullEnvelopeInterval?[,])normalizedIntervals.Clone();
        _unsupportedOrdered = Array.AsReadOnly(unsupportedRows
            .OrderBy(row => row.StationIndex).ThenBy(row => row.RowIndex).ToArray());
        _unsupported = _unsupportedOrdered.ToDictionary(row => (row.StationIndex, row.RowIndex));
        AsymmetryNormalizedRowCount = asymmetryNormalizedRowCount;
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
        SourceContentHash = sourceContentHash;
        SourceTransform = sourceTransform;
    }

    public HullEnvelopeBounds Bounds { get; }
    public int StationCount => _z.Length;
    public int RowCount => _y.Length;
    public IReadOnlyList<UnsupportedEnvelopeRow> UnsupportedRows => _unsupportedOrdered;
    public int AsymmetryNormalizedRowCount { get; }
    public IReadOnlyList<DesignDiagnostic> Diagnostics { get; }
    public string SourceContentHash { get; }
    public HistoricalSourceTransform? SourceTransform { get; }
    public bool IsUsable => UnsupportedRows.Count == 0 && !Diagnostics.Any(diagnostic => diagnostic.IsError);

    public HullEnvelopeQueryStatus QueryNormalized(
        double normalizedZ,
        double normalizedY,
        out HullEnvelopeInterval interval)
    {
        interval = default;
        if (!double.IsFinite(normalizedZ) || !double.IsFinite(normalizedY) ||
            normalizedZ < 0 || normalizedZ > 1 || normalizedY < 0 || normalizedY > 1)
            return HullEnvelopeQueryStatus.OutsideSampleDomain;

        var zIndex = NearestIndex(_z, normalizedZ);
        var yIndex = NearestIndex(_y, normalizedY);
        if (_unsupported.ContainsKey((zIndex, yIndex)))
            return HullEnvelopeQueryStatus.UnsupportedMultipleIntervals;
        if (_intervals[zIndex, yIndex] is not { } normalized)
            return HullEnvelopeQueryStatus.Empty;

        interval = new HullEnvelopeInterval(
            Bounds.MinX + normalized.MinX * Bounds.Beam,
            Bounds.MinX + normalized.MaxX * Bounds.Beam);
        return HullEnvelopeQueryStatus.Available;
    }

    internal HullEnvelopeInterval? GetNormalizedInterval(int stationIndex, int rowIndex) =>
        _intervals[stationIndex, rowIndex];

    internal double GetNormalizedZ(int stationIndex) => _z[stationIndex];
    internal double GetNormalizedY(int rowIndex) => _y[rowIndex];

    private static int NearestIndex(double[] values, double value)
    {
        var index = Array.BinarySearch(values, value);
        if (index >= 0)
            return index;
        index = ~index;
        if (index == 0)
            return 0;
        if (index == values.Length)
            return values.Length - 1;
        return value - values[index - 1] <= values[index] - value ? index - 1 : index;
    }

    private static void ValidateAxis(IReadOnlyList<double> values, string parameter)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (!double.IsFinite(values[i]) || values[i] is < 0 or > 1 ||
                i > 0 && values[i] <= values[i - 1])
                throw new ArgumentException("Sample positions must be finite, strictly increasing and normalized.", parameter);
        }
    }
}
