using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Design;
using FtdHullGenerator.Domain.Historical;

namespace FtdHullGenerator.Geometry.HullSources;

public sealed record HistoricalEnvelopeAssetRow(
    double NormalizedZ,
    double NormalizedY,
    double? NormalizedMinX,
    double? NormalizedMaxX);

/// <summary>
/// Distributable derived asset. It contains normalized row samples, metadata and validation
/// summaries only; source vertices, faces and source-file paths are deliberately absent.
/// </summary>
public sealed record HistoricalEnvelopeAsset(
    int SchemaVersion,
    string AssetContentHash,
    HistoricalEnvelopeMetadata Metadata,
    HullEnvelopeBounds SourceBoundsMetres,
    IReadOnlyList<HistoricalEnvelopeAssetRow> Rows,
    HistoricalFidelityReport Fidelity)
{
    public const int CurrentSchemaVersion = 2;

    internal static JsonSerializerOptions JsonOptions(bool indented = false)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = indented,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public static HistoricalEnvelopeAsset CreateForPackaging(
        HistoricalEnvelopeMetadata metadata,
        SampledHullEnvelope envelope,
        HistoricalFidelityReport fidelity)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(fidelity);
        var diagnostics = metadata.Validate().Concat(fidelity.Validate()).ToList();
        if (fidelity.Representation != HistoricalHullRepresentation.SampledEnvelopeFallback)
            diagnostics.Add(DesignDiagnostic.Error(HistoricalDiagnosticCodes.FidelityInvalid,
                "Shape V2 fit reports cannot be packaged until fitted parameters are serialized and runtime-bound."));
        if (!string.Equals(metadata.SourceContentHash, envelope.SourceContentHash,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(fidelity.SourceContentHash, envelope.SourceContentHash,
                StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(DesignDiagnostic.Error(HistoricalDiagnosticCodes.MetadataInvalid,
                "Metadata, fidelity report and sampled envelope must identify the same source content hash."));
        }
        if (envelope.SourceTransform is null || metadata.SourceTransform != envelope.SourceTransform)
        {
            diagnostics.Add(DesignDiagnostic.Error(HistoricalDiagnosticCodes.MetadataInvalid,
                "Packaged numeric scale and axis mapping must equal the transform actually used for sampling."));
        }
        if (metadata.KeelDatumMetres < envelope.Bounds.MinY - 1e-9 ||
            metadata.DeckDatumMetres > envelope.Bounds.MaxY + 1e-9 ||
            metadata.WaterlineMetres < metadata.KeelDatumMetres ||
            metadata.WaterlineMetres > metadata.DeckDatumMetres)
        {
            diagnostics.Add(DesignDiagnostic.Error(HistoricalDiagnosticCodes.MetadataInvalid,
                "Keel, waterline and deck datums must be ordered and lie inside the mapped source hull bounds."));
        }
        if (!envelope.IsUsable)
            diagnostics.Add(DesignDiagnostic.Error(HistoricalDiagnosticCodes.UnsupportedRowIntervals,
                "An invalid or multi-interval sampled envelope cannot be packaged."));
        if (!fidelity.CanUseNamedPreset)
            diagnostics.Add(DesignDiagnostic.Error(HistoricalDiagnosticCodes.PackagingEvidenceInvalid,
                "The derived envelope is not eligible for a named preset: geometry, smoothing, dimensions " +
                "and rights must each be independently verified."));
        if (diagnostics.Any(diagnostic => diagnostic.IsError))
            throw new InvalidOperationException(string.Join(Environment.NewLine, diagnostics));

        var rows = new List<HistoricalEnvelopeAssetRow>(envelope.StationCount * envelope.RowCount);
        for (var z = 0; z < envelope.StationCount; z++)
        for (var y = 0; y < envelope.RowCount; y++)
        {
            var interval = envelope.GetNormalizedInterval(z, y);
            rows.Add(new HistoricalEnvelopeAssetRow(
                envelope.GetNormalizedZ(z), envelope.GetNormalizedY(y),
                interval?.MinX, interval?.MaxX));
        }

        var readOnlyRows = Array.AsReadOnly(rows.ToArray());
        var contentHash = ComputeContentHash(metadata, envelope.Bounds, readOnlyRows, fidelity);
        return new HistoricalEnvelopeAsset(CurrentSchemaVersion, contentHash, metadata, envelope.Bounds,
            readOnlyRows, fidelity);
    }

    public string ToJson(bool indented = true) => JsonSerializer.Serialize(this, JsonOptions(indented));

    internal static string ComputeContentHash(
        HistoricalEnvelopeMetadata metadata,
        HullEnvelopeBounds bounds,
        IReadOnlyList<HistoricalEnvelopeAssetRow> rows,
        HistoricalFidelityReport fidelity)
    {
        var canonical = JsonSerializer.Serialize(new { metadata, bounds, rows, fidelity }, JsonOptions());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}

/// <summary>A validated package plus its immutable runtime envelope adapter.</summary>
public sealed class LoadedHistoricalEnvelopeAsset
{
    internal LoadedHistoricalEnvelopeAsset(HistoricalEnvelopeAsset asset, SampledHullEnvelope source)
    {
        Asset = asset;
        Source = source;
    }

    public HistoricalEnvelopeAsset Asset { get; }
    public IReadOnlyHullEnvelopeSource Source { get; }
    internal SampledHullEnvelope SampledSource => (SampledHullEnvelope)Source;
}

public sealed record HistoricalEnvelopeAssetLoadResult(
    LoadedHistoricalEnvelopeAsset? Loaded,
    IReadOnlyList<DesignDiagnostic> Diagnostics)
{
    public bool Success => Loaded is not null && !Diagnostics.Any(item => item.IsError);
}

/// <summary>Bounded, versioned package loader. Invalid assets never produce a runtime source.</summary>
public static class HistoricalEnvelopeAssetLoader
{
    public const int DefaultMaxAssetBytes = 16 * 1024 * 1024;
    public const int MaxRows = 131_072;

    public static HistoricalEnvelopeAssetLoadResult Load(
        string path,
        int maxBytes = DefaultMaxAssetBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        if (maxBytes is < 1 or > 256 * 1024 * 1024)
            return Failure("Historical asset byte limit is outside the supported range.");

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.SequentialScan);
            if (stream.Length is < 1 || stream.Length > maxBytes)
                return Failure($"Historical asset is {stream.Length} bytes; limit is {maxBytes} bytes.");
            var asset = JsonSerializer.DeserializeAsync<HistoricalEnvelopeAsset>(
                    stream, HistoricalEnvelopeAsset.JsonOptions(), cancellationToken)
                .AsTask().GetAwaiter().GetResult();
            cancellationToken.ThrowIfCancellationRequested();
            if (stream.Length > maxBytes)
                return Failure("Historical asset grew beyond its configured byte limit while loading.");
            return asset is null ? Failure("Historical asset JSON is empty.") : Validate(asset);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return Failure($"Historical asset could not be loaded: {exception.Message}");
        }
    }

    public static HistoricalEnvelopeAssetLoadResult Validate(HistoricalEnvelopeAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var diagnostics = new List<DesignDiagnostic>();
        if (asset.SchemaVersion != HistoricalEnvelopeAsset.CurrentSchemaVersion)
            diagnostics.Add(Error($"Historical envelope schema {asset.SchemaVersion} is unsupported."));
        if (string.IsNullOrWhiteSpace(asset.AssetContentHash) || asset.Metadata is null ||
            asset.Fidelity is null || asset.Rows is null ||
            !asset.SourceBoundsMetres.IsValid)
            diagnostics.Add(Error("Historical envelope package is structurally incomplete."));
        if (diagnostics.HasErrors())
            return new HistoricalEnvelopeAssetLoadResult(null, diagnostics.AsReadOnly());

        var metadata = asset.Metadata!;
        var fidelity = asset.Fidelity!;
        var rows = asset.Rows!;
        diagnostics.AddRange(metadata.Validate());
        diagnostics.AddRange(fidelity.Validate());
        if (fidelity.Representation != HistoricalHullRepresentation.SampledEnvelopeFallback)
            diagnostics.Add(Error(
                "Shape V2 fit assets are unsupported until fitted parameters are serialized and runtime-bound."));
        if (diagnostics.HasErrors())
            return new HistoricalEnvelopeAssetLoadResult(null, diagnostics.AsReadOnly());
        if (!fidelity.CanUseNamedPreset)
            diagnostics.Add(Error("Historical envelope package did not pass fidelity and rights gates."));
        if (!string.Equals(metadata.SourceContentHash, fidelity.SourceContentHash,
                StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(Error("Historical package provenance hashes disagree."));
        if (!string.Equals(asset.AssetContentHash,
                HistoricalEnvelopeAsset.ComputeContentHash(metadata, asset.SourceBoundsMetres, rows, fidelity),
                StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(Error("Historical package content hash does not match its normalized envelope data."));
        if (metadata.KeelDatumMetres < asset.SourceBoundsMetres.MinY - 1e-9 ||
            metadata.DeckDatumMetres > asset.SourceBoundsMetres.MaxY + 1e-9 ||
            metadata.WaterlineMetres < metadata.KeelDatumMetres ||
            metadata.WaterlineMetres > metadata.DeckDatumMetres)
            diagnostics.Add(Error("Historical package datums are outside its mapped source bounds."));
        if (rows.Count is < 6 or > MaxRows || rows.OfType<HistoricalEnvelopeAssetRow>().Count() != rows.Count)
            diagnostics.Add(Error($"Historical package row count must be between 6 and {MaxRows}."));
        if (diagnostics.HasErrors())
            return new HistoricalEnvelopeAssetLoadResult(null, diagnostics.AsReadOnly());

        var zValues = rows.Select(row => row.NormalizedZ).Distinct().Order().ToArray();
        var yValues = rows.Select(row => row.NormalizedY).Distinct().Order().ToArray();
        if (zValues.Length < 3 || yValues.Length < 2 ||
            (long)zValues.Length * yValues.Length != rows.Count ||
            (long)zValues.Length * yValues.Length > MaxRows ||
            zValues.Any(value => !double.IsFinite(value) || value is < 0 or > 1) ||
            yValues.Any(value => !double.IsFinite(value) || value is < 0 or > 1))
        {
            diagnostics.Add(Error("Historical package rows do not form one bounded normalized grid."));
            return new HistoricalEnvelopeAssetLoadResult(null, diagnostics.AsReadOnly());
        }

        var zIndex = zValues.Select((value, index) => (value, index)).ToDictionary(item => item.value, item => item.index);
        var yIndex = yValues.Select((value, index) => (value, index)).ToDictionary(item => item.value, item => item.index);
        var intervals = new HullEnvelopeInterval?[zValues.Length, yValues.Length];
        var seen = new HashSet<(int Z, int Y)>();
        foreach (var row in rows)
        {
            if (!seen.Add((zIndex[row.NormalizedZ], yIndex[row.NormalizedY])))
            {
                diagnostics.Add(Error("Historical package contains a duplicated grid row."));
                break;
            }
            if ((row.NormalizedMinX is null) != (row.NormalizedMaxX is null))
            {
                diagnostics.Add(Error("Historical package interval has only one lateral endpoint."));
                break;
            }
            if (row.NormalizedMinX is { } min && row.NormalizedMaxX is { } max)
            {
                if (!double.IsFinite(min) || !double.IsFinite(max) || min < 0 || max > 1 || min > max)
                {
                    diagnostics.Add(Error("Historical package contains an invalid normalized interval."));
                    break;
                }
                intervals[zIndex[row.NormalizedZ], yIndex[row.NormalizedY]] = new HullEnvelopeInterval(min, max);
            }
        }
        if (diagnostics.HasErrors())
            return new HistoricalEnvelopeAssetLoadResult(null, diagnostics.AsReadOnly());

        var source = new SampledHullEnvelope(asset.SourceBoundsMetres, zValues, yValues, intervals,
            [], fidelity.AsymmetryNormalizedRowCount, [], metadata.SourceContentHash,
            metadata.SourceTransform);
        return new HistoricalEnvelopeAssetLoadResult(new LoadedHistoricalEnvelopeAsset(asset, source),
            diagnostics.AsReadOnly());
    }

    private static HistoricalEnvelopeAssetLoadResult Failure(string message) =>
        new(null, Array.AsReadOnly(new[] { Error(message) }));
    private static DesignDiagnostic Error(string message) =>
        DesignDiagnostic.Error(HistoricalDiagnosticCodes.AssetLoadInvalid, message);
}

public static class HistoricalFidelityReports
{
    public static HistoricalFidelityReport FromFit(
        ShapeV2FitResult fit,
        SampledHullEnvelope source,
        HistoricalFidelityMetric nativeSmoothingChange,
        HistoricalPackagingStatus packaging,
        HistoricalAccuracyLabel requestedAccuracy)
    {
        ArgumentNullException.ThrowIfNull(fit);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(nativeSmoothingChange);
        ArgumentNullException.ThrowIfNull(packaging);
        return new HistoricalFidelityReport(
            HistoricalFidelityReport.CurrentSchemaVersion,
            HistoricalHullRepresentation.ShapeV2Fit,
            fit.SourceFit,
            fit.VoxelQuantization,
            nativeSmoothingChange,
            null,
            packaging,
            source.UnsupportedRows.Count,
            source.AsymmetryNormalizedRowCount,
            source.SourceContentHash,
            requestedAccuracy);
    }

    public static HistoricalFidelityReport FromSampledFallback(
        SampledHullEnvelope source,
        HistoricalFidelityMetric directContinuousSourceToCandidate,
        HistoricalFidelityMetric adapterVoxelQuantization,
        HistoricalFallbackValidation fallbackValidation,
        HistoricalFidelityMetric nativeSmoothingChange,
        HistoricalPackagingStatus packaging,
        HistoricalAccuracyLabel requestedAccuracy)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(directContinuousSourceToCandidate);
        ArgumentNullException.ThrowIfNull(adapterVoxelQuantization);
        ArgumentNullException.ThrowIfNull(fallbackValidation);
        ArgumentNullException.ThrowIfNull(nativeSmoothingChange);
        ArgumentNullException.ThrowIfNull(packaging);
        return new HistoricalFidelityReport(HistoricalFidelityReport.CurrentSchemaVersion,
            HistoricalHullRepresentation.SampledEnvelopeFallback,
            directContinuousSourceToCandidate, adapterVoxelQuantization, nativeSmoothingChange,
            fallbackValidation, packaging,
            source.UnsupportedRows.Count, source.AsymmetryNormalizedRowCount,
            source.SourceContentHash, requestedAccuracy);
    }
}
