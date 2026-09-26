namespace FtdHullGenerator.UI.Editor;

/// <summary>
/// Rejects late preview results and exposes only a validated result for the editor's current
/// revision. Generation cancellation remains cooperative; revision matching is the final guard.
/// </summary>
public sealed class RevisionPreviewBoundary<T> : IDisposable where T : class
{
    private readonly object _sync = new();
    private CancellationTokenSource? _activeCancellation;
    private long _sequence;
    private long _currentRevision;
    private PreviewSnapshot<T>? _current;

    public void MoveToRevision(long revision)
    {
        lock (_sync)
        {
            if (_currentRevision == revision)
                return;
            _currentRevision = revision;
            _current = null;
            _activeCancellation?.Cancel();
        }
    }

    public PreviewRequest Begin(long revision)
    {
        lock (_sync)
        {
            MoveToRevisionCore(revision);
            _current = null;
            _activeCancellation?.Dispose();
            _activeCancellation = new CancellationTokenSource();
            return new PreviewRequest(revision, ++_sequence, _activeCancellation.Token);
        }
    }

    public bool TryAccept(PreviewRequest request, T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (_sync)
        {
            if (request.CancellationToken.IsCancellationRequested ||
                request.Revision != _currentRevision || request.Sequence != _sequence)
                return false;
            _current = new PreviewSnapshot<T>(request.Revision, value);
            return true;
        }
    }

    public bool TryGetCurrent(long revision, out T? value)
    {
        lock (_sync)
        {
            if (_current is { } snapshot && snapshot.Revision == revision)
            {
                value = snapshot.Value;
                return true;
            }
            value = null;
            return false;
        }
    }

    /// <summary>
    /// Invalidates exportable preview state without changing the document revision. This is used
    /// when machine-local access changes while immutable project intent must remain untouched.
    /// </summary>
    public void InvalidateCurrent()
    {
        lock (_sync)
        {
            _current = null;
            _activeCancellation?.Cancel();
            _sequence++;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _activeCancellation?.Cancel();
            _activeCancellation?.Dispose();
            _activeCancellation = null;
            _current = null;
        }
    }

    private void MoveToRevisionCore(long revision)
    {
        if (_currentRevision == revision)
            return;
        _currentRevision = revision;
        _current = null;
        _activeCancellation?.Cancel();
    }
}

public readonly record struct PreviewRequest(long Revision, long Sequence, CancellationToken CancellationToken);

public sealed record PreviewSnapshot<T>(long Revision, T Value) where T : class;
