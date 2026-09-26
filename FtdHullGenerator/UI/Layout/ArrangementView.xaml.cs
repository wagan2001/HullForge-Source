using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using FtdHullGenerator.Domain.Components;
using FtdHullGenerator.Domain.Design;
using FtdHullGenerator.UI.Components;
using FtdHullGenerator.UI.Workspace;

namespace FtdHullGenerator.UI.Layout;

/// <summary>
/// The frozen 2.0 arrangement workspace: a bow-to-stern barbette ruler over one shared barbette
/// property editor. It binds to the existing <see cref="WorkspaceViewModel"/> draft, so create,
/// edit, remove and drag all apply as one undo item and never touch the committed revision.
/// </summary>
public partial class ArrangementView : UserControl
{
    public static readonly DependencyProperty WorkspaceProperty = DependencyProperty.Register(
        nameof(Workspace), typeof(WorkspaceViewModel), typeof(ArrangementView),
        new PropertyMetadata(null, (source, _) => ((ArrangementView)source).WorkspaceChanged()));

    private BarbetteEditorViewModel? _viewModel;

    public ArrangementView()
    {
        InitializeComponent();
        Ruler.BarbetteMoved += RulerBarbetteMoved;
    }

    public WorkspaceViewModel? Workspace
    {
        get => (WorkspaceViewModel?)GetValue(WorkspaceProperty);
        set => SetValue(WorkspaceProperty, value);
    }

    internal BarbetteEditorViewModel? ViewModel => _viewModel;

    internal BarbetteRuler RulerForTests => Ruler;

    internal void AddBarbetteForTests() => AddBarbetteClicked(this, new RoutedEventArgs());

    internal void RemoveBarbetteForTests() => RemoveBarbetteClicked(this, new RoutedEventArgs());

    private void WorkspaceChanged()
    {
        Detach();
        if (Workspace is not { } workspace)
        {
            EditorRoot.DataContext = null;
            return;
        }

        _viewModel = new BarbetteEditorViewModel(workspace);
        EditorRoot.DataContext = _viewModel;
        BarbetteList.SelectedIndex = _viewModel.Barbettes.Count > 0 ? 0 : -1;
    }

    private void Detach()
    {
        // Moving between hosts replaces the data context; the bound ruler model simply falls back
        // to null. The view deliberately survives WPF Unloaded, which also fires on tab switches.
        _viewModel?.Dispose();
        _viewModel = null;
        EditorRoot.DataContext = null;
    }

    private void RulerBarbetteMoved(object? sender, BarbetteMovedEventArgs eventArgs) =>
        _viewModel?.MoveBarbette(eventArgs.BarbetteId, eventArgs.RulerCenter);

    private void AddBarbetteClicked(object sender, RoutedEventArgs eventArgs) =>
        _viewModel?.AddBarbette();

    private void RemoveBarbetteClicked(object sender, RoutedEventArgs eventArgs) =>
        _viewModel?.RemoveSelectedBarbette();

    private void AddArmorLayerClicked(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is FrameworkElement { Tag: BarbetteArmorRole role })
            _viewModel?.SelectedBarbette?.AddLayer(role);
    }

    private void RemoveArmorLayerClicked(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is FrameworkElement { Tag: BarbetteArmorRole role })
            _viewModel?.SelectedBarbette?.RemoveLayer(role);
    }
}

/// <summary>
/// The dedicated barbette clear-diameter input. It offers only the domain's odd whole-metre
/// progression (<see cref="BarbetteDefinition.NextSupportedClearDiameter" /> /
/// <see cref="BarbetteDefinition.PreviousSupportedClearDiameter" />) and never rounds or clamps a
/// typed value: the raw parsed double travels through the two-way <see cref="Value" /> binding, and
/// when the view model rejects it the binding restores the last legal value. The rejection line is
/// the view model's visible BAR009 diagnostic.
/// </summary>
public sealed class BarbetteDiameterInput : UserControl
{
    private readonly TextBox _textBox;
    private readonly RepeatButton _decrease;
    private readonly RepeatButton _increase;
    private bool _syncing;

    public BarbetteDiameterInput()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _textBox = new TextBox
        {
            MinHeight = 28,
            Padding = new Thickness(6, 3, 6, 3),
            TextAlignment = TextAlignment.Right,
        };
        _textBox.LostKeyboardFocus += (_, _) => CommitText(restoreInvalid: true);
        _textBox.PreviewKeyDown += TextBoxPreviewKeyDown;
        _textBox.PreviewMouseWheel += (_, args) =>
        {
            Nudge(Math.Sign(args.Delta));
            args.Handled = true;
        };
        Grid.SetColumn(_textBox, 0);
        grid.Children.Add(_textBox);

        var steppers = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 0, 0) };
        _decrease = StepButton("−", "Decrease to the previous odd whole-metre clear diameter");
        _increase = StepButton("+", "Increase to the next odd whole-metre clear diameter");
        _decrease.Click += (_, _) => Nudge(-1);
        _increase.Click += (_, _) => Nudge(1);
        steppers.Children.Add(_decrease);
        steppers.Children.Add(_increase);
        Grid.SetColumn(steppers, 1);
        grid.Children.Add(steppers);

        var unit = new TextBlock
        {
            Text = "m",
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        unit.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        unit.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        Grid.SetColumn(unit, 2);
        grid.Children.Add(unit);
        Content = grid;
        Loaded += (_, _) =>
        {
            UpdateText(Value);
            UpdateSteppers();
            AutomationProperties.SetName(_textBox, AutomationName);
        };
    }

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(BarbetteDiameterInput),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (source, args) => ((BarbetteDiameterInput)source).ExternalValueChanged((double)args.NewValue)));

    public static readonly DependencyProperty AutomationNameProperty = DependencyProperty.Register(
        nameof(AutomationName), typeof(string), typeof(BarbetteDiameterInput),
        new PropertyMetadata("Barbette clear internal diameter", (source, args) =>
            AutomationProperties.SetName(((BarbetteDiameterInput)source)._textBox, (string)args.NewValue)));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public string AutomationName
    {
        get => (string)GetValue(AutomationNameProperty);
        set => SetValue(AutomationNameProperty, value);
    }

    internal TextBox TextBoxForTests => _textBox;
    internal RepeatButton DecreaseButtonForTests => _decrease;
    internal RepeatButton IncreaseButtonForTests => _increase;
    internal void CommitTextForTests() => CommitText(restoreInvalid: true);
    internal void NudgeForTests(int direction) => Nudge(direction);

    private void TextBoxPreviewKeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key is Key.Up or Key.Down)
        {
            Nudge(eventArgs.Key == Key.Up ? 1 : -1);
            eventArgs.Handled = true;
        }
        else if (eventArgs.Key == Key.Enter)
        {
            CommitText(restoreInvalid: true);
            eventArgs.Handled = true;
        }
    }

    private void Nudge(int direction)
    {
        var basis = TryMeasure(Value);
        if (direction > 0)
        {
            var next = basis is { } current
                ? BarbetteDefinition.NextSupportedClearDiameter(current)
                : DesignMeasure.FromMetres(BarbetteDefinition.MinimumClearDiameterMetres);
            if (basis is { } currentBasis && next <= currentBasis)
                return;
            SetCurrentValue(ValueProperty, next.Metres);
            UpdateText(next.Metres);
            return;
        }

        if (basis is not { } measure ||
            BarbetteDefinition.PreviousSupportedClearDiameter(measure) is not { } previous)
            return;
        SetCurrentValue(ValueProperty, previous.Metres);
        UpdateText(previous.Metres);
    }

    /// <summary>
    /// Commits the raw parsed double through the two-way binding. It is deliberately not rounded or
    /// clamped: an even or fractional entry must reach the view model so the domain BAR009 rule can
    /// reject it visibly, and the binding then restores the last legal value.
    /// </summary>
    private void CommitText(bool restoreInvalid)
    {
        if (_syncing)
            return;
        if (TryParse(_textBox.Text, out var parsed) && double.IsFinite(parsed))
        {
            SetCurrentValue(ValueProperty, parsed);
            return;
        }

        if (restoreInvalid)
            UpdateText(Value);
    }

    private static bool TryParse(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) &&
        double.IsFinite(value);

    private static DesignMeasure? TryMeasure(double value)
    {
        if (!double.IsFinite(value) || Math.Abs(value) > DesignLimits.MaxDesignTwiceMetres / 2d)
            return null;
        return DesignMeasure.FromMetres(value);
    }

    private void ExternalValueChanged(double value)
    {
        if (_syncing)
            return;
        UpdateText(value);
        UpdateSteppers();
    }

    private void UpdateSteppers()
    {
        var basis = TryMeasure(Value);
        _decrease.IsEnabled = basis is { } current &&
            BarbetteDefinition.PreviousSupportedClearDiameter(current) is not null;
        _increase.IsEnabled = basis is not { } next || BarbetteDefinition.NextSupportedClearDiameter(next) > next;
    }

    private void UpdateText(double value)
    {
        _syncing = true;
        _textBox.Text = value.ToString("0.###", CultureInfo.CurrentCulture);
        _syncing = false;
    }

    private static RepeatButton StepButton(string content, string tooltip)
    {
        var button = new RepeatButton
        {
            Content = content,
            ToolTip = tooltip,
            Width = 28,
            MinHeight = 28,
            Padding = new Thickness(0),
            Delay = 400,
            Interval = 90,
        };
        AutomationProperties.SetName(button, tooltip);
        return button;
    }
}
