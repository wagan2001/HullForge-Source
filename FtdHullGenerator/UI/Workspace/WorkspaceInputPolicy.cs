using System.Windows.Input;

namespace FtdHullGenerator.UI.Workspace;

public enum WorkspaceKeyAction
{
    None,
    Apply,
    Cancel,
}

/// <summary>
/// The application shortcuts the normal 2.0 surface forwards between the docked and detached
/// workspace hosts. Project New/Open/Save/Save As are deliberately absent: 2.0 is a short
/// design-and-export session, and native blueprint export remains the only output.
/// </summary>
public enum WorkspaceApplicationShortcut
{
    Undo,
    Redo,
}

/// <summary>Pure keyboard policy shared by either host and directly testable without a window.</summary>
public static class WorkspaceInputPolicy
{
    public static WorkspaceKeyAction ResolveEditorKey(
        Key key,
        ModifierKeys modifiers,
        bool isEligibleSingleLineTextBox)
    {
        if (modifiers != ModifierKeys.None)
            return WorkspaceKeyAction.None;
        if (key == Key.Escape)
            return WorkspaceKeyAction.Cancel;
        if (key == Key.Enter && isEligibleSingleLineTextBox)
            return WorkspaceKeyAction.Apply;
        return WorkspaceKeyAction.None;
    }

    public static WorkspaceApplicationShortcut? ResolveApplicationShortcut(Key key, ModifierKeys modifiers)
    {
        if ((modifiers & ModifierKeys.Control) == 0 ||
            (modifiers & ~(ModifierKeys.Control | ModifierKeys.Shift)) != 0)
            return null;

        return key switch
        {
            Key.Z when modifiers == ModifierKeys.Control => WorkspaceApplicationShortcut.Undo,
            Key.Y when modifiers == ModifierKeys.Control => WorkspaceApplicationShortcut.Redo,
            _ => null,
        };
    }
}
