using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace FtdHullGenerator.UI.Scenes;

/// <summary>
/// Small presentation-only overlay kept inside the preview lock. It gives every scene a
/// direct Grid escape and leaves the host window free to add richer chrome later.
/// </summary>
internal sealed class PreviewSceneSelectorAdorner : Adorner
{
    /// <summary>
    /// The scene kinds the normal 2.0 surface offers. Dock remains a valid dormant
    /// <see cref="PreviewSceneKind" /> for preserved implementation and tests, but it is
    /// deliberately absent from the product selector.
    /// </summary>
    internal static IReadOnlyList<PreviewSceneKind> NormalScenes => PreviewSceneSettings.ProductKinds;

    private readonly VisualCollection _visuals;
    private readonly Grid _root = new();
    private readonly Border _panel = new();
    private readonly StackPanel _options = new();
    private readonly ToggleButton _grid = SceneButton("Grid");
    private readonly ToggleButton _ocean = SceneButton("Ocean");
    private readonly ToggleButton _gridOverlay = SceneButton("Lines");
    private readonly CheckBox _lowDetail = new() { Content = "Low detail", Margin = new Thickness(0, 6, 12, 0) };
    private readonly CheckBox _reducedMotion = new() { Content = "Reduced motion", Margin = new Thickness(0, 6, 12, 0) };
    private readonly CheckBox _deterministic = new() { Content = "Fixed evidence scene", Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBox _waterline = new() { Width = 76, MinHeight = 28, Margin = new Thickness(5, 0, 5, 0), Padding = new Thickness(6, 3, 6, 3) };
    private readonly StackPanel _waterlineRow = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _freeboardRow = new()
    {
        Orientation = Orientation.Horizontal,
        Margin = new Thickness(0, 6, 0, 0),
        Visibility = Visibility.Collapsed,
    };
    private readonly Slider _freeboardSlider = new()
    {
        Width = 150,
        MinWidth = 90,
        MinHeight = 28,
        IsSnapToTickEnabled = true,
        TickFrequency = PreviewSceneFreeboard.StepMetres,
        SmallChange = PreviewSceneFreeboard.StepMetres,
        LargeChange = 1,
        Margin = new Thickness(4, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly TextBlock _freeboardReadout = new()
    {
        Margin = new Thickness(7, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly TextBlock _status = new() { Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 360 };
    private readonly TextBlock _occlusionBadge = new()
    {
        Margin = new Thickness(8, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
        FontWeight = FontWeights.SemiBold,
        Visibility = Visibility.Collapsed,
    };
    private readonly Func<PreviewSceneSettings> _read;
    private readonly Action<PreviewSceneSettings> _write;
    private readonly Action _showUnderside;
    private readonly Func<PreviewSceneDeckDatum?> _readDeckDatum;
    private bool _updating;

    public PreviewSceneSelectorAdorner(
        UIElement adornedElement,
        Func<PreviewSceneSettings> read,
        Action<PreviewSceneSettings> write,
        Action showUnderside,
        Func<PreviewSceneDeckDatum?> readDeckDatum)
        : base(adornedElement)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(write);
        ArgumentNullException.ThrowIfNull(showUnderside);
        ArgumentNullException.ThrowIfNull(readDeckDatum);
        _read = read;
        _write = write;
        _showUnderside = showUnderside;
        _readDeckDatum = readDeckDatum;
        _visuals = new VisualCollection(this) { _root };

        _panel.SetResourceReference(FrameworkElement.StyleProperty, "PreviewScenePanelStyle");
        _panel.HorizontalAlignment = HorizontalAlignment.Left;
        _panel.VerticalAlignment = VerticalAlignment.Top;
        _panel.Margin = new Thickness(8);

        var shell = new StackPanel();
        var bar = new WrapPanel { Orientation = Orientation.Horizontal };
        bar.Children.Add(new TextBlock
        {
            Text = "Scene",
            Margin = new Thickness(0, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.SemiBold,
        });
        bar.Children.Add(_grid);
        bar.Children.Add(_ocean);
        _gridOverlay.Margin = new Thickness(8, 0, 0, 0);
        _gridOverlay.ToolTip = "Independent one-metre/adaptive presentation grid. Never exported.";
        bar.Children.Add(_gridOverlay);

        var more = new ToggleButton { Content = "Options", Margin = new Thickness(4, 0, 0, 0), MinHeight = 28 };
        more.SetResourceReference(FrameworkElement.StyleProperty, "CompactToggleStyle");
        more.Checked += (_, _) => _options.Visibility = Visibility.Visible;
        more.Unchecked += (_, _) => _options.Visibility = Visibility.Collapsed;
        bar.Children.Add(more);
        _occlusionBadge.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        bar.Children.Add(_occlusionBadge);
        shell.Children.Add(bar);

        // The freeboard control sits directly beneath the scene bar, not inside Options: it is
        // the primary Ocean affordance, derived from the authoritative reference deck datum.
        _freeboardRow.Children.Add(new TextBlock
        {
            Text = "Freeboard",
            Margin = new Thickness(0, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.SemiBold,
        });
        _freeboardSlider.ToolTip =
            "Nominal reference-deck height above the visual waterline. Presentation only; never exported.";
        _freeboardSlider.ValueChanged += (_, _) => CommitFreeboard();
        _freeboardRow.Children.Add(_freeboardSlider);
        _freeboardRow.Children.Add(_freeboardReadout);
        shell.Children.Add(_freeboardRow);

        _options.Visibility = Visibility.Collapsed;
        _options.Margin = new Thickness(0, 8, 0, 0);
        var waterlineRow = _waterlineRow;
        waterlineRow.Children.Add(new TextBlock
        {
            Text = "Waterline Y",
            VerticalAlignment = VerticalAlignment.Center,
        });
        var lower = CompactButton("−", "Lower the visual waterline by one metre.");
        var raise = CompactButton("+", "Raise the visual waterline by one metre.");
        lower.Click += (_, _) => ChangeWaterline(-1);
        raise.Click += (_, _) => ChangeWaterline(1);
        waterlineRow.Children.Add(lower);
        waterlineRow.Children.Add(_waterline);
        waterlineRow.Children.Add(raise);
        waterlineRow.Children.Add(new TextBlock
        {
            Text = "m · visual only",
            Margin = new Thickness(7, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        _options.Children.Add(waterlineRow);

        var checks = new WrapPanel();
        checks.Children.Add(_lowDetail);
        checks.Children.Add(_reducedMotion);
        checks.Children.Add(_deterministic);
        _options.Children.Add(checks);

        var underside = CompactButton("Underside", "Inspect from below; water and dock furniture fade automatically.");
        underside.Margin = new Thickness(0, 7, 0, 0);
        underside.HorizontalAlignment = HorizontalAlignment.Left;
        underside.Click += (_, _) => _showUnderside();
        _options.Children.Add(underside);
        _status.SetResourceReference(FrameworkElement.StyleProperty, "MetricLabelStyle");
        _options.Children.Add(_status);
        shell.Children.Add(_options);
        _panel.Child = shell;
        _root.Children.Add(_panel);

        _grid.Click += (_, _) => SelectScene(PreviewSceneKind.Grid);
        _ocean.Click += (_, _) => SelectScene(PreviewSceneKind.Ocean);
        _gridOverlay.Click += (_, _) => Change(settings => settings with { ShowGridOverlay = _gridOverlay.IsChecked == true });
        _lowDetail.Click += (_, _) => Change(settings => settings with { LowDetail = _lowDetail.IsChecked == true });
        _reducedMotion.Click += (_, _) => Change(settings => settings with { ReducedMotion = _reducedMotion.IsChecked == true });
        _deterministic.Click += (_, _) => Change(settings => settings with
        {
            Deterministic = _deterministic.IsChecked == true,
            SceneTimeSeconds = _deterministic.IsChecked == true ? 0 : settings.SceneTimeSeconds,
        });
        _waterline.LostKeyboardFocus += (_, _) => CommitWaterline();
        _waterline.KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.Key != Key.Enter)
                return;
            CommitWaterline();
            eventArgs.Handled = true;
        };

        Update(_read(), new PreviewScenePresentationState(PreviewSceneKind.Grid, PreviewSceneOcclusion.Normal, "Grid inspection scene."));
    }

    public void Update(PreviewSceneSettings settings, PreviewScenePresentationState state)
    {
        _updating = true;
        try
        {
            _grid.IsChecked = settings.Kind == PreviewSceneKind.Grid;
            _ocean.IsChecked = settings.Kind == PreviewSceneKind.Ocean;
            _gridOverlay.IsChecked = settings.ShowGridOverlay;
            _lowDetail.IsChecked = settings.LowDetail;
            _reducedMotion.IsChecked = settings.ReducedMotion;
            _deterministic.IsChecked = settings.Deterministic;
            _waterline.Text = settings.Waterline.ToString("0.##", CultureInfo.CurrentCulture);
            _waterline.IsEnabled = settings.Kind == PreviewSceneKind.Ocean;
            // Ocean uses the derived freeboard control directly beneath the scene bar; the raw
            // waterline row stays available to Grid and dormant Dock in Options.
            _waterlineRow.Visibility = settings.Kind == PreviewSceneKind.Ocean
                ? Visibility.Collapsed
                : Visibility.Visible;
            var deckDatum = _readDeckDatum();
            var showFreeboard = deckDatum is not null &&
                                PreviewSceneFreeboard.IsAvailable(settings.Kind);
            _freeboardRow.Visibility = showFreeboard ? Visibility.Visible : Visibility.Collapsed;
            if (showFreeboard)
            {
                var datum = deckDatum.GetValueOrDefault();
                var freeboard = PreviewSceneFreeboard.FromWaterline(
                    datum.ReferenceDeckElevationMetres, settings.Waterline);
                var (minimum, maximum) = PreviewSceneFreeboard.Range(datum, freeboard);
                _freeboardSlider.Minimum = minimum;
                _freeboardSlider.Maximum = maximum;
                _freeboardSlider.Value = Math.Clamp(freeboard, minimum, maximum);
                _freeboardReadout.Text = $"{freeboard:0.##} m · visual only";
            }
            _status.Text = state.StatusText;
            _occlusionBadge.Text = state.Occlusion switch
            {
                PreviewSceneOcclusion.Hidden => "Environment hidden",
                PreviewSceneOcclusion.Faded => "Environment faded",
                _ => string.Empty,
            };
            _occlusionBadge.Visibility = state.Occlusion == PreviewSceneOcclusion.Normal
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
        finally
        {
            _updating = false;
        }
    }

    protected override int VisualChildrenCount => _visuals.Count;

    protected override Visual GetVisualChild(int index) => _visuals[index];

    protected override Size MeasureOverride(Size constraint)
    {
        _root.Measure(constraint);
        return constraint;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _root.Arrange(new Rect(finalSize));
        return finalSize;
    }

    private void SelectScene(PreviewSceneKind kind) => Change(settings => settings with { Kind = kind });

    private void ChangeWaterline(double delta) => Change(settings => settings with { Waterline = settings.Waterline + delta });

    private void CommitFreeboard()
    {
        if (_updating)
            return;
        if (_readDeckDatum() is not { } datum)
            return;
        Change(settings => settings with
        {
            Waterline = PreviewSceneFreeboard.ToWaterline(
                datum.ReferenceDeckElevationMetres, _freeboardSlider.Value),
        });
    }

    private void CommitWaterline()
    {
        if (_updating)
            return;
        if (!double.TryParse(_waterline.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) ||
            !double.IsFinite(value) || Math.Abs(value) > 10_000)
        {
            Update(_read(), new PreviewScenePresentationState(_read().Kind, PreviewSceneOcclusion.Normal, "Enter a finite waterline within +/-10,000 m."));
            return;
        }
        Change(settings => settings with { Waterline = value });
    }

    private void Change(Func<PreviewSceneSettings, PreviewSceneSettings> update)
    {
        if (_updating)
            return;
        var next = update(_read());
        _write(next);
    }

    private static ToggleButton SceneButton(string content)
    {
        var button = new ToggleButton { Content = content, Margin = new Thickness(4, 0, 0, 0), MinHeight = 28 };
        button.SetResourceReference(FrameworkElement.StyleProperty, "CompactToggleStyle");
        return button;
    }

    private static Button CompactButton(string content, string tooltip)
    {
        var button = new Button { Content = content, ToolTip = tooltip, MinWidth = 28, MinHeight = 28, Margin = new Thickness(4, 0, 0, 0) };
        button.SetResourceReference(FrameworkElement.StyleProperty, "CompactButtonStyle");
        return button;
    }
}
