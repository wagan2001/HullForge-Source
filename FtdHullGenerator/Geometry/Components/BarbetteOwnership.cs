using FtdHullGenerator.Domain.Components;
using FtdHullGenerator.Domain.Composition;
using FtdHullGenerator.Domain.Design;
using FtdHullGenerator.Geometry.Composition;

namespace FtdHullGenerator.Geometry.Components;

/// <summary>One requested barbette at its solved longitudinal position.</summary>
public sealed record BarbettePlacement(
    BarbetteDefinition Definition,
    DesignMeasure CenterX,
    DesignMeasure CenterZ);

/// <summary>
/// One barbette's hull-reconciled result plus the ownership metadata the renderer, ruler and later
/// internal-structure work consume. It is the single resolved barbette truth: nothing downstream
/// re-derives clear bounds, armor extents or the realized clear depth.
/// </summary>
public sealed record BarbetteOwnershipEntry(
    BarbetteDefinition Definition,
    DesignMeasure CenterX,
    DesignMeasure CenterZ,
    BarbetteGenerationResult Generation)
{
    public string Id => Definition.Id;
    public string NodeId => Definition.NodeId;

    /// <summary>
    /// The realized protected clear-volume envelope, or <c>null</c> for a rejected request. An
    /// invalid generation never exposes bounds even when its measurement and layout were retained
    /// for diagnostics, so no invalid metadata can expand downstream reservations.
    /// </summary>
    public DesignBounds? ClearVolumeBounds => Generation.IsValid ? Generation.ClearVolumeBounds : null;

    /// <summary>
    /// The measured longitudinal clear-space half-extent BAR03's ruler consumes, or zero for a
    /// rejected request. An invalid generation never contributes a clear extent.
    /// </summary>
    public DesignMeasure ClearLongitudinalHalfExtent => Generation.IsValid
        ? Generation.Measurement?.ClearLongitudinalHalfExtent ?? DesignMeasure.Zero
        : DesignMeasure.Zero;
}

/// <summary>
/// The whole multi-barbette ownership outcome: one deterministic physical owner per armor cell,
/// enforced protected-clear separation and every red/yellow diagnostic.
/// </summary>
public sealed record BarbetteOwnershipResult(
    IReadOnlyList<BarbetteOwnershipEntry> Barbettes,
    IReadOnlyList<BarbetteSolidIntent> Solids,
    IReadOnlyList<HullCell> SupersededArmorCells,
    IReadOnlyList<DesignDiagnostic> Diagnostics,
    DesignMeasure ShipMidpointZ)
{
    public bool IsValid => !Diagnostics.HasErrors();
}

/// <summary>
/// BAR02's cross-barbette ownership pass. Each barbette is reconciled against the evaluated hull
/// first; this pass then enforces the frozen multi-barbette rules without moving either barbette:
/// protected clear volumes never overlap and keep at least one metre of structural separation, and
/// one voxel has exactly one physical owner chosen by longitudinal midpoint with a forward tie.
/// </summary>
public static class BarbetteOwnership
{
    private static readonly (int X, int Y, int Z)[] FaceNeighbors =
    [
        (-1, 0, 0), (1, 0, 0),
        (0, -1, 0), (0, 1, 0),
        (0, 0, -1), (0, 0, 1),
    ];

    /// <summary>
    /// Reconciles every requested barbette against the evaluated hull and resolves their shared
    /// ownership. <paramref name="shipMidpointZ"/> is the ship's longitudinal midpoint in design
    /// coordinates; it only ever breaks a shared-armor tie and never relocates a barbette.
    /// </summary>
    public static BarbetteOwnershipResult Reconcile(
        IReadOnlyList<BarbettePlacement> placements,
        DesignMeasure shipMidpointZ,
        HullBuildContext context,
        BarbetteGenerationLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(placements);
        ArgumentNullException.ThrowIfNull(context);
        limits ??= BarbetteGenerationLimits.Default;
        cancellationToken.ThrowIfCancellationRequested();

        var diagnostics = new List<DesignDiagnostic>();
        var ordered = placements.OrderBy(placement => placement.Definition.Id, StringComparer.Ordinal)
            .ToArray();
        var entries = new List<BarbetteOwnershipEntry>(ordered.Length);
        var generations = new BarbetteGenerationResult[ordered.Length];
        for (var index = 0; index < ordered.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var placement = ordered[index];
            var generation = BarbetteGenerator.Generate(placement.Definition, placement.CenterX,
                placement.CenterZ, context, limits, cancellationToken);
            generations[index] = generation;
            diagnostics.AddRange(generation.Diagnostics);
            entries.Add(new BarbetteOwnershipEntry(placement.Definition, placement.CenterX,
                placement.CenterZ, generation));
        }

        var clearVolumes = new HashSet<HullCell>[ordered.Length];
        for (var index = 0; index < ordered.Length; index++)
            clearVolumes[index] = ProtectedClearVolumeOf(generations[index]);

        ValidateClearSeparation(ordered, clearVolumes, diagnostics, cancellationToken);
        ValidateArmorDoesNotPenetrateClearVolumes(ordered, generations, clearVolumes, diagnostics,
            cancellationToken);

        var ranked = Enumerable.Range(0, ordered.Length)
            .OrderBy(index => index, Comparer<int>.Create((left, right) =>
                CompareOwnership(ordered[left].CenterZ, ordered[right].CenterZ, shipMidpointZ,
                    ordered[left].Definition.Id, ordered[right].Definition.Id)))
            .ToArray();

        var mergedSolids = new List<BarbetteSolidIntent>();
        var owners = new Dictionary<HullCell, int>();
        var losers = new Dictionary<(int Winner, int Loser), int>();
        foreach (var index in ranked)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // An invalid result is diagnostic-only. It never owns armor, even if a future rejection
            // path were to retain partial solids alongside its measurement and layout.
            if (!generations[index].IsValid)
                continue;
            foreach (var solid in generations[index].Solids)
            {
                if (owners.TryAdd(solid.Cell, index))
                {
                    mergedSolids.Add(solid);
                    continue;
                }

                var winner = owners[solid.Cell];
                var key = (winner, index);
                losers[key] = losers.TryGetValue(key, out var count) ? count + 1 : 1;
            }
        }

        foreach (var ((winner, loser), count) in losers.OrderBy(pair => pair.Key.Winner)
                     .ThenBy(pair => pair.Key.Loser))
        {
            diagnostics.Add(new DesignDiagnostic(
                BarbetteDiagnosticCodes.ArmorOwnershipMerged,
                DesignSeverity.Warning,
                $"Barbettes '{ordered[loser].Definition.Id}' and '{ordered[winner].Definition.Id}' share " +
                $"{count} armor cell(s); '{ordered[winner].Definition.Id}' owns them because it is closer " +
                "to the ship longitudinal midpoint (forward wins an equal-distance tie).",
                ordered[loser].Definition.NodeId,
                nameof(BarbetteDefinition.SideArmor),
                SuggestedCorrection: "Overlapping exterior armor merges by design; no barbette is moved."));
        }

        var superseded = entries.Where(entry => entry.Generation.IsValid)
            .SelectMany(entry => entry.Generation.SupersededArmorCells)
            .Distinct()
            .OrderBy(cell => cell.Z).ThenBy(cell => cell.Y).ThenBy(cell => cell.X)
            .ToArray();
        var resolvedSolids = mergedSolids
            .OrderBy(intent => intent.Cell.Z).ThenBy(intent => intent.Cell.Y).ThenBy(intent => intent.Cell.X)
            .ToArray();
        return new BarbetteOwnershipResult(entries, resolvedSolids, superseded, diagnostics, shipMidpointZ);
    }

    /// <summary>
    /// The protected clear volume of one barbette: the circular cavity prism plus the complete square
    /// neck shaft. Deliberate armor-air reservations inside an armor stack are not part of it.
    /// An invalid generation returns an empty set even when its measurement and layout were retained
    /// for diagnostics, so a rejected request can never expand protected cells or reservations.
    /// </summary>
    internal static HashSet<HullCell> ProtectedClearVolumeOf(BarbetteGenerationResult generation)
    {
        var cells = new HashSet<HullCell>();
        // Fail closed before reading retained diagnostic metadata.
        if (!generation.IsValid)
            return cells;
        if (generation.Measurement is not { } measurement || generation.Layout is not { } layout)
            return cells;
        foreach (var cell in measurement.ClearCells)
            for (var y = layout.ClearVolumeMinY; y <= layout.ClearVolumeMaxY; y++)
                cells.Add(new HullCell(cell.X, y, cell.Z));
        foreach (var cell in measurement.NeckClearCells)
            for (var y = layout.RoofBottomY; y <= layout.NeckTopY; y++)
                cells.Add(new HullCell(cell.X, y, cell.Z));
        return cells;
    }

    /// <summary>
    /// Distinct protected clear volumes may never overlap and must keep at least one metre of
    /// structural material between them. Face adjacency is the discrete one-metre separator test.
    /// </summary>
    private static void ValidateClearSeparation(
        BarbettePlacement[] ordered,
        HashSet<HullCell>[] clearVolumes,
        List<DesignDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        for (var left = 0; left < ordered.Length; left++)
        for (var right = left + 1; right < ordered.Length; right++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HullCell? overlap = null;
            foreach (var cell in clearVolumes[left])
            {
                if (!clearVolumes[right].Contains(cell))
                    continue;
                overlap = cell;
                break;
            }

            if (overlap is { } shared)
            {
                diagnostics.Add(new DesignDiagnostic(
                    BarbetteDiagnosticCodes.ClearVolumeOverlap,
                    DesignSeverity.Error,
                    $"Protected clear volumes of barbettes '{ordered[left].Definition.Id}' and " +
                    $"'{ordered[right].Definition.Id}' overlap at ({shared.X}, {shared.Y}, {shared.Z}).",
                    ordered[right].Definition.NodeId,
                    nameof(BarbetteDefinition.ClearDiameter),
                    SuggestedCorrection: "Separate the barbettes or reduce the clear diameter/depth; neither barbette is moved automatically.",
                    AffectedBounds: CellBounds(shared)));
                continue;
            }

            HullCell? touching = null;
            foreach (var cell in clearVolumes[left])
            {
                foreach (var offset in FaceNeighbors)
                {
                    var neighbor = new HullCell(cell.X + offset.X, cell.Y + offset.Y, cell.Z + offset.Z);
                    if (!clearVolumes[right].Contains(neighbor))
                        continue;
                    touching = neighbor;
                    break;
                }

                if (touching is not null)
                    break;
            }

            if (touching is { } contact)
                diagnostics.Add(new DesignDiagnostic(
                    BarbetteDiagnosticCodes.ClearVolumeSeparation,
                    DesignSeverity.Error,
                    $"Protected clear volumes of barbettes '{ordered[left].Definition.Id}' and " +
                    $"'{ordered[right].Definition.Id}' are face-adjacent at " +
                    $"({contact.X}, {contact.Y}, {contact.Z}) with no one-metre structural separator.",
                    ordered[right].Definition.NodeId,
                    nameof(BarbetteDefinition.ClearDiameter),
                    DesignMeasure.FromMetres(1),
                    DesignMeasure.Zero,
                    "Separate the barbettes by at least one metre of structure; neither barbette is moved automatically.",
                    CellBounds(contact)));
        }
    }

    private static void ValidateArmorDoesNotPenetrateClearVolumes(
        BarbettePlacement[] ordered,
        BarbetteGenerationResult[] generations,
        HashSet<HullCell>[] clearVolumes,
        List<DesignDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        for (var left = 0; left < ordered.Length; left++)
        {
            if (!generations[left].IsValid)
                continue;
            for (var right = 0; right < ordered.Length; right++)
            {
                if (left == right || clearVolumes[right].Count == 0)
                    continue;
                cancellationToken.ThrowIfCancellationRequested();
                HullCell? penetration = null;
                foreach (var intent in generations[left].Solids)
                {
                    if (!clearVolumes[right].Contains(intent.Cell))
                        continue;
                    penetration = intent.Cell;
                    break;
                }

                if (penetration is not { } blocked)
                    continue;
                diagnostics.Add(new DesignDiagnostic(
                    BarbetteDiagnosticCodes.ArmorPenetratesClearVolume,
                    DesignSeverity.Error,
                    $"Barbette '{ordered[left].Definition.Id}' armor would penetrate the protected clear " +
                    $"volume of barbette '{ordered[right].Definition.Id}' at " +
                    $"({blocked.X}, {blocked.Y}, {blocked.Z}).",
                    ordered[left].Definition.NodeId,
                    nameof(BarbetteDefinition.SideArmor),
                    SuggestedCorrection: "Separate the barbettes or reduce the requested armor/clear sizes.",
                    AffectedBounds: CellBounds(blocked)));
            }
        }
    }

    /// <summary>
    /// Deterministic ownership order: closer to the ship longitudinal midpoint first, then the
    /// forward barbette, then the ordinal id so the result never depends on input order.
    /// </summary>
    private static int CompareOwnership(
        DesignMeasure leftZ,
        DesignMeasure rightZ,
        DesignMeasure shipMidpointZ,
        string leftId,
        string rightId)
    {
        var leftDistance = Math.Abs((long)leftZ.TwiceMetres - shipMidpointZ.TwiceMetres);
        var rightDistance = Math.Abs((long)rightZ.TwiceMetres - shipMidpointZ.TwiceMetres);
        if (leftDistance != rightDistance)
            return leftDistance.CompareTo(rightDistance);
        if (leftZ.TwiceMetres != rightZ.TwiceMetres)
            return rightZ.TwiceMetres.CompareTo(leftZ.TwiceMetres);
        return string.CompareOrdinal(leftId, rightId);
    }

    private static DesignBounds CellBounds(HullCell cell) => new(
        DesignMeasure.FromCellAnchor(cell.X), DesignMeasure.FromCellAnchor(cell.X),
        DesignMeasure.FromCellAnchor(cell.Y), DesignMeasure.FromCellAnchor(cell.Y),
        DesignMeasure.FromCellAnchor(cell.Z), DesignMeasure.FromCellAnchor(cell.Z));
}
