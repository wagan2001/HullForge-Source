using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Projects;
using FtdHullGenerator.Infrastructure.Projects;
using FtdHullGenerator.UI.Editor;
using static SlopeFillTestSupport;

/// <summary>F04's UI-independent editor, preview-revision and recovery contract checks.</summary>
internal static class EditorStateTests
{
    public static void Run()
    {
        TransactionIsOneUndoItem();
        ControlProjectionPreservesDocumentSemantics();
        InvalidDraftAndPreviewRevisionStaySafe();
        ProjectSaveReopenAndRecoveryWork();
        Console.WriteLine("Editor state: atomic transactions, undo/redo, dirty tracking, revision-safe preview, save/reopen, and recovery passed.");
    }

    private static void TransactionIsOneUndoItem()
    {
        var original = ShipDocument.CreateNew("Transaction ship", HullEditorSettings.Default);
        var editor = new EditorSession(original);
        var startingRevision = editor.Revision;

        using (var transaction = editor.BeginTransaction())
        {
            transaction.Update(document => WithHull(document,
                document.Hull with { Length = 120, Width = 25 }));
            transaction.Update(document => document with { Name = "Dependent edit" });
            transaction.Apply();
        }

        Require(editor.Revision == startingRevision + 1 && editor.Document.Name == "Dependent edit" &&
                editor.Document.Hull.Length == 120 && editor.Document.Hull.Width == 25,
            "A dependent draft did not apply as one immutable revision.");
        Require(editor.IsDirty && editor.CanUndo && !editor.CanRedo,
            "A committed edit did not update dirty/history state.");

        var editedRevision = editor.Revision;
        Require(editor.Undo() && editor.Document == original && editor.Revision == editedRevision + 1,
            "One undo did not restore the whole dependent transaction with a new revision identity.");
        Require(!editor.IsDirty && editor.CanRedo,
            "Undoing to the saved snapshot did not clear dirty state or expose redo.");
        Require(editor.Redo() && editor.Document.Name == "Dependent edit" && editor.IsDirty,
            "Redo did not restore the complete transaction.");

        var beforeCancel = editor.Document;
        using (var transaction = editor.BeginTransaction())
        {
            transaction.Update(document => document with { Name = "Must not escape" });
            transaction.Cancel();
        }
        Require(editor.Document == beforeCancel,
            "Cancelling a draft changed the committed document.");

        var stale = editor.BeginTransaction();
        editor.Commit(editor.Document with { Name = "Newer revision" });
        stale.Update(document => document with { Name = "Stale overwrite" });
        var staleRejected = false;
        try
        {
            stale.Apply();
        }
        catch (InvalidOperationException)
        {
            staleRejected = true;
        }
        Require(staleRejected && editor.Document.Name == "Newer revision",
            "A transaction based on an older revision overwrote a newer commit.");
    }

    private static void ControlProjectionPreservesDocumentSemantics()
    {
        var inheritedBottom = HullEditorSettings.Default with
        {
            Beamify = false,
            BottomArmor = null,
            Superstructure = HullEditorSettings.Default.EffectiveSuperstructure with
                { Style = SuperstructureStyle.CenterIsland },
        };
        var projection = EditorControlProjection.FromHull(inheritedBottom);
        Require(projection.KeepSingleBlocks && projection.BottomArmorInheritsSide &&
                projection.DisplayedBottomArmor.Equals(inheritedBottom.HullArmor) &&
                EditorControlProjection.ResolveBottomArmor(projection.BottomArmorInheritsSide,
                    projection.DisplayedBottomArmor) is null,
            "Document-to-control projection changed Beamify or expanded inherited bottom armor.");

        foreach (var style in Enum.GetValues<SuperstructureStyle>())
        {
            var parameters = inheritedBottom with
            {
                Superstructure = inheritedBottom.EffectiveSuperstructure with { Style = style },
            };
            Require(EditorControlProjection.FromHull(parameters).LegacySuperstructureStyle == style,
                $"The legacy {style} superstructure was coerced during control projection.");
        }

        var explicitBottom = ArmorLayout.Single(MaterialKind.Lead);
        projection = EditorControlProjection.FromHull(inheritedBottom with { BottomArmor = explicitBottom });
        Require(!projection.BottomArmorInheritsSide &&
                ReferenceEquals(EditorControlProjection.ResolveBottomArmor(false, projection.DisplayedBottomArmor),
                    explicitBottom),
            "An explicit bottom armor stack was changed into inherited armor.");

        Require(EditorControlProjection.CanGenerate(HullSource.Regional) &&
                !EditorControlProjection.CanGenerate(HullSource.Historical("asset", "1", "hash")),
            "The editor preview bridge would substitute regional geometry for a historical source.");

        var stableDocument = ShipDocument.CreateNew("Stable expanded intent", inheritedBottom) with
        {
            Hull = inheritedBottom with { Smoothing = SmoothingMethod.CombinedSlopeFill },
        };
        var normalExposure = new FeatureExposurePolicy(false);
        Require(EditorControlProjection.CanGenerate(stableDocument, normalExposure,
                    out var stableReason) && stableReason is null,
            "A former commercial feature was not available in the normal development surface.");
        var unavailableDocument = stableDocument with
        {
            Hull = stableDocument.Hull with { Smoothing = SmoothingMethod.InvertedTriangleFill },
        };
        Require(!EditorControlProjection.CanGenerate(unavailableDocument, normalExposure,
                    out var unavailableReason) && unavailableReason is not null &&
                unavailableDocument.Hull.Smoothing == SmoothingMethod.InvertedTriangleFill,
            "An unavailable feature was not blocked while preserving project intent.");
        var accessSession = new EditorSession(unavailableDocument);
        var retainedDocument = accessSession.Document;
        _ = EditorControlProjection.FromHull(retainedDocument.Hull);
        _ = EditorControlProjection.CanGenerate(retainedDocument, normalExposure, out _);
        Require(ReferenceEquals(accessSession.Document, retainedDocument) && !accessSession.IsDirty &&
                accessSession.Revision == 1,
            "Evaluating feature exposure changed or dirtied immutable project intent.");
    }

    private static void InvalidDraftAndPreviewRevisionStaySafe()
    {
        var editor = new EditorSession(ShipDocument.CreateNew("Preview ship", HullEditorSettings.Default));
        using var boundary = new RevisionPreviewBoundary<object>();
        boundary.MoveToRevision(editor.Revision);

        var first = boundary.Begin(editor.Revision);
        editor.MarkInvalidDraft();
        boundary.MoveToRevision(editor.Revision);
        Require(editor.IsDirty && editor.HasInvalidDraft && first.CancellationToken.IsCancellationRequested &&
                !boundary.TryAccept(first, new object()),
            "A late valid preview escaped after the visible draft became invalid.");

        Require(editor.Undo() && !editor.HasInvalidDraft,
            "Undo did not restore the immutable document behind an invalid visible draft.");
        boundary.MoveToRevision(editor.Revision);
        var current = new object();
        var request = boundary.Begin(editor.Revision);
        Require(boundary.TryAccept(request, current) && boundary.TryGetCurrent(editor.Revision, out var accepted) &&
                ReferenceEquals(current, accepted),
            "A current validated preview was not made available to export.");

        boundary.InvalidateCurrent();
        Require(request.CancellationToken.IsCancellationRequested &&
                !boundary.TryGetCurrent(editor.Revision, out _),
            "An access-only invalidation left an old preview exportable.");

        var replacement = boundary.Begin(editor.Revision);
        Require(!boundary.TryGetCurrent(editor.Revision, out _),
            "Starting a replacement build left the older preview exportable.");
        Require(!boundary.TryAccept(request, new object()) && boundary.TryAccept(replacement, new object()),
            "Preview sequence matching allowed an older task to replace its successor.");
    }

    private static void ProjectSaveReopenAndRecoveryWork()
    {
        var root = Path.Combine(Path.GetTempPath(), "HullForge-F04-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try
        {
            var projects = Path.Combine(root, "Projects");
            var recovery = Path.Combine(root, "Recovery");
            Directory.CreateDirectory(projects);
            var path = Path.Combine(projects, "round-trip" + ProjectStore.DocumentExtension);
            var original = ShipDocument.CreateNew("Round trip", HullEditorSettings.Default);
            var editor = new EditorSession(original);
            editor.Commit(WithHull(editor.Document, editor.Document.Hull with { Length = 137 }));

            var save = ProjectStore.Save(editor.Document, path);
            Require(save.Succeeded, "The real project store did not save an editor snapshot.");
            editor.MarkSaved(save.Path!);
            Require(!editor.IsDirty && editor.ProjectPath == Path.GetFullPath(path),
                "A successful manual save did not establish the editor savepoint.");

            var loaded = ProjectStore.Load(path);
            Require(loaded.Succeeded && loaded.Document!.Hull.Length == 137,
                "A new application state did not reopen the saved editor document.");
            var reopened = new EditorSession(ShipDocument.CreateNew("Other", HullEditorSettings.Default));
            reopened.Open(loaded.Document!, path);
            Require(!reopened.IsDirty && reopened.Document.DocumentId == original.DocumentId,
                "Opening a project did not preserve identity or establish clean state.");

            reopened.Commit(WithHull(reopened.Document, reopened.Document.Hull with { Height = 19 }));
            var store = new EditorRecoveryStore(recovery);
            var autosave = store.Save(reopened.Document);
            Require(autosave.Succeeded && File.Exists(autosave.Path),
                "Autosave did not create an atomic recovery snapshot.");
            var candidates = store.FindCandidates();
            Require(candidates.Count == 1 && candidates[0].Document.Hull.Height == 19,
                "Recovery selection did not return the newest complete document.");
            reopened.Recover(candidates[0].Document);
            Require(reopened.IsDirty && reopened.ProjectPath is null,
                "A recovered design could overwrite an old manual path without Save As.");
            store.DeleteCandidate(candidates[0]);
            Require(store.FindCandidates().Count == 0,
                "Discarding a recovery candidate left it selectable.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static ShipDocument WithHull(ShipDocument document, HullParameters parameters) => document with
    {
        Hull = parameters,
        Smoothing = document.Smoothing with { NativeMethod = parameters.Smoothing },
    };
}
