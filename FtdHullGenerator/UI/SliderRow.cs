using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace FtdHullGenerator.UI;

/// <summary>
/// A named curve control: label, graduated travel, and a monospace digit field that
/// always shows the committed value.
/// </summary>
public sealed class SliderRow : UserControl
{
    private readonly TextBox _valueBox;
    private readonly Slider _slider;
    private bool _syncing;

    public SliderRow()
    {
        var panel = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(94) });
        panel.ColumnDefinitions.Add(new ColumnDefinition());
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });

        var label = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
        };
        label.SetResourceReference(TextBlock.FontFamilyProperty, "PanelFont");
        label.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        label.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(Label)) { Source = this });
        Grid.SetColumn(label, 0);
        panel.Children.Add(label);

        _slider = new Slider
        {
            TickFrequency = 0.3,
            TickPlacement = TickPlacement.BottomRight,
            IsSnapToTickEnabled = false,
            SmallChange = 0.05,
            LargeChange = 0.3,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _slider.SetBinding(Slider.MinimumProperty, new System.Windows.Data.Binding(nameof(Minimum)) { Source = this });
        _slider.SetBinding(Slider.MaximumProperty, new System.Windows.Data.Binding(nameof(Maximum)) { Source = this });
        _slider.SetBinding(Slider.ValueProperty, new System.Windows.Data.Binding(nameof(Value)) { Source = this, Mode = System.Windows.Data.BindingMode.TwoWay });
        _slider.ValueChanged += (_, _) =>
        {
            if (_syncing)
                return;
            UpdateText(_slider.Value);
            ValueChanged?.Invoke(this, new RoutedEventArgs());
        };
        Grid.SetColumn(_slider, 1);
        panel.Children.Add(_slider);

        _valueBox = new TextBox
        {
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(6, 3, 6, 3),
            MinHeight = 28,
            TextAlignment = TextAlignment.Right,
        };
        _valueBox.PreviewMouseWheel += (_, args) =>
        {
            var basis = TryGetValue(out var parsed) && parsed >= Minimum && parsed <= Maximum
                ? parsed : Value;
            var next = Math.Clamp(Math.Round(basis + Math.Sign(args.Delta) * _slider.SmallChange, 4), Minimum, Maximum);
            if (next != Value)
            {
                UpdateText(next);
                _syncing = true;
                _slider.Value = next;
                _syncing = false;
                ValueChanged?.Invoke(this, new RoutedEventArgs());
            }
            args.Handled = true;
        };
        _valueBox.TextChanged += (_, _) =>
        {
            if (_syncing)
                return;
            if (TryGetValue(out var value) && value >= Minimum && value <= Maximum)
            {
                _syncing = true;
                Value = value;
                _slider.Value = value;
                _syncing = false;
            }
            ValueChanged?.Invoke(this, new RoutedEventArgs());
        };
        _valueBox.LostKeyboardFocus += (_, _) => CommitText();
        _valueBox.KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.Key != System.Windows.Input.Key.Enter)
                return;
            CommitText();
            eventArgs.Handled = true;
        };
        Grid.SetColumn(_valueBox, 2);
        panel.Children.Add(_valueBox);

        Content = panel;
        Loaded += (_, _) => UpdateText(Value);
    }

    public event RoutedEventHandler? ValueChanged;

    internal TextBox TextBoxForTests => _valueBox;

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(SliderRow), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(SliderRow), new PropertyMetadata(-0.9d));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(SliderRow), new PropertyMetadata(0.9d));
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(SliderRow), new PropertyMetadata(0d));

    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public bool TryGetValue(out double value) =>
        double.TryParse(_valueBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) &&
        double.IsFinite(value);

    /// <summary>
    /// Sets the committed value and its digit field together. Callers that initialize the
    /// row programmatically (a preset or a loaded document) use this instead of the raw
    /// <see cref="Value"/> property so the visible field never lags the value.
    /// </summary>
    public void SetDisplayedValue(double value)
    {
        UpdateText(value);
        _slider.Value = Math.Clamp(value, Minimum, Maximum);
    }

    private void CommitText()
    {
        if (TryGetValue(out var value) && value >= Minimum && value <= Maximum)
        {
            _syncing = true;
            Value = value;
            _slider.Value = value;
            _syncing = false;
            UpdateText(value);
            ValueChanged?.Invoke(this, new RoutedEventArgs());
            return;
        }

        UpdateText(Value);
    }

    private void UpdateText(double value)
    {
        _syncing = true;
        Value = value;
        _valueBox.Text = value.ToString("+0.00;-0.00;0.00", CultureInfo.CurrentCulture);
        _syncing = false;
    }
}
