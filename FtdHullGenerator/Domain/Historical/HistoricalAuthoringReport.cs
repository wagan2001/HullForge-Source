using FtdHullGenerator.Domain;

namespace FtdHullGenerator.Domain.Historical;

/// <summary>Bounded topology counters; source vertices and faces never enter an authoring report.</summary>
public sealed record HistoricalTopologyReportSummary(
    int BoundaryEdgeCount,
    int NonManifoldEdgeCount,
    int InconsistentWindingEdgeCount,
    int ConnectedComponentCount,
    double SignedVolumeCubicMetres);

/// <summary>Bounded sampled-envelope counters; row data stays in the derived asset only.</summary>
public sealed record HistoricalSamplingReportSummary(
    int StationCount,
    int RowCount,
    int UnsupportedRowCount,
    int AsymmetryNormalizedRowCount);

/// <summary>Bounded Shape V2 fit evidence; evaluated hull blocks are deliberately excluded.</summary>
public sealed record HistoricalFitReportSummary(
    bool Accepted,
    HullParameters? Parameters,
    HistoricalFidelityMetric SourceFit,
    HistoricalFidelityMetric VoxelQuantization,
    int Evaluations,
    IReadOnlyList<string> Diagnostics);

/// <summary>Bounded sampled-fallback evidence; context, hull, blocks and occupied cells are excluded.</summary>
public sealed record HistoricalFallbackReportSummary(
    HistoricalFidelityMetric VoxelQuantization,
    HistoricalFallbackValidation Validation);

/// <summary>The only report envelope emitted by the bounded H02 command-line authoring tool.</summary>
public sealed record HistoricalAuthoringReport(
    int SchemaVersion,
    HistoricalAuthoringOutcome Outcome,
    string Stage,
    HistoricalEnvelopeMetadata? Metadata,
    string? SourceContentHash,
    HistoricalTopologyReportSummary? Topology,
    HistoricalSamplingReportSummary? Sampling,
    HistoricalFitReportSummary? Fit,
    HistoricalFallbackReportSummary? Fallback,
    HistoricalFidelityReport? Fidelity,
    IReadOnlyList<string> Diagnostics)
{
    public const int CurrentSchemaVersion = 1;
}
