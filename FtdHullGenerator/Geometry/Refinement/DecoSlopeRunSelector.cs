using System.Collections.Immutable;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Decorations;
using FtdHullGenerator.Domain.Design;
using FtdHullGenerator.Geometry.Composition;
using FtdHullGenerator.Infrastructure;

namespace FtdHullGenerator.Geometry.Refinement;

/// <summary>
/// Automatic anchor/run discovery for the normal Deco Vertical and Deco Horizontal choices.
///
/// The frozen product rule is a finite local one: start from the final resolved native plan, take
/// only an exclusively-owned native smoothing <see cref="BlockShape.Slope4" /> anchor, measure the
/// visual run the proven native pass was beveling (its own candidate run, which the pass could only
/// build to 4 m), and request the longest valid supported integer decoration length from 5 through
/// 40 m. A shorter supported length is used when the longest one has no evidenced donor part; an
/// anchor with no supported length is left exactly as the native method produced it. No free-floating
/// decoration is invented and no native placement, cell or anchor identity is touched.
/// </summary>
public static class DecoSlopeRunSelector
{
    /// <summary>
    /// The measured run is the native pass's own contour measurement, never an arbitrary span or
    /// curve fit. It is derived only from the final non-smoothing placements so an adjacent
    /// extension record cannot lengthen a later run.
    /// </summary>
    private readonly record struct RowExtent(int MinZ, int MaxZ);

    /// <summary>
    /// Returns one deterministic request per eligible anchor, ordered by anchor position. Ineligible
    /// anchors are simply absent, which leaves their native result unchanged.
    /// </summary>
    public static ImmutableArray<SlopeExtensionRequest> Select(
        ShipGenerationSnapshot snapshot,
        SlopeExtensionKind kind,
        ICollection<DesignDiagnostic> diagnostics,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(diagnostics);
        token.ThrowIfCancellationRequested();

        if (!IsResolvedSnapshot(snapshot))
            return [];

        var hull = snapshot.Hull;
        var fingerprint = ShipCatalogFingerprint.Compute(snapshot.Catalog);
        if (hull.ResolvedCatalogFingerprint != fingerprint)
            return [];

        var (totalOccupancy, baseRows, uniqueByPosition) = IndexNativePlan(hull, token);
        var requests = ImmutableArray.CreateBuilder<SlopeExtensionRequest>();
        foreach (var anchor in hull.Blocks
                     .Where(block => block.Shape == BlockShape.Slope4 && block.Origin == BlockOrigin.Smoothing)
                     .OrderBy(block => block.X).ThenBy(block => block.Y).ThenBy(block => block.Z)
                     .ThenBy(block => block.Rotation))
        {
            token.ThrowIfCancellationRequested();
            if (!IsSupportedRotation(kind, anchor.Rotation))
                continue;
            if (!IsEligibleAnchor(anchor, hull, uniqueByPosition, totalOccupancy, snapshot.Catalog))
                continue;
            var run = MeasureRun(kind, anchor, hull.MinX + hull.MaxX, baseRows);
            if (run is not { } available)
                continue;
            if (SelectLength(available, anchor.Material, snapshot.Catalog) is not { } length)
                continue;
            if (requests.Count >= VerticalSlopeExtensionPlanner.MaximumRequests)
            {
                diagnostics.Add(DesignDiagnostic.Error(SlopeExtensionDiagnosticCodes.PayloadLimit,
                    $"Automatic decorative selection found more than {VerticalSlopeExtensionPlanner.MaximumRequests} " +
                    "eligible native anchors, which exceeds the D02 decoration payload budget.",
                    snapshot.Document.DocumentId, nameof(GeneratedHull.Blocks)) with
                    { SuggestedCorrection = "Reduce the hull or split the selection; the decoration payload cannot be truncated." });
                return [];
            }
            requests.Add(new SlopeExtensionRequest(anchor, length));
        }
        token.ThrowIfCancellationRequested();
        return requests.ToImmutable();
    }

    /// <summary>True when the anchor rotation belongs to the requested proven native family.</summary>
    public static bool IsSupportedRotation(SlopeExtensionKind kind, int rotation) => kind switch
    {
        SlopeExtensionKind.Vertical => rotation is 4 or 6 or 12 or 14,
        SlopeExtensionKind.Horizontal => rotation is 16 or 17 or 18 or 19,
        _ => false,
    };

    private static bool IsResolvedSnapshot(ShipGenerationSnapshot snapshot) =>
        !snapshot.Diagnostics.IsDefault && !snapshot.Diagnostics.Any(diagnostic => diagnostic.IsError) &&
        !string.IsNullOrWhiteSpace(snapshot.Document.DocumentId) &&
        snapshot.Hull.SourceDocumentId == snapshot.Document.DocumentId &&
        snapshot.Hull.SourceRevision == snapshot.Revision &&
        snapshot.Hull.ResolvedCatalogVersion == snapshot.Catalog.GameVersion;

    private static (Dictionary<(int X, int Y, int Z), int> Occupancy,
        Dictionary<(int X, int Y), RowExtent> BaseRows,
        Dictionary<(int X, int Y, int Z), BlockPlacement> UniqueByPosition) IndexNativePlan(
            GeneratedHull hull, CancellationToken token)
    {
        var occupancy = new Dictionary<(int X, int Y, int Z), int>(hull.Blocks.Count * 2);
        var baseRows = new Dictionary<(int X, int Y), RowExtent>();
        var uniqueByPosition = new Dictionary<(int X, int Y, int Z), BlockPlacement>();
        var ambiguous = new HashSet<(int X, int Y, int Z)>();
        foreach (var block in hull.Blocks)
        {
            token.ThrowIfCancellationRequested();
            if (!uniqueByPosition.TryAdd(block.Position, block))
                ambiguous.Add(block.Position);
            foreach (var cell in block.OccupiedCells)
            {
                occupancy[cell] = occupancy.GetValueOrDefault(cell) + 1;
                if (block.Origin == BlockOrigin.Smoothing)
                    continue;
                var key = (cell.X, cell.Y);
                baseRows[key] = baseRows.TryGetValue(key, out var extent)
                    ? new RowExtent(Math.Min(extent.MinZ, cell.Z), Math.Max(extent.MaxZ, cell.Z))
                    : new RowExtent(cell.Z, cell.Z);
            }
        }
        foreach (var position in ambiguous)
            uniqueByPosition.Remove(position);
        return (occupancy, baseRows, uniqueByPosition);
    }

    private static bool IsEligibleAnchor(
        BlockPlacement anchor,
        GeneratedHull hull,
        Dictionary<(int X, int Y, int Z), BlockPlacement> uniqueByPosition,
        Dictionary<(int X, int Y, int Z), int> occupancy,
        FtdBlockCatalog catalog)
    {
        if (!uniqueByPosition.TryGetValue(anchor.Position, out var final) || final != anchor)
            return false;
        if (anchor.ArmorDepth != 0 || anchor.UsePoles)
            return false;
        if (anchor.OccupiedCells.Any(cell => occupancy[cell] != 1))
            return false;
        if (anchor.OccupiedCells.Any(cell => cell.X < hull.MinX || cell.X > hull.MaxX ||
                cell.Y < hull.MinY || cell.Y > hull.MaxY ||
                cell.Z < hull.MinZ || cell.Z > hull.MaxZ))
            return false;
        if (anchor.Material is not (MaterialKind.Wood or MaterialKind.Metal or
                MaterialKind.LightweightAlloy or MaterialKind.HeavyArmor))
            return false;
        var host = catalog.Resolve(anchor.Material, BlockShape.Slope4);
        return !host.IsFallback && host.Guid != Guid.Empty &&
            host.Shape == BlockShape.Slope4 && host.Material == anchor.Material;
    }

    /// <summary>
    /// The visual run available to one anchor, in metres, or null when the final base plan no longer
    /// describes the contour that produced the anchor. The measurement is the native pass's own:
    /// a longitudinal tread reaches the neighbouring contour row, a vertical riser spans its
    /// constant-edge run, and a horizontal slope reaches its inboard width step.
    /// </summary>
    private static int? MeasureRun(
        SlopeExtensionKind kind,
        BlockPlacement anchor,
        int mirrorSum,
        Dictionary<(int X, int Y), RowExtent> baseRows)
    {
        var (x, y, z) = (anchor.X, anchor.Y, anchor.Z);
        switch (kind)
        {
            case SlopeExtensionKind.Vertical:
                switch (anchor.Rotation)
                {
                    case 12: // bow tread, extends +Z toward the next row's forward edge
                        return baseRows.TryGetValue((x, y + 1), out var forward)
                            ? forward.MaxZ - z + 1
                            : null;
                    case 14: // stern tread, extends -Z toward the next row's aft edge
                        return baseRows.TryGetValue((x, y + 1), out var aft)
                            ? z - aft.MinZ + 1
                            : null;
                    case 4: // ascending bow riser, extends -Y along its constant forward edge
                        return ConstantEdgeRun(baseRows, x, y, useMaxZ: true);
                    case 6: // ascending stern riser, extends -Y along its constant aft edge
                        return ConstantEdgeRun(baseRows, x, y, useMaxZ: false);
                    default:
                        return null;
                }

            case SlopeExtensionKind.Horizontal:
                // The proven pass measures the outboard row against the inboard row it bevels
                // toward; the mirrored starboard anchor uses the mirrored inboard row.
                var supportX = 2 * x < mirrorSum ? x + 1 : x - 1;
                if (!baseRows.TryGetValue((supportX, y), out var support))
                    return null;
                return anchor.Rotation switch
                {
                    16 or 18 => support.MaxZ - z + 1,
                    17 or 19 => z - support.MinZ + 1,
                    _ => null,
                };

            default:
                return null;
        }
    }

    private static int ConstantEdgeRun(
        Dictionary<(int X, int Y), RowExtent> baseRows, int x, int y, bool useMaxZ)
    {
        if (!baseRows.TryGetValue((x, y), out var origin))
            return 0;
        var edge = useMaxZ ? origin.MaxZ : origin.MinZ;
        var count = 0;
        for (var row = y; row >= y - NativeSlopeExtensionRule.MaximumLengthMetres; row--)
        {
            if (!baseRows.TryGetValue((x, row), out var current))
                break;
            if ((useMaxZ ? current.MaxZ : current.MinZ) != edge)
                break;
            count++;
        }
        return count;
    }

    /// <summary>
    /// The longest valid supported length: the candidate run capped at 40 m, then the longest
    /// shorter length whose evidenced donor part resolves from the installed catalog. The donor is
    /// chosen by the construction's <c>M = ceil(L / 10)</c> bucket, so a missing longer donor falls
    /// back to the longest shorter bucket that is installed. A null result leaves the native anchor.
    /// </summary>
    private static int? SelectLength(int available, MaterialKind material, FtdBlockCatalog catalog)
    {
        var candidate = Math.Min(available, NativeSlopeExtensionRule.MaximumLengthMetres);
        if (candidate < NativeSlopeExtensionRule.MinimumLengthMetres)
            return null;
        for (var meshLength = (candidate + 9) / 10; meshLength >= 1; meshLength--)
        {
            var length = Math.Min(candidate, meshLength * 10);
            if (length < NativeSlopeExtensionRule.MinimumLengthMetres)
                break;
            var shape = meshLength switch
            {
                1 => BlockShape.Slope1,
                2 => BlockShape.Slope2,
                3 => BlockShape.Slope3,
                _ => BlockShape.Slope4,
            };
            var donor = catalog.Resolve(material, shape);
            if (!donor.IsFallback && donor.Guid != Guid.Empty &&
                donor.Shape == shape && donor.Material == material)
                return length;
        }
        return null;
    }
}
