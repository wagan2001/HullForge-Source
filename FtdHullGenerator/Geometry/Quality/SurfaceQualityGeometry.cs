using FtdHullGenerator.Domain;

namespace FtdHullGenerator.Geometry;

/// <summary>Exposed and step area attributed to one lattice cell, in square metres.</summary>
public readonly record struct SurfaceCellArea(double Exposed, double Step);

/// <summary>Whole-hull exposed and step area, in square metres.</summary>
public readonly record struct SurfaceAreaTotals(double Exposed, double Step);

/// <summary>
/// Occupancy and surface measurements prepared once per hull. The row/column extents make the
/// overhang, jagged and landmark detectors O(cells); the exposed-surface pass is an
/// export-effective geometric coverage measurement (<see cref="SurfaceCoverage" />) that attributes
/// each face's uncovered residual to the owning placement's cells instead of to the nearest cell.
/// </summary>
public sealed class SurfaceQualityGeometry
{
    private readonly GeneratedHull _hull;
    private readonly HashSet<(int X, int Y, int Z)> _occupied;
    private readonly Dictionary<(int Y, int Z), int> _maxXByRow = [];
    private readonly Dictionary<(int Y, int Z), int> _minXByRow = [];
    private readonly Dictionary<(int X, int Z), int> _maxYByColumn = [];
    private readonly Dictionary<(int X, int Z), int> _minYByColumn = [];
    private readonly Dictionary<int, int> _topYByZ = [];
    private readonly Dictionary<int, int> _bottomYByZ = [];
    private IReadOnlyDictionary<(int X, int Y, int Z), SurfaceCellArea>? _hullSkinAreas;
    private SurfaceAreaTotals _hullSkinTotals;
    private IReadOnlyDictionary<(int X, int Y, int Z), SurfaceCellArea>? _boundaryAreas;
    private SurfaceAreaTotals _boundaryTotals;
    private SurfaceCoverage? _coverage;

    public SurfaceQualityGeometry(GeneratedHull hull)
    {
        ArgumentNullException.ThrowIfNull(hull);
        _hull = hull;
        _occupied = [];
        foreach (var block in hull.Blocks)
        foreach (var cell in block.OccupiedCells)
        {
            if (!_occupied.Add(cell)) continue;
            var row = (cell.Y, cell.Z);
            if (!_maxXByRow.TryGetValue(row, out var maxX) || cell.X > maxX) _maxXByRow[row] = cell.X;
            if (!_minXByRow.TryGetValue(row, out var minX) || cell.X < minX) _minXByRow[row] = cell.X;
            var column = (cell.X, cell.Z);
            if (!_maxYByColumn.TryGetValue(column, out var maxY) || cell.Y > maxY) _maxYByColumn[column] = cell.Y;
            if (!_minYByColumn.TryGetValue(column, out var minY) || cell.Y < minY) _minYByColumn[column] = cell.Y;
            if (!_topYByZ.TryGetValue(cell.Z, out var top) || cell.Y > top) _topYByZ[cell.Z] = cell.Y;
            if (!_bottomYByZ.TryGetValue(cell.Z, out var bottom) || cell.Y < bottom) _bottomYByZ[cell.Z] = cell.Y;
        }
    }

    /// <summary>Every lattice cell filled by any placement, including all cells of multi-cell footprints.</summary>
    public IReadOnlySet<(int X, int Y, int Z)> Occupied => _occupied;

    /// <summary>True when the exact cell is filled.</summary>
    public bool IsOccupied((int X, int Y, int Z) cell) => _occupied.Contains(cell);

    /// <summary>Maximum X filled at each (Y,Z) row. A missing row is empty.</summary>
    public IReadOnlyDictionary<(int Y, int Z), int> MaxXByRow => _maxXByRow;

    /// <summary>Minimum X filled at each (Y,Z) row. A missing row is empty.</summary>
    public IReadOnlyDictionary<(int Y, int Z), int> MinXByRow => _minXByRow;

    /// <summary>Maximum Y filled at each (X,Z) column. A missing column is empty.</summary>
    public IReadOnlyDictionary<(int X, int Z), int> MaxYByColumn => _maxYByColumn;

    /// <summary>Minimum Y filled at each (X,Z) column. A missing column is empty.</summary>
    public IReadOnlyDictionary<(int X, int Z), int> MinYByColumn => _minYByColumn;

    internal GeneratedHull Hull => _hull;
    internal int MinX => _hull.MinX;
    internal int MaxX => _hull.MaxX;
    internal int MinY => _hull.MinY;
    internal int MaxY => _hull.MaxY;
    internal int MinZ => _hull.MinZ;
    internal int MaxZ => _hull.MaxZ;

    /// <summary>Highest filled Y at station Z, or -1 when the station is empty.</summary>
    public int TopYAt(int z) => _topYByZ.TryGetValue(z, out var value) ? value : -1;

    /// <summary>Lowest filled Y at station Z, or -1 when the station is empty.</summary>
    public int BottomYAt(int z) => _bottomYByZ.TryGetValue(z, out var value) ? value : -1;

    /// <summary>
    /// The export-effective coverage measurement, computed once and reused by every consumer.
    /// </summary>
    public SurfaceCoverage Coverage => _coverage ??= new SurfaceCoverage(_hull);

    /// <summary>Alias for <see cref="Coverage" /> matching the measurement verb used elsewhere.</summary>
    public SurfaceCoverage MeasureCoverage() => Coverage;

    /// <summary>
    /// Exterior hull-skin exposed/step area per cell, computed once and reused. Interior armor and
    /// enclosed cavities contribute zero, so a detector can never mistake an internal boundary for an
    /// outer-surface defect. Sums <paramref name="totals" /> over the cells in sorted order so
    /// identical occupancy always produces bit-identical totals.
    /// </summary>
    public IReadOnlyDictionary<(int X, int Y, int Z), SurfaceCellArea> MeasureExposedSurface(out SurfaceAreaTotals totals)
    {
        if (_hullSkinAreas is null)
        {
            var coverage = Coverage;
            _hullSkinAreas = coverage.HullSkinCellAreas;
            _hullSkinTotals = coverage.HullSkinTotals;
        }

        totals = _hullSkinTotals;
        return _hullSkinAreas;
    }

    /// <summary>
    /// All-boundary exposed/step area per cell (hull skin plus interior armor), computed once and
    /// reused. Use this only to report the full boundary; detectors must consume
    /// <see cref="MeasureExposedSurface(out SurfaceAreaTotals)" /> so interior armor stays out of
    /// outer-surface findings.
    /// </summary>
    public IReadOnlyDictionary<(int X, int Y, int Z), SurfaceCellArea> MeasureBoundarySurface(out SurfaceAreaTotals totals)
    {
        if (_boundaryAreas is null)
        {
            var coverage = Coverage;
            _boundaryAreas = coverage.BoundaryCellAreas;
            _boundaryTotals = coverage.Totals;
        }

        totals = _boundaryTotals;
        return _boundaryAreas;
    }

    /// <summary>
    /// Measures each cell's exterior hull-skin exposed and step area with the export-effective
    /// geometric coverage engine. Faces are generated from the shape the exporter will write, uncovered
    /// residuals are split across the owning placement's cells, and interior armor, enclosed cavities
    /// and covered residuals are excluded. This is a documented, cell-attributed extension of the
    /// <see cref="SurfacePatchMetrics" /> definitions; that class is not modified.
    /// </summary>
    public static Dictionary<(int X, int Y, int Z), SurfaceCellArea> MeasureExposedSurface(
        GeneratedHull hull, out SurfaceAreaTotals totals)
    {
        ArgumentNullException.ThrowIfNull(hull);
        var coverage = new SurfaceCoverage(hull);
        totals = coverage.HullSkinTotals;
        return new Dictionary<(int X, int Y, int Z), SurfaceCellArea>(coverage.HullSkinCellAreas);
    }

    /// <summary>
    /// Measures each cell's all-boundary exposed and step area (hull skin plus interior armor) with the
    /// export-effective geometric coverage engine. Reporting only; detectors use
    /// <see cref="MeasureExposedSurface(GeneratedHull, out SurfaceAreaTotals)" />.
    /// </summary>
    public static Dictionary<(int X, int Y, int Z), SurfaceCellArea> MeasureBoundarySurface(
        GeneratedHull hull, out SurfaceAreaTotals totals)
    {
        ArgumentNullException.ThrowIfNull(hull);
        var coverage = new SurfaceCoverage(hull);
        totals = coverage.Totals;
        return new Dictionary<(int X, int Y, int Z), SurfaceCellArea>(coverage.BoundaryCellAreas);
    }
}
