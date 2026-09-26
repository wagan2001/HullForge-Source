using FtdHullGenerator.Domain.Components;
using FtdHullGenerator.Domain.Design;

namespace FtdHullGenerator.Geometry.Components;

/// <summary>
/// The checked planar preflight for one barbette: the bore scan square, the side-armor expansion
/// bound, the neck clear square and the neck armor exterior square, plus the direct Chebyshev-ring
/// enumeration the neck planner must use instead of scanning a square's interior.
/// </summary>
/// <remarks>
/// <para>
/// HF-06 integration (APPLIED on the Team-A-integrated tip). <c>BarbetteGenerator.BuildPlan</c>
/// calls <see cref="Preflight(BarbetteDefinition, BarbetteGenerationLimits?, CancellationToken)"/>
/// before it allocates any planar mask: on an invalid result it copies the diagnostics and returns
/// <c>null</c>, otherwise it reads <see cref="BarbettePlanPreflightResult.ScanRadius"/>. That call
/// sits BEFORE <c>var bore = new HashSet&lt;BarbettePlanCell&gt;()</c>, so no mask is allocated for
/// an over-budget or overflowing request.
/// </para>
/// <para>
/// <c>BarbetteGenerator.BuildNeckPlan</c> takes a <see cref="CancellationToken"/> and walks each
/// neck armor layer with <see cref="EnumerateSquarePerimeter"/> (or <see cref="SquarePerimeter"/>)
/// instead of scanning the square interior, pre-sizing the ring list with the checked capacity
/// <c>checked(radius * 8)</c>. The token is threaded from <c>BuildPlan</c> through the call site and
/// through the neck build/validation loops.
/// </para>
/// <para>
/// <c>EstimateIntentCount</c> remains the final 3D output guard. No budget may be raised to mask a
/// preflight failure. The definition overload attributes a failure to
/// <see cref="BarbetteDefinition.NodeId"/>; the older in-generator budget diagnostic passed
/// <see cref="BarbetteDefinition.Id"/> in that slot, so a rejected barbette's diagnostic node is now
/// its document node rather than its component id.
/// </para>
/// </remarks>
public sealed record BarbettePlanPreflightResult(
    bool IsValid,
    int ScanRadius,
    long BoreScanCells,
    long SideArmorBoundCells,
    long NeckClearCells,
    long NeckArmorRingCells,
    long NeckExteriorCells,
    long EstimatedPlanCells,
    IReadOnlyList<DesignDiagnostic> Diagnostics);

/// <summary>
/// Fail-closed planar preflight and direct square-perimeter enumeration for the barbette planner.
/// Every size is derived with <c>checked</c> arithmetic and compared against an explicit cell
/// budget; an overflow or an over-budget structure returns a diagnostic result instead of
/// allocating, and the helper never raises the caller's budget.
/// </summary>
public static class BarbettePlanPreflight
{
    /// <summary>
    /// Preflights the bore scan, side-armor expansion, neck clear square and neck armor exterior
    /// square from explicit integer geometry and an explicit cell budget. Returns a diagnostic
    /// result (never throws for bad arithmetic) when any structure overflows the supported integer
    /// range or the combined estimate exceeds <paramref name="maxPlanCells"/>.
    /// </summary>
    public static BarbettePlanPreflightResult Preflight(
        int clearDiameterTwice,
        int sideArmorThicknessMetres,
        int neckClearSizeMetres,
        int neckArmorThicknessMetres,
        int maxPlanCells,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (maxPlanCells < 1)
            return Failure(
                BarbetteDiagnosticCodes.InvalidBudget,
                $"The barbette planar mask budget must be positive; got {maxPlanCells:N0}.",
                field: nameof(BarbetteGenerationLimits.MaxPlanCells),
                suggestedCorrection: "Pass a positive explicit planar mask budget.");

        if (clearDiameterTwice <= 0 || sideArmorThicknessMetres < 0 ||
            neckClearSizeMetres <= 0 || neckArmorThicknessMetres < 0)
            return Failure(
                BarbetteDiagnosticCodes.DegenerateMask,
                "The barbette planar preflight requires a positive clear diameter and neck size with " +
                "non-negative armor thicknesses.",
                field: nameof(BarbetteDefinition.ClearDiameter),
                suggestedCorrection: "Supply a positive clear diameter and neck size with non-negative armor.");

        try
        {
            var scanRadius = checked((clearDiameterTwice + 3) / 4 + 2);
            var boreSide = checked(2 * scanRadius + 1);
            var boreCells = checked((long)boreSide * boreSide);
            var sideSide = checked(boreSide + 2 * sideArmorThicknessMetres);
            var sideCells = checked((long)sideSide * sideSide);

            var clearHalf = (neckClearSizeMetres - 1) / 2;
            var neckClearCells = checked((long)neckClearSizeMetres * neckClearSizeMetres);
            var neckExteriorSide = checked(neckClearSizeMetres + 2 * neckArmorThicknessMetres);
            var neckExteriorCells = checked((long)neckExteriorSide * neckExteriorSide);
            var neckArmorRingCells = checked(8L * (
                (long)neckArmorThicknessMetres * (clearHalf + 1) +
                (long)neckArmorThicknessMetres * (neckArmorThicknessMetres - 1) / 2));

            var estimate = checked(boreCells + sideCells + neckClearCells + neckArmorRingCells +
                neckExteriorCells);
            if (estimate > maxPlanCells)
                return Failure(
                    BarbetteDiagnosticCodes.PlanBudgetExceeded,
                    $"Barbette planar preflight needs up to {estimate:N0} cells (bore {boreCells:N0}, " +
                    $"side {sideCells:N0}, neck clear {neckClearCells:N0}, neck armor " +
                    $"{neckArmorRingCells:N0}, neck exterior {neckExteriorCells:N0}), above the " +
                    $"configured {maxPlanCells:N0}-cell mask budget.",
                    field: nameof(BarbetteGenerationLimits.MaxPlanCells),
                    requested: DesignMeasure.FromTwiceMetres(clearDiameterTwice),
                    suggestedCorrection: "Reduce the clear diameter, side/neck armor thickness or neck " +
                        "size; do not raise the mask budget to mask the failure.",
                    scanRadius: scanRadius,
                    boreScanCells: boreCells,
                    sideArmorBoundCells: sideCells,
                    neckClearCells: neckClearCells,
                    neckArmorRingCells: neckArmorRingCells,
                    neckExteriorCells: neckExteriorCells,
                    estimatedPlanCells: estimate);

            return new BarbettePlanPreflightResult(true, scanRadius, boreCells, sideCells,
                neckClearCells, neckArmorRingCells, neckExteriorCells, estimate, []);
        }
        catch (OverflowException)
        {
            return Failure(
                BarbetteDiagnosticCodes.PlanBudgetExceeded,
                "Barbette planar preflight arithmetic overflowed the supported integer range; the " +
                "request cannot be planned within any budget.",
                field: nameof(BarbetteGenerationLimits.MaxPlanCells),
                requested: clearDiameterTwice > 0 ? DesignMeasure.FromTwiceMetres(clearDiameterTwice) : null,
                suggestedCorrection: "Reduce the clear diameter, side/neck armor thickness or neck " +
                    "size; do not raise the mask budget to mask the failure.");
        }
    }

    /// <summary>
    /// Preflights the planar structures for a barbette definition against its explicit generation
    /// limits. This is the integration-hook entry point; see the class remarks for the exact
    /// <c>BuildPlan</c> call site.
    /// </summary>
    public static BarbettePlanPreflightResult Preflight(
        BarbetteDefinition definition,
        BarbetteGenerationLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        limits ??= BarbetteGenerationLimits.Default;
        var result = Preflight(
            definition.ClearDiameter.TwiceMetres,
            definition.SideArmorThicknessMetres,
            definition.NeckClearSizeMetres,
            definition.NeckArmorThicknessMetres,
            limits.MaxPlanCells,
            cancellationToken);
        if (result.IsValid)
            return result;

        // Attribute the failure to the barbette node so the hook does not weaken the structured
        // diagnostic the base generator used to emit.
        return result with
        {
            Diagnostics = result.Diagnostics
                .Select(diagnostic => diagnostic.NodeId is null
                    ? diagnostic with { NodeId = definition.NodeId }
                    : diagnostic)
                .ToArray(),
        };
    }

    /// <summary>
    /// Visits exactly the cells of the Chebyshev ring at <paramref name="radius"/> around
    /// (<paramref name="centerX"/>, <paramref name="centerZ"/>) in deterministic z-then-x order.
    /// A radius-zero ring is the single centre cell; a radius-<c>r</c> ring visits exactly
    /// <c>8r</c> cells and never touches the square interior.
    /// </summary>
    public static void EnumerateSquarePerimeter(
        int centerX,
        int centerZ,
        int radius,
        Action<BarbettePlanCell> visit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(visit);
        if (radius < 0)
            throw new ArgumentOutOfRangeException(nameof(radius), radius,
                "A square-perimeter radius cannot be negative.");
        cancellationToken.ThrowIfCancellationRequested();

        if (radius == 0)
        {
            visit(new BarbettePlanCell(centerX, centerZ));
            return;
        }

        var minX = checked(centerX - radius);
        var maxX = checked(centerX + radius);
        var minZ = checked(centerZ - radius);
        var maxZ = checked(centerZ + radius);

        // Top edge, then the two side cells of every middle row, then the bottom edge. Emitting
        // whole rows in ascending z and each row in ascending x is exactly the z-then-x order.
        // Each loop breaks at its last index before incrementing, so a centre at the extreme of the
        // integer range can never wrap and spin forever.
        for (var x = minX; x <= maxX; x++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            visit(new BarbettePlanCell(x, minZ));
            if (x == maxX)
                break;
        }

        for (var z = minZ + 1; z <= maxZ - 1; z++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            visit(new BarbettePlanCell(minX, z));
            visit(new BarbettePlanCell(maxX, z));
            if (z == maxZ - 1)
                break;
        }

        for (var x = minX; x <= maxX; x++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            visit(new BarbettePlanCell(x, maxZ));
            if (x == maxX)
                break;
        }
    }

    /// <summary>
    /// Materializes the deterministic z-then-x Chebyshev ring at <paramref name="radius"/>.
    /// See <see cref="EnumerateSquarePerimeter"/> for the exact visit order and shape.
    /// </summary>
    public static IReadOnlyList<BarbettePlanCell> SquarePerimeter(
        int centerX,
        int centerZ,
        int radius,
        CancellationToken cancellationToken = default)
    {
        if (radius < 0)
            throw new ArgumentOutOfRangeException(nameof(radius), radius,
                "A square-perimeter radius cannot be negative.");
        cancellationToken.ThrowIfCancellationRequested();

        // Saturate the pre-sized capacity instead of throwing an unchecked overflow for an absurd
        // radius; the enumeration itself is inherently O(radius).
        var capacity = radius <= int.MaxValue / 8 ? checked(radius * 8) : 0;
        var ring = new List<BarbettePlanCell>(capacity);
        EnumerateSquarePerimeter(centerX, centerZ, radius, ring.Add, cancellationToken);
        return ring.AsReadOnly();
    }

    private static BarbettePlanPreflightResult Failure(
        string code,
        string message,
        string? field = null,
        DesignMeasure? requested = null,
        string? suggestedCorrection = null,
        int scanRadius = 0,
        long boreScanCells = 0,
        long sideArmorBoundCells = 0,
        long neckClearCells = 0,
        long neckArmorRingCells = 0,
        long neckExteriorCells = 0,
        long estimatedPlanCells = 0) =>
        new(false, scanRadius, boreScanCells, sideArmorBoundCells, neckClearCells, neckArmorRingCells,
            neckExteriorCells, estimatedPlanCells,
            [new DesignDiagnostic(code, DesignSeverity.Error, message, Field: field, Requested: requested,
                SuggestedCorrection: suggestedCorrection)]);
}
