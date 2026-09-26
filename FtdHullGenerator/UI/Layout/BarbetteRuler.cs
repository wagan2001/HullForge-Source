using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using FtdHullGenerator.Domain.Design;
using FtdHullGenerator.Geometry.Components;

namespace FtdHullGenerator.UI.Layout;

/// <summary>Raised when the user drags or nudges one barbette to a new ruler position.</summary>
public sealed class BarbetteMovedEventArgs(string barbetteId, DesignMeasure rulerCenter) : EventArgs
{
    public string BarbetteId { get; } = barbetteId;
    public DesignMeasure RulerCenter { get; } = rulerCenter;
}

/// <summary>
/// Presentation-only bow-to-stern barbette ruler. It draws the frozen 2.0 readout model verbatim,
/// snaps every drag to a whole-metre cell anchor, and reports one moved barbette at a time. It owns
/// no geometry rule: clear extents and validity come from the supplied model.
/// </summary>
public sealed class BarbetteRuler : FrameworkElement
{
    private const double LeftMargin = 30;
    private const double RightMargin = 30;
    private const double HitRadius = 16;

    private string? _draggingId;

    public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(
        nameof(Model), typeof(BarbetteRulerModel), typeof(BarbetteRuler),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelectedBarbetteIdProperty = DependencyProperty.Register(
        nameof(SelectedBarbetteId), typeof(string), typeof(BarbetteRuler),
        new FrameworkPropertyMetadata(null,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty SurfaceBrushProperty = DependencyProperty.Register(
        nameof(SurfaceBrush), typeof(Brush), typeof(BarbetteRuler),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty OutlineBrushProperty = DependencyProperty.Register(
        nameof(OutlineBrush), typeof(Brush), typeof(BarbetteRuler),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TextBrushProperty = DependencyProperty.Register(
        nameof(TextBrush), typeof(Brush), typeof(BarbetteRuler),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(
        nameof(AccentBrush), typeof(Brush), typeof(BarbetteRuler),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty WarningBrushProperty = DependencyProperty.Register(
        nameof(WarningBrush), typeof(Brush), typeof(BarbetteRuler),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ErrorBrushProperty = DependencyProperty.Register(
        nameof(ErrorBrush), typeof(Brush), typeof(BarbetteRuler),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelectionBrushProperty = DependencyProperty.Register(
        nameof(SelectionBrush), typeof(Brush), typeof(BarbetteRuler),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public BarbetteRuler()
    {
        Focusable = true;
        SetResourceReference(SurfaceBrushProperty, "RecessBrush");
        SetResourceReference(OutlineBrushProperty, "PanelBorderBrush");
        SetResourceReference(TextBrushProperty, "SecondaryTextBrush");
        SetResourceReference(AccentBrushProperty, "AccentBrush");
        SetResourceReference(WarningBrushProperty, "WarningBrush");
        SetResourceReference(ErrorBrushProperty, "ErrorBrush");
        SetResourceReference(SelectionBrushProperty, "SelectionBrush");
    }

    public event EventHandler<BarbetteMovedEventArgs>? BarbetteMoved;

    public BarbetteRulerModel? Model
    {
        get => (BarbetteRulerModel?)GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    public string? SelectedBarbetteId
    {
        get => (string?)GetValue(SelectedBarbetteIdProperty);
        set => SetValue(SelectedBarbetteIdProperty, value);
    }

    public Brush? SurfaceBrush
    {
        get => (Brush?)GetValue(SurfaceBrushProperty);
        set => SetValue(SurfaceBrushProperty, value);
    }

    public Brush? OutlineBrush
    {
        get => (Brush?)GetValue(OutlineBrushProperty);
        set => SetValue(OutlineBrushProperty, value);
    }

    public Brush? TextBrush
    {
        get => (Brush?)GetValue(TextBrushProperty);
        set => SetValue(TextBrushProperty, value);
    }

    public Brush? AccentBrush
    {
        get => (Brush?)GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    public Brush? WarningBrush
    {
        get => (Brush?)GetValue(WarningBrushProperty);
        set => SetValue(WarningBrushProperty, value);
    }

    public Brush? ErrorBrush
    {
        get => (Brush?)GetValue(ErrorBrushProperty);
        set => SetValue(ErrorBrushProperty, value);
    }

    public Brush? SelectionBrush
    {
        get => (Brush?)GetValue(SelectionBrushProperty);
        set => SetValue(SelectionBrushProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(Math.Max(620, double.IsInfinity(availableSize.Width) ? 620 : availableSize.Width), 138);

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs eventArgs)
    {
        base.OnMouseLeftButtonDown(eventArgs);
        Focus();
        var entry = HitTestEntry(eventArgs.GetPosition(this).X);
        if (entry is null)
            return;

        SelectedBarbetteId = entry.BarbetteId;
        _draggingId = entry.BarbetteId;
        CaptureMouse();
        eventArgs.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs eventArgs)
    {
        base.OnMouseMove(eventArgs);
        if (_draggingId is null || eventArgs.LeftButton != MouseButtonState.Pressed)
            return;
        MoveTo(_draggingId, eventArgs.GetPosition(this).X);
        eventArgs.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs eventArgs)
    {
        base.OnMouseLeftButtonUp(eventArgs);
        if (_draggingId is null)
            return;
        MoveTo(_draggingId, eventArgs.GetPosition(this).X);
        _draggingId = null;
        ReleaseMouseCapture();
        eventArgs.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs eventArgs)
    {
        base.OnLostMouseCapture(eventArgs);
        _draggingId = null;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs eventArgs)
    {
        base.OnPreviewKeyDown(eventArgs);
        if (SelectedBarbetteId is not { } id || Model is not { } model)
            return;
        var entry = model.FindEntry(id);
        if (entry is null)
            return;

        // One arrow press is exactly one block: a whole-metre world-Z step on the snap lattice.
        var delta = eventArgs.Key switch
        {
            Key.Left => DesignMeasure.FromMetres(-1),
            Key.Right => DesignMeasure.FromMetres(1),
            _ => (DesignMeasure?)null,
        };
        if (delta is not { } step)
            return;

        var requested = model.SnapToCellAnchor(entry.RulerCenter + step);
        BarbetteMoved?.Invoke(this, new BarbetteMovedEventArgs(id, requested));
        eventArgs.Handled = true;
    }

    /// <summary>The horizontal position of a ruler coordinate, for deterministic interaction probes.</summary>
    internal double XForTests(DesignMeasure rulerCenter)
    {
        var end = Math.Max(1, Model?.RulerEnd.Metres ?? 1);
        var left = LeftMargin;
        var right = Math.Max(left + 1, ActualWidth - RightMargin);
        return left + Math.Clamp(rulerCenter.Metres / end, 0, 1) * (right - left);
    }

    internal BarbetteRulerEntry? HitTestForTests(double x) => HitTestEntry(x);

    internal void DragToForTests(string barbetteId, double x) => MoveTo(barbetteId, x);

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var model = Model;
        var background = SurfaceBrush ?? Brushes.Transparent;
        var border = OutlineBrush ?? Brushes.Gray;
        var text = TextBrush ?? Brushes.LightGray;
        var accent = AccentBrush ?? Brushes.DeepSkyBlue;
        var warning = WarningBrush ?? Brushes.Orange;
        var error = ErrorBrush ?? Brushes.Red;
        var selection = SelectionBrush ?? Brushes.Transparent;

        drawingContext.DrawRoundedRectangle(background, new Pen(border, 1),
            new Rect(0.5, 0.5, Math.Max(0, ActualWidth - 1), Math.Max(0, ActualHeight - 1)), 2, 2);

        if (model is null || !model.HasEntries)
        {
            DrawText(drawingContext, "No centerline barbettes yet. Add one to place it on the ruler.",
                14, 14, text, 11);
            return;
        }

        var end = Math.Max(1, model.RulerEnd.Metres);
        var left = LeftMargin;
        var right = Math.Max(left + 1, ActualWidth - RightMargin);
        double X(DesignMeasure s) => left + Math.Clamp(s.Metres / end, 0, 1) * (right - left);

        var axisY = 92d;
        drawingContext.DrawLine(new Pen(border, 1), new Point(left, axisY), new Point(right, axisY));
        var metreEnd = Math.Max(1, (int)Math.Ceiling(end));
        var step = metreEnd <= 40 ? 5 : metreEnd <= 100 ? 10 : 20;
        for (var metre = 0; metre <= metreEnd; metre += step)
        {
            var x = X(DesignMeasure.FromMetres(metre));
            drawingContext.DrawLine(new Pen(border, 1), new Point(x, axisY - 4), new Point(x, axisY + 5));
            DrawText(drawingContext, metre.ToString(CultureInfo.InvariantCulture), x - 5, axisY + 9, text, 9);
        }

        foreach (var entry in model.Entries)
        {
            var selected = string.Equals(entry.BarbetteId, SelectedBarbetteId, StringComparison.Ordinal);
            var brush = entry.IsBlocking ? error : selected ? accent : text;
            var barLeft = X(entry.ClearBowEdge);
            var barRight = X(entry.ClearSternEdge);
            if (selected)
                drawingContext.DrawRoundedRectangle(selection, null,
                    new Rect(Math.Min(barLeft, barRight) - 4, 24, Math.Abs(barRight - barLeft) + 8, 22), 3, 3);
            drawingContext.DrawRoundedRectangle(null, new Pen(brush, selected ? 3 : 2),
                new Rect(Math.Min(barLeft, barRight), 27, Math.Max(2, Math.Abs(barRight - barLeft)), 16), 2, 2);

            var centerX = X(entry.RulerCenter);
            drawingContext.DrawLine(new Pen(brush, selected ? 2 : 1),
                new Point(centerX, 23), new Point(centerX, 47));
            DrawText(drawingContext, $"{entry.BarbetteId}", Math.Max(left, centerX - 24), 9, brush, 10);
        }

        foreach (var segment in model.Segments)
        {
            var forward = model.FindEntry(segment.ForwardBarbetteId);
            var aft = model.FindEntry(segment.AftBarbetteId);
            if (forward is null || aft is null)
                continue;
            var x0 = X(forward.ClearSternEdge);
            var x1 = X(aft.ClearBowEdge);
            var brush = segment.State switch
            {
                BarbetteRulerSegmentState.Overlapping => error,
                BarbetteRulerSegmentState.BelowMinimumSeparation => warning,
                _ => text,
            };
            var pen = new Pen(brush, segment.State == BarbetteRulerSegmentState.Valid ? 1 : 2);
            drawingContext.DrawLine(pen, new Point(x0, 60), new Point(x1, 60));
            drawingContext.DrawLine(pen, new Point(x0, 57), new Point(x0, 63));
            drawingContext.DrawLine(pen, new Point(x1, 57), new Point(x1, 63));
            var label = $"{segment.ClearGap.Metres:0.#} m";
            DrawText(drawingContext, label, Math.Max(left, (x0 + x1) / 2 - 16), 65, brush, 10);
        }

        if (model.BowMargin is { } bowMargin)
            DrawText(drawingContext, $"bow {bowMargin.Metres:0.#} m", left, 118, text, 10);
        if (model.SternMargin is { } sternMargin)
            DrawText(drawingContext, $"stern {sternMargin.Metres:0.#} m", right - 78, 118, text, 10);
        DrawText(drawingContext, "BOW", left, 124, text, 9);
        DrawText(drawingContext, "STERN", right - 38, 124, text, 9);
    }

    private void MoveTo(string barbetteId, double x)
    {
        if (Model is not { } model)
            return;
        var left = LeftMargin;
        var right = Math.Max(left + 1, ActualWidth - RightMargin);
        var end = Math.Max(1, model.RulerEnd.Metres);
        var metres = (x - left) / (right - left) * end;
        var requested = model.SnapToCellAnchor(DesignMeasure.FromMetres(metres));
        var clamped = requested.Clamp(DesignMeasure.Zero, model.RulerEnd);
        BarbetteMoved?.Invoke(this, new BarbetteMovedEventArgs(barbetteId, clamped));
    }

    private BarbetteRulerEntry? HitTestEntry(double x)
    {
        if (Model is not { } model || !model.HasEntries)
            return null;
        var left = LeftMargin;
        var right = Math.Max(left + 1, ActualWidth - RightMargin);
        var end = Math.Max(1, model.RulerEnd.Metres);
        double X(DesignMeasure s) => left + Math.Clamp(s.Metres / end, 0, 1) * (right - left);

        BarbetteRulerEntry? closest = null;
        var closestDistance = double.MaxValue;
        foreach (var entry in model.Entries)
        {
            var centerX = X(entry.RulerCenter);
            var distance = Math.Abs(centerX - x);
            if (distance <= HitRadius && distance < closestDistance)
            {
                closest = entry;
                closestDistance = distance;
            }
        }

        if (closest is not null)
            return closest;

        foreach (var entry in model.Entries)
        {
            if (x >= X(entry.ClearBowEdge) && x <= X(entry.ClearSternEdge))
                return entry;
        }

        return null;
    }

    private void DrawText(DrawingContext context, string value, double x, double y, Brush brush, double size)
    {
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal,
            FontWeights.Normal, FontStretches.Normal);
        var formatted = new FormattedText(value, CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight, typeface, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        context.DrawText(formatted, new Point(x, y));
    }
}
