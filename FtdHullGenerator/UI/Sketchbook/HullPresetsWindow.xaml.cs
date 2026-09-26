using System.ComponentModel;
using System.Windows;

namespace FtdHullGenerator.UI.Sketchbook;

/// <summary>
/// The modeless Hull Presets window. It hosts the single <see cref="SketchbookBrowser" />; the
/// catalog, the native thumbnails and the apply transaction stay owned by the editor, so this
/// window never holds editor state of its own.
/// </summary>
/// <remarks>
/// Closing the window hides it instead of destroying it: the editor's Hull Presets button can
/// always show or re-activate the same instance, and only application shutdown closes it for
/// real through <see cref="CloseForShutdown" />.
/// </remarks>
public sealed partial class HullPresetsWindow : Window
{
    private bool _allowClose;

    public HullPresetsWindow()
    {
        InitializeComponent();
    }

    /// <summary>The browser control this window hosts; the catalog is never copied here.</summary>
    public SketchbookBrowser Browser => BrowserControl;

    /// <summary>Allows the one close that application shutdown performs.</summary>
    public void CloseForShutdown()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs eventArgs)
    {
        if (!_allowClose)
        {
            eventArgs.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(eventArgs);
    }
}
