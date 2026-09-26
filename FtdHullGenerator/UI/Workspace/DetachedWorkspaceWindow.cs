using System.ComponentModel;
using System.Windows;
using System.Windows.Input;

namespace FtdHullGenerator.UI.Workspace;

/// <summary>A second host for the existing workspace view model; it never owns editor state.</summary>
public sealed class DetachedWorkspaceWindow : Window
{
    private bool _allowClose;
    private readonly Func<WorkspaceApplicationShortcut, bool> _forwardShortcut;

    public DetachedWorkspaceWindow(
        WorkspaceViewModel viewModel,
        Window owner,
        Func<WorkspaceApplicationShortcut, bool> forwardShortcut)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(owner);
        _forwardShortcut = forwardShortcut ?? throw new ArgumentNullException(nameof(forwardShortcut));
        Title = "Hull Forge — Workspace";
        Owner = owner;
        Width = 920;
        Height = 430;
        MinWidth = 560;
        MinHeight = 270;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        UseLayoutRounding = true;

        Host = new WorkspaceHost { ViewModel = viewModel };
        Host.ToggleHostRequested += (_, _) => RedockRequested?.Invoke(this, EventArgs.Empty);
        Content = Host;
        Closing += PreserveDraftOnWindowClose;
        Loaded += (_, _) => Host.FocusWorkspace();
        PreviewKeyDown += ForwardApplicationShortcut;
    }

    public event EventHandler? RedockRequested;

    internal WorkspaceHost Host { get; }

    public void CloseForRedock()
    {
        _allowClose = true;
        Close();
    }

    private void PreserveDraftOnWindowClose(object? sender, CancelEventArgs eventArgs)
    {
        if (_allowClose)
            return;
        eventArgs.Cancel = true;
        Dispatcher.BeginInvoke(() => RedockRequested?.Invoke(this, EventArgs.Empty));
    }

    internal bool ForwardShortcut(Key key, ModifierKeys modifiers)
    {
        var shortcut = WorkspaceInputPolicy.ResolveApplicationShortcut(key, modifiers);
        return shortcut is not null && _forwardShortcut(shortcut.Value);
    }

    private void ForwardApplicationShortcut(object sender, KeyEventArgs eventArgs)
    {
        if (ForwardShortcut(eventArgs.Key, Keyboard.Modifiers))
            eventArgs.Handled = true;
    }
}
