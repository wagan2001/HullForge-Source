using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Design;
using FtdHullGenerator.Domain.Historical;

namespace FtdHullGenerator.Geometry.HullSources;

/// <summary>A sampled read-only view of the current regional generator's evaluated solid.</summary>
public sealed class RegionalShapeV2EnvelopeSource : IReadOnlyHullEnvelopeSource
{
    private readonly SampledHullEnvelope _envelope;

    private RegionalShapeV2EnvelopeSource(SampledHullEnvelope envelope) => _envelope = envelope;

    public HullEnvelopeBounds Bounds => _envelope.Bounds;
    public int StationCount => _envelope.StationCount;
    public int RowCount => _envelope.RowCount;
    public IReadOnlyList<UnsupportedEnvelopeRow> UnsupportedRows => _envelope.UnsupportedRows;
    internal SampledHullEnvelope Snapshot => _envelope;

    public HullEnvelopeQueryStatus QueryNormalized(
        double normalizedZ,
        double normalizedY,
        out HullEnvelopeInterval interval) =>
        _envelope.QueryNormalized(normalizedZ, normalizedY, out interval);

    public static RegionalShapeV2EnvelopeSource Sample(
        HullParameters parameters,
        int stationCount,
        int rowCount,
        CancellationToken cancellationToken = default)
    {
        if (stationCount is < 3 or > 512 || rowCount is < 2 or > 256 ||
            (long)stationCount * rowCount > 131_072)
            throw new ArgumentOutOfRangeException(nameof(stationCount), "Shape V2 sampling exceeds the H02 grid budget.");

        var context = HullGenerator.CreateContext(parameters, cancellationToken: cancellationToken);
        if (context.Diagnostics.Any(diagnostic => diagnostic.IsError))
            throw new HullGenerationException(context.Diagnostics.Select(diagnostic => diagnostic.Message).ToArray());
        var bounds = new HullEnvelopeBounds(
            context.MinX - 0.5, context.MaxX + 0.5,
            context.MinY - 0.5, context.MaxY + 0.5,
            context.MinZ - 0.5, context.MaxZ + 0.5);
        var z = Enumerable.Range(0, stationCount).Select(index => (index + 0.5) / stationCount).ToArray();
        var y = Enumerable.Range(0, rowCount).Select(index => (index + 0.5) / rowCount).ToArray();
        var intervals = new HullEnvelopeInterval?[stationCount, rowCount];
        for (var zIndex = 0; zIndex < stationCount; zIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var worldZ = Math.Clamp((int)Math.Floor(context.MinZ + z[zIndex] * (context.MaxZ - context.MinZ + 1)),
                context.MinZ, context.MaxZ);
            for (var yIndex = 0; yIndex < rowCount; yIndex++)
            {
                var worldY = Math.Clamp((int)Math.Floor(context.MinY + y[yIndex] * (context.MaxY - context.MinY + 1)),
                    context.MinY, context.MaxY);
                var occupied = Enumerable.Range(context.MinX, context.MaxX - context.MinX + 1)
                    .Where(x => context.IsOccupied(x, worldY, worldZ)).ToArray();
                if (occupied.Length == 0)
                    continue;
                intervals[zIndex, yIndex] = new HullEnvelopeInterval(
                    (occupied.Min() - 0.5 - bounds.MinX) / bounds.Beam,
                    (occupied.Max() + 0.5 - bounds.MinX) / bounds.Beam);
            }
        }

        var envelope = new SampledHullEnvelope(
            bounds, z, y, intervals, [], 0, [], "shape-v2-evaluated");
        return new RegionalShapeV2EnvelopeSource(envelope);
    }
}

public sealed record ShapeV2FitOptions(
    int MaxEvaluations = 256,
    int MaxTargetLengthCells = 512,
    int MaxTargetWidthCells = 256,
    int MaxTargetHeightCells = 128,
    int MaxCandidateVoxelCells = 8_000_000,
    double SourceFitAcceptanceMetres = 1,
    double VoxelAcceptanceMetres = 0.5,
    bool IncludeBulb = false);

public sealed record ShapeV2FitResult(
    HullParameters? Parameters,
    HistoricalFidelityMetric SourceFit,
    HistoricalFidelityMetric VoxelQuantization,
    int Evaluations,
    IReadOnlyList<DesignDiagnostic> Diagnostics)
{
    public bool Accepted => Parameters is not null && SourceFit.Status == HistoricalFidelityStageStatus.Passed &&
                            VoxelQuantization.Status == HistoricalFidelityStageStatus.Passed &&
                            !Diagnostics.Any(diagnostic => diagnostic.IsError);
}

/// <summary>
/// Deterministic, resource-bounded fit of the current Shape V2 parameterization. It changes only
/// dimensions and Shape V2 controls on a caller-supplied parameter template; armor and legacy output
/// behavior remain untouched.
/// </summary>
public static class ShapeV2HullSourceFitter
{
    public static ShapeV2FitResult Fit(
        SampledHullEnvelope source,
        HistoricalEnvelopeMetadata metadata,
        HullParameters? template = null,
        ShapeV2FitOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(metadata);
        template ??= HullParameters.Default;
        options ??= new ShapeV2FitOptions();
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ValidateOptions(source, options);
        diagnostics.AddRange(metadata.Validate());
        if (!string.Equals(metadata.SourceContentHash, source.SourceContentHash,
                StringComparison.OrdinalIgnoreCase) ||
            source.SourceTransform is not null && metadata.SourceTransform != source.SourceTransform)
            diagnostics.Add(DesignDiagnostic.Error(HistoricalDiagnosticCodes.MetadataInvalid,
                "Fit metadata must identify the sampled source hash and numeric transform."));
        if (metadata.KeelDatumMetres < source.Bounds.MinY - 1e-9 ||
            metadata.DeckDatumMetres > source.Bounds.MaxY + 1e-9 ||
            metadata.WaterlineMetres < metadata.KeelDatumMetres ||
            metadata.WaterlineMetres > metadata.DeckDatumMetres)
            diagnostics.Add(DesignDiagnostic.Error(HistoricalDiagnosticCodes.MetadataInvalid,
                "Fit keel, waterline and deck datums must be ordered inside the source bounds."));
        if (!source.IsUsable)
            diagnostics.Add(DesignDiagnostic.Error(HistoricalDiagnosticCodes.UnsupportedRowIntervals,
                "Shape V2 fitting requires a sampled source with no unsupported or invalid rows."));
        if (diagnostics.Any(diagnostic => diagnostic.IsError))
            return Failed(diagnostics);

        if (source.Bounds.Length > options.MaxTargetLengthCells + 0.5 ||
            source.Bounds.Beam > options.MaxTargetWidthCells + 0.5 ||
            source.Bounds.Depth > options.MaxTargetHeightCells + 0.5)
        {
            diagnostics.Add(DesignDiagnostic.Error(HistoricalDiagnosticCodes.InputLimitExceeded,
                "Source dimensions exceed the configured Shape V2 fit limits."));
            return Failed(diagnostics);
        }

        var length = Math.Max(HullParameters.MinimumLength,
            (int)Math.Round(source.Bounds.Length, MidpointRounding.AwayFromZero));
        var width = Math.Max(HullParameters.MinimumWidth,
            (int)Math.Round(source.Bounds.Beam, MidpointRounding.AwayFromZero));
        var datumDepth = metadata.DeckDatumMetres - metadata.KeelDatumMetres;
        var height = Math.Max(HullParameters.MinimumHeight,
            (int)Math.Round(datumDepth, MidpointRounding.AwayFromZero));
        var estimatedCandidateCells = checked((long)length * width * (height * 2L));
        if (length > options.MaxTargetLengthCells || width > options.MaxTargetWidthCells ||
            height > options.MaxTargetHeightCells || estimatedCandidateCells > options.MaxCandidateVoxelCells)
        {
            diagnostics.Add(DesignDiagnostic.Error(HistoricalDiagnosticCodes.InputLimitExceeded,
                $"Rounded source dimensions {length}×{width}×{height} require an estimated " +
                $"{estimatedCandidateCells:N0} candidate cells; the configured fit cap is " +
                $"{options.MaxCandidateVoxelCells:N0}."));
            return Failed(diagnostics);
        }

        var initial = template with
        {
            Length = length,
            Width = width,
            Height = height,
            Shape = template.EffectiveShape,
            HasBulb = options.IncludeBulb && template.HasBulb,
        };
        var evaluations = 0;
        var best = Evaluate(source, metadata, initial, options, cancellationToken);
        evaluations++;

        // Coarse then fine coordinate descent. Every neighbor list is in a fixed order and ties
        // retain the earlier candidate, so hash-map order and worker count cannot affect a fit.
        foreach (var (controlStep, regionStep, bulbStep) in new[] { (0.45, 20, 10), (0.15, 5, 2) })
        {
            var improved = true;
            while (improved && evaluations < options.MaxEvaluations)
            {
                improved = false;
                foreach (var neighbor in Neighbors(best.Parameters, controlStep, regionStep,
                             bulbStep, options.IncludeBulb))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (evaluations >= options.MaxEvaluations)
                        break;
                    var scored = Evaluate(source, metadata, neighbor, options, cancellationToken);
                    evaluations++;
                    if (scored.Score + 1e-12 < best.Score)
                    {
                        best = scored;
                        improved = true;
                    }
                }
            }
        }

        if (evaluations >= options.MaxEvaluations)
            diagnostics.Add(DesignDiagnostic.Warning(HistoricalDiagnosticCodes.FitBudgetExceeded,
                $"Shape V2 fitting stopped at its deterministic {options.MaxEvaluations}-evaluation budget."));

        if (best.SampleCount == 0 || !double.IsFinite(best.Score))
        {
            diagnostics.Add(DesignDiagnostic.Error(HistoricalDiagnosticCodes.FitBudgetExceeded,
                "No valid Shape V2 candidate could be evaluated from the supplied parameter template."));
            return Failed(diagnostics);
        }

        var sourceStatus = best.SourceMax <= options.SourceFitAcceptanceMetres
            ? HistoricalFidelityStageStatus.Passed : HistoricalFidelityStageStatus.Failed;
        var voxelStatus = best.VoxelMax <= options.VoxelAcceptanceMetres
            ? HistoricalFidelityStageStatus.Passed : HistoricalFidelityStageStatus.Failed;
        var sourceMetric = new HistoricalFidelityMetric(sourceStatus, best.SourceMax, best.SourceMean,
            best.SourceRms,
            best.SampleCount, options.SourceFitAcceptanceMetres,
            "Direct continuous source boundary versus Shape V2 voxel boundary");
        var voxelMetric = new HistoricalFidelityMetric(voxelStatus, best.VoxelMax, best.VoxelMean,
            best.VoxelRms,
            best.SampleCount, options.VoxelAcceptanceMetres,
            "Continuous source boundary versus nearest one-metre representable boundary");
        return new ShapeV2FitResult(best.Parameters, sourceMetric, voxelMetric, evaluations,
            Array.AsReadOnly(diagnostics.ToArray()));
    }

    private static List<DesignDiagnostic> ValidateOptions(SampledHullEnvelope source, ShapeV2FitOptions options)
    {
        var diagnostics = new List<DesignDiagnostic>();
        if (options.MaxEvaluations is < 1 or > 4_096 || options.MaxTargetLengthCells is < 3 or > 2_048 ||
            options.MaxTargetWidthCells is < 1 or > 1_024 || options.MaxTargetHeightCells is < 2 or > 512 ||
            options.MaxCandidateVoxelCells is < 1 or > 64_000_000 ||
            !double.IsFinite(options.SourceFitAcceptanceMetres) || options.SourceFitAcceptanceMetres < 0 ||
            options.SourceFitAcceptanceMetres > 1 ||
            !double.IsFinite(options.VoxelAcceptanceMetres) || options.VoxelAcceptanceMetres < 0 ||
            options.VoxelAcceptanceMetres > HistoricalFidelityReport.MaximumVoxelAcceptanceMetres ||
            (long)source.StationCount * source.RowCount > 131_072)
        {
            diagnostics.Add(DesignDiagnostic.Error(HistoricalDiagnosticCodes.InputLimitExceeded,
                "Shape V2 fitting options or source grid exceed the supported safety bounds."));
        }
        return diagnostics;
    }

    private static Evaluation Evaluate(
        SampledHullEnvelope source,
        HistoricalEnvelopeMetadata metadata,
        HullParameters parameters,
        ShapeV2FitOptions options,
        CancellationToken cancellationToken)
    {
        RegionalShapeV2EnvelopeSource candidate;
        try
        {
            candidate = RegionalShapeV2EnvelopeSource.Sample(
                parameters, source.StationCount, source.RowCount, cancellationToken);
        }
        catch (HullGenerationException)
        {
            return Evaluation.Rejected(parameters);
        }

        var sourceSquared = 0d;
        var voxelSquared = 0d;
        var sourceTotal = 0d;
        var voxelTotal = 0d;
        var sourceMax = 0d;
        var voxelMax = 0d;
        var count = 0;

        Add(Math.Abs(candidate.Bounds.Length - source.Bounds.Length),
            Math.Abs(source.Bounds.Length - parameters.Length));
        Add(Math.Abs(candidate.Bounds.Beam - source.Bounds.Beam),
            Math.Abs(source.Bounds.Beam - parameters.Width));
        Add(Math.Abs(parameters.Height - (metadata.DeckDatumMetres - metadata.KeelDatumMetres)),
            Math.Abs((metadata.DeckDatumMetres - metadata.KeelDatumMetres) - parameters.Height));

        for (var z = 0; z < source.StationCount; z++)
        for (var y = 0; y < source.RowCount; y++)
        {
            if ((y & 31) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            var sourceInterval = source.GetNormalizedInterval(z, y);
            var sourceY = source.Bounds.MinY + source.GetNormalizedY(y) * source.Bounds.Depth;
            var candidateY = sourceY - metadata.KeelDatumMetres - 0.5;
            var candidateNormalizedY = (candidateY - candidate.Bounds.MinY) / candidate.Bounds.Depth;
            var candidateStatus = candidate.QueryNormalized(
                source.GetNormalizedZ(z), candidateNormalizedY, out var candidateMetres);
            if (sourceInterval is null && candidateStatus == HullEnvelopeQueryStatus.Empty)
                continue;

            if (sourceInterval is null || candidateStatus != HullEnvelopeQueryStatus.Available)
            {
                var penalty = parameters.Width / 2.0;
                Add(penalty, 0);
                continue;
            }

            var sourceMinRelative = (sourceInterval.Value.MinX - 0.5) * source.Bounds.Beam;
            var sourceMaxRelative = (sourceInterval.Value.MaxX - 0.5) * source.Bounds.Beam;
            var quantizedMin = QuantizeBoundary(sourceMinRelative, parameters.Width);
            var quantizedMax = QuantizeBoundary(sourceMaxRelative, parameters.Width);
            var candidateCenter = (candidate.Bounds.MinX + candidate.Bounds.MaxX) / 2;
            Add(Math.Abs((candidateMetres.MinX - candidateCenter) - sourceMinRelative),
                Math.Abs(sourceMinRelative - quantizedMin));
            Add(Math.Abs((candidateMetres.MaxX - candidateCenter) - sourceMaxRelative),
                Math.Abs(sourceMaxRelative - quantizedMax));
        }

        // Pin the declared waterline explicitly rather than hoping a cell-centred authoring row
        // happens to land on it. This binds all three source datums into the fit.
        var sourceWaterlineY = (metadata.WaterlineMetres - source.Bounds.MinY) / source.Bounds.Depth;
        var candidateWaterlineY = (metadata.WaterlineMetres - metadata.KeelDatumMetres - 0.5 -
                                   candidate.Bounds.MinY) / candidate.Bounds.Depth;
        for (var z = 0; z < source.StationCount; z++)
        {
            var normalizedZ = source.GetNormalizedZ(z);
            var sourceStatus = source.QueryNormalized(normalizedZ, sourceWaterlineY, out var sourceWaterline);
            var candidateStatus = candidate.QueryNormalized(normalizedZ, candidateWaterlineY,
                out var candidateWaterline);
            if (sourceStatus == HullEnvelopeQueryStatus.Empty && candidateStatus == HullEnvelopeQueryStatus.Empty)
                continue;
            if (sourceStatus != HullEnvelopeQueryStatus.Available ||
                candidateStatus != HullEnvelopeQueryStatus.Available)
            {
                Add(parameters.Width / 2.0, 0);
                continue;
            }
            var sourceCenter = (source.Bounds.MinX + source.Bounds.MaxX) / 2;
            var candidateCenter = (candidate.Bounds.MinX + candidate.Bounds.MaxX) / 2;
            foreach (var (sourceBoundary, candidateBoundary) in new[]
                     {
                         (sourceWaterline.MinX - sourceCenter, candidateWaterline.MinX - candidateCenter),
                         (sourceWaterline.MaxX - sourceCenter, candidateWaterline.MaxX - candidateCenter),
                     })
            {
                var quantized = QuantizeBoundary(sourceBoundary, parameters.Width);
                Add(Math.Abs(candidateBoundary - sourceBoundary), Math.Abs(quantized - sourceBoundary));
            }
        }

        if (count == 0)
            return Evaluation.Rejected(parameters);
        var sourceRms = Math.Sqrt(sourceSquared / count);
        var voxelRms = Math.Sqrt(voxelSquared / count);
        var sourceMean = sourceTotal / count;
        var voxelMean = voxelTotal / count;
        return new Evaluation(parameters, sourceMax + sourceRms, sourceMax, sourceMean,
            sourceRms, voxelMax, voxelMean, voxelRms, count);

        void Add(double sourceError, double voxelError)
        {
            sourceMax = Math.Max(sourceMax, sourceError);
            voxelMax = Math.Max(voxelMax, voxelError);
            sourceSquared += sourceError * sourceError;
            voxelSquared += voxelError * voxelError;
            sourceTotal += sourceError;
            voxelTotal += voxelError;
            count++;
        }
    }

    private static double QuantizeBoundary(double sourceRelative, int width)
    {
        var firstBoundary = -width / 2.0;
        return firstBoundary + Math.Round(sourceRelative - firstBoundary, MidpointRounding.ToEven);
    }

    private static IEnumerable<HullParameters> Neighbors(
        HullParameters value, double step, int regionStep, int bulbStep, bool includeBulb)
    {
        var shape = value.EffectiveShape;
        foreach (var style in Enum.GetValues<BowStyle>())
            if (style != value.BowStyle) yield return value with { BowStyle = style };
        foreach (var style in Enum.GetValues<SternStyle>())
            if (style != value.SternStyle) yield return value with { SternStyle = style };
        foreach (var style in Enum.GetValues<BodyStyle>().Where(style => style != BodyStyle.Custom))
            if (style != shape.Body.Style) yield return WithShape(value, shape with { Body = BodyShapeSettings.ForStyle(style) });

        foreach (var delta in new[] { -step, step })
        {
            yield return WithShape(value, shape with { Bow = shape.Bow with { Fullness = Control(shape.Bow.Fullness + delta) } });
            yield return WithShape(value, shape with { Bow = shape.Bow with { Flare = Control(shape.Bow.Flare + delta) } });
            yield return WithShape(value, shape with { Body = shape.Body with { Style = BodyStyle.Custom, Fullness = Control(shape.Body.Fullness + delta) } });
            yield return WithShape(value, shape with { Body = shape.Body with { Style = BodyStyle.Custom, SideShape = Control(shape.Body.SideShape + delta) } });
            yield return WithShape(value, shape with { Body = shape.Body with { Style = BodyStyle.Custom, Chine = Control(shape.Body.Chine + delta) } });
            yield return WithShape(value, shape with { Body = shape.Body with { Style = BodyStyle.Custom, FlatBottom = Math.Clamp(shape.Body.FlatBottom + delta, 0, 1) } });
            yield return WithShape(value, shape with { Stern = shape.Stern with { Fullness = Control(shape.Stern.Fullness + delta) } });
            yield return WithShape(value, shape with { Stern = shape.Stern with { SideShape = Control(shape.Stern.SideShape + delta) } });
        }
        foreach (var delta in new[] { -regionStep, regionStep })
        {
            yield return WithShape(value, shape with { Bow = shape.Bow with { EntranceLengthPercent = Region(shape.Bow.EntranceLengthPercent + delta) } });
            yield return WithShape(value, shape with { Stern = shape.Stern with { RunLengthPercent = Region(shape.Stern.RunLengthPercent + delta) } });
        }
        foreach (var delta in new[] { -1, 1 })
        {
            var maximumRise = Math.Max(0, value.Height - 2);
            yield return WithShape(value, shape with { Profile = shape.Profile with
                { BowDeckRise = Math.Clamp(shape.Profile.BowDeckRise + delta, 0, maximumRise) } });
            yield return WithShape(value, shape with { Profile = shape.Profile with
                { SternDeckRise = Math.Clamp(shape.Profile.SternDeckRise + delta, 0, maximumRise) } });
            yield return WithShape(value, shape with { Profile = shape.Profile with
                { BowKeelRise = Math.Clamp(shape.Profile.BowKeelRise + delta, 0, maximumRise) } });
            yield return WithShape(value, shape with { Profile = shape.Profile with
                { SternKeelRise = Math.Clamp(shape.Profile.SternKeelRise + delta, 0, maximumRise) } });
        }
        if (includeBulb)
        {
            yield return value with { HasBulb = !value.HasBulb,
                Bulb = value.HasBulb ? value.Bulb : BulbSettings.Default };
            if (value.HasBulb)
            {
                var bulb = value.EffectiveBulb;
                foreach (var delta in new[] { -bulbStep, bulbStep })
                {
                    yield return value with { Bulb = bulb with { LengthPercent = Math.Clamp(
                        bulb.LengthPercent + delta, BulbSettings.MinimumLengthPercent, BulbSettings.MaximumLengthPercent) } };
                    yield return value with { Bulb = bulb with { WidthPercent = Math.Clamp(
                        bulb.WidthPercent + delta, BulbSettings.MinimumWidthPercent, BulbSettings.MaximumWidthPercent) } };
                    yield return value with { Bulb = bulb with { ForeAftPercent = Math.Clamp(
                        bulb.ForeAftPercent + delta, BulbSettings.MinimumForeAftPercent, BulbSettings.MaximumForeAftPercent) } };
                    yield return value with { Bulb = bulb with { RisePercent = Math.Clamp(
                        bulb.RisePercent + delta, BulbSettings.MinimumRisePercent, BulbSettings.MaximumRisePercent) } };
                }
            }
        }
    }

    private static HullParameters WithShape(HullParameters value, HullShapeSettings shape) => value with
    {
        Shape = shape,
        BowFullness = shape.Bow.Fullness,
        SternFullness = shape.Stern.Fullness,
        CrossSectionCurve = shape.Body.Fullness,
    };

    private static double Control(double value) => Math.Clamp(value,
        HullShapeSettings.MinimumControl, HullShapeSettings.MaximumControl);
    private static int Region(int value) => Math.Clamp(value,
        HullShapeSettings.MinimumRegionLengthPercent, HullShapeSettings.MaximumRegionLengthPercent);

    private static ShapeV2FitResult Failed(IReadOnlyList<DesignDiagnostic> diagnostics) => new(
        null,
        HistoricalFidelityMetric.NotMeasured("Shape V2 source fit"),
        HistoricalFidelityMetric.NotMeasured("Voxel quantization"),
        0,
        Array.AsReadOnly(diagnostics.ToArray()));

    private sealed record Evaluation(
        HullParameters Parameters,
        double Score,
        double SourceMax,
        double SourceMean,
        double SourceRms,
        double VoxelMax,
        double VoxelMean,
        double VoxelRms,
        int SampleCount)
    {
        public static Evaluation Rejected(HullParameters parameters) =>
            new(parameters, double.PositiveInfinity, double.PositiveInfinity,
                double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity,
                double.PositiveInfinity, double.PositiveInfinity, 0);
    }
}
