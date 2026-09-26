using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Design;
using FtdHullGenerator.Domain.Historical;
using FtdHullGenerator.Geometry;
using FtdHullGenerator.Geometry.Composition;
using FtdHullGenerator.Geometry.HullSources;

return Run(args);

static int Run(string[] arguments)
{
    if (arguments.Length != 1)
    {
        Console.Error.WriteLine("Usage: HistoricalAuthoring <request.json>");
        return 2;
    }
    using var cancellation = new CancellationTokenSource();
    ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellation.Cancel();
    };
    Console.CancelKeyPress += cancelHandler;
    try { return RunCore(arguments[0], cancellation.Token); }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("Historical authoring cancelled; no partial output was retained.");
        return 130;
    }
    finally { Console.CancelKeyPress -= cancelHandler; }
}

static int RunCore(string requestFile, CancellationToken cancellationToken)
{
    var jsonOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
    jsonOptions.Converters.Add(new ArmorLayoutJsonConverter());
    jsonOptions.Converters.Add(new JsonStringEnumConverter());

    var requestResult = ReadRequest(requestFile, jsonOptions, cancellationToken);
    if (requestResult.Request is null)
    {
        Console.Error.WriteLine($"Invalid request: {requestResult.Error}");
        return 2;
    }
    var request = requestResult.Request;
    if (string.IsNullOrWhiteSpace(request.SourceObj) || string.IsNullOrWhiteSpace(request.OutputReport) ||
        request.AxisMapping is null || request.Metadata is null || request.Packaging is null ||
        request.Sampling is null || request.Fit is null)
    {
        Console.Error.WriteLine("Invalid request: source, report, axes, metadata and packaging evidence are required.");
        return 2;
    }

    string sourcePath;
    string reportPath;
    string? assetPath;
    try
    {
        var requestDirectory = Path.GetDirectoryName(Path.GetFullPath(requestFile))!;
        var requestIdentity = CanonicalIdentity(Path.GetFullPath(requestFile));
        sourcePath = Path.GetFullPath(request.SourceObj, requestDirectory);
        reportPath = Path.GetFullPath(request.OutputReport, requestDirectory);
        assetPath = string.IsNullOrWhiteSpace(request.OutputAsset)
            ? null : Path.GetFullPath(request.OutputAsset, requestDirectory);
        var sourceIdentity = CanonicalIdentity(sourcePath);
        var reportIdentity = CanonicalIdentity(reportPath);
        var assetIdentity = assetPath is null ? null : CanonicalIdentity(assetPath);
        if (SamePath(requestIdentity, sourceIdentity) || SamePath(requestIdentity, reportIdentity) ||
            assetIdentity is not null && SamePath(requestIdentity, assetIdentity) ||
            SamePath(sourceIdentity, reportIdentity) ||
            assetIdentity is not null && (SamePath(sourceIdentity, assetIdentity) ||
                                          SamePath(reportIdentity, assetIdentity)))
            throw new InvalidDataException(
                "The request, sourceObj, outputReport and outputAsset must be distinct after resolving links.");
    }
    catch (Exception exception) when (exception is ArgumentException or NotSupportedException or
                                      PathTooLongException or IOException or UnauthorizedAccessException or
                                      InvalidDataException)
    {
        Console.Error.WriteLine($"Invalid request path: {exception.Message}");
        return 2;
    }

    var import = HistoricalObjImporter.Import(sourcePath, new HistoricalObjImportOptions(
        request.MetresPerSourceUnit, request.AxisMapping,
        new HashSet<string>(request.IncludedHullGroups ?? [], StringComparer.Ordinal)),
        cancellationToken);
    if (!import.Success)
        return WriteOutcome(new HistoricalAuthoringReport(
            HistoricalAuthoringReport.CurrentSchemaVersion,
            HistoricalAuthoringOutcome.InvalidInput, "import", null, null, null, null, null, null,
            null, Messages(import.Diagnostics)));

    var actualMetadata = request.Metadata with
    {
        SourceContentHash = import.Mesh!.SourceContentHash,
        SourceTransform = import.Mesh.SourceTransform,
    };
    var sampled = HistoricalEnvelopeSampler.Sample(import.Mesh, request.Sampling, cancellationToken);
    if (!sampled.Success)
    {
        var unsupported = sampled.Diagnostics.Any(diagnostic =>
            diagnostic.Code == HistoricalDiagnosticCodes.UnsupportedRowIntervals);
        return WriteOutcome(new HistoricalAuthoringReport(
            HistoricalAuthoringReport.CurrentSchemaVersion,
            unsupported
                ? HistoricalAuthoringOutcome.UnsupportedByCurrentEnvelopeRepresentation
                : HistoricalAuthoringOutcome.InvalidInput,
            "sampling", actualMetadata, import.Mesh.SourceContentHash, Topology(import.Topology),
            null, null, null, null, Messages(sampled.Diagnostics)));
    }

    var fit = ShapeV2HullSourceFitter.Fit(sampled.Envelope!, actualMetadata,
        options: request.Fit, cancellationToken: cancellationToken);
    var nativeSmoothing = request.NativeSmoothingChange ?? HistoricalFidelityMetric.NotMeasured(
        "Native smoothing change", "No independent native smoothing comparison was supplied.");
    HistoricalEnvelopeCompositionEvaluation? fallback = null;
    HistoricalFidelityReport? fidelity = null;
    if (!fit.Accepted && request.SampledFallbackSourceFit is not null)
    {
        if (request.FallbackParameters is null)
            return WriteOutcome(Report(HistoricalAuthoringOutcome.InvalidInput,
                "fallback-composition",
                ["fallbackParameters are required to validate the exact runtime voxel/armor output."]));
        var parameterErrors = request.FallbackParameters.Validate();
        if (parameterErrors.Count != 0)
            return WriteOutcome(Report(HistoricalAuthoringOutcome.InvalidInput,
                "fallback-composition", parameterErrors));
        try
        {
            fallback = HistoricalEnvelopeCompositionAdapter.EvaluateForAuthoring(
                sampled.Envelope!, actualMetadata, request.FallbackParameters,
                request.Fit.VoxelAcceptanceMetres,
                request.Fit.MaxCandidateVoxelCells, cancellationToken);
        }
        catch (HullGenerationException exception)
        {
            return WriteOutcome(Report(
                HistoricalAuthoringOutcome.UnsupportedByCurrentEnvelopeRepresentation,
                "fallback-composition", exception.Errors));
        }
        catch (ArgumentException exception)
        {
            return WriteOutcome(Report(HistoricalAuthoringOutcome.InvalidInput,
                "fallback-composition", [exception.Message]));
        }
        fidelity = HistoricalFidelityReports.FromSampledFallback(sampled.Envelope!,
            request.SampledFallbackSourceFit, fallback.VoxelQuantization, fallback.Validation,
            nativeSmoothing, request.Packaging, request.RequestedAccuracy);
    }
    else
    {
        fidelity = HistoricalFidelityReports.FromFit(fit, sampled.Envelope!, nativeSmoothing,
            request.Packaging, request.RequestedAccuracy);
    }

    var contractErrors = actualMetadata.Validate().Concat(request.Packaging.Validate())
        .Concat(fidelity.Validate()).Where(diagnostic => diagnostic.IsError).ToArray();
    HistoricalAuthoringOutcome outcome;
    IReadOnlyList<string> outcomeDiagnostics;
    if (contractErrors.Length != 0)
    {
        outcome = HistoricalAuthoringOutcome.InvalidInput;
        outcomeDiagnostics = Messages(contractErrors);
    }
    else
    {
        outcome = HistoricalAuthoringOutcomes.ClassifyRepresentability(fit.Accepted, fidelity);
        outcomeDiagnostics = fit.Accepted
            ? ["Shape V2 fit is report-only until every fitted parameter is serialized and runtime-bound."]
            : Messages(fit.Diagnostics);
    }

    var report = Report(outcome, "complete", outcomeDiagnostics);
    var reportExit = WriteOutcome(report);
    if (reportExit == 3) return reportExit;

    var packageableOutcome = outcome is HistoricalAuthoringOutcome.Representable or
        HistoricalAuthoringOutcome.RepresentableWithDocumentedApproximation;
    if (assetPath is not null && packageableOutcome)
    {
        try
        {
            var asset = HistoricalEnvelopeAsset.CreateForPackaging(
                actualMetadata, sampled.Envelope!, fidelity);
            var assetBytes = new UTF8Encoding(false).GetBytes(asset.ToJson());
            if (!TryAtomicWrite(assetPath, assetBytes, cancellationToken, out var assetError))
                throw new IOException(assetError);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or
                                          UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Report written, but the package was not created or replaced: {exception.Message}");
            return 3;
        }
    }

    Console.WriteLine($"Historical authoring report: {reportPath}");
    if (assetPath is not null && packageableOutcome && File.Exists(assetPath))
        Console.WriteLine($"Derived envelope asset: {assetPath}");
    return outcome.ExitCode();

    HistoricalAuthoringReport Report(
        HistoricalAuthoringOutcome reportOutcome,
        string stage,
        IReadOnlyList<string> diagnostics) => new(
        HistoricalAuthoringReport.CurrentSchemaVersion, reportOutcome, stage, actualMetadata,
        import.Mesh.SourceContentHash, Topology(import.Topology),
        new HistoricalSamplingReportSummary(sampled.Envelope!.StationCount,
            sampled.Envelope.RowCount, sampled.Envelope.UnsupportedRows.Count,
            sampled.Envelope.AsymmetryNormalizedRowCount),
        new HistoricalFitReportSummary(fit.Accepted, fit.Parameters, fit.SourceFit,
            fit.VoxelQuantization, fit.Evaluations, Messages(fit.Diagnostics)),
        fallback is null ? null : new HistoricalFallbackReportSummary(
            fallback.VoxelQuantization, fallback.Validation),
        fidelity, diagnostics);

    int WriteOutcome(HistoricalAuthoringReport authoringReport)
    {
        try
        {
            if (!HistoricalAuthoringReportSerializer.WriteAtomic(
                    reportPath, authoringReport, jsonOptions, out var error,
                    cancellationToken: cancellationToken))
            {
                Console.Error.WriteLine(
                    $"Historical authoring stopped at {authoringReport.Stage}; report failed: {error}");
                return 3;
            }
            return authoringReport.Outcome.ExitCode();
        }
        catch (InvalidDataException exception)
        {
            Console.Error.WriteLine($"Historical authoring report was not written: {exception.Message}");
            return 3;
        }
    }

    static HistoricalTopologyReportSummary? Topology(HistoricalTopologySummary? topology) =>
        topology is null ? null : new HistoricalTopologyReportSummary(
            topology.BoundaryEdgeCount, topology.NonManifoldEdgeCount,
            topology.InconsistentWindingEdgeCount, topology.ConnectedComponentCount,
            topology.SignedVolumeCubicMetres);

    static string[] Messages(IEnumerable<DesignDiagnostic> diagnostics) =>
        diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}").ToArray();
}

static (HistoricalAuthoringRequest? Request, string? Error) ReadRequest(
    string path, JsonSerializerOptions options, CancellationToken cancellationToken)
{
    const int maximumBytes = 1024 * 1024;
    try
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.SequentialScan);
        if (stream.Length is < 1 or > maximumBytes)
            return (null, $"request size must be 1..{maximumBytes} bytes.");
        var bytes = new byte[maximumBytes + 1];
        var total = 0;
        while (total < bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(bytes, total, bytes.Length - total);
            if (read == 0) break;
            total += read;
        }
        if (total > maximumBytes || stream.ReadByte() != -1)
            return (null, "request grew beyond its byte limit while reading.");
        var request = JsonSerializer.Deserialize<HistoricalAuthoringRequest>(
            bytes.AsSpan(0, total), options);
        return request is null ? (null, "request JSON is empty.") : (request, null);
    }
    catch (OperationCanceledException) { throw; }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or
                                      ArgumentException or NotSupportedException or PathTooLongException)
    {
        return (null, exception.Message);
    }
}

static bool TryAtomicWrite(
    string path,
    byte[] contents,
    CancellationToken cancellationToken,
    out string? error)
{
    var directory = Path.GetDirectoryName(path)!;
    var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
    try
    {
        Directory.CreateDirectory(directory);
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                   FileShare.None, 64 * 1024, FileOptions.WriteThrough))
        {
            const int chunkBytes = 64 * 1024;
            for (var offset = 0; offset < contents.Length; offset += chunkBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                stream.Write(contents, offset, Math.Min(chunkBytes, contents.Length - offset));
            }
            cancellationToken.ThrowIfCancellationRequested();
            stream.Flush(flushToDisk: true);
        }
        cancellationToken.ThrowIfCancellationRequested();
        File.Move(temporary, path, overwrite: true);
        error = null;
        return true;
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
        error = exception.Message;
        return false;
    }
    finally
    {
        try { if (File.Exists(temporary)) File.Delete(temporary); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}

static string CanonicalIdentity(string path)
{
    var full = Path.GetFullPath(path);
    if (File.Exists(full))
        return new FileInfo(full).ResolveLinkTarget(returnFinalTarget: true)?.FullName ??
               Path.Combine(ResolveDirectory(Path.GetDirectoryName(full)!), Path.GetFileName(full));
    if (Directory.Exists(full))
        return ResolveDirectory(full);
    var resolvedDirectory = ResolveDirectory(Path.GetDirectoryName(full)!);
    return Path.Combine(resolvedDirectory, Path.GetFileName(full));
}

static string ResolveDirectory(string directory)
{
    var full = Path.GetFullPath(directory);
    var root = Path.GetPathRoot(full) ?? throw new DirectoryNotFoundException(directory);
    var current = root;
    foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar,
                 StringSplitOptions.RemoveEmptyEntries))
    {
        current = Path.Combine(current, part);
        if (!Directory.Exists(current))
            continue;
        current = new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? current;
    }
    return current;
}

static bool SamePath(string left, string right) =>
    string.Equals(Path.TrimEndingDirectorySeparator(left), Path.TrimEndingDirectorySeparator(right),
        StringComparison.OrdinalIgnoreCase);

internal sealed record HistoricalAuthoringRequest
{
    public required string SourceObj { get; init; }
    public required string OutputReport { get; init; }
    public string? OutputAsset { get; init; }
    public double MetresPerSourceUnit { get; init; }
    public ObjAxisMapping AxisMapping { get; init; } = ObjAxisMapping.Identity;
    public string[]? IncludedHullGroups { get; init; }
    public HistoricalEnvelopeSamplingOptions Sampling { get; init; } = new();
    public ShapeV2FitOptions Fit { get; init; } = new();
    public required HistoricalEnvelopeMetadata Metadata { get; init; }
    public required HistoricalPackagingStatus Packaging { get; init; }
    public HistoricalFidelityMetric? NativeSmoothingChange { get; init; }
    public HistoricalFidelityMetric? SampledFallbackSourceFit { get; init; }
    public HullParameters? FallbackParameters { get; init; }
    public HistoricalAccuracyLabel RequestedAccuracy { get; init; } = HistoricalAccuracyLabel.Approximate;
}

internal sealed class ArmorLayoutJsonConverter : JsonConverter<ArmorLayout>
{
    public override ArmorLayout Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("layers", out var layers) &&
            !document.RootElement.TryGetProperty("Layers", out layers) ||
            layers.ValueKind != JsonValueKind.Array || layers.GetArrayLength() is < 1 or > 64)
            throw new JsonException("Armor layout must contain 1..64 layers.");
        var parsed = new List<ArmorLayer>(layers.GetArrayLength());
        foreach (var layer in layers.EnumerateArray())
        {
            if (layer.ValueKind != JsonValueKind.Object)
                throw new JsonException("Armor layer must be an object.");
            var material = layer.TryGetProperty("material", out var materialElement) ||
                           layer.TryGetProperty("Material", out materialElement)
                ? materialElement.Deserialize<MaterialKind?>(options)
                : throw new JsonException("Armor layer material is required; null denotes air.");
            var construction = layer.TryGetProperty("construction", out var constructionElement) ||
                               layer.TryGetProperty("Construction", out constructionElement)
                ? constructionElement.Deserialize<ArmorConstruction>(options)
                : ArmorConstruction.Solid;
            parsed.Add(new ArmorLayer(material, construction));
        }
        return new ArmorLayout(parsed);
    }

    public override void Write(
        Utf8JsonWriter writer,
        ArmorLayout value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("layers");
        writer.WriteStartArray();
        foreach (var layer in value.Layers)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("material");
            JsonSerializer.Serialize(writer, layer.Material, options);
            writer.WritePropertyName("construction");
            JsonSerializer.Serialize(writer, layer.Construction, options);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}
