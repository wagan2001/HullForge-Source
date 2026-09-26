using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using FtdHullGenerator.Domain.Design;

namespace FtdHullGenerator.UI.Components;

/// <summary>
/// Accessible half-metre numeric entry over the complete design-coordinate range. Unlike a
/// convenience picker it never makes a valid loaded value uneditable merely because it is large.
/// </summary>
public sealed class DesignMeasureInput : UserControl
{
    private readonly TextBox _textBox;
    private bool _syncing;

    public DesignMeasureInput()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _textBox = new TextBox
        {
            MinHeight = 28,
            Padding = new Thickness(6, 3, 6, 3),
            TextAlignment = TextAlignment.Right,
        };
        _textBox.TextChanged += (_, _) => CommitText(restoreInvalid: false);
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
        var down = StepButton("−", "Decrease by half a metre");
        var up = StepButton("+", "Increase by half a metre");
        down.Click += (_, _) => Nudge(-1);
        up.Click += (_, _) => Nudge(1);
        steppers.Children.Add(down);
        steppers.Children.Add(up);
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
            AutomationProperties.SetName(_textBox, AutomationName);
        };
    }

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(DesignMeasureInput),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (source, args) => ((DesignMeasureInput)source).ExternalValueChanged((double)args.NewValue)));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(DesignMeasureInput),
        new PropertyMetadata(-DesignLimits.MaxDesignTwiceMetres / 2d));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(DesignMeasureInput),
        new PropertyMetadata(DesignLimits.MaxDesignTwiceMetres / 2d));

    public static readonly DependencyProperty AutomationNameProperty = DependencyProperty.Register(
        nameof(AutomationName), typeof(string), typeof(DesignMeasureInput),
        new PropertyMetadata("Design measurement", (source, args) =>
            AutomationProperties.SetName(((DesignMeasureInput)source)._textBox, (string)args.NewValue)));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public string AutomationName { get => (string)GetValue(AutomationNameProperty); set => SetValue(AutomationNameProperty, value); }

    internal TextBox TextBoxForTests => _textBox;

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
        var basis = TryParse(_textBox.Text, out var parsed) ? parsed : Value;
        Value = Math.Clamp(basis + direction * 0.5, Minimum, Maximum);
        UpdateText(Value);
    }

    private void CommitText(bool restoreInvalid)
    {
        if (_syncing)
            return;
        if (TryParse(_textBox.Text, out var parsed) && parsed >= Minimum && parsed <= Maximum)
        {
            var exact = DesignMeasure.FromMetres(parsed).Metres;
            if (Math.Abs(exact - parsed) < 0.0000001)
                SetCurrentValue(ValueProperty, exact);
            return;
        }
        if (restoreInvalid)
            UpdateText(Value);
    }

    private static bool TryParse(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) &&
        double.IsFinite(value);

    private void ExternalValueChanged(double value)
    {
        if (_syncing)
            return;
        UpdateText(value);
    }

    private void UpdateText(double value)
    {
        _syncing = true;
        _textBox.Text = value.ToString("0.0#", CultureInfo.CurrentCulture);
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
