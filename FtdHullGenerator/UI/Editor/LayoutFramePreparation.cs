using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Design;
using FtdHullGenerator.Domain.Layout;
using FtdHullGenerator.Geometry.Layout;

namespace FtdHullGenerator.UI.Editor;

/// <summary>
/// A caller-supplied request for a derived layout frame. It binds the hull parameters to the exact
/// immutable revision they were read from; the preparation backend assigns the monotonic job
/// identity itself.
/// </summary>
public sealed record LayoutFrameRequest(
    HullParameters Parameters,
    string DocumentId,
    long Revision,
    string? CatalogIdentity = null,
    CancellationToken CancellationToken = default);

/// <summary>One preparation job: the captured request plus the identity the backend assigned.</summary>
public sealed record LayoutFrameJob(
    HullParameters Parameters,
    string DocumentId,
    long Revision,
    long JobId,
    string? CatalogIdentity,
    CancellationToken CancellationToken);

/// <summary>
/// Background preparation of the derived <see cref="ResolvedLayoutFrame"/>. It captures the source
/// document identity, revision, catalog identity, cancellation token and a monotonically increasing
/// job identity before the first asynchronous preparation await, then re-verifies document,
/// revision and job identity after every await so a superseded or cancelled job never publishes.
/// </summary>
/// <remarks>
/// This backend is deliberately outside the editor session: it never calls
/// <see cref="EditorSession.Commit"/>, never mutates a <c>ShipDocument</c>, never creates an
/// undo/history item and never writes to the document. Resolving a frame is derived work only.
/// The synchronous <see cref="ResolveNow(LayoutFrameRequest)"/> entry point is the first-Add path:
/// it obtains a valid frame for the current revision before any barbette is created, so the editor
/// never starts from <see cref="LayoutDatum.Origin"/> and reinterprets it later.
/// </remarks>
public sealed class LayoutFramePreparation : IDisposable
{
    private readonly Func<LayoutFrameJob, LayoutFrameResolution> _resolver;
    private readonly object _sync = new();
    private CancellationTokenSource? _active;
    private long _sequence;
    private ResolvedLayoutFrame? _current;

    public LayoutFramePreparation()
        : this(job => LayoutFrameResolver.Resolve(job.Parameters, job.DocumentId, job.Revision,
            job.JobId, job.CatalogIdentity, job.CancellationToken))
    {
    }

    /// <summary>Test/extension seam: substitutes the preparation delegate while keeping the identity gate.</summary>
    public LayoutFramePreparation(Func<LayoutFrameJob, LayoutFrameResolution> resolver)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    /// <summary>The identity of the most recently started job, monotonic for the lifetime of this instance.</summary>
    public long CurrentJobId
    {
        get
        {
            lock (_sync)
                return _sequence;
        }
    }

    /// <summary>
    /// Synchronous resolve for first-Add. It supersedes any in-flight background job, resolves the
    /// frame for the requested revision and publishes it only when it is still the current job.
    /// </summary>
    public LayoutFrameResolution ResolveNow(LayoutFrameRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DocumentId);

        var (jobId, token) = BeginJob(request.CancellationToken);
        LayoutFrameResolution resolution;
        try
        {
            resolution = _resolver(new LayoutFrameJob(request.Parameters, request.DocumentId,
                request.Revision, jobId, request.CatalogIdentity, token));
        }
        catch (OperationCanceledException)
        {
            return Cancelled(request.DocumentId);
        }

        return Publish(jobId, request.DocumentId, request.Revision, resolution);
    }

    public LayoutFrameResolution ResolveNow(
        HullParameters parameters,
        string documentId,
        long revision,
        string? catalogIdentity = null,
        CancellationToken cancellationToken = default) =>
        ResolveNow(new LayoutFrameRequest(parameters, documentId, revision, catalogIdentity, cancellationToken));

    /// <summary>
    /// Asynchronous resolve for background preparation. The request identity and job identity are
    /// captured before the first await, and the published frame is re-checked afterwards.
    /// </summary>
    public Task<LayoutFrameResolution> ResolveAsync(LayoutFrameRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DocumentId);

        // Capture everything the background work needs before the first await. Nothing below reads
        // mutable state or the caller's request object again.
        var parameters = request.Parameters;
        var documentId = request.DocumentId;
        var revision = request.Revision;
        var catalogIdentity = request.CatalogIdentity;
        var (jobId, token) = BeginJob(request.CancellationToken);

        return RunAsync();

        async Task<LayoutFrameResolution> RunAsync()
        {
            LayoutFrameResolution resolution;
            try
            {
                resolution = await Task.Run(() => _resolver(new LayoutFrameJob(parameters, documentId,
                    revision, jobId, catalogIdentity, token)), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return Cancelled(documentId);
            }

            // Identity/cancellation verification after the await: a superseded job or a cancelled
            // request must never publish, and a stale frame must never become current.
            if (token.IsCancellationRequested)
                return Cancelled(documentId);
            return Publish(jobId, documentId, revision, resolution);
        }
    }

    public Task<LayoutFrameResolution> ResolveAsync(
        HullParameters parameters,
        string documentId,
        long revision,
        string? catalogIdentity = null,
        CancellationToken cancellationToken = default) =>
        ResolveAsync(new LayoutFrameRequest(parameters, documentId, revision, catalogIdentity, cancellationToken));

    /// <summary>
    /// The published frame only when it still belongs to the given document and revision. Returns
    /// <c>null</c> for a superseded frame, a cancelled job or a previous revision.
    /// </summary>
    public ResolvedLayoutFrame? CurrentFor(string documentId, long revision)
    {
        lock (_sync)
            return _current is { } frame && frame.IsCurrent(documentId, revision) ? frame : null;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            // Supersede any in-flight job so a racing Publish cannot repopulate a disposed instance.
            _sequence++;
            _active?.Cancel();
            _active?.Dispose();
            _active = null;
            _current = null;
        }
    }

    private (long JobId, CancellationToken Token) BeginJob(CancellationToken external)
    {
        lock (_sync)
        {
            _active?.Cancel();
            _active?.Dispose();
            var linked = CancellationTokenSource.CreateLinkedTokenSource(external);
            _active = linked;
            return (++_sequence, linked.Token);
        }
    }

    private LayoutFrameResolution Publish(
        long jobId,
        string documentId,
        long revision,
        LayoutFrameResolution resolution)
    {
        lock (_sync)
        {
            if (jobId != _sequence || _active?.IsCancellationRequested == true)
                return resolution with { Frame = null };
            if (resolution.Frame is not { } frame || !frame.IsCurrent(documentId, revision))
            {
                _current = null;
                return resolution with { Frame = null };
            }

            // A frame for an older revision must never roll a newer published frame backwards:
            // stale preparation cannot overwrite current state. A different document restarts the
            // comparison because its revisions are independent.
            if (_current is { } current &&
                string.Equals(current.SourceDocumentId, documentId, StringComparison.Ordinal) &&
                frame.SourceRevision < current.SourceRevision)
                return resolution with { Frame = null };

            _current = frame;
            return resolution;
        }
    }

    private static LayoutFrameResolution Cancelled(string documentId) =>
        LayoutFrameResolution.Unavailable(new DesignDiagnostic(LayoutFrameDiagnosticCodes.FrameUnavailable,
            DesignSeverity.Info, "Layout frame preparation was cancelled.", documentId));
}
