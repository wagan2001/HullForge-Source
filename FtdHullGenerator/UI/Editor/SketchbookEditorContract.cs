using FtdHullGenerator.Domain.Sketchbook;

namespace FtdHullGenerator.UI.Editor;

/// <summary>The one normal editor transaction for applying a sketchbook entry.</summary>
/// <remarks>
/// This is deliberately the whole product surface for applying an Alternate Naval Sketchbook
/// entry: resolve the document, commit it as one immutable revision, and return the resolution.
/// There is no preview, no layout-frame preparation, no background work and no second history
/// model. Re-applying an entry that already matches the current document is a no-op transaction
/// and creates no revision.
/// </remarks>
public static class SketchbookEditorContract
{
    /// <summary>
    /// Applies one sketchbook entry to the session as exactly one editor transaction and returns
    /// the resolved application, including the diagnostics the committed document produces.
    /// </summary>
    public static SketchbookApplication Apply(
        EditorSession session,
        AlternateNavalSketchbookEntry entry,
        SketchbookSizePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(entry);

        var application = SketchbookShapeInitializer.Apply(entry, policy, session.Document);
        using var transaction = session.BeginTransaction();
        transaction.Update(_ => application.Document);
        transaction.Apply();
        return application;
    }
}
