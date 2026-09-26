using FtdHullGenerator.Domain.Components;
using FtdHullGenerator.Domain.Design;

namespace FtdHullGenerator.UI.Components;

/// <summary>Conservative authoring policy only; persisted advanced intent and the plane engine stay intact.</summary>
internal static class SimpleInternalLayout
{
    // Each family gets its own bounded share of the document's 256-plane budget.
    public const int PlaneLimit = 64;

    public static int[] GapChoices(InternalPlaneFamily family, bool singleBlockCenterline) =>
        Enumerable.Range(2, 39).Where(gap => family != InternalPlaneFamily.LongitudinalBulkhead ||
            (gap % 2 != 0) == singleBlockCenterline).ToArray();

    public static InternalStructureFamily Create(InternalStructureFamily source, int gap) => source with
    {
        Thickness = 1,
        Spacing = DesignMeasure.FromMetres(gap),
        SpacingKind = InternalSpacingKind.ClearCompartmentGap,
        Count = PlaneLimit,
        CountMode = InternalPlaneCountMode.RepeatToBoundary,
        Datum = source.Family == InternalPlaneFamily.LongitudinalBulkhead
            ? InternalPlaneDatum.CenterPlane : InternalPlaneDatum.MinimumHullExtent,
        Direction = source.Family == InternalPlaneFamily.LongitudinalBulkhead
            ? InternalRepeatDirection.Both : InternalRepeatDirection.Positive,
        Offset = source.Family == InternalPlaneFamily.LongitudinalBulkhead
            ? DesignMeasure.Zero : DesignMeasure.FromMetres(gap + 1),
        MirrorSymmetry = true,
        IncludeCentralPlane = false,
        RepetitionExtent = null,
    };
}
