using FtdHullGenerator.UI.Editor;

namespace FtdHullGenerator.UI.Workspace;

/// <summary>
/// Connects an uncommitted workspace draft to the existing revision-safe preview boundary. Drafts
/// have no committed revision identity, so they invalidate the export-authoritative preview
/// immediately and are never scheduled as export-authoritative work. A dirty draft may still ask
/// its host to schedule a debounced, non-authoritative live preview of the current draft through
/// <c>scheduleDraftPreview</c>; that work never enters the boundary. Apply or Cancel schedules only
/// the then-current committed revision.
/// </summary>
public sealed class WorkspacePreviewCoordinator<T> : IDisposable where T : class
{
    private readonly WorkspaceViewModel _workspace;
    private readonly RevisionPreviewBoundary<T> _boundary;
    private readonly Action _invalidateVisiblePreview;
    private readonly Action<long> _scheduleCommittedRevision;
    private readonly Action? _scheduleDraftPreview;
    private bool _draftInvalidated;

    public WorkspacePreviewCoordinator(
        WorkspaceViewModel workspace,
        RevisionPreviewBoundary<T> boundary,
        Action invalidateVisiblePreview,
        Action<long> scheduleCommittedRevision,
        Action? scheduleDraftPreview = null)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _boundary = boundary ?? throw new ArgumentNullException(nameof(boundary));
        _invalidateVisiblePreview = invalidateVisiblePreview ??
            throw new ArgumentNullException(nameof(invalidateVisiblePreview));
        _scheduleCommittedRevision = scheduleCommittedRevision ??
            throw new ArgumentNullException(nameof(scheduleCommittedRevision));
        _scheduleDraftPreview = scheduleDraftPreview;
        _workspace.DraftChanged += DraftChanged;
    }

    public void Dispose() => _workspace.DraftChanged -= DraftChanged;

    private void DraftChanged(object? sender, WorkspaceDraftChangedEventArgs eventArgs)
    {
        if (eventArgs.Kind == WorkspaceDraftChangeKind.Updated)
        {
            if (!eventArgs.WasDirty)
            {
                if (_draftInvalidated)
                    RestoreCommittedPreview();
                return;
            }
            _boundary.InvalidateCurrent();
            _invalidateVisiblePreview();
            _draftInvalidated = true;
            // The draft is not a committed revision: this only asks for a debounced,
            // non-authoritative preview of the current draft.
            _scheduleDraftPreview?.Invoke();
            return;
        }

        if (!eventArgs.WasDirty)
            return;

        RestoreCommittedPreview();
    }

    private void RestoreCommittedPreview()
    {
        var revision = _workspace.Session.Revision;
        _boundary.MoveToRevision(revision);
        _boundary.InvalidateCurrent();
        _invalidateVisiblePreview();
        _draftInvalidated = false;
        _scheduleCommittedRevision(revision);
    }
}
