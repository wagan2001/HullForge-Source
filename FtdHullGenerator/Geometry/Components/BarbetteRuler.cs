using FtdHullGenerator.Domain.Components;
using FtdHullGenerator.Domain.Design;

namespace FtdHullGenerator.Geometry.Components;

/// <summary>One barbette's requested longitudinal placement, in ruler coordinates from the bow.</summary>
public sealed record BarbetteRulerRequest(
    string BarbetteId,
    string NodeId,
    BarbetteDefinition Definition,
    DesignMeasure RulerCenter);

/// <summary>How an adjacent pair's protected clear volumes relate.</summary>
public enum BarbetteRulerSegmentState
{
    /// <summary>At least the frozen one-metre structural separator exists between the cavities.</summary>
    Valid = 0,

    /// <summary>The cavities are closer than one metre apart but do not share a cell.</summary>
    BelowMinimumSeparation = 1,

    /// <summary>The cavities share at least one raster cell.</summary>
    Overlapping = 2,
}

/// <summary>
/// One barbette drawn on the bow-to-stern ruler. The clear extents come from the BAR01 measured
/// raster clear bounds, so armor thickness never enters a readout and the exterior footprint never
/// inflates the inter-barbette clear gap.
/// </summary>
public sealed record BarbetteRulerEntry(
    string BarbetteId,
    string NodeId,
    DesignMeasure RulerCenter,
    DesignMeasure WorldZCenter,
    DesignMeasure ClearBowExtent,
    DesignMeasure ClearSternExtent,
    DesignMeasure ClearBowEdge,
    DesignMeasure ClearSternEdge,
    DesignMeasure? BowMargin,
    DesignMeasure? SternMargin,
    bool IsBlocking,
    IReadOnlyList<DesignDiagnostic> Diagnostics)
{
    public DesignMeasure ClearDiameterExtent => ClearBowExtent + ClearSternExtent;
}

/// <summary>One adjacent pair's longitudinal clear-volume separation.</summary>
public sealed record BarbetteRulerSegment(
    string ForwardBarbetteId,
    string AftBarbetteId,
    DesignMeasure CenterDistance,
    DesignMeasure ClearGap,
    BarbetteRulerSegmentState State);

/// <summary>
/// The frozen 2.0 bow-to-stern ruler readouts. It consumes the same BAR01 raster measurement that
/// generation uses instead of re-deriving clear geometry, and it never moves a requested position:
/// invalid placements stay where the user put them and are diagnosed.
/// </summary>
public sealed record BarbetteRulerModel(
    DesignMeasure RulerEnd,
    DesignMeasure LayoutBowZ,
    IReadOnlyList<BarbetteRulerEntry> Entries,
    IReadOnlyList<BarbetteRulerSegment> Segments,
    DesignMeasure? BowMargin,
    DesignMeasure? SternMargin,
    IReadOnlyList<DesignDiagnostic> Diagnostics)
{
    public static BarbetteRulerModel Empty { get; } = new(
        DesignMeasure.Zero, DesignMeasure.Zero, [], [], null, null, []);

    public bool HasEntries => Entries.Count > 0;

    public DesignMeasure WorldZToRulerCenter(DesignMeasure worldZ) => LayoutBowZ - worldZ;

    public DesignMeasure RulerCenterToWorldZ(DesignMeasure rulerCenter) => LayoutBowZ - rulerCenter;

    public BarbetteRulerEntry? FindEntry(string barbetteId) =>
        Entries.FirstOrDefault(entry => string.Equals(entry.BarbetteId, barbetteId, StringComparison.Ordinal));

    /// <summary>
    /// Snaps a requested ruler centre to the nearest whole-metre world-Z cell anchor. A barbette is
    /// never placed half-way between two voxel columns, so a drag moves in exact one-block steps.
    /// </summary>
    public DesignMeasure SnapToCellAnchor(DesignMeasure requestedRulerCenter)
    {
        var worldZ = RulerCenterToWorldZ(requestedRulerCenter);
        var wholeMetres = (int)Math.Round(worldZ.Metres, MidpointRounding.AwayFromZero);
        return RulerCenterToWorldZ(DesignMeasure.FromMetres(wholeMetres));
    }

    /// <summary>
    /// Builds the ruler readouts. <paramref name="rulerEnd"/> is the stern end in ruler coordinates;
    /// the bow end is zero. Entries are reported bow to stern.
    /// </summary>
    public static BarbetteRulerModel Build(
        IReadOnlyList<BarbetteRulerRequest> requests,
        DesignMeasure rulerEnd,
        DesignMeasure layoutBowZ,
        DesignMeasure centerPlaneX,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0)
            return Empty with { RulerEnd = rulerEnd, LayoutBowZ = layoutBowZ };

        var ordered = requests
            .OrderBy(request => request.RulerCenter.TwiceMetres)
            .ThenBy(request => request.BarbetteId, StringComparer.Ordinal)
            .ToArray();

        var entries = new List<BarbetteRulerEntry>(ordered.Length);
        var diagnostics = new List<DesignDiagnostic>();
        foreach (var request in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            entries.Add(MeasureEntry(request, rulerEnd, layoutBowZ, centerPlaneX, cancellationToken));
        }

        // Foremost/rearmost margins are centre-to-tip measurements, deliberately not clear-edge ones.
        for (var index = 0; index < entries.Count; index++)
        {
            entries[index] = entries[index] with
            {
                BowMargin = index == 0 ? entries[index].RulerCenter : null,
                SternMargin = index == entries.Count - 1 ? rulerEnd - entries[index].RulerCenter : null,
            };
        }

        var segments = new List<BarbetteRulerSegment>(Math.Max(0, entries.Count - 1));
        for (var index = 1; index < entries.Count; index++)
        {
            var forward = entries[index - 1];
            var aft = entries[index];
            var centerDistance = aft.RulerCenter - forward.RulerCenter;
            // Exact raster separation: the forward cavity's stern edge to the aft cavity's bow edge.
            var clearGap = centerDistance - forward.ClearSternExtent - aft.ClearBowExtent;
            var state = clearGap < DesignMeasure.Zero
                ? BarbetteRulerSegmentState.Overlapping
                : clearGap < DesignMeasure.FromMetres(BarbetteDefinition.MinimumClearSeparationMetres)
                    ? BarbetteRulerSegmentState.BelowMinimumSeparation
                    : BarbetteRulerSegmentState.Valid;
            var segment = new BarbetteRulerSegment(forward.BarbetteId, aft.BarbetteId, centerDistance,
                clearGap, state);
            segments.Add(segment);
            if (state == BarbetteRulerSegmentState.Overlapping)
                diagnostics.Add(new DesignDiagnostic(BarbetteDiagnosticCodes.PlacementClearVolumeOverlap,
                    DesignSeverity.Error,
                    $"Barbettes '{forward.BarbetteId}' and '{aft.BarbetteId}' have overlapping protected clear volumes " +
                    $"({(-clearGap).Metres:0.#} m of shared cavity).",
                    forward.BarbetteId, nameof(BarbetteRulerSegment.ClearGap),
                    clearGap, DesignMeasure.Zero,
                    "Move one barbette apart; the requested position is kept until you change it."));
            else if (state == BarbetteRulerSegmentState.BelowMinimumSeparation)
                diagnostics.Add(new DesignDiagnostic(BarbetteDiagnosticCodes.MinimumClearSeparation,
                    DesignSeverity.Error,
                    $"Barbettes '{forward.BarbetteId}' and '{aft.BarbetteId}' are only {clearGap.Metres:0.#} m apart; " +
                    $"the frozen contract needs {BarbetteDefinition.MinimumClearSeparationMetres} m of structural separator.",
                    forward.BarbetteId, nameof(BarbetteRulerSegment.ClearGap),
                    clearGap, DesignMeasure.FromMetres(BarbetteDefinition.MinimumClearSeparationMetres),
                    "Move one barbette apart; the requested position is kept until you change it."));
        }

        foreach (var entry in entries)
            diagnostics.AddRange(entry.Diagnostics);

        return new BarbetteRulerModel(rulerEnd, layoutBowZ, entries, segments,
            entries.Count == 0 ? null : entries[0].BowMargin,
            entries.Count == 0 ? null : entries[^1].SternMargin,
            diagnostics);
    }

    private static BarbetteRulerEntry MeasureEntry(
        BarbetteRulerRequest request,
        DesignMeasure rulerEnd,
        DesignMeasure layoutBowZ,
        DesignMeasure centerPlaneX,
        CancellationToken cancellationToken)
    {
        var worldZ = layoutBowZ - request.RulerCenter;
        var measured = BarbetteGenerator.Measure(request.Definition, centerPlaneX, worldZ,
            cancellationToken: cancellationToken);
        var entryDiagnostics = measured.Diagnostics.ToList();

        DesignMeasure bowExtent = DesignMeasure.Zero;
        DesignMeasure sternExtent = DesignMeasure.Zero;
        if (measured.Measurement is { } measurement)
        {
            // The forward (+Z, smaller ruler s) and aft (-Z, larger ruler s) clear edges are read
            // straight from the measured raster bounds. Taking each facing edge keeps the readout
            // exact even when the conservative circle raster is not symmetric about the centre.
            bowExtent = DesignMeasure.Max(DesignMeasure.Zero, measurement.ClearBounds.MaxZ - worldZ);
            sternExtent = DesignMeasure.Max(DesignMeasure.Zero, worldZ - measurement.ClearBounds.MinZ);
        }

        var clearBowEdge = request.RulerCenter - bowExtent;
        var clearSternEdge = request.RulerCenter + sternExtent;
        var blocking = entryDiagnostics.Any(diagnostic => diagnostic.IsError);
        if (clearBowEdge < DesignMeasure.Zero || clearSternEdge > rulerEnd)
        {
            blocking = true;
            entryDiagnostics.Add(new DesignDiagnostic(BarbetteDiagnosticCodes.ClearVolumeOutsideRuler,
                DesignSeverity.Error,
                $"Barbette '{request.BarbetteId}' protected clear volume spans ruler " +
                $"{clearBowEdge.Metres:0.#}..{clearSternEdge.Metres:0.#} m, outside the 0..{rulerEnd.Metres:0.#} m ship.",
                request.BarbetteId, nameof(BarbetteRulerEntry.RulerCenter),
                request.RulerCenter, rulerEnd,
                "Move the barbette inboard; the requested position is kept until you change it."));
        }

        return new BarbetteRulerEntry(
            request.BarbetteId,
            request.NodeId,
            request.RulerCenter,
            worldZ,
            bowExtent,
            sternExtent,
            clearBowEdge,
            clearSternEdge,
            null,
            null,
            blocking,
            entryDiagnostics);
    }
}
