using System.Windows;
using System.Windows.Controls;
using FtdHullGenerator.Domain.Components;
using FtdHullGenerator.Geometry.Components;
using FtdHullGenerator.UI.Workspace;

namespace FtdHullGenerator.UI.Components;

public sealed class InternalFamilyEventArgs(InternalPlaneFamily family) : EventArgs
{
    public InternalPlaneFamily Family { get; } = family;
}

public partial class InternalStructureEditor : UserControl, IDisposable
{
    private InternalStructureEditorViewModel? _viewModel;

    public InternalStructureEditor()
    {
        InitializeComponent();
    }

    public event EventHandler<InternalFamilyEventArgs>? CutawayRequested;
    public event EventHandler<InternalFamilyEventArgs>? SelectedFamilyChanged;

    public void Attach(WorkspaceViewModel workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        _viewModel?.Dispose();
        _viewModel = new InternalStructureEditorViewModel(workspace);
        _viewModel.PropertyChanged += ViewModelPropertyChanged;
        DataContext = _viewModel;
        FamilyList.SelectedIndex = 0;
    }

    public void SetGenerationResult(InternalStructureGenerationResult? result) =>
        _viewModel?.SetGenerationResult(result);

    public void SetGenerationDiagnostics(IEnumerable<FtdHullGenerator.Domain.Design.DesignDiagnostic>? diagnostics) =>
        _viewModel?.SetGenerationDiagnostics(diagnostics);

    public InternalStructureEditorViewModel? ViewModel => _viewModel;

    public void Dispose()
    {
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= ViewModelPropertyChanged;
        _viewModel?.Dispose();
        _viewModel = null;
        DataContext = null;
    }

    private void InspectCutawayClicked(object sender, RoutedEventArgs eventArgs)
    {
        if (_viewModel?.SelectedFamily is { } selected)
            CutawayRequested?.Invoke(this, new InternalFamilyEventArgs(selected.Family));
    }

    private void AutomaticLayoutClicked(object sender, RoutedEventArgs eventArgs) =>
        _viewModel?.SelectedFamily?.UseAutomaticLayout();

    private void ViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(InternalStructureEditorViewModel.SelectedFamily) &&
            _viewModel?.SelectedFamily is { } selected)
            SelectedFamilyChanged?.Invoke(this, new InternalFamilyEventArgs(selected.Family));
    }
}
