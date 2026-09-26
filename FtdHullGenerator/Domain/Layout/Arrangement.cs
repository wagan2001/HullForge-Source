using System.Collections.Immutable;
using System.Text.Json.Serialization;
using FtdHullGenerator.Domain.Design;

namespace FtdHullGenerator.Domain.Layout;

/// <summary>Top-level kinds that consume the arrangement strip.</summary>
public enum ArrangementNodeKind
{
    Barbette = 0,
    SuperstructureSpan = 1,
}

/// <summary>How a gap segment is measured. The default is clear edge-to-edge distance.</summary>
public enum ArrangementMeasure
{
    ClearEdgeGap = 0,
    CenterPitch = 1,
}

/// <summary>The single positional anchor of the chain.</summary>
public enum ArrangementAnchorKind
{
    BowDatum = 0,
    SternDatum = 1,
}

/// <summary>What happens to component sizes and gap values when the hull is resized.</summary>
public enum ArrangementResizePolicy
{
    /// <summary>Keep physical component sizes and absolute gap values; re-evaluate the anchor.</summary>
    PreserveAbsolute = 0,

    /// <summary>Scale proportional rules with the hull length. Never applied without an explicit choice.</summary>
    ScaleWithLength = 1,
}

/// <summary>
/// A subordinate point handle inside one node: a superstructure root, a span boundary or a
/// tower root. Handles are children of their node and never consume the strip a second time.
/// </summary>
public sealed record ArrangementHandle(
    string Id,
    string Name,
    DesignMeasure OffsetFromNodeCenter,
    string? TargetId = null);

/// <summary>
/// One occupied longitudinal interval. <see cref="OuterHalfExtent"/> is the realized outer
/// footprint, not a nominal diameter: spacing follows what will actually be placed.
/// </summary>
/// <remarks>
/// <see cref="RequestedCenter"/> is the frozen 2.0 barbette placement: an explicit ruler
/// coordinate that pins this node's centre so a drag moves exactly one node and never reflows a
/// neighbour. It is <c>null</c> for the legacy linked-gap chain, which derives every centre from
/// the bow/stern margins and gap segments.
/// </remarks>
public sealed record ArrangementNode(
    string Id,
    ArrangementNodeKind Kind,
    string ComponentId,
    DesignMeasure OuterHalfExtent,
    ImmutableArray<ArrangementHandle> Handles,
    DesignMeasure? RequestedCenter = null)
{
    public static ArrangementNode Create(
        string id,
        ArrangementNodeKind kind,
        string componentId,
        DesignMeasure outerHalfExtent,
        params ArrangementHandle[] handles) =>
        new(id, kind, componentId, outerHalfExtent, (handles ?? []).ToImmutableArray());

    /// <summary>Realized interval this node occupies when its centre sits at <paramref name="center"/>.</summary>
    public DesignSpan SpanAt(DesignMeasure center) => new(center - OuterHalfExtent, center + OuterHalfExtent);
}

/// <summary>
/// One gap segment in the chain. <see cref="NamedGapId"/> references a shared variable; two
/// segments that reference the same variable move together, which is how one 5 m gun-pair gap
/// drives both ends.
/// </summary>
public sealed record ArrangementGap(
    string Id,
    ArrangementMeasure Measure,
    DesignMeasure Value,
    string? NamedGapId = null);

/// <summary>A named, shared gap value.</summary>
public sealed record NamedArrangementGap(
    string Id,
    string Name,
    ArrangementMeasure Measure,
    DesignMeasure Value);

/// <summary>
/// The ordered bow-to-stern chain: bow margin, node, gap, node, gap, … , stern margin. The
/// ruler runs bow → stern left to right; the engine coordinate is <c>z = layoutBowZ - s</c>.
/// </summary>
public sealed record Arrangement(
    ImmutableArray<ArrangementNode> Nodes,
    ImmutableArray<ArrangementGap> Gaps,
    ImmutableArray<NamedArrangementGap> NamedGaps,
    DesignMeasure BowMargin,
    DesignMeasure SternMargin,
    ArrangementAnchorKind Anchor,
    ArrangementResizePolicy ResizePolicy,
    string? FlexibleGapId = null)
{
    public static Arrangement Empty { get; } = new(
        [], [], [], DesignMeasure.Zero, DesignMeasure.Zero,
        ArrangementAnchorKind.BowDatum, ArrangementResizePolicy.PreserveAbsolute, null);

    [JsonIgnore]
    public int ExpectedGapCount => Nodes.Length == 0 ? 0 : Nodes.Length - 1;

    /// <summary>
    /// True when every node carries its own explicit centre, so the frozen 2.0 ruler places nodes
    /// directly and the linked-gap chain is not consulted.
    /// </summary>
    [JsonIgnore]
    public bool IsFullyExplicitlyPlaced =>
        Nodes.Length > 0 && Nodes.All(node => node.RequestedCenter is not null);

    public NamedArrangementGap? FindNamedGap(string id) =>
        NamedGaps.FirstOrDefault(gap => string.Equals(gap.Id, id, StringComparison.Ordinal));

    public ArrangementNode? FindNode(string id) =>
        Nodes.FirstOrDefault(node => string.Equals(node.Id, id, StringComparison.Ordinal));

    /// <summary>
    /// The value a segment currently contributes: its shared variable when it references one,
    /// otherwise its literal value.
    /// </summary>
    public DesignMeasure ResolveGapValue(ArrangementGap gap)
    {
        ArgumentNullException.ThrowIfNull(gap);
        if (gap.NamedGapId is null)
            return gap.Value;
        return FindNamedGap(gap.NamedGapId)?.Value ?? gap.Value;
    }

    public IEnumerable<DesignDiagnostic> Validate()
    {
        var errors = new List<DesignDiagnostic>();
        // A fully explicit (frozen 2.0 barbette) arrangement places every node by its requested
        // centre, so the chain's bow/stern margins and gap segments carry no meaning and are not
        // required. A single unpinned node restores the legacy chain contract.
        if (!IsFullyExplicitlyPlaced && Gaps.Length != ExpectedGapCount)
            errors.Add(new DesignDiagnostic(DesignDiagnosticCodes.ArrangementGapCountMismatch, DesignSeverity.Error,
                $"The chain has {Nodes.Length} node(s) and must declare {ExpectedGapCount} gap(s), but declares {Gaps.Length}.",
                Field: nameof(Gaps),
                SuggestedCorrection: ExpectedGapCount == 0
                    ? "A chain with fewer than two nodes declares no gaps."
                    : $"Declare exactly {ExpectedGapCount} gap segment(s)."));

        if (BowMargin < DesignMeasure.Zero)
            errors.Add(new DesignDiagnostic(DesignDiagnosticCodes.ArrangementNegativeGap, DesignSeverity.Error,
                "The bow margin cannot be negative.", Field: nameof(BowMargin),
                Requested: BowMargin, Realized: DesignMeasure.Zero));
        if (SternMargin < DesignMeasure.Zero)
            errors.Add(new DesignDiagnostic(DesignDiagnosticCodes.ArrangementNegativeGap, DesignSeverity.Error,
                "The stern margin cannot be negative.", Field: nameof(SternMargin),
                Requested: SternMargin, Realized: DesignMeasure.Zero));

        var nodeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in Nodes)
        {
            errors.AddRange(RequireIdentifier(node.Id, "arrangement node", node.Id));
            if (!nodeIds.Add(node.Id))
                errors.Add(DesignDiagnostic.Error(DesignDiagnosticCodes.IdentifierDuplicate,
                    $"Two arrangement nodes share the id '{node.Id}'.", node.Id));
            if (!Enum.IsDefined(node.Kind))
                errors.Add(DesignDiagnostic.Error(DesignDiagnosticCodes.ArrangementUnknownNode,
                    $"Node '{node.Id}' declares an unsupported kind.", node.Id));
            if (!node.OuterHalfExtent.IsWithinDesignBounds)
                errors.Add(new DesignDiagnostic(DesignDiagnosticCodes.MeasureOutOfRange, DesignSeverity.Error,
                    $"Node '{node.Id}' places its realized half-extent outside the supported design range.",
                    node.Id, nameof(node.OuterHalfExtent), node.OuterHalfExtent,
                    DesignMeasure.FromTwiceMetres(DesignLimits.MaxDesignTwiceMetres),
                    "Reduce the coordinate magnitude or split the ship."));
            if (node.OuterHalfExtent < DesignMeasure.Zero)
                errors.Add(new DesignDiagnostic(DesignDiagnosticCodes.ArrangementNegativeExtent, DesignSeverity.Error,
                    $"Node '{node.Id}' has a negative realized half-extent.", node.Id, nameof(node.OuterHalfExtent),
                    node.OuterHalfExtent, DesignMeasure.Zero,
                    "Measure the realized outer footprint before solving the chain."));

            if (node.RequestedCenter is { } requested && !requested.IsWithinDesignBounds)
                errors.Add(new DesignDiagnostic(DesignDiagnosticCodes.MeasureOutOfRange, DesignSeverity.Error,
                    $"Node '{node.Id}' places its requested centre outside the supported design range.",
                    node.Id, nameof(node.RequestedCenter), requested,
                    DesignMeasure.FromTwiceMetres(DesignLimits.MaxDesignTwiceMetres),
                    "Reduce the coordinate magnitude or split the ship."));

            var handleIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var handle in node.Handles)
            {
                errors.AddRange(RequireIdentifier(handle.Id, "arrangement handle", node.Id));
                if (!handleIds.Add(handle.Id))
                    errors.Add(DesignDiagnostic.Error(DesignDiagnosticCodes.IdentifierDuplicate,
                        $"Node '{node.Id}' has two handles with the id '{handle.Id}'.", node.Id));
                if (!handle.OffsetFromNodeCenter.IsWithinDesignBounds)
                    errors.Add(new DesignDiagnostic(DesignDiagnosticCodes.MeasureOutOfRange, DesignSeverity.Error,
                        $"Handle '{handle.Id}' sits outside the supported design range.", node.Id, handle.Id,
                        handle.OffsetFromNodeCenter, DesignMeasure.FromTwiceMetres(DesignLimits.MaxDesignTwiceMetres)));
                if (handle.OffsetFromNodeCenter.Magnitude > node.OuterHalfExtent)
                    errors.Add(new DesignDiagnostic(DesignDiagnosticCodes.ArrangementChildOutsideParent,
                        DesignSeverity.Error,
                        $"Handle '{handle.Id}' lies outside its owning node '{node.Id}'.",
                        node.Id, handle.Id, handle.OffsetFromNodeCenter, node.OuterHalfExtent,
                        "Move the handle inside the span, or widen the span deliberately."));
            }
        }

        var namedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var named in NamedGaps)
        {
            errors.AddRange(RequireIdentifier(named.Id, "named gap", named.Id));
            if (!namedIds.Add(named.Id))
                errors.Add(DesignDiagnostic.Error(DesignDiagnosticCodes.IdentifierDuplicate,
                    $"Two named gaps share the id '{named.Id}'.", named.Id));
            if (!Enum.IsDefined(named.Measure))
                errors.Add(DesignDiagnostic.Error(DesignDiagnosticCodes.ArrangementUnknownNamedGap,
                    $"Named gap '{named.Id}' declares an unsupported measurement.", named.Id));
            if (named.Value < DesignMeasure.Zero)
                errors.Add(new DesignDiagnostic(DesignDiagnosticCodes.ArrangementNegativeGap, DesignSeverity.Error,
                    $"Named gap '{named.Id}' cannot be negative.", named.Id, nameof(named.Value),
                    named.Value, DesignMeasure.Zero));
        }

        var gapIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var gap in Gaps)
        {
            errors.AddRange(RequireIdentifier(gap.Id, "arrangement gap", gap.Id));
            if (!gapIds.Add(gap.Id))
                errors.Add(DesignDiagnostic.Error(DesignDiagnosticCodes.IdentifierDuplicate,
                    $"Two arrangement gaps share the id '{gap.Id}'.", gap.Id));
            if (!Enum.IsDefined(gap.Measure))
                errors.Add(DesignDiagnostic.Error(DesignDiagnosticCodes.ArrangementUnknownNamedGap,
                    $"Gap '{gap.Id}' declares an unsupported measurement.", gap.Id));
            if (gap.Value < DesignMeasure.Zero)
                errors.Add(new DesignDiagnostic(DesignDiagnosticCodes.ArrangementNegativeGap, DesignSeverity.Error,
                    $"Gap '{gap.Id}' cannot be negative.", gap.Id, nameof(gap.Value),
                    gap.Value, DesignMeasure.Zero));

            if (gap.NamedGapId is null)
                continue;

            var named = FindNamedGap(gap.NamedGapId);
            if (named is null)
            {
                errors.Add(DesignDiagnostic.Error(DesignDiagnosticCodes.ArrangementUnknownNamedGap,
                    $"Gap '{gap.Id}' references the unknown named gap '{gap.NamedGapId}'.", gap.Id));
                continue;
            }

            if (named.Measure != gap.Measure)
                errors.Add(DesignDiagnostic.Error(DesignDiagnosticCodes.ArrangementMeasureMismatch,
                    $"Gap '{gap.Id}' measures {gap.Measure} but shares the {named.Measure} variable '{named.Id}'.",
                    gap.Id, nameof(gap.Measure)));
        }

        if (FlexibleGapId is not null && !gapIds.Contains(FlexibleGapId))
            errors.Add(DesignDiagnostic.Error(DesignDiagnosticCodes.ArrangementUnknownNamedGap,
                $"The flexible gap '{FlexibleGapId}' is not one of the chain's gap segments."));

        if (!Enum.IsDefined(Anchor))
            errors.Add(DesignDiagnostic.Error(DesignDiagnosticCodes.ArrangementUnsupportedEnumeration,
                $"The chain declares an unsupported anchor '{Anchor}'.", null, nameof(Anchor)));
        if (!Enum.IsDefined(ResizePolicy))
            errors.Add(DesignDiagnostic.Error(DesignDiagnosticCodes.ArrangementUnsupportedEnumeration,
                $"The chain declares an unsupported resize policy '{ResizePolicy}'.", null, nameof(ResizePolicy)));

        return errors;
    }

    internal static IEnumerable<DesignDiagnostic> RequireIdentifier(string? id, string what, string? nodeId)
    {
        if (string.IsNullOrWhiteSpace(id))
            yield return DesignDiagnostic.Error(DesignDiagnosticCodes.IdentifierMissing,
                $"A {what} has no stable identifier.", nodeId);
        else if (id.Length > DesignLimits.MaxIdentifierLength)
            yield return DesignDiagnostic.Error(DesignDiagnosticCodes.IdentifierMissing,
                $"The {what} identifier '{id}' exceeds {DesignLimits.MaxIdentifierLength} characters.", nodeId);
    }
}
