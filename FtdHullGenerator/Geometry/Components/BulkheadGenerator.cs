using System.Collections.Immutable;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Components;
using FtdHullGenerator.Domain.Composition;
using FtdHullGenerator.Domain.Design;
using FtdHullGenerator.Geometry.Composition;

namespace FtdHullGenerator.Geometry.Components;

/// <summary>A named set of cells that internal structure must never occupy.</summary>
public sealed record RequiredVoidExclusion(string Id, ImmutableArray<HullCell> Cells);

/// <summary>Bounded first-pass generation policy. These are Hull Forge limits, not game limits.</summary>
public sealed record BulkheadGenerationOptions
{
    public static BulkheadGenerationOptions Default { get; } = new();

    public int MaxPlaneCount { get; init; } = DesignLimits.MaxInternalPlanesPerDocument;
    public int MaxCandidateCells { get; init; } = 5_000_000;
    public int MaxRequiredVoidCells { get; init; } = 2_000_000;
    /// <summary>
    /// Development/test seam for inspecting raw intent. Production composition must keep this
    /// enabled; a false value does not establish physical support and must never authorize export.
    /// </summary>
    public bool RequireVerifiedSupport { get; init; } = true;
}

/// <summary>One plane's contribution to an eventual physical cell.</summary>
public sealed record InternalCellContribution(
    string OwnerId,
    InternalPlaneFamily Family,
    MaterialKind Material);

/// <summary>
/// One deduplicated internal physical cell. <see cref="Contributions"/> retains every crossing
/// owner while <see cref="Material"/> follows the document's explicit internal-only priority.
/// </summary>
public sealed record InternalStructureCellIntent(
    HullCell Cell,
    MaterialKind Material,
    string WinningOwnerId,
    InternalPlaneFamily WinningFamily,
    ImmutableArray<InternalCellContribution> Contributions);

/// <summary>Stable, inspectable realization metadata for one requested plane.</summary>
public sealed record InternalPlaneRealization(
    string OwnerId,
    InternalPlaneFamily Family,
    MaterialKind Material,
    DesignMeasure RequestedCenter,
    int CandidateCellCount,
    int RealizedCellCount,
    int RequiredVoidCellCount,
    int CavityClippedCellCount,
    DesignBounds? RealizedBounds);

/// <summary>
/// Inspectable B01 output consumed by the later composition stage. Invalid results may retain cell
/// intent for diagnostics, but callers must require <see cref="IsValid"/> before materialization.
/// Budget/configuration failures return no partial intent.
/// </summary>
public sealed record InternalStructureGenerationResult(
    ImmutableArray<InternalStructureCellIntent> Cells,
    ImmutableArray<InternalPlaneRealization> Planes,
    ImmutableArray<DesignDiagnostic> Diagnostics,
    long EstimatedCandidateCells)
{
    public bool IsValid => !Diagnostics.Any(diagnostic => diagnostic.IsError);
}

/// <summary>
/// Deterministically plans longitudinal bulkheads, internal decks and transverse bulkheads against
/// the actual feature-free hull context. It emits cell intent only: B01 does not mutate armor,
/// pack beams, select catalog entries or claim that a non-cubic adjacency is a physical join.
/// </summary>
public static class BulkheadGenerator
{
    private enum Axis
    {
        X,
        Y,
        Z,
    }

    private sealed record PlanePlan(
        string OwnerId,
        InternalStructureFamily Settings,
        DesignMeasure Center,
        ImmutableArray<int> AxisCells);

    private sealed class MutableCell
    {
        public List<InternalCellContribution> Contributions { get; } = [];
    }

    private sealed class MutablePlane
    {
        public required PlanePlan Plan { get; init; }
        public int CandidateCount { get; set; }
        public int RequiredVoidCount { get; set; }
        public int CavityClippedCount { get; set; }
        public HashSet<HullCell> Cells { get; } = [];
    }

    private static readonly (int X, int Y, int Z)[] FaceDirections =
    [
        (-1, 0, 0), (1, 0, 0), (0, -1, 0),
        (0, 1, 0), (0, 0, -1), (0, 0, 1),
    ];

    public static InternalStructureGenerationResult Generate(
        HullBuildContext context,
        InternalStructure settings,
        IEnumerable<RequiredVoidExclusion>? requiredVoids = null,
        BulkheadGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(settings);
        options ??= BulkheadGenerationOptions.Default;
        cancellationToken.ThrowIfCancellationRequested();

        var diagnostics = new List<DesignDiagnostic>();
        diagnostics.AddRange(context.Diagnostics);
        diagnostics.AddRange(settings.Validate());
        ValidateOptions(options, diagnostics);
        var voidCells = BuildRequiredVoidMask(requiredVoids, options, diagnostics, cancellationToken);
        if (diagnostics.HasErrors())
            return Empty(diagnostics);

        var plans = BuildPlanePlans(context, settings, options, diagnostics, cancellationToken);
        if (diagnostics.HasErrors())
            return Empty(diagnostics);

        var estimate = EstimateCandidates(context, plans);
        if (estimate > options.MaxCandidateCells)
        {
            diagnostics.Add(new DesignDiagnostic(InternalStructureDiagnosticCodes.CandidateBudgetExceeded,
                DesignSeverity.Error,
                $"The internal planes may inspect {estimate:N0} cells; the configured cap is " +
                $"{options.MaxCandidateCells:N0}.",
                Field: nameof(BulkheadGenerationOptions.MaxCandidateCells),
                SuggestedCorrection: "Reduce plane count, thickness or repetition extent."));
            return Empty(diagnostics, estimate);
        }

        var cells = new Dictionary<HullCell, MutableCell>();
        var realized = new List<MutablePlane>(plans.Count);
        foreach (var plan in plans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var plane = new MutablePlane { Plan = plan };
            Rasterize(context, plane, voidCells, cells, cancellationToken);
            realized.Add(plane);
            if (plane.Cells.Count == 0 && plan.Settings.CountMode == InternalPlaneCountMode.FixedCount)
                diagnostics.Add(new DesignDiagnostic(InternalStructureDiagnosticCodes.FixedCountDoesNotFit,
                    DesignSeverity.Error,
                    $"The requested {plan.Settings.Family} plane at {plan.Center} has no usable cavity cells.",
                    NodeId: plan.OwnerId,
                    Field: nameof(InternalStructureFamily.Offset),
                    Requested: plan.Center,
                    SuggestedCorrection: "Move the plane inside the usable cavity or use repeat-to-boundary mode."));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var priority = settings.JunctionPriority
            .Select((family, rank) => (family, rank))
            .ToDictionary(pair => pair.family, pair => pair.rank);
        var finalCells = cells.Select(pair => ResolveCell(pair.Key, pair.Value, priority))
            .OrderBy(intent => intent.Cell.Z)
            .ThenBy(intent => intent.Cell.Y)
            .ThenBy(intent => intent.Cell.X)
            .ToImmutableArray();

        if (options.RequireVerifiedSupport)
            AddSupportDiagnostics(context, finalCells, diagnostics, cancellationToken);

        var planeResults = realized.Select(plane => new InternalPlaneRealization(
                plane.Plan.OwnerId,
                plane.Plan.Settings.Family,
                plane.Plan.Settings.Material,
                plane.Plan.Center,
                plane.CandidateCount,
                plane.Cells.Count,
                plane.RequiredVoidCount,
                plane.CavityClippedCount,
                BoundsOf(plane.Cells)))
            .ToImmutableArray();

        cancellationToken.ThrowIfCancellationRequested();
        return new InternalStructureGenerationResult(finalCells, planeResults,
            diagnostics.ToImmutableArray(), estimate);
    }

    private static void ValidateOptions(BulkheadGenerationOptions options, List<DesignDiagnostic> diagnostics)
    {
        if (options.MaxPlaneCount is < 1 or > DesignLimits.MaxInternalPlanesPerDocument)
            diagnostics.Add(DesignDiagnostic.Error(InternalStructureDiagnosticCodes.PlaneBudgetExceeded,
                $"The plane cap must be between 1 and {DesignLimits.MaxInternalPlanesPerDocument}.",
                field: nameof(BulkheadGenerationOptions.MaxPlaneCount)));
        if (options.MaxCandidateCells < 1)
            diagnostics.Add(DesignDiagnostic.Error(InternalStructureDiagnosticCodes.CandidateBudgetExceeded,
                "The candidate-cell cap must be positive.", field: nameof(BulkheadGenerationOptions.MaxCandidateCells)));
        if (options.MaxRequiredVoidCells < 0)
            diagnostics.Add(DesignDiagnostic.Error(InternalStructureDiagnosticCodes.RequiredVoidInvalid,
                "The required-void cell cap cannot be negative.",
                field: nameof(BulkheadGenerationOptions.MaxRequiredVoidCells)));
    }

    private static HashSet<HullCell> BuildRequiredVoidMask(
        IEnumerable<RequiredVoidExclusion>? requiredVoids,
        BulkheadGenerationOptions options,
        List<DesignDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var cells = new HashSet<HullCell>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        long visitedCellCount = 0;
        foreach (var exclusion in requiredVoids ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (exclusion is null || string.IsNullOrWhiteSpace(exclusion.Id) ||
                exclusion.Id.Length > DesignLimits.MaxIdentifierLength || !ids.Add(exclusion.Id))
            {
                diagnostics.Add(DesignDiagnostic.Error(InternalStructureDiagnosticCodes.RequiredVoidInvalid,
                    "Every required void needs a distinct bounded identifier.",
                    exclusion?.Id, nameof(RequiredVoidExclusion.Id)));
                continue;
            }

            if (exclusion.Cells.IsDefault)
            {
                diagnostics.Add(DesignDiagnostic.Error(InternalStructureDiagnosticCodes.RequiredVoidInvalid,
                    $"Required void '{exclusion.Id}' has an uninitialized cell set.", exclusion.Id,
                    nameof(RequiredVoidExclusion.Cells)));
                continue;
            }

            foreach (var cell in exclusion.Cells)
            {
                if ((visitedCellCount++ & 0x3ff) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                cells.Add(cell);
                if (cells.Count > options.MaxRequiredVoidCells)
                {
                    diagnostics.Add(new DesignDiagnostic(InternalStructureDiagnosticCodes.RequiredVoidInvalid,
                        DesignSeverity.Error,
                        $"Required voids contain more than {options.MaxRequiredVoidCells:N0} distinct cells.",
                        Field: nameof(BulkheadGenerationOptions.MaxRequiredVoidCells),
                        SuggestedCorrection: "Reduce well/exclusion extents before generation."));
                    return cells;
                }
            }
        }

        return cells;
    }

    private static List<PlanePlan> BuildPlanePlans(
        HullBuildContext context,
        InternalStructure settings,
        BulkheadGenerationOptions options,
        List<DesignDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var plans = new List<PlanePlan>();
        foreach (var familyKind in Enum.GetValues<InternalPlaneFamily>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var family = settings.Find(familyKind)!;
            if (!family.Enabled)
                continue;

            var centers = BuildCenters(context, family, diagnostics);
            var familyPlans = new List<PlanePlan>();
            var ordinal = 0;
            foreach (var center in centers.OrderBy(value => value.TwiceMetres))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (((center.TwiceMetres - (family.Thickness - 1)) & 1) != 0)
                {
                    diagnostics.Add(new DesignDiagnostic(InternalStructureDiagnosticCodes.CenterSlabParity,
                        DesignSeverity.Error,
                        $"A {family.Thickness}-cell {family.Family} slab cannot be centred at {center} on integer cells.",
                        Field: nameof(InternalStructureFamily.Offset),
                        Requested: center,
                        SuggestedCorrection: "Shift the centre by 0.5 m or choose thickness with compatible parity."));
                    continue;
                }
                if (!TryGetAxisCells(context, family, center, out var axisCells))
                {
                    if (family.CountMode == InternalPlaneCountMode.FixedCount)
                        diagnostics.Add(new DesignDiagnostic(InternalStructureDiagnosticCodes.FixedCountDoesNotFit,
                            DesignSeverity.Error,
                            $"A fixed {family.Family} slab centred at {center} does not fit its base-hull interval.",
                            Field: nameof(InternalStructureFamily.Offset),
                            Requested: center,
                            SuggestedCorrection: "Adjust offset/count or use repeat-to-boundary mode."));
                    continue;
                }

                familyPlans.Add(new PlanePlan(OwnerId(family.Family, ++ordinal), family, center, axisCells));
            }

            for (var index = 1; index < familyPlans.Count; index++)
            {
                if (familyPlans[index - 1].AxisCells.Intersect(familyPlans[index].AxisCells).Any())
                    diagnostics.Add(new DesignDiagnostic(InternalStructureDiagnosticCodes.PlaneOverlap,
                        DesignSeverity.Error,
                        $"Two {family.Family} slabs overlap on their repetition axis.",
                        NodeId: familyPlans[index].OwnerId,
                        Field: nameof(InternalStructureFamily.Spacing),
                        Requested: family.EffectivePitch,
                        Realized: DesignMeasure.FromMetres(family.Thickness),
                        SuggestedCorrection: "Increase pitch/clear gap or reduce slab thickness."));
            }

            plans.AddRange(familyPlans);
            if (plans.Count > options.MaxPlaneCount)
            {
                diagnostics.Add(new DesignDiagnostic(InternalStructureDiagnosticCodes.PlaneBudgetExceeded,
                    DesignSeverity.Error,
                    $"The realized plane count exceeds the configured cap of {options.MaxPlaneCount}.",
                    Field: nameof(BulkheadGenerationOptions.MaxPlaneCount),
                    SuggestedCorrection: "Reduce counts or narrow repeat-to-boundary extents."));
                return plans;
            }
        }

        return plans;
    }

    private static IReadOnlyList<DesignMeasure> BuildCenters(
        HullBuildContext context,
        InternalStructureFamily family,
        List<DesignDiagnostic> diagnostics)
    {
        var axis = AxisFor(family.Family);
        var axisMin = DesignMeasure.FromCellAnchor(MinFor(context, axis));
        var axisMax = DesignMeasure.FromCellAnchor(MaxFor(context, axis));
        var requestedExtent = family.RepetitionExtent ?? new DesignSpan(axisMin, axisMax);
        var extentStart = DesignMeasure.Max(axisMin, requestedExtent.Start);
        var extentEnd = DesignMeasure.Min(axisMax, requestedExtent.End);
        if (extentEnd < extentStart)
        {
            diagnostics.Add(new DesignDiagnostic(InternalStructureDiagnosticCodes.ExtentInvalid,
                DesignSeverity.Error,
                $"The {family.Family} repetition extent does not intersect the base hull.",
                Field: nameof(InternalStructureFamily.RepetitionExtent),
                AffectedBounds: AxisBounds(axis, requestedExtent.Start, requestedExtent.End)));
            return [];
        }

        var extent = new DesignSpan(extentStart, extentEnd);
        var datum = ResolveDatum(context, family, axis);
        var origin = datum + family.Offset;
        var pitch = family.EffectivePitch;
        var centers = family.Family == InternalPlaneFamily.LongitudinalBulkhead
            ? BuildLongitudinalCenters(context, family, origin, pitch, extent, diagnostics)
            : BuildLinearCenters(family, origin, pitch, extent, diagnostics);

        return centers.Distinct().OrderBy(center => center.TwiceMetres).ToArray();
    }

    private static IReadOnlyList<DesignMeasure> BuildLongitudinalCenters(
        HullBuildContext context,
        InternalStructureFamily family,
        DesignMeasure origin,
        DesignMeasure pitch,
        DesignSpan extent,
        List<DesignDiagnostic> diagnostics)
    {
        // The datum is the actual centre plane; offset is an exact symmetric displacement of the
        // nearest pair. A zero offset means the natural pitch (with a centre slab) or half-pitch
        // (without one), so the two nearest slabs remain one pitch apart.
        var center = context.CenterPlaneX;
        if (origin != center + family.Offset)
            throw new InvalidOperationException("The longitudinal datum must resolve to the centre plane.");

        if (family.IncludeCentralPlane && !context.Lattice.IsThicknessRepresentable(family.Thickness))
        {
            var suggestion = context.Lattice.SuggestCompatibleThickness(family.Thickness);
            diagnostics.Add(new DesignDiagnostic(InternalStructureDiagnosticCodes.CenterSlabParity,
                DesignSeverity.Error,
                $"A {family.Thickness}-cell central wall is not representable on the " +
                $"{context.Lattice.Width}-cell lateral lattice.",
                Field: nameof(InternalStructureFamily.Thickness),
                Requested: DesignMeasure.FromMetres(family.Thickness),
                SuggestedCorrection: $"Use thickness {suggestion} or omit the central wall."));
            return [];
        }

        var centers = new List<DesignMeasure>();
        if (family.IncludeCentralPlane && extent.Contains(center))
            centers.Add(center);
        else if (family.IncludeCentralPlane && family.CountMode == InternalPlaneCountMode.FixedCount)
            diagnostics.Add(new DesignDiagnostic(InternalStructureDiagnosticCodes.FixedCountDoesNotFit,
                DesignSeverity.Error,
                "The requested central longitudinal wall lies outside the enabled extent.",
                Field: nameof(InternalStructureFamily.RepetitionExtent),
                Requested: center,
                SuggestedCorrection: "Widen the repetition extent to include the real centre plane or omit the central wall."));

        var pairBudget = family.CountMode == InternalPlaneCountMode.FixedCount
            ? family.Count / 2
            : Math.Max(0, (family.Count - centers.Count) / 2);
        if (pairBudget == 0)
            return centers;

        DesignMeasure firstDistance;
        if (family.Offset != DesignMeasure.Zero)
            firstDistance = family.Offset.Magnitude;
        else if (family.IncludeCentralPlane)
            firstDistance = pitch;
        else
            firstDistance = pitch.TryHalve() ?? DesignMeasure.Zero;

        if (firstDistance <= DesignMeasure.Zero)
        {
            diagnostics.Add(DesignDiagnostic.Error(DesignDiagnosticCodes.InternalStructureInvalidSpacing,
                "Mirrored longitudinal pairs need a positive, half-metre-representable distance from centre.",
                field: nameof(InternalStructureFamily.Spacing)));
            return centers;
        }

        for (var index = 0; index < pairBudget; index++)
        {
            var distance = firstDistance + pitch * index;
            var lower = center - distance;
            var upper = center + distance;
            if (!extent.Contains(lower) || !extent.Contains(upper))
            {
                if (family.CountMode == InternalPlaneCountMode.FixedCount)
                {
                    // The normal fit diagnostic is emitted by TryGetAxisCells when a centre is in
                    // extent but its slab is out; this handles centres outside the explicit extent.
                    diagnostics.Add(new DesignDiagnostic(InternalStructureDiagnosticCodes.FixedCountDoesNotFit,
                        DesignSeverity.Error,
                        "A fixed mirrored longitudinal pair lies outside the enabled extent.",
                        Field: nameof(InternalStructureFamily.Count),
                        Requested: distance,
                        SuggestedCorrection: "Reduce count/offset or widen the repetition extent."));
                }
                break;
            }

            centers.Add(lower);
            centers.Add(upper);
        }

        return centers;
    }

    private static IReadOnlyList<DesignMeasure> BuildLinearCenters(
        InternalStructureFamily family,
        DesignMeasure origin,
        DesignMeasure pitch,
        DesignSpan extent,
        List<DesignDiagnostic> diagnostics)
    {
        var centers = new List<DesignMeasure>();
        var limit = family.Count;
        switch (family.EffectiveDirection)
        {
            case InternalRepeatDirection.Positive:
                for (var index = 0; index < limit; index++)
                {
                    var center = origin + pitch * index;
                    if (!extent.Contains(center))
                    {
                        if (family.CountMode == InternalPlaneCountMode.RepeatToBoundary)
                            break;
                        AddExtentDiagnostic(center);
                        continue;
                    }
                    centers.Add(center);
                }
                break;

            case InternalRepeatDirection.Negative:
                for (var index = 0; index < limit; index++)
                {
                    var center = origin - pitch * index;
                    if (!extent.Contains(center))
                    {
                        if (family.CountMode == InternalPlaneCountMode.RepeatToBoundary)
                            break;
                        AddExtentDiagnostic(center);
                        continue;
                    }
                    centers.Add(center);
                }
                break;

            case InternalRepeatDirection.Both:
                if ((limit & 1) != 0)
                {
                    if (extent.Contains(origin))
                        centers.Add(origin);
                    else if (family.CountMode == InternalPlaneCountMode.FixedCount)
                        AddExtentDiagnostic(origin);
                }
                var pairs = limit / 2;
                var first = (limit & 1) != 0 ? pitch : pitch.TryHalve() ?? DesignMeasure.Zero;
                if (pairs > 0 && first <= DesignMeasure.Zero)
                {
                    diagnostics.Add(DesignDiagnostic.Error(DesignDiagnosticCodes.InternalStructureInvalidSpacing,
                        "An even bidirectional count needs a positive, half-metre-representable half pitch.",
                        field: nameof(InternalStructureFamily.Spacing)));
                    break;
                }
                for (var index = 0; index < pairs; index++)
                {
                    var distance = first + pitch * index;
                    var lower = origin - distance;
                    var upper = origin + distance;
                    if ((!extent.Contains(lower) || !extent.Contains(upper)) &&
                        family.CountMode == InternalPlaneCountMode.RepeatToBoundary)
                        break;
                    if (extent.Contains(lower) && extent.Contains(upper))
                    {
                        centers.Add(lower);
                        centers.Add(upper);
                    }
                    else
                    {
                        AddExtentDiagnostic(!extent.Contains(lower) ? lower : upper);
                        break;
                    }
                }
                break;
        }

        return centers;

        void AddExtentDiagnostic(DesignMeasure center) => diagnostics.Add(new DesignDiagnostic(
            InternalStructureDiagnosticCodes.FixedCountDoesNotFit,
            DesignSeverity.Error,
            $"A fixed {family.Family} plane at {center} lies outside the enabled extent.",
            Field: nameof(InternalStructureFamily.Count),
            Requested: center,
            SuggestedCorrection: "Reduce count/offset or widen the repetition extent."));
    }

    private static DesignMeasure ResolveDatum(
        HullBuildContext context,
        InternalStructureFamily family,
        Axis axis) => family.EffectiveDatum switch
        {
            InternalPlaneDatum.HullOrigin => DesignMeasure.Zero,
            InternalPlaneDatum.CenterPlane when family.Family == InternalPlaneFamily.LongitudinalBulkhead =>
                context.CenterPlaneX,
            InternalPlaneDatum.MinimumHullExtent => DesignMeasure.FromCellAnchor(MinFor(context, axis)),
            InternalPlaneDatum.MaximumHullExtent => DesignMeasure.FromCellAnchor(MaxFor(context, axis)),
            _ => DesignMeasure.Zero, // Invalid combinations were rejected by settings.Validate().
        };

    private static bool TryGetAxisCells(
        HullBuildContext context,
        InternalStructureFamily family,
        DesignMeasure center,
        out ImmutableArray<int> cells)
    {
        var axis = AxisFor(family.Family);
        var numerator = center.TwiceMetres - (family.Thickness - 1);
        if ((numerator & 1) != 0)
        {
            cells = [];
            return false;
        }

        var first = numerator / 2;
        var last = checked(first + family.Thickness - 1);
        if (first < MinFor(context, axis) || last > MaxFor(context, axis))
        {
            cells = [];
            return false;
        }

        cells = Enumerable.Range(first, family.Thickness).ToImmutableArray();
        return true;
    }

    private static long EstimateCandidates(HullBuildContext context, IReadOnlyList<PlanePlan> plans)
    {
        long estimate = 0;
        var width = (long)context.MaxX - context.MinX + 1;
        var height = (long)context.MaxY - context.MinY + 1;
        var length = (long)context.MaxZ - context.MinZ + 1;
        foreach (var plan in plans)
        {
            var slab = plan.AxisCells.Length;
            estimate = checked(estimate + plan.Settings.Family switch
            {
                InternalPlaneFamily.LongitudinalBulkhead => slab * height * length,
                InternalPlaneFamily.InternalDeck => slab * width * length,
                InternalPlaneFamily.TransverseBulkhead => slab * width * height,
                _ => 0,
            });
        }
        return estimate;
    }

    private static void Rasterize(
        HullBuildContext context,
        MutablePlane plane,
        HashSet<HullCell> requiredVoids,
        Dictionary<HullCell, MutableCell> cells,
        CancellationToken cancellationToken)
    {
        switch (plane.Plan.Settings.Family)
        {
            case InternalPlaneFamily.LongitudinalBulkhead:
                foreach (var x in plane.Plan.AxisCells)
                    for (var z = context.MinZ; z <= context.MaxZ; z++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var floor = context.FloorYAt(z);
                        var deck = context.DeckYAt(z);
                        if (floor == int.MinValue || deck == int.MinValue)
                            continue;
                        for (var y = floor; y <= deck; y++)
                            TryAdd(new HullCell(x, y, z));
                    }
                break;

            case InternalPlaneFamily.InternalDeck:
                foreach (var y in plane.Plan.AxisCells)
                    for (var z = context.MinZ; z <= context.MaxZ; z++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        for (var x = context.MinX; x <= context.MaxX; x++)
                            TryAdd(new HullCell(x, y, z));
                    }
                break;

            case InternalPlaneFamily.TransverseBulkhead:
                foreach (var z in plane.Plan.AxisCells)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var floor = context.FloorYAt(z);
                    var deck = context.DeckYAt(z);
                    if (floor == int.MinValue || deck == int.MinValue)
                        continue;
                    for (var y = floor; y <= deck; y++)
                        for (var x = context.MinX; x <= context.MaxX; x++)
                            TryAdd(new HullCell(x, y, z));
                }
                break;
        }

        return;

        void TryAdd(HullCell cell)
        {
            plane.CandidateCount++;
            if (!context.IsUsableCavity(cell.X, cell.Y, cell.Z))
            {
                plane.CavityClippedCount++;
                return;
            }
            if (requiredVoids.Contains(cell))
            {
                plane.RequiredVoidCount++;
                return;
            }

            plane.Cells.Add(cell);
            if (!cells.TryGetValue(cell, out var aggregate))
            {
                aggregate = new MutableCell();
                cells.Add(cell, aggregate);
            }
            aggregate.Contributions.Add(new InternalCellContribution(plane.Plan.OwnerId,
                plane.Plan.Settings.Family, plane.Plan.Settings.Material));
        }
    }

    private static InternalStructureCellIntent ResolveCell(
        HullCell cell,
        MutableCell mutable,
        IReadOnlyDictionary<InternalPlaneFamily, int> priority)
    {
        var contributions = mutable.Contributions
            .OrderBy(contribution => priority[contribution.Family])
            .ThenBy(contribution => contribution.OwnerId, StringComparer.Ordinal)
            .ToImmutableArray();
        var winner = contributions[0];
        return new InternalStructureCellIntent(cell, winner.Material, winner.OwnerId, winner.Family,
            contributions);
    }

    private static void AddSupportDiagnostics(
        HullBuildContext context,
        ImmutableArray<InternalStructureCellIntent> cells,
        List<DesignDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var byCell = cells.ToDictionary(intent => intent.Cell);
        var remaining = new HashSet<HullCell>(byCell.Keys);
        var queue = new Queue<HullCell>();
        // Cells already have stable Z/Y/X order. Walking that array for component seeds avoids an
        // O(component-count * remaining-cells log remaining-cells) repeated sort on large ships.
        foreach (var intent in cells)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = intent.Cell;
            if (!remaining.Remove(start))
                continue;
            queue.Enqueue(start);
            var component = new List<HullCell>();
            var hasVerifiedHullContact = false;
            while (queue.Count > 0)
            {
                var cell = queue.Dequeue();
                component.Add(cell);
                foreach (var direction in FaceDirections)
                {
                    var neighbor = new HullCell(cell.X + direction.X, cell.Y + direction.Y, cell.Z + direction.Z);
                    if (remaining.Remove(neighbor))
                        queue.Enqueue(neighbor);
                    if (context.TryGetArmor(neighbor.X, neighbor.Y, neighbor.Z, out var armor) &&
                        armor.IsStructuralArmor && armor.Construction == ArmorConstruction.Solid)
                        hasVerifiedHullContact = true;
                }
            }

            if (hasVerifiedHullContact)
                continue;

            var owners = component.SelectMany(cell => byCell[cell].Contributions)
                .Select(contribution => contribution.OwnerId)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
            diagnostics.Add(new DesignDiagnostic(InternalStructureDiagnosticCodes.UnsupportedRegion,
                DesignSeverity.Error,
                "An internal region has no verified full-face contact to solid hull structure. " +
                "Adjacency to pole or beam-slope armor is not treated as physical proof.",
                NodeId: owners.FirstOrDefault(),
                SuggestedCorrection: "Move or extend a plane to verified solid armor, or provide analytic native-contact evidence during integration.",
                AffectedBounds: BoundsOf(component)));
        }
    }

    private static DesignBounds? BoundsOf(IEnumerable<HullCell> source)
    {
        using var enumerator = source.GetEnumerator();
        if (!enumerator.MoveNext())
            return null;
        var first = enumerator.Current;
        var minX = first.X;
        var maxX = first.X;
        var minY = first.Y;
        var maxY = first.Y;
        var minZ = first.Z;
        var maxZ = first.Z;
        while (enumerator.MoveNext())
        {
            var cell = enumerator.Current;
            minX = Math.Min(minX, cell.X);
            maxX = Math.Max(maxX, cell.X);
            minY = Math.Min(minY, cell.Y);
            maxY = Math.Max(maxY, cell.Y);
            minZ = Math.Min(minZ, cell.Z);
            maxZ = Math.Max(maxZ, cell.Z);
        }
        return new DesignBounds(DesignMeasure.FromCellAnchor(minX), DesignMeasure.FromCellAnchor(maxX),
            DesignMeasure.FromCellAnchor(minY), DesignMeasure.FromCellAnchor(maxY),
            DesignMeasure.FromCellAnchor(minZ), DesignMeasure.FromCellAnchor(maxZ));
    }

    private static DesignBounds AxisBounds(Axis axis, DesignMeasure start, DesignMeasure end) => axis switch
    {
        Axis.X => new DesignBounds(start, end, DesignMeasure.Zero, DesignMeasure.Zero,
            DesignMeasure.Zero, DesignMeasure.Zero),
        Axis.Y => new DesignBounds(DesignMeasure.Zero, DesignMeasure.Zero, start, end,
            DesignMeasure.Zero, DesignMeasure.Zero),
        Axis.Z => new DesignBounds(DesignMeasure.Zero, DesignMeasure.Zero,
            DesignMeasure.Zero, DesignMeasure.Zero, start, end),
        _ => throw new ArgumentOutOfRangeException(nameof(axis)),
    };

    private static Axis AxisFor(InternalPlaneFamily family) => family switch
    {
        InternalPlaneFamily.LongitudinalBulkhead => Axis.X,
        InternalPlaneFamily.InternalDeck => Axis.Y,
        InternalPlaneFamily.TransverseBulkhead => Axis.Z,
        _ => throw new ArgumentOutOfRangeException(nameof(family)),
    };

    private static int MinFor(HullBuildContext context, Axis axis) => axis switch
    {
        Axis.X => context.MinX,
        Axis.Y => context.MinY,
        Axis.Z => context.MinZ,
        _ => throw new ArgumentOutOfRangeException(nameof(axis)),
    };

    private static int MaxFor(HullBuildContext context, Axis axis) => axis switch
    {
        Axis.X => context.MaxX,
        Axis.Y => context.MaxY,
        Axis.Z => context.MaxZ,
        _ => throw new ArgumentOutOfRangeException(nameof(axis)),
    };

    private static string OwnerId(InternalPlaneFamily family, int ordinal) =>
        $"internal:{family switch
        {
            InternalPlaneFamily.LongitudinalBulkhead => "longitudinal",
            InternalPlaneFamily.InternalDeck => "deck",
            InternalPlaneFamily.TransverseBulkhead => "transverse",
            _ => "unknown",
        }}:{ordinal:D3}";

    private static InternalStructureGenerationResult Empty(
        IEnumerable<DesignDiagnostic> diagnostics,
        long estimate = 0) => new([], [], diagnostics.ToImmutableArray(), estimate);
}
