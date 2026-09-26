using FtdHullGenerator.Domain;

namespace FtdHullGenerator.Geometry.Smoothing;

/// <summary>A complete mirrored assembly and the original material needed at its boundaries.</summary>
public sealed record SurfaceConstructionRegion(
    int Id,
    string Pattern,
    IReadOnlyList<BlockPlacement> Placements,
    IReadOnlyList<SurfaceSpanEdit> SpanEdits,
    IReadOnlyList<(int X, int Y, int Z)> RequiredBacking);

public sealed record SurfaceFitResult(GeneratedHull Hull, IReadOnlyList<int> FailedRegionIds,
    IReadOnlyList<string> Diagnostics)
{
    public bool Succeeded => FailedRegionIds.Count == 0;
}

/// <summary>
/// Reserves complete assemblies before armor is built. Failed fitting never leaves
/// half an assembly behind: the caller rebuilds the solid using WithoutRegions.
/// </summary>
public sealed class SurfaceConstructionPlan
{
    internal SurfaceConstructionPlan(HullSurfaceGrid originalGrid,
        IReadOnlyList<SurfaceConstructionRegion> regions, IReadOnlyList<string> diagnostics)
    {
        OriginalGrid = originalGrid;
        Regions = regions;
        Diagnostics = diagnostics;
        SpanEdits = regions.SelectMany(region => region.SpanEdits).Distinct().ToArray();
        ModifiedGrid = originalGrid.WithEdits(SpanEdits);
    }

    public HullSurfaceGrid OriginalGrid { get; }
    public HullSurfaceGrid ModifiedGrid { get; }
    public IReadOnlyList<SurfaceConstructionRegion> Regions { get; }
    public IReadOnlyList<SurfaceSpanEdit> SpanEdits { get; }
    public IReadOnlyList<string> Diagnostics { get; }

    public SurfaceConstructionPlan WithoutRegions(IEnumerable<int> failedRegionIds)
    {
        var rejected = failedRegionIds.ToHashSet();
        // Undoing a span can alter which neighbouring cells armor extraction
        // classifies as exposed, even when the fitted footprints do not overlap.
        // Roll those dependent regions back in the same rebuild.
        bool changed;
        do
        {
            changed = false;
            var changedRows = Regions.Where(region => rejected.Contains(region.Id))
                .SelectMany(region => region.SpanEdits).Select(edit => (edit.Y, edit.Z)).ToHashSet();
            foreach (var region in Regions.Where(region => !rejected.Contains(region.Id)))
            {
                var dependent = region.Placements.SelectMany(part => part.OccupiedCells)
                    .Concat(region.RequiredBacking).Any(cell =>
                        changedRows.Contains((cell.Y, cell.Z)) || changedRows.Contains((cell.Y - 1, cell.Z)) ||
                        changedRows.Contains((cell.Y + 1, cell.Z)) || changedRows.Contains((cell.Y, cell.Z - 1)) ||
                        changedRows.Contains((cell.Y, cell.Z + 1)));
                if (dependent) changed |= rejected.Add(region.Id);
            }
        } while (changed);
        return new(OriginalGrid, Regions.Where(region => !rejected.Contains(region.Id)).ToArray(),
            Diagnostics.Concat(rejected.Count == 0 ? [] :
                new[] { $"Restored {rejected.Count} construction regions after armor reconciliation." }).ToArray());
    }

    public SurfaceFitResult TryFit(GeneratedHull hull, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hull);
        var byCell = hull.Blocks.SelectMany(block => block.OccupiedCells.Select(cell => (cell, block)))
            .ToDictionary(pair => pair.cell, pair => pair.block);
        var removed = new HashSet<(int X, int Y, int Z)>();
        var additions = new List<BlockPlacement>();
        var failed = new List<int>();
        var diagnostics = new List<string>(Diagnostics);
        var reasons = new Dictionary<string, int>();
        foreach (var region in Regions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cells = region.Placements.SelectMany(block => block.OccupiedCells).ToArray();
            MaterialKind? material = null;
            var valid = cells.Distinct().Count() == cells.Length;
            foreach (var cell in cells)
            {
                if (!byCell.TryGetValue(cell, out var existing) || existing.Shape != BlockShape.Cube ||
                    existing.ArmorDepth != 0 || existing.UsePoles ||
                    existing.Construction != ArmorConstruction.Solid || removed.Contains(cell) ||
                    existing.Origin != BlockOrigin.Shell || material is not null && existing.Material != material)
                {
                    valid = false;
                    var reason = !byCell.ContainsKey(cell) ? "missing surface cell" :
                        existing.ArmorDepth != 0 ? "surface classified behind armor" : "ineligible surface material or construction";
                    reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
                    break;
                }
                material = existing.Material;
            }

            if (valid)
            {
                foreach (var cell in region.RequiredBacking)
                {
                    // Requiring a real full cube here protects reserved air and
                    // geometrically incomplete pole/beam support, not just lattice adjacency.
                    if (!byCell.TryGetValue(cell, out var backing) || backing.Shape != BlockShape.Cube ||
                        backing.UsePoles || backing.Construction != ArmorConstruction.Solid ||
                        backing.Material != material || removed.Contains(cell))
                    {
                        valid = false;
                        var reason = !byCell.ContainsKey(cell) ? "missing boundary support" : "ineligible boundary material or construction";
                        reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
                        break;
                    }
                }
            }
            if (!valid || material is null)
            {
                failed.Add(region.Id);
                continue;
            }
            removed.UnionWith(cells);
            additions.AddRange(region.Placements.Select(block => block with
            {
                Material = material.Value, Origin = BlockOrigin.Smoothing, ArmorDepth = 0,
            }));
        }
        if (failed.Count > 0)
            diagnostics.Add($"{failed.Count} complete construction regions require their original contour after armor fitting.");
        foreach (var reason in reasons)
            diagnostics.Add($"Construction reconciliation: {reason.Value} regions with {reason.Key}.");
        var blocks = hull.Blocks.Where(block => !removed.Contains(block.Position)).Concat(additions)
            .OrderBy(block => block.Z).ThenBy(block => block.Y).ThenBy(block => block.X)
            .ThenBy(block => block.Shape).ThenBy(block => block.Rotation).ToArray();
        return new(hull with { Blocks = blocks }, failed, diagnostics);
    }
}
