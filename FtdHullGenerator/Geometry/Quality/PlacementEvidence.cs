namespace FtdHullGenerator.Geometry;

/// <summary>
/// Turns evidence cells into deterministic, reproducible source-placement descriptions. Detectors use
/// this to record which placements produced a finding so a reader can recreate the fixture without the
/// report; it never changes a measurement or a threshold.
/// </summary>
internal static class PlacementEvidence
{
    /// <summary>
    /// Describes every effective placement in either hull that owns at least one evidence cell. Ids are
    /// sorted ordinally so the result is independent of placement order. Returns empty when no placement
    /// owns any evidence cell (for example a purely geometric section intersection).
    /// </summary>
    public static (IReadOnlyList<string> Ids, string? Description) Describe(
        SurfaceQualityGeometry baseline,
        SurfaceQualityGeometry candidate,
        IEnumerable<(int X, int Y, int Z)> cells)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(cells);

        var evidence = cells as HashSet<(int X, int Y, int Z)> ?? cells.ToHashSet();
        var ids = new SortedSet<string>(StringComparer.Ordinal);
        Collect(baseline, "baseline", evidence, ids);
        Collect(candidate, "candidate", evidence, ids);
        return (ids.ToArray(), ids.Count == 0 ? null : string.Join("; ", ids));
    }

    private static void Collect(
        SurfaceQualityGeometry geometry, string hull, HashSet<(int X, int Y, int Z)> evidence, SortedSet<string> ids)
    {
        var blocks = geometry.Hull.Blocks;
        var effective = geometry.Coverage.EffectivePlacements;
        for (var index = 0; index < blocks.Count && index < effective.Count; index++)
        {
            var placement = effective[index];
            var owns = false;
            foreach (var cell in placement.OccupiedCells)
            {
                if (!evidence.Contains(cell)) continue;
                owns = true;
                break;
            }

            if (!owns) continue;
            ids.Add(
                $"{hull}:placement[{index}] {placement.Shape}@{placement.X},{placement.Y},{placement.Z} " +
                $"rot{placement.Rotation} depth{placement.ArmorDepth}");
        }
    }
}
