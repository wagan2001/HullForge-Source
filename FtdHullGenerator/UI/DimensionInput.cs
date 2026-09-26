using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Input;

namespace FtdHullGenerator.UI;

/// <summary>
/// Integer text entry paired with a convenient bounded slider. Text entry is not
/// clamped to the slider maximum, so unusually large hulls remain expressible.
/// The row reads like an instrument channel: name, digits, unit, then a graduated
/// travel with its end stops labelled.
/// </summary>
public sealed class DimensionInput : UserControl
{
    private readonly TextBlock _label;
    private readonly TextBlock _unitLabel;
    private readonly TextBlock _sliderMinimumLabel;
    private readonly TextBlock _sliderMaximumLabel;
    private readonly Slider _slider;
    private readonly TextBox _textBox;
    private readonly RepeatButton _decrementButton;
    private readonly RepeatButton _incrementButton;
    private bool _syncing;

    public DimensionInput()
    {
        var panel = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.ColumnDefinitions.Add(new ColumnDefinition());
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });

        _label = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
        };
        _label.SetResourceReference(TextBlock.FontFamilyProperty, "PanelFont");
        _label.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        _label.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(Label)) { Source = this });

        _textBox = new TextBox
        {
            Margin = new Thickness(8, 0, 0, 4),
            Padding = new Thickness(6, 3, 6, 3),
            MinHeight = 28,
            TextAlignment = TextAlignment.Right,
        };
        _textBox.PreviewMouseWheel += (_, args) =>
        {
            Nudge(Math.Sign(args.Delta));
            args.Handled = true;
        };
        Grid.SetColumn(_textBox, 1);

        _unitLabel = new TextBlock
        {
            Margin = new Thickness(6, 0, 0, 4),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 11,
        };
        _unitLabel.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        _unitLabel.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        _unitLabel.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(Unit)) { Source = this });
        Grid.SetColumn(_unitLabel, 3);

        // Steppers. They repeat on hold, and they move by Step, so a row that counts in
        // odd metres steps 19 to 21 rather than landing on the even value in between.
        _decrementButton = CreateStepperButton(up: false, "Step down");
        _incrementButton = CreateStepperButton(up: true, "Step up");
        _decrementButton.Click += (_, _) => Nudge(-1);
        _incrementButton.Click += (_, _) => Nudge(1);
        var steppers = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(4, 0, 0, 4),
            VerticalAlignment = VerticalAlignment.Center,
        };
        steppers.Children.Add(_decrementButton);
        steppers.Children.Add(_incrementButton);
        Grid.SetColumn(steppers, 2);

        // The slider is not snapped to its ticks: the ticks are graduations at a readable
        // interval, while whole-metre stepping comes from rounding in the value handler.
        _slider = new Slider
        {
            TickPlacement = System.Windows.Controls.Primitives.TickPlacement.BottomRight,
            IsSnapToTickEnabled = false,
            SmallChange = 1,
            LargeChange = 10,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _slider.SetBinding(Slider.MinimumProperty, new System.Windows.Data.Binding(nameof(SliderMinimum)) { Source = this });
        _slider.SetBinding(Slider.MaximumProperty, new System.Windows.Data.Binding(nameof(SliderMaximum)) { Source = this });
        Grid.SetRow(_slider, 1);
        Grid.SetColumnSpan(_slider, 4);

        // End-stop captions state the slider's travel, which is narrower than the
        // range the text box accepts.
        var scale = new Grid { Margin = new Thickness(0, 1, 0, 0) };
        scale.ColumnDefinitions.Add(new ColumnDefinition());
        scale.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _sliderMinimumLabel = CreateScaleLabel(string.Empty);
        _sliderMaximumLabel = CreateScaleLabel(string.Empty);
        Grid.SetColumn(_sliderMaximumLabel, 1);
        scale.Children.Add(_sliderMinimumLabel);
        scale.Children.Add(_sliderMaximumLabel);
        Grid.SetRow(scale, 2);
        Grid.SetColumnSpan(scale, 4);

        _textBox.TextChanged += (_, _) =>
        {
            if (_syncing)
                return;
            if (TryGetValue(out var value))
            {
                _syncing = true;
                Value = value;
                _slider.Value = Math.Clamp(value, SliderMinimum, SliderMaximum);
                _syncing = false;
            }
            ValueChanged?.Invoke(this, new RoutedEventArgs());
        };
        _slider.ValueChanged += (_, _) =>
        {
            if (_syncing)
                return;
            SetText(Quantize((int)Math.Round(_slider.Value, MidpointRounding.AwayFromZero)));
            ValueChanged?.Invoke(this, new RoutedEventArgs());
        };

        panel.Children.Add(_label);
        panel.Children.Add(_textBox);
        panel.Children.Add(_unitLabel);
        panel.Children.Add(steppers);
        panel.Children.Add(_slider);
        panel.Children.Add(scale);
        Content = panel;
        Loaded += (_, _) =>
        {
            SetText(Value);
            UpdateScaleCaption();
        };
    }

    public event RoutedEventHandler? ValueChanged;

    internal TextBox TextBoxForTests => _textBox;

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(DimensionInput), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(
        nameof(Unit), typeof(string), typeof(DimensionInput), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty SliderMinimumProperty = DependencyProperty.Register(
        nameof(SliderMinimum), typeof(double), typeof(DimensionInput),
        new PropertyMetadata(0d, (source, _) => ((DimensionInput)source).UpdateScaleCaption()));

    public static readonly DependencyProperty SliderMaximumProperty = DependencyProperty.Register(
        nameof(SliderMaximum), typeof(double), typeof(DimensionInput),
        new PropertyMetadata(500d, (source, _) => ((DimensionInput)source).UpdateScaleCaption()));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(int), typeof(DimensionInput),
        new PropertyMetadata(0, (source, args) => ((DimensionInput)source).ExternalValueChanged((int)args.NewValue)));

    /// <summary>
    /// How far one stepper press, or one notch of the slider, moves the value. A step of
    /// two paired with an odd <see cref="SliderMinimum" /> is what gives the width row its
    /// odd-only travel; the default of one leaves every other row counting normally.
    /// </summary>
    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(
        nameof(Step), typeof(int), typeof(DimensionInput),
        new PropertyMetadata(1, (source, _) => ((DimensionInput)source).UpdateStep()));

    /// <summary>
    /// Whether the row quantises to <see cref="Step" />. Turning this off leaves the step
    /// buttons moving by one and lets any whole value through, which is how the editor's
    /// debug option re-admits even widths without rebuilding the row.
    /// </summary>
    public static readonly DependencyProperty IsSteppedProperty = DependencyProperty.Register(
        nameof(IsStepped), typeof(bool), typeof(DimensionInput),
        new PropertyMetadata(true, (source, _) => ((DimensionInput)source).UpdateStep()));

    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public double SliderMinimum { get => (double)GetValue(SliderMinimumProperty); set => SetValue(SliderMinimumProperty, value); }
    public double SliderMaximum { get => (double)GetValue(SliderMaximumProperty); set => SetValue(SliderMaximumProperty, value); }
    public int Value { get => (int)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public int Step { get => (int)GetValue(StepProperty); set => SetValue(StepProperty, value); }
    public bool IsStepped { get => (bool)GetValue(IsSteppedProperty); set => SetValue(IsSteppedProperty, value); }

    /// <summary>The interval the row actually moves in, which is one unless stepping is on.</summary>
    private int EffectiveStep => IsStepped && Step > 1 ? Step : 1;

    public bool TryGetValue(out int value) =>
        int.TryParse(_textBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out value);

    public void SetDisplayedValue(int value) => SetText(value);

    /// <summary>
    /// Mirrors a value written from outside (a data binding or a caller) into the digits and the
    /// slider. Without this the row kept showing its construction-time value while the bound
    /// property had already changed.
    /// </summary>
    private void ExternalValueChanged(int value)
    {
        if (_syncing)
            return;
        SetText(value);
    }

    /// <summary>
    /// Builds one stepper. The glyph is drawn as a small chevron path rather than set as
    /// text, so the button does not depend on a symbol font being installed, and the face
    /// is taken from the shared key styling so it matches every other button on the panel.
    /// </summary>
    private static RepeatButton CreateStepperButton(bool up, string tooltip)
    {
        var chevron = new System.Windows.Shapes.Path
        {
            Data = System.Windows.Media.Geometry.Parse(up ? "M 0,3.5 L 4,0 L 8,3.5" : "M 0,0 L 4,3.5 L 8,0"),
            StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        chevron.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "PrimaryTextBrush");

        var button = new RepeatButton
        {
            Content = chevron,
            Width = 26,
            MinHeight = 28,
            Padding = new Thickness(0),
            Margin = new Thickness(up ? 3 : 0, 0, 0, 0),
            ToolTip = tooltip,
            Focusable = false,
            Delay = 400,
            Interval = 90,
        };
        button.SetResourceReference(StyleProperty, "StepperButtonStyle");
        return button;
    }

    /// <summary>
    /// Moves the value one step in a direction and reports it, clamped to the slider's
    /// travel so holding a stepper down stops at the end rather than running past it into
    /// values only the text box is meant to accept.
    /// </summary>
    private void Nudge(int direction)
    {
        var next = Quantize(Value + direction * EffectiveStep);
        if (next == Value)
            return;
        SetText(next);
        ValueChanged?.Invoke(this, new RoutedEventArgs());
    }

    /// <summary>The row's ladder origin: the first value its travel admits.</summary>
    private int Origin => (int)Math.Ceiling(SliderMinimum);

    /// <summary>The top of the row's travel.</summary>
    private int Ceiling => (int)Math.Floor(SliderMaximum);

    /// <summary>Snaps a value onto this row's ladder, held inside the slider's travel.</summary>
    private int Quantize(int value) => StepLadder.Snap(value, Origin, EffectiveStep, Ceiling);

    /// <summary>
    /// Re-applies the ladder after the step or the stepping switch changes. Snapping to
    /// the nearest rung can land above the slider maximum — a width of 100 rounds up to
    /// 101 on the odd ladder — so a value at the top of the travel comes down onto the
    /// last rung instead of stepping off the end of it.
    /// </summary>
    private void UpdateStep()
    {
        _slider.SmallChange = EffectiveStep;
        _slider.LargeChange = Math.Max(EffectiveStep, 10);
        var snapped = Quantize(Value);
        if (snapped == Value)
            return;
        SetText(snapped);
        ValueChanged?.Invoke(this, new RoutedEventArgs());
    }

    private static TextBlock CreateScaleLabel(string text)
    {
        var label = new TextBlock { Text = text, FontSize = 10 };
        label.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        label.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        return label;
    }

    private void UpdateScaleCaption()
    {
        _sliderMinimumLabel.Text = SliderMinimum.ToString("0", CultureInfo.CurrentCulture);
        _sliderMaximumLabel.Text = SliderMaximum.ToString("0", CultureInfo.CurrentCulture);
        _slider.TickFrequency = GraduationStep(SliderMinimum, SliderMaximum);
    }

    /// <summary>
    /// The interval between tick marks under the slider. Rows span very different
    /// travels (0–500 m, 0–100 m, 2–30 %, −15–15 %), so one fixed count of ticks would
    /// crowd some rows and leave others bare.
    /// </summary>
    private static double GraduationStep(double minimum, double maximum)
    {
        var travel = maximum - minimum;
        if (!double.IsFinite(travel) || travel <= 0)
            return 1;

        // Aim for eight intervals, then round that rough step up to the next 1, 2, or 5
        // times a power of ten, so a 0-500 row graduates every 100 and a 0-100 row every
        // 20 rather than at whatever an even division happens to produce. Every row counts
        // a whole quantity, so a step below one metre or one percent is floored away.
        var rough = travel / 8;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(rough)));
        var mantissa = rough / magnitude;
        var rounded = mantissa switch
        {
            <= 1 => 1,
            <= 2 => 2,
            <= 5 => 5,
            _ => 10,
        };
        return Math.Max(1, rounded * magnitude);
    }

    private void SetText(int value)
    {
        _syncing = true;
        Value = value;
        _textBox.Text = value.ToString(CultureInfo.CurrentCulture);
        _slider.Value = Math.Clamp(value, SliderMinimum, SliderMaximum);
        _syncing = false;
    }
}
