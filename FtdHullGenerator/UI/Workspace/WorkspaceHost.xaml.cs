using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FtdHullGenerator.UI.Workspace;

public partial class WorkspaceHost : UserControl
{
    public WorkspaceHost() => InitializeComponent();

    public event EventHandler? ToggleHostRequested;

    public WorkspaceViewModel? ViewModel
    {
        get => DataContext as WorkspaceViewModel;
        set => DataContext = value;
    }

    internal TabControl Tabs => WorkspaceTabs;

    internal Button ApplyButton => ApplyDraftButton;

    public bool FocusWorkspace()
    {
        WorkspaceTabs.Focus();
        return ReferenceEquals(Keyboard.Focus(WorkspaceTabs), WorkspaceTabs);
    }

    private void ToggleHostClicked(object sender, RoutedEventArgs eventArgs) =>
        ToggleHostRequested?.Invoke(this, EventArgs.Empty);

    private void ApplyClicked(object sender, RoutedEventArgs eventArgs) => ApplyDraft();

    private void CancelClicked(object sender, RoutedEventArgs eventArgs) => ViewModel?.Cancel();

    private void HostPreviewKeyDown(object sender, KeyEventArgs eventArgs)
    {
        var eligibleTextBox = eventArgs.OriginalSource is TextBox
        {
            AcceptsReturn: false,
            IsReadOnly: false,
            IsEnabled: true,
        };
        switch (WorkspaceInputPolicy.ResolveEditorKey(eventArgs.Key, Keyboard.Modifiers, eligibleTextBox))
        {
            case WorkspaceKeyAction.Apply:
                if (eventArgs.OriginalSource is TextBox textBox)
                    textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                ApplyDraft();
                eventArgs.Handled = true;
                break;
            case WorkspaceKeyAction.Cancel:
                ViewModel?.Cancel();
                eventArgs.Handled = true;
                break;
        }
    }

    private void ApplyDraft()
    {
        if (ViewModel?.Apply() == WorkspaceApplyResult.Conflict)
            MessageBox.Show(Window.GetWindow(this), ViewModel.DraftStatus, "Workspace draft",
                MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
