using FtdHullGenerator.Domain.Design;
using FtdHullGenerator.Domain.Layout;

namespace FtdHullGenerator.Geometry.Layout;

/// <summary>
/// Solver-specific diagnostic codes, appended to the document register in
/// <c>FtdHullGenerator.Domain.Design.DesignDiagnosticCodes</c> (which owns DOC and LAY001–LAY008)
/// using the reserved LAY1xx range so a code is never repurposed. Parity reuses the register's own
/// ArrangementCenterlineUnrepresentable (LAY007) rather than declaring a second code for the same
/// condition.
/// </summary>
public static class ArrangementDiagnosticCodes
{
    public const string ChainExceedsSupportedLength = "LAY101";
    public const string FlexibleRemainderNegative = "LAY102";
    public const string SternDatumNeedsSupportedEnd = "LAY104";

    /// <summary>A centre pitch too small to fit its two rings: the rings would overlap.</summary>
    public const string CenterPitchTooSmall = "LAY105";

    /// <summary>A stern-datum chain would start before the ruler origin.</summary>
    public const string ChainStartsBeforeOrigin = "LAY106";
}

/// <summary>Whether the chain produced a usable layout, and if not, why.</summary>
public enum ArrangementSolveStatus
{
    /// <summary>Every constraint is satisfied and the realized intervals are reported.</summary>
    Solved = 0,

    /// <summary>The input arrangement is not a well-formed chain; no intervals are reported.</summary>
    Invalid = 1,

    /// <summary>The chain is well formed but cannot be realized as asked: it overruns its support
    /// envelope, leaves a negative remainder, or lands off the requested lattice.</summary>
    DoesNotFit = 2,
}

/// <summary>Optional lattice discipline applied to the solved chain.</summary>
public enum ArrangementParityRequirement
{
    None = 0,

    /// <summary>Every node centre must land on a whole metre (an integer cell anchor).</summary>
    WholeMetreCenters = 1,

    /// <summary>Every node edge must land on a whole metre.</summary>
    WholeMetreEdges = 2,
}

/// <summary>
/// Solver inputs. <see cref="LayoutBowZ"/> is the world-Z engine coordinate of the ruler's zero
/// point, so the conversion is the contract's <c>z = layoutBowZ - s</c>.
/// </summary>
public sealed record ArrangementSolveOptions(
    DesignMeasure LayoutBowZ,
    DesignMeasure? SupportedRulerEnd = null,
    ArrangementParityRequirement Parity = ArrangementParityRequirement.None,
    bool SolveFlexibleRemainder = true,
    DesignMeasure CenterPlaneX = default)
{
    public static ArrangementSolveOptions At(DesignMeasure layoutBowZ) => new(layoutBowZ);
}

/// <summary>A solved child handle: a superstructure root, span boundary or tower root.</summary>
public sealed record ArrangementHandleSolution(
    string HandleId,
    string Name,
    DesignMeasure RulerPosition,
    DesignMeasure WorldZPosition,
    string? TargetId);

/// <summary>A solved occupied interval with its realized ruler and world coordinates.</summary>
public sealed record ArrangementNodeSolution(
    string NodeId,
    ArrangementNodeKind Kind,
    string ComponentId,
    DesignMeasure OuterHalfExtent,
    DesignSpan RulerSpan,
    DesignMeasure RulerCenter,
    DesignMeasure WorldZCenter,
    IReadOnlyList<ArrangementHandleSolution> Handles);

/// <summary>A solved gap segment, including the realized clear gap it produces.</summary>
public sealed record ArrangementGapSolution(
    string GapId,
    ArrangementMeasure Measure,
    string? NamedGapId,
    DesignMeasure Value,
    DesignMeasure CenterDistance,
    DesignMeasure RealizedClearGap,
    bool IsFlexible);

/// <summary>
/// The solver's output. <see cref="Nodes"/> is always populated for a well-formed chain, even when
/// the result does not fit, so a caller can keep showing the last requested intent and mark it
/// stale instead of silently dropping the user's values.
/// </summary>
public sealed record ArrangementSolution(
    ArrangementSolveStatus Status,
    IReadOnlyList<ArrangementNodeSolution> Nodes,
    IReadOnlyList<ArrangementGapSolution> Gaps,
    IReadOnlyList<DesignDiagnostic> Diagnostics,
    DesignMeasure RequiredRulerLength,
    DesignMeasure? SupportedRulerEnd)
{
    public bool IsSolved => Status == ArrangementSolveStatus.Solved;

    public DesignMeasure UnusedRulerLength =>
        SupportedRulerEnd is { } supported && supported > RequiredRulerLength
            ? supported - RequiredRulerLength
            : DesignMeasure.Zero;

    public DesignMeasure ExcessRulerLength =>
        SupportedRulerEnd is { } supported && RequiredRulerLength > supported
            ? RequiredRulerLength - supported
            : DesignMeasure.Zero;

    public ArrangementNodeSolution? FindNode(string nodeId) =>
        Nodes.FirstOrDefault(node => string.Equals(node.NodeId, nodeId, StringComparison.Ordinal));
}

/// <summary>
/// The pure, UI-independent arrangement solver. It walks one ordered bow-to-stern chain of
/// occupied intervals and gap segments, resolving shared named gap variables, and reports realized
/// intervals, child anchors and diagnostics. It never changes hull shape, never rounds a value
/// silently, and has no WPF or generator dependency.
/// </summary>
public static class ArrangementSolver
{
    public static ArrangementSolution Solve(Arrangement arrangement, ArrangementSolveOptions options)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(options);

        var diagnostics = new List<DesignDiagnostic>();
        var validation = arrangement.Validate().ToArray();
        if (validation.HasErrors())
            return new ArrangementSolution(ArrangementSolveStatus.Invalid, [], [], validation,
                DesignMeasure.Zero, options.SupportedRulerEnd);
        diagnostics.AddRange(validation);

        if (arrangement.Nodes.Length == 0)
            return new ArrangementSolution(ArrangementSolveStatus.Solved, [], [],
                diagnostics, arrangement.BowMargin + arrangement.SternMargin, options.SupportedRulerEnd);

        // Frozen 2.0 barbettes carry their own requested centre. Pinning is deliberately a
        // separate path so the legacy linked-gap chain stays byte-for-byte unchanged.
        if (arrangement.Nodes.Any(node => node.RequestedCenter is not null))
            return SolveWithPinnedCenters(arrangement, options, diagnostics);

        // Resolve every segment's contributing value before positioning anything: a shared named
        // variable is one value, so both linked ends move together by construction.
        var gapValues = arrangement.Gaps.Select(arrangement.ResolveGapValue).ToArray();
        var nodeLengths = arrangement.Nodes.Select(node => node.OuterHalfExtent * 2).ToArray();

        // A gap's *declared* value and the ruler length it consumes are two different quantities for
        // a centre pitch: a declared pitch of p between half-extents a and b realizes an edge
        // distance of p - a - b. Every length, remainder and stern-datum calculation below therefore
        // works in realized edge distances, never in declared values.
        var edgeLengths = new DesignMeasure[arrangement.Gaps.Length];
        var hasOverlap = false;
        for (var index = 0; index < arrangement.Gaps.Length; index++)
        {
            var gap = arrangement.Gaps[index];
            var halfBefore = arrangement.Nodes[index].OuterHalfExtent;
            var halfAfter = arrangement.Nodes[index + 1].OuterHalfExtent;
            edgeLengths[index] = gap.Measure == ArrangementMeasure.CenterPitch
                ? gapValues[index] - halfBefore - halfAfter
                : gapValues[index];

            if (edgeLengths[index] < DesignMeasure.Zero)
            {
                hasOverlap = true;
                diagnostics.Add(new DesignDiagnostic(ArrangementDiagnosticCodes.CenterPitchTooSmall,
                    DesignSeverity.Error,
                    $"Gap '{gap.Id}' declares a {gapValues[index].Metres} m centre pitch between rings whose " +
                    $"realized half-extents are {halfBefore.Metres} m and {halfAfter.Metres} m, so the rings " +
                    "would overlap.",
                    gap.Id, nameof(gap.Value), gapValues[index],
                    halfBefore + halfAfter,
                    "Increase the centre pitch, or measure the segment as a clear edge gap instead."));
            }
        }

        var fixedLength = arrangement.BowMargin + arrangement.SternMargin + Sum(edgeLengths) + Sum(nodeLengths);

        var flexibleIndex = -1;
        if (arrangement.FlexibleGapId is not null)
            for (var index = 0; index < arrangement.Gaps.Length; index++)
                if (string.Equals(arrangement.Gaps[index].Id, arrangement.FlexibleGapId, StringComparison.Ordinal))
                    flexibleIndex = index;

        if (flexibleIndex >= 0 && options.SolveFlexibleRemainder && options.SupportedRulerEnd is { } supportedEnd)
        {
            var remainderEdge = supportedEnd - (fixedLength - edgeLengths[flexibleIndex]);
            if (remainderEdge < DesignMeasure.Zero)
            {
                diagnostics.Add(new DesignDiagnostic(ArrangementDiagnosticCodes.FlexibleRemainderNegative,
                    DesignSeverity.Error,
                    $"The chain already needs {fixedLength.Metres} m of ruler but only {supportedEnd.Metres} m is " +
                    $"supported, so the flexible gap '{arrangement.Gaps[flexibleIndex].Id}' has no remainder left.",
                    arrangement.FlexibleGapId, nameof(arrangement.FlexibleGapId),
                    fixedLength, supportedEnd,
                    "Free up ruler by shortening the bow/stern margin or another component, or unlock the gap."));
                gapValues[flexibleIndex] = DesignMeasure.Zero;
                edgeLengths[flexibleIndex] = DesignMeasure.Zero;
            }
            else
            {
                edgeLengths[flexibleIndex] = remainderEdge;
                var gap = arrangement.Gaps[flexibleIndex];
                gapValues[flexibleIndex] = gap.Measure == ArrangementMeasure.CenterPitch
                    ? remainderEdge + arrangement.Nodes[flexibleIndex].OuterHalfExtent +
                      arrangement.Nodes[flexibleIndex + 1].OuterHalfExtent
                    : remainderEdge;
            }
        }

        var requiredTotal = Sum(edgeLengths) + Sum(nodeLengths) + arrangement.BowMargin + arrangement.SternMargin;

        // Place the chain. A bow-datum chain starts at its bow margin; a stern-datum chain is
        // positioned so that its stern margin lands on the supported end.
        DesignMeasure cursor;
        if (arrangement.Anchor == ArrangementAnchorKind.SternDatum)
        {
            if (options.SupportedRulerEnd is not { } sternEnd)
            {
                diagnostics.Add(DesignDiagnostic.Error(ArrangementDiagnosticCodes.SternDatumNeedsSupportedEnd,
                    "A stern-datum chain needs the supported ruler end to position itself.", null,
                    nameof(Arrangement.Anchor)));
                return new ArrangementSolution(ArrangementSolveStatus.Invalid, [], [], diagnostics,
                    requiredTotal, options.SupportedRulerEnd);
            }

            cursor = sternEnd - arrangement.SternMargin - (Sum(edgeLengths) + Sum(nodeLengths));
            if (cursor < DesignMeasure.Zero)
            {
                diagnostics.Add(new DesignDiagnostic(ArrangementDiagnosticCodes.ChainStartsBeforeOrigin,
                    DesignSeverity.Error,
                    $"A stern-datum chain of {requiredTotal.Metres} m does not fit in the {sternEnd.Metres} m " +
                    $"supported ruler: it would start at {cursor.Metres} m, before the ruler origin.",
                    arrangement.Nodes[0].Id, nameof(Arrangement.Anchor),
                    requiredTotal, sternEnd,
                    "Shorten the chain, reduce the stern margin, or anchor it at the bow datum."));
                cursor = DesignMeasure.Zero;
            }
        }
        else
        {
            cursor = arrangement.BowMargin;
        }

        var nodes = new List<ArrangementNodeSolution>(arrangement.Nodes.Length);
        var gaps = new List<ArrangementGapSolution>(arrangement.Gaps.Length);
        var previous = default(ArrangementNodeSolution?);
        for (var index = 0; index < arrangement.Nodes.Length; index++)
        {
            var node = arrangement.Nodes[index];
            var length = nodeLengths[index];
            var span = new DesignSpan(cursor, cursor + length);
            var center = cursor + node.OuterHalfExtent;
            var handles = node.Handles
                .Select(handle =>
                {
                    var ruler = center + handle.OffsetFromNodeCenter;
                    return new ArrangementHandleSolution(handle.Id, handle.Name, ruler,
                        options.LayoutBowZ - ruler, handle.TargetId);
                })
                .ToArray();
            var solution = new ArrangementNodeSolution(node.Id, node.Kind, node.ComponentId,
                node.OuterHalfExtent, span, center, options.LayoutBowZ - center, handles);
            nodes.Add(solution);

            if (index > 0 && previous is not null)
            {
                var gap = arrangement.Gaps[index - 1];
                var value = gapValues[index - 1];
                var centerDistance = center - previous.RulerCenter;
                var realizedClearGap = centerDistance - previous.OuterHalfExtent - node.OuterHalfExtent;
                if (gap.Measure == ArrangementMeasure.CenterPitch)
                    realizedClearGap = value - previous.OuterHalfExtent - node.OuterHalfExtent;
                gaps.Add(new ArrangementGapSolution(gap.Id, gap.Measure, gap.NamedGapId, value,
                    gap.Measure == ArrangementMeasure.CenterPitch ? value : centerDistance,
                    realizedClearGap, flexibleIndex == index - 1));
            }

            cursor = span.End;
            if (index < arrangement.Gaps.Length)
            {
                var gap = arrangement.Gaps[index];
                var value = gapValues[index];
                // A clear edge gap adds distance between the outer edges; a centre pitch is the
                // distance between centres, so it has to account for the next ring's half-extent.
                cursor = gap.Measure == ArrangementMeasure.CenterPitch
                    ? center + value - arrangement.Nodes[index + 1].OuterHalfExtent
                    : span.End + value;
            }

            previous = solution;
        }

        var required = requiredTotal;
        var status = ArrangementSolveStatus.Solved;
        if (hasOverlap)
            status = ArrangementSolveStatus.DoesNotFit;

        if (options.SupportedRulerEnd is { } support && required > support)
        {
            status = ArrangementSolveStatus.DoesNotFit;
            var offenders = nodes
                .Where(node => node.RulerSpan.End > support)
                .Select(node => node.NodeId)
                .ToArray();
            var worst = nodes.Where(node => node.RulerSpan.End > support)
                .OrderByDescending(node => node.RulerSpan.End.TwiceMetres)
                .FirstOrDefault();
            diagnostics.Add(new DesignDiagnostic(ArrangementDiagnosticCodes.ChainExceedsSupportedLength,
                DesignSeverity.Error,
                $"The chain needs {required.Metres} m of ruler but only {support.Metres} m is supported " +
                $"({(required - support).Metres} m too long).",
                worst?.NodeId ?? offenders.FirstOrDefault(), null,
                required, support,
                offenders.Length == 0
                    ? "Reduce a margin or free ruler space."
                    : $"Shorten or move: {string.Join(", ", offenders)}.",
                worst is null ? null : DesignBounds.FromRulerSpan(worst.RulerSpan, options.CenterPlaneX)));
        }

        if (options.Parity != ArrangementParityRequirement.None)
        {
            foreach (var node in nodes)
            {
                var offLattice = options.Parity switch
                {
                    ArrangementParityRequirement.WholeMetreCenters => !node.RulerCenter.IsWholeMetre,
                    ArrangementParityRequirement.WholeMetreEdges => !node.RulerSpan.Start.IsWholeMetre,
                    _ => false,
                };
                if (!offLattice)
                    continue;

                status = ArrangementSolveStatus.DoesNotFit;
                var offendingEdge = options.Parity == ArrangementParityRequirement.WholeMetreCenters
                    ? node.RulerCenter
                    : node.RulerSpan.Start;
                diagnostics.Add(new DesignDiagnostic(DesignDiagnosticCodes.ArrangementCenterlineUnrepresentable,
                    DesignSeverity.Error,
                    $"Node '{node.NodeId}' cannot land on the requested lattice " +
                    $"({(options.Parity == ArrangementParityRequirement.WholeMetreCenters ? "centre" : "forward edge")} " +
                    $"at {offendingEdge.Metres} m).",
                    node.NodeId, nameof(options.Parity),
                    offendingEdge, null,
                    "Adjust the linked gap or margin by half a metre, or relax the parity requirement."));
            }
        }

        return new ArrangementSolution(status, nodes, gaps, diagnostics, required, options.SupportedRulerEnd);
    }

    /// <summary>
    /// Places every node that carries a requested centre exactly where the user put it. A pinned
    /// node is never nudged to satisfy a margin or a neighbour, and a drag therefore changes one
    /// node only. Nodes without a requested centre keep the legacy chain behaviour so a mixed
    /// loaded arrangement still resolves.
    /// </summary>
    private static ArrangementSolution SolveWithPinnedCenters(
        Arrangement arrangement,
        ArrangementSolveOptions options,
        List<DesignDiagnostic> diagnostics)
    {
        var gapValues = arrangement.Gaps.Select(arrangement.ResolveGapValue).ToArray();
        var nodeLengths = arrangement.Nodes.Select(node => node.OuterHalfExtent * 2).ToArray();

        var edgeLengths = new DesignMeasure[arrangement.Gaps.Length];
        var hasOverlap = false;
        for (var index = 0; index < arrangement.Gaps.Length; index++)
        {
            var gap = arrangement.Gaps[index];
            var halfBefore = arrangement.Nodes[index].OuterHalfExtent;
            var halfAfter = arrangement.Nodes[index + 1].OuterHalfExtent;
            edgeLengths[index] = gap.Measure == ArrangementMeasure.CenterPitch
                ? gapValues[index] - halfBefore - halfAfter
                : gapValues[index];

            if (edgeLengths[index] < DesignMeasure.Zero)
            {
                hasOverlap = true;
                diagnostics.Add(new DesignDiagnostic(ArrangementDiagnosticCodes.CenterPitchTooSmall,
                    DesignSeverity.Error,
                    $"Gap '{gap.Id}' declares a {gapValues[index].Metres} m centre pitch between rings whose " +
                    $"realized half-extents are {halfBefore.Metres} m and {halfAfter.Metres} m, so the rings " +
                    "would overlap.",
                    gap.Id, nameof(gap.Value), gapValues[index],
                    halfBefore + halfAfter,
                    "Increase the centre pitch, or measure the segment as a clear edge gap instead."));
            }
        }

        // A stern datum positions a chain; a pinned node positions itself, so a pinned arrangement
        // always starts its unpinned remainder at the bow margin.
        var cursor = arrangement.BowMargin;
        var nodes = new List<ArrangementNodeSolution>(arrangement.Nodes.Length);
        var gaps = new List<ArrangementGapSolution>(arrangement.Gaps.Length);
        ArrangementNodeSolution? previous = null;
        var anyUnpinned = false;
        var required = DesignMeasure.Zero;

        for (var index = 0; index < arrangement.Nodes.Length; index++)
        {
            var node = arrangement.Nodes[index];
            var length = nodeLengths[index];
            DesignSpan span;
            DesignMeasure center;
            if (node.RequestedCenter is { } requested)
            {
                center = requested;
                span = new DesignSpan(center - node.OuterHalfExtent, center + node.OuterHalfExtent);
            }
            else
            {
                anyUnpinned = true;
                span = new DesignSpan(cursor, cursor + length);
                center = cursor + node.OuterHalfExtent;
            }

            var handles = node.Handles
                .Select(handle =>
                {
                    var ruler = center + handle.OffsetFromNodeCenter;
                    return new ArrangementHandleSolution(handle.Id, handle.Name, ruler,
                        options.LayoutBowZ - ruler, handle.TargetId);
                })
                .ToArray();
            var solution = new ArrangementNodeSolution(node.Id, node.Kind, node.ComponentId,
                node.OuterHalfExtent, span, center, options.LayoutBowZ - center, handles);
            nodes.Add(solution);
            required = DesignMeasure.Max(required, span.End);

            if (index > 0 && previous is not null && index - 1 < arrangement.Gaps.Length)
            {
                var gap = arrangement.Gaps[index - 1];
                var value = gapValues[index - 1];
                var centerDistance = center - previous.RulerCenter;
                var realizedClearGap = centerDistance - previous.OuterHalfExtent - node.OuterHalfExtent;
                if (gap.Measure == ArrangementMeasure.CenterPitch)
                    realizedClearGap = value - previous.OuterHalfExtent - node.OuterHalfExtent;
                gaps.Add(new ArrangementGapSolution(gap.Id, gap.Measure, gap.NamedGapId, value,
                    gap.Measure == ArrangementMeasure.CenterPitch ? value : centerDistance,
                    realizedClearGap, false));
            }

            cursor = span.End;
            if (index < arrangement.Gaps.Length)
            {
                var gap = arrangement.Gaps[index];
                cursor = gap.Measure == ArrangementMeasure.CenterPitch
                    ? center + gapValues[index] - arrangement.Nodes[index + 1].OuterHalfExtent
                    : span.End + edgeLengths[index];
            }

            previous = solution;
        }

        if (anyUnpinned)
        {
            required += arrangement.SternMargin;
            if (options.SupportedRulerEnd is { } support && required > support)
            {
                var worst = nodes.OrderByDescending(item => item.RulerSpan.End.TwiceMetres).First();
                diagnostics.Add(new DesignDiagnostic(ArrangementDiagnosticCodes.ChainExceedsSupportedLength,
                    DesignSeverity.Error,
                    $"The chain needs {required.Metres} m of ruler but only {support.Metres} m is supported " +
                    $"({(required - support).Metres} m too long).",
                    worst.NodeId, null, required, support,
                    "Reduce a margin or free ruler space.",
                    DesignBounds.FromRulerSpan(worst.RulerSpan, options.CenterPlaneX)));
                return new ArrangementSolution(ArrangementSolveStatus.DoesNotFit, nodes, gaps, diagnostics,
                    required, options.SupportedRulerEnd);
            }
        }

        if (hasOverlap)
            return new ArrangementSolution(ArrangementSolveStatus.DoesNotFit, nodes, gaps, diagnostics,
                required, options.SupportedRulerEnd);

        return new ArrangementSolution(ArrangementSolveStatus.Solved, nodes, gaps, diagnostics, required,
            options.SupportedRulerEnd);
    }

    private static DesignMeasure Sum(IEnumerable<DesignMeasure> values)
    {
        var total = DesignMeasure.Zero;
        foreach (var value in values)
            total += value;
        return total;
    }
}
