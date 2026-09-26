using FtdHullGenerator.Domain.Projects;
using FtdHullGenerator.Serialization.Projects;

namespace FtdHullGenerator.UI.Editor;

/// <summary>
/// Owns immutable design revisions independently of WPF controls. A committed transaction is one
/// history item even when it changes several dependent fields.
/// </summary>
public sealed class EditorSession
{
    private readonly List<ShipDocument> _history = [];
    private int _historyIndex;
    private string? _savedFingerprint;
    private bool _invalidDraft;

    public EditorSession(ShipDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        _history.Add(document);
        _savedFingerprint = Fingerprint(document);
        Revision = 1;
    }

    public event EventHandler? Changed;

    public ShipDocument Document => _history[_historyIndex];

    public string? ProjectPath { get; private set; }

    /// <summary>A monotonic identity for preview work. Undo and redo also create new revisions.</summary>
    public long Revision { get; private set; }

    public bool HasInvalidDraft => _invalidDraft;

    public bool IsDirty => _invalidDraft || !string.Equals(_savedFingerprint, Fingerprint(Document), StringComparison.Ordinal);

    public bool CanUndo => _invalidDraft || _historyIndex > 0;

    public bool CanRedo => !_invalidDraft && _historyIndex + 1 < _history.Count;

    public EditorTransaction BeginTransaction() => new(this, Revision, Document);

    public bool Commit(ShipDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        _invalidDraft = false;
        if (ReferenceEquals(document, Document) || document == Document)
        {
            RaiseChanged();
            return false;
        }

        if (_historyIndex + 1 < _history.Count)
            _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
        _history.Add(document);
        _historyIndex++;
        AdvanceRevision();
        return true;
    }

    /// <summary>
    /// Records that visible controls no longer describe a valid document. The previous immutable
    /// document remains available for undo/recovery, but its preview is immediately stale.
    /// </summary>
    public void MarkInvalidDraft()
    {
        if (_invalidDraft)
            return;
        _invalidDraft = true;
        AdvanceRevision();
    }

    public bool Undo()
    {
        if (_invalidDraft)
        {
            _invalidDraft = false;
            AdvanceRevision();
            return true;
        }
        if (_historyIndex == 0)
            return false;
        _historyIndex--;
        AdvanceRevision();
        return true;
    }

    public bool Redo()
    {
        if (_invalidDraft || _historyIndex + 1 >= _history.Count)
            return false;
        _historyIndex++;
        AdvanceRevision();
        return true;
    }

    public void New(ShipDocument document)
    {
        Replace(document, path: null, markDirty: false);
    }

    public void Open(ShipDocument document, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Replace(document, Path.GetFullPath(path), markDirty: false);
    }

    /// <summary>A recovery is intentionally unsaved and must go through Save As.</summary>
    public void Recover(ShipDocument document)
    {
        Replace(document, path: null, markDirty: true);
    }

    public void MarkSaved(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ProjectPath = Path.GetFullPath(path);
        _invalidDraft = false;
        _savedFingerprint = Fingerprint(Document);
        RaiseChanged();
    }

    private void Replace(ShipDocument document, string? path, bool markDirty)
    {
        ArgumentNullException.ThrowIfNull(document);
        _history.Clear();
        _history.Add(document);
        _historyIndex = 0;
        ProjectPath = path;
        _invalidDraft = false;
        _savedFingerprint = markDirty ? null : Fingerprint(document);
        AdvanceRevision();
    }

    private void AdvanceRevision()
    {
        Revision++;
        RaiseChanged();
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private static string? Fingerprint(ShipDocument document)
    {
        var serialized = ProjectDocumentSerializer.Serialize(document);
        return serialized.Succeeded ? serialized.Json : null;
    }

    internal void Apply(EditorTransaction transaction, ShipDocument document)
    {
        if (!ReferenceEquals(transaction.Owner, this))
            throw new InvalidOperationException("The transaction belongs to another editor session.");
        if (transaction.BaseRevision != Revision || transaction.BaseDocument != Document)
            throw new InvalidOperationException(
                "The editor changed after this transaction began. Start a new transaction from the current revision.");
        Commit(document);
    }
}

/// <summary>A coherent draft that either becomes one undo item or is discarded in full.</summary>
public sealed class EditorTransaction : IDisposable
{
    private bool _completed;

    internal EditorTransaction(EditorSession owner, long baseRevision, ShipDocument document)
    {
        Owner = owner;
        BaseRevision = baseRevision;
        BaseDocument = document;
        Document = document;
    }

    internal EditorSession Owner { get; }

    internal long BaseRevision { get; }

    internal ShipDocument BaseDocument { get; }

    public ShipDocument Document { get; private set; }

    public void Update(Func<ShipDocument, ShipDocument> update)
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        Document = update(Document) ?? throw new InvalidOperationException("A transaction cannot contain a null document.");
    }

    public void Apply()
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        _completed = true;
        Owner.Apply(this, Document);
    }

    public void Cancel() => _completed = true;

    public void Dispose() => Cancel();
}
