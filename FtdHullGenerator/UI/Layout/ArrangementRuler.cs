using System.Globalization;
using System.Windows;
using System.Windows.Media;
using FtdHullGenerator.Domain.Layout;
using FtdHullGenerator.Geometry.Layout;

namespace FtdHullGenerator.UI.Layout;

/// <summary>
/// Presentation-only bow-to-stern ruler. It draws solver output verbatim and contains no spacing,
/// snapping, support, or generation rules.
/// </summary>
public sealed class ArrangementRuler : FrameworkElement
{
    public static readonly DependencyProperty SolutionProperty = DependencyProperty.Register(
        nameof(Solution), typeof(ArrangementSolution), typeof(ArrangementRuler),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    // Palette brushes are attached as dynamic resource references so a theme swap silently
    // re-renders the ruler; TryFindResource inside OnRender would keep the old palette.
    public static readonly DependencyProperty SurfaceBrushProperty = DependencyProperty.Register(
        nameof(SurfaceBrush), typeof(Brush), typeof(ArrangementRuler),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty OutlineBrushProperty = DependencyProperty.Register(
        nameof(OutlineBrush), typeof(Brush), typeof(ArrangementRuler),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TextBrushProperty = DependencyProperty.Register(
        nameof(TextBrush), typeof(Brush), typeof(ArrangementRuler),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(
        nameof(AccentBrush), typeof(Brush), typeof(ArrangementRuler),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty WarningBrushProperty = DependencyProperty.Register(
        nameof(WarningBrush), typeof(Brush), typeof(ArrangementRuler),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public ArrangementRuler()
    {
        SetResourceReference(SurfaceBrushProperty, "RecessBrush");
        SetResourceReference(OutlineBrushProperty, "PanelBorderBrush");
        SetResourceReference(TextBrushProperty, "SecondaryTextBrush");
        SetResourceReference(AccentBrushProperty, "AccentBrush");
        SetResourceReference(WarningBrushProperty, "WarningBrush");
    }

    public ArrangementSolution? Solution
    {
        get => (ArrangementSolution?)GetValue(SolutionProperty);
        set => SetValue(SolutionProperty, value);
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

    protected override Size MeasureOverride(Size availableSize) =>
        new(Math.Max(620, double.IsInfinity(availableSize.Width) ? 620 : availableSize.Width), 142);

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var solution = Solution;
        var background = SurfaceBrush ?? Brushes.Transparent;
        var border = OutlineBrush ?? Brushes.Gray;
        var text = TextBrush ?? Brushes.LightGray;
        var accent = AccentBrush ?? Brushes.DeepSkyBlue;
        var warning = WarningBrush ?? Brushes.Orange;

        drawingContext.DrawRoundedRectangle(background, new Pen(border, 1),
            new Rect(0.5, 0.5, Math.Max(0, ActualWidth - 1), Math.Max(0, ActualHeight - 1)), 2, 2);

        if (solution is null)
        {
            DrawText(drawingContext, "No resolved arrangement", 14, 14, text, 11);
            return;
        }

        var end = Math.Max(1, (solution.SupportedRulerEnd ?? solution.RequiredRulerLength).TwiceMetres);
        var left = 28d;
        var right = Math.Max(left + 1, ActualWidth - 28d);
        var rulerY = 96d;
        double X(int twiceMetres) => left + Math.Clamp(twiceMetres / (double)end, 0, 1) * (right - left);

        // The silhouette is intentionally schematic: support geometry is resolved elsewhere.
        var silhouette = new StreamGeometry();
        using (var geometry = silhouette.Open())
        {
            geometry.BeginFigure(new Point(left, 74), false, false);
            geometry.LineTo(new Point(left + 24, 52), true, false);
            geometry.LineTo(new Point(right - 18, 52), true, false);
            geometry.LineTo(new Point(right, 66), true, false);
            geometry.LineTo(new Point(right - 12, 82), true, false);
            geometry.LineTo(new Point(left + 18, 82), true, false);
            geometry.LineTo(new Point(left, 74), true, false);
        }
        silhouette.Freeze();
        drawingContext.DrawGeometry(null, new Pen(border, 1), silhouette);

        foreach (var node in solution.Nodes)
        {
            var x0 = X(node.RulerSpan.Start.TwiceMetres);
            var x1 = X(node.RulerSpan.End.TwiceMetres);
            var nodeBrush = solution.IsSolved ? accent : warning;
            if (node.Kind == ArrangementNodeKind.Barbette)
            {
                var radius = Math.Max(5, (x1 - x0) / 2);
                drawingContext.DrawEllipse(null, new Pen(nodeBrush, 2),
                    new Point((x0 + x1) / 2, 65), radius, Math.Min(20, radius));
            }
            else
            {
                drawingContext.DrawRoundedRectangle(null, new Pen(nodeBrush, 2),
                    new Rect(x0, 47, Math.Max(2, x1 - x0), 36), 2, 2);
            }
            DrawText(drawingContext, node.NodeId, Math.Max(left, x0), 27, text, 10);

            foreach (var handle in node.Handles)
            {
                var hx = X(handle.RulerPosition.TwiceMetres);
                drawingContext.DrawLine(new Pen(nodeBrush, 1), new Point(hx, 43), new Point(hx, 87));
                DrawText(drawingContext, handle.Name, hx + 2, 84, text, 8);
            }
        }

        for (var index = 0; index < solution.Gaps.Count && index + 1 < solution.Nodes.Count; index++)
        {
            var gap = solution.Gaps[index];
            var x0 = X(solution.Nodes[index].RulerSpan.End.TwiceMetres);
            var x1 = X(solution.Nodes[index + 1].RulerSpan.Start.TwiceMetres);
            var gapPen = new Pen(text, 1);
            drawingContext.DrawLine(gapPen, new Point(x0, 91), new Point(x1, 91));
            drawingContext.DrawLine(gapPen, new Point(x0, 88), new Point(x0, 94));
            drawingContext.DrawLine(gapPen, new Point(x1, 88), new Point(x1, 94));
            var label = $"{gap.NamedGapId ?? gap.GapId} {gap.RealizedClearGap.Metres:0.#} m";
            DrawText(drawingContext, label, Math.Max(left, (x0 + x1) / 2 - 22), 98, text, 8);
        }

        drawingContext.DrawLine(new Pen(border, 1), new Point(left, rulerY), new Point(right, rulerY));
        var metreEnd = Math.Max(1, (int)Math.Ceiling(end / 2d));
        var step = metreEnd <= 40 ? 5 : metreEnd <= 100 ? 10 : 20;
        for (var metre = 0; metre <= metreEnd; metre += step)
        {
            var x = X(checked(metre * 2));
            drawingContext.DrawLine(new Pen(border, 1), new Point(x, rulerY - 4), new Point(x, rulerY + 5));
            DrawText(drawingContext, metre.ToString(CultureInfo.InvariantCulture), x - 4, rulerY + 23, text, 9);
        }
        DrawText(drawingContext, "BOW", left, 126, text, 9);
        DrawText(drawingContext, "STERN", right - 34, 126, text, 9);
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
