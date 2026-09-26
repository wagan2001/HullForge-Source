namespace FtdHullGenerator.Geometry;

/// <summary>
/// Inclusive lattice bounds for a finding's evidence. Min values are the smallest occupied
/// coordinates and Max values the largest, so the box is non-empty whenever the source list is.
/// </summary>
public readonly record struct HullCellBounds(int MinX, int MaxX, int MinY, int MaxY, int MinZ, int MaxZ)
{
    /// <summary>
    /// Builds the tightest axis-aligned box around the supplied cells. An empty sequence yields a
    /// degenerate all-zero box so callers never see inverted ranges.
    /// </summary>
    public static HullCellBounds FromCells(IEnumerable<(int X, int Y, int Z)> cells)
    {
        ArgumentNullException.ThrowIfNull(cells);
        var any = false;
        int minX = 0, maxX = 0, minY = 0, maxY = 0, minZ = 0, maxZ = 0;
        foreach (var (x, y, z) in cells)
        {
            if (!any)
            {
                minX = maxX = x;
                minY = maxY = y;
                minZ = maxZ = z;
                any = true;
                continue;
            }

            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
            if (z < minZ) minZ = z;
            if (z > maxZ) maxZ = z;
        }

        return new HullCellBounds(minX, maxX, minY, maxY, minZ, maxZ);
    }

    /// <summary>Human-readable inclusive ranges, e.g. <c>x[2..4] y[1..3] z[10..18]</c>.</summary>
    public string ToDisplay() => $"x[{MinX}..{MaxX}] y[{MinY}..{MaxY}] z[{MinZ}..{MaxZ}]";
}
