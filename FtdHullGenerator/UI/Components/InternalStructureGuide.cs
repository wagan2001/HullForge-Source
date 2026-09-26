using System.Windows;
using System.Windows.Media;
using FtdHullGenerator.Domain.Components;

namespace FtdHullGenerator.UI.Components;

public enum InternalGuideView
{
    Front,
    Side,
}

/// <summary>A compact axis-labelled schematic; it never substitutes for resolved hull geometry.</summary>
public sealed class InternalStructureGuide : FrameworkElement
{
    public static readonly DependencyProperty FamilyProperty = DependencyProperty.Register(
        nameof(Family), typeof(InternalPlaneFamily), typeof(InternalStructureGuide),
        new FrameworkPropertyMetadata(InternalPlaneFamily.LongitudinalBulkhead,
            FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ViewProperty = DependencyProperty.Register(
        nameof(View), typeof(InternalGuideView), typeof(InternalStructureGuide),
        new FrameworkPropertyMetadata(InternalGuideView.Front,
            FrameworkPropertyMetadataOptions.AffectsRender));

    // Palette brushes are attached as dynamic resource references so a theme swap silently
    // re-renders the schematic; TryFindResource inside OnRender would keep the old palette.
    public static readonly DependencyProperty OutlineBrushProperty = DependencyProperty.Register(
        nameof(OutlineBrush), typeof(Brush), typeof(InternalStructureGuide),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PlaneBrushProperty = DependencyProperty.Register(
        nameof(PlaneBrush), typeof(Brush), typeof(InternalStructureGuide),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelBrushProperty = DependencyProperty.Register(
        nameof(LabelBrush), typeof(Brush), typeof(InternalStructureGuide),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public InternalStructureGuide()
    {
        SetResourceReference(OutlineBrushProperty, "SecondaryTextBrush");
        SetResourceReference(PlaneBrushProperty, "AccentBrush");
        SetResourceReference(LabelBrushProperty, "MutedTextBrush");
    }

    public InternalPlaneFamily Family
    {
        get => (InternalPlaneFamily)GetValue(FamilyProperty);
        set => SetValue(FamilyProperty, value);
    }

    public InternalGuideView View
    {
        get => (InternalGuideView)GetValue(ViewProperty);
        set => SetValue(ViewProperty, value);
    }

    public Brush? OutlineBrush
    {
        get => (Brush?)GetValue(OutlineBrushProperty);
        set => SetValue(OutlineBrushProperty, value);
    }

    public Brush? PlaneBrush
    {
        get => (Brush?)GetValue(PlaneBrushProperty);
        set => SetValue(PlaneBrushProperty, value);
    }

    public Brush? LabelBrush
    {
        get => (Brush?)GetValue(LabelBrushProperty);
        set => SetValue(LabelBrushProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var width = Math.Max(1, ActualWidth);
        var height = Math.Max(1, ActualHeight);
        var outline = OutlineBrush ?? Brushes.Gray;
        var accent = PlaneBrush ?? Brushes.Orange;
        var muted = LabelBrush ?? Brushes.DarkGray;
        var outlinePen = new Pen(outline, 1.2);
        var planePen = new Pen(accent, 3);
        var left = 18d;
        var top = 18d;
        var right = width - 10;
        var bottom = height - 18;

        if (View == InternalGuideView.Front)
        {
            var hull = new StreamGeometry();
            using (var context = hull.Open())
            {
                context.BeginFigure(new Point(left, top + 8), true, true);
                context.LineTo(new Point(right, top + 8), true, false);
                context.QuadraticBezierTo(new Point(right - 8, bottom - 5),
                    new Point(width / 2, bottom), true, false);
                context.QuadraticBezierTo(new Point(left + 8, bottom - 5),
                    new Point(left, top + 8), true, false);
            }
            drawingContext.DrawGeometry(null, outlinePen, hull);
            if (Family == InternalPlaneFamily.LongitudinalBulkhead)
                drawingContext.DrawLine(planePen, new Point(width / 2, top + 8), new Point(width / 2, bottom));
            else if (Family == InternalPlaneFamily.InternalDeck)
                drawingContext.DrawLine(planePen, new Point(left + 5, height / 2), new Point(right - 5, height / 2));
            else
                drawingContext.DrawRectangle(accent, null, new Rect(left + 3, top + 11, right - left - 6, bottom - top - 14));
            Label(drawingContext, "X →", new Point(right - 25, bottom - 14), muted);
            Label(drawingContext, "Y ↑", new Point(left, 1), muted);
            Label(drawingContext, "FRONT", new Point(width / 2 - 18, 1), muted);
        }
        else
        {
            drawingContext.DrawRoundedRectangle(null, outlinePen,
                new Rect(left, top + 8, right - left, bottom - top - 10), 22, 12);
            if (Family == InternalPlaneFamily.TransverseBulkhead)
                drawingContext.DrawLine(planePen, new Point(width / 2, top + 9), new Point(width / 2, bottom - 3));
            else if (Family == InternalPlaneFamily.InternalDeck)
                drawingContext.DrawLine(planePen, new Point(left + 4, height / 2), new Point(right - 4, height / 2));
            else
                drawingContext.DrawRectangle(accent, null, new Rect(left + 4, top + 12, right - left - 8, bottom - top - 18));
            Label(drawingContext, "Z → BOW", new Point(right - 50, bottom - 14), muted);
            Label(drawingContext, "Y ↑", new Point(left, 1), muted);
            Label(drawingContext, "SIDE", new Point(width / 2 - 14, 1), muted);
        }
    }

    private void Label(DrawingContext context, string text, Point origin, Brush brush)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        context.DrawText(new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, new Typeface("Segoe UI"), 8, brush, dpi), origin);
    }
}
