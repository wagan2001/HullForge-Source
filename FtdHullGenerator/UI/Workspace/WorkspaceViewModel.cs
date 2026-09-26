using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using FtdHullGenerator.Domain.Design;
using FtdHullGenerator.Domain.Layout;
using FtdHullGenerator.Domain.Projects;
using FtdHullGenerator.Geometry.Composition;
using FtdHullGenerator.Geometry.Layout;
using FtdHullGenerator.UI.Editor;

namespace FtdHullGenerator.UI.Workspace;

public enum WorkspaceApplyResult
{
    NoChanges,
    Applied,
    Conflict,
}

public enum WorkspaceCloseChoice
{
    KeepEditing,
    Apply,
    Discard,
}

public enum WorkspaceDraftChangeKind
{
    Updated,
    Applied,
    Cancelled,
}

public sealed record WorkspaceDraftChangedEventArgs(WorkspaceDraftChangeKind Kind, bool WasDirty);

/// <summary>
/// One window-independent workspace state. Docked and detached hosts bind to this exact instance,
/// so moving the editor never creates another document or another draft transaction.
/// </summary>
public sealed class WorkspaceViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly EditorSession _session;
    private readonly LayoutFramePreparation _layoutFramePreparation;
    private EditorTransaction? _transaction;
    private ShipDocument? _baseDocument;
    private long _baseRevision;
    private bool _hasConflict;
    private bool _isDetached;
    private int _selectedTabIndex;
    private ArrangementSolution? _arrangementSolution;
    private ResolvedLayoutFrame? _layoutFrame;
    private string? _layoutCatalogIdentity;
    private ImmutableArray<DesignDiagnostic> _resolvedGenerationDiagnostics = [];
    private long? _resolvedGenerationRevision;
    private string? _resolvedGenerationDocumentId;
    private string? _resolvedCatalogVersion;
    private string? _resolvedCatalogFingerprint;

    public WorkspaceViewModel(EditorSession session)
        : this(session, new LayoutFramePreparation())
    {
    }

    /// <summary>Test seam: substitutes the one preparation while keeping the workspace's identity gate.</summary>
    internal WorkspaceViewModel(EditorSession session, LayoutFramePreparation layoutFramePreparation)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _layoutFramePreparation = layoutFramePreparation ??
            throw new ArgumentNullException(nameof(layoutFramePreparation));
        _session.Changed += SessionChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? DocumentChanged;

    public event EventHandler<WorkspaceDraftChangedEventArgs>? DraftChanged;

    public EditorSession Session => _session;

    public ShipDocument Document => _transaction?.Document ?? _session.Document;

    public bool HasDraft => _transaction is not null;

    public bool IsDraftDirty => _transaction is not null && _transaction.Document != _baseDocument;

    public bool HasConflict => _hasConflict;

    public bool CanApply => IsDraftDirty && !HasConflict;

    public bool CanCancel => HasDraft;

    public string DraftStatus => HasConflict
        ? "The project changed outside this panel. Cancel this draft and begin again."
        : IsDraftDirty
            ? "Draft changes are not yet part of the project."
            : "Editing the current project revision.";

    public bool IsDetached
    {
        get => _isDetached;
        internal set
        {
            if (_isDetached == value)
                return;
            _isDetached = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HostActionLabel));
        }
    }

    public string HostActionLabel => IsDetached ? "Re-dock" : "Detach";

    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set
        {
            if (_selectedTabIndex == value)
                return;
            _selectedTabIndex = value;
            OnPropertyChanged();
        }
    }

    /// <summary>The already-resolved layout supplied by the application composition boundary.</summary>
    public ArrangementSolution? ArrangementSolution
    {
        get => _arrangementSolution;
        private set
        {
            if (ReferenceEquals(_arrangementSolution, value))
                return;
            _arrangementSolution = value;
            OnPropertyChanged();
        }
    }

    public void SetArrangementSolution(ArrangementSolution? solution) => ArrangementSolution = solution;

    /// <summary>
    /// The one application-owned derived-frame preparation. Resolving a frame is derived work: it
    /// never commits an editor transaction, never creates an undo item and never writes the document.
    /// </summary>
    public LayoutFramePreparation LayoutFramePreparation => _layoutFramePreparation;

    /// <summary>
    /// The currently published derived frame, or <c>null</c>. This is identity-gated derived state
    /// for the committed document/revision/catalog; it is deliberately not editor history.
    /// </summary>
    public ResolvedLayoutFrame? LayoutFrame => _layoutFrame;

    /// <summary>
    /// The catalog identity a frame must bind to. <see cref="MainWindow" /> sets it when it adopts
    /// the installed catalog; changing it invalidates the published frame.
    /// </summary>
    public string? LayoutCatalogIdentity
    {
        get => _layoutCatalogIdentity;
        set
        {
            if (string.Equals(_layoutCatalogIdentity, value, StringComparison.Ordinal))
                return;
            _layoutCatalogIdentity = value;
            ClearLayoutFrame();
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Publishes a derived frame only when it is current for the committed document/revision and
    /// binds the current catalog identity. A <c>null</c> frame clears the published frame; a
    /// non-current or wrong-catalog frame is rejected and never overwrites the current one.
    /// </summary>
    public bool TryPublishLayoutFrame(ResolvedLayoutFrame? frame)
    {
        if (frame is null)
        {
            ClearLayoutFrame();
            return false;
        }

        if (!frame.IsCurrent(_session.Document.DocumentId, _session.Revision) ||
            !string.Equals(frame.CatalogIdentity, _layoutCatalogIdentity, StringComparison.Ordinal))
            return false;

        if (_layoutFrame == frame)
            return true;
        _layoutFrame = frame;
        OnPropertyChanged(nameof(LayoutFrame));
        return true;
    }

    /// <summary>Drops the published derived frame; a no-op when none is published.</summary>
    public void ClearLayoutFrame()
    {
        if (_layoutFrame is null)
            return;
        _layoutFrame = null;
        OnPropertyChanged(nameof(LayoutFrame));
    }

    /// <summary>
    /// Resolves the frame for the current committed revision synchronously and publishes it through
    /// the one preparation. This is the first-Add path: it must return before any component is
    /// created so the editor never starts from <see cref="LayoutDatum.Origin" />.
    /// </summary>
    public LayoutFrameResolution ResolveLayoutFrameNow(CancellationToken cancellationToken = default)
    {
        var document = _session.Document;
        var resolution = _layoutFramePreparation.ResolveNow(document.Hull, document.DocumentId,
            _session.Revision, _layoutCatalogIdentity, cancellationToken);
        TryPublishLayoutFrame(resolution.Frame);
        return resolution;
    }

    /// <summary>Background frame preparation for the current committed revision.</summary>
    public Task<LayoutFrameResolution> ResolveLayoutFrameAsync(CancellationToken cancellationToken = default) =>
        ResolveLayoutFrameAsync(_layoutCatalogIdentity, cancellationToken);

    /// <summary>
    /// Background frame preparation. The job identity is captured before the first await, and a
    /// superseded job, a revision that moved on or a different document is rejected rather than
    /// clearing a newer published frame. A current job that fails to resolve also leaves any
    /// published frame untouched: only a revision/document/catalog change or a hull-changing draft
    /// invalidates a frame, so a failed prepare can never wipe a frame that is still current.
    /// </summary>
    public async Task<LayoutFrameResolution> ResolveLayoutFrameAsync(string? catalogIdentity,
        CancellationToken cancellationToken = default)
    {
        var document = _session.Document;
        var documentId = document.DocumentId;
        var revision = _session.Revision;
        var task = _layoutFramePreparation.ResolveAsync(document.Hull, documentId, revision,
            catalogIdentity, cancellationToken);
        var jobId = _layoutFramePreparation.CurrentJobId;
        var resolution = await task;
        if (_layoutFramePreparation.CurrentJobId != jobId ||
            _session.Revision != revision ||
            !string.Equals(_session.Document.DocumentId, documentId, StringComparison.Ordinal))
            return resolution;
        if (resolution.Frame is { } frame)
            TryPublishLayoutFrame(frame);
        return resolution;
    }

    /// <summary>
    /// The findings of the last resolved generation accepted for the current committed revision.
    /// Read-only: publication only ever succeeds for <see cref="EditorSession.Revision" /> and the
    /// current document, and a revision change, document change or dirty draft clears them. This is
    /// deliberately separate from the ruler's live placement diagnostics.
    /// </summary>
    public IReadOnlyList<DesignDiagnostic> ResolvedGenerationDiagnostics =>
        _resolvedGenerationDiagnostics;

    public long? ResolvedGenerationRevision => _resolvedGenerationRevision;

    public string? ResolvedCatalogVersion => _resolvedCatalogVersion;

    public string? ResolvedCatalogFingerprint => _resolvedCatalogFingerprint;

    public bool HasResolvedGeneration => _resolvedGenerationRevision == _session.Revision &&
        _resolvedGenerationDocumentId == _session.Document.DocumentId;

    /// <summary>
    /// Accepts the diagnostics of one resolved snapshot only when it describes the current
    /// committed revision and document and no draft shadows it. A stale publication is rejected
    /// rather than replacing the current findings.
    /// </summary>
    public bool PublishResolvedGeneration(ShipGenerationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return PublishResolvedGeneration(snapshot.Revision, snapshot.Document.DocumentId,
            snapshot.Diagnostics, snapshot.Hull.ResolvedCatalogVersion,
            snapshot.Hull.ResolvedCatalogFingerprint);
    }

    internal bool PublishResolvedGeneration(
        long revision,
        string documentId,
        IEnumerable<DesignDiagnostic> diagnostics,
        string? catalogVersion,
        string? catalogFingerprint)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (IsDraftDirty || revision != _session.Revision ||
            !string.Equals(documentId, _session.Document.DocumentId, StringComparison.Ordinal))
            return false;

        _resolvedGenerationDiagnostics = diagnostics.ToImmutableArray();
        _resolvedGenerationRevision = revision;
        _resolvedGenerationDocumentId = documentId;
        _resolvedCatalogVersion = catalogVersion;
        _resolvedCatalogFingerprint = catalogFingerprint;
        RaiseResolvedGeneration();
        return true;
    }

    /// <summary>
    /// Drops the published resolved-generation findings and their catalog identity. The derived
    /// layout frame is deliberately NOT cleared here: it is hull-derived and stays valid for an
    /// arrangement-only draft, so the ruler, arrangement solve and generation keep consuming the
    /// same frame. A hull-changing draft invalidates it in <see cref="UpdateDraft"/>.
    /// </summary>
    public void ClearResolvedGeneration()
    {
        if (_resolvedGenerationRevision is null && _resolvedGenerationDiagnostics.IsEmpty &&
            _resolvedCatalogVersion is null && _resolvedCatalogFingerprint is null)
            return;
        _resolvedGenerationDiagnostics = [];
        _resolvedGenerationRevision = null;
        _resolvedGenerationDocumentId = null;
        _resolvedCatalogVersion = null;
        _resolvedCatalogFingerprint = null;
        RaiseResolvedGeneration();
    }

    private void RaiseResolvedGeneration()
    {
        OnPropertyChanged(nameof(ResolvedGenerationDiagnostics));
        OnPropertyChanged(nameof(ResolvedGenerationRevision));
        OnPropertyChanged(nameof(ResolvedCatalogVersion));
        OnPropertyChanged(nameof(ResolvedCatalogFingerprint));
        OnPropertyChanged(nameof(HasResolvedGeneration));
    }

    /// <summary>Updates the one shared draft. Several dependent updates still apply as one undo item.</summary>
    public void UpdateDraft(Func<ShipDocument, ShipDocument> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        EnsureTransaction();
        if (_hasConflict)
            throw new InvalidOperationException("The workspace draft is based on an older project revision.");
        _transaction!.Update(update);
        var isDirty = IsDraftDirty;
        // A draft is not the committed revision: its resolved findings are not authoritative.
        if (isDirty)
        {
            ClearResolvedGeneration();
            // The derived frame is hull-derived, so it stays valid while the draft keeps the
            // committed hull and the ruler/arrangement/generation keep consuming the same frame.
            // A draft that rebuilds the hull parameters invalidates it. Reference identity is
            // sufficient and correct here because arrangement/internal drafts never rebuild Hull.
            if (!ReferenceEquals(Document.Hull, _session.Document.Hull))
                ClearLayoutFrame();
        }
        RaiseDraftProperties();
        // Invalidate export-authoritative state before any presentation subscriber performs
        // layout work for the draft.
        DraftChanged?.Invoke(this, new WorkspaceDraftChangedEventArgs(WorkspaceDraftChangeKind.Updated, isDirty));
        DocumentChanged?.Invoke(this, EventArgs.Empty);
    }

    public WorkspaceApplyResult Apply()
    {
        if (_transaction is null || !IsDraftDirty)
        {
            ClearTransaction();
            return WorkspaceApplyResult.NoChanges;
        }
        if (_hasConflict)
            return WorkspaceApplyResult.Conflict;

        var transaction = _transaction;
        _transaction = null;
        _baseDocument = null;
        try
        {
            transaction.Apply();
        }
        catch (InvalidOperationException)
        {
            _hasConflict = true;
            transaction.Dispose();
            RaiseDraftProperties();
            return WorkspaceApplyResult.Conflict;
        }

        _hasConflict = false;
        RaiseDraftProperties();
        DocumentChanged?.Invoke(this, EventArgs.Empty);
        DraftChanged?.Invoke(this, new WorkspaceDraftChangedEventArgs(WorkspaceDraftChangeKind.Applied, true));
        return WorkspaceApplyResult.Applied;
    }

    public void Cancel()
    {
        var wasDirty = IsDraftDirty;
        ClearTransaction();
        RaiseDraftProperties();
        DocumentChanged?.Invoke(this, EventArgs.Empty);
        DraftChanged?.Invoke(this, new WorkspaceDraftChangedEventArgs(WorkspaceDraftChangeKind.Cancelled, wasDirty));
    }

    /// <summary>Resolves an actual editor close; moving between hosts is deliberately not a close.</summary>
    public bool TryClose(WorkspaceCloseChoice choice)
    {
        if (!IsDraftDirty)
        {
            Cancel();
            return true;
        }

        return choice switch
        {
            WorkspaceCloseChoice.KeepEditing => false,
            WorkspaceCloseChoice.Discard => CancelAndClose(),
            WorkspaceCloseChoice.Apply => Apply() is WorkspaceApplyResult.Applied or WorkspaceApplyResult.NoChanges,
            _ => false,
        };
    }

    public void Dispose()
    {
        _session.Changed -= SessionChanged;
        ClearTransaction();
        _layoutFramePreparation.Dispose();
    }

    private void EnsureTransaction()
    {
        if (_transaction is not null)
            return;
        _baseRevision = _session.Revision;
        _baseDocument = _session.Document;
        _transaction = _session.BeginTransaction();
        _hasConflict = false;
    }

    private void SessionChanged(object? sender, EventArgs eventArgs)
    {
        if (_transaction is not null &&
            (_session.Revision != _baseRevision || _session.Document != _baseDocument))
            _hasConflict = true;
        // A derived frame belongs to exactly one committed revision and document identity.
        if (_layoutFrame is { } frame && !frame.IsCurrent(_session.Document.DocumentId, _session.Revision))
            ClearLayoutFrame();
        // A resolved generation belongs to exactly one committed revision and document identity.
        if (_resolvedGenerationRevision is not null &&
            (_resolvedGenerationRevision != _session.Revision ||
             !string.Equals(_resolvedGenerationDocumentId, _session.Document.DocumentId,
                 StringComparison.Ordinal)))
            ClearResolvedGeneration();
        RaiseDraftProperties();
        DocumentChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ClearTransaction()
    {
        _transaction?.Dispose();
        _transaction = null;
        _baseDocument = null;
        _hasConflict = false;
    }

    private bool CancelAndClose()
    {
        Cancel();
        return true;
    }

    private void RaiseDraftProperties()
    {
        OnPropertyChanged(nameof(Document));
        OnPropertyChanged(nameof(HasDraft));
        OnPropertyChanged(nameof(IsDraftDirty));
        OnPropertyChanged(nameof(HasConflict));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(DraftStatus));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
