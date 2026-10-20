using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Gitruvian.Core;
using Gitruvian.Desktop.Infrastructure;
using Gitruvian.Desktop.ViewModels;

namespace Gitruvian.Desktop.Controls;

/// <summary>Desenha a parte do grafo de uma linha: raias (retas ou curvas), o nó do commit e merges.</summary>
public sealed class GraphCell : Control
{
    public const double LaneWidth = 16;
    public const double NodeRadius = 4.5;

    public static readonly StyledProperty<GraphItem?> ItemProperty =
        AvaloniaProperty.Register<GraphCell, GraphItem?>(nameof(Item));

    static GraphCell()
    {
        AffectsRender<GraphCell>(ItemProperty);
        AffectsMeasure<GraphCell>(ItemProperty);
    }

    public GraphItem? Item
    {
        get => GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    private static double X(int column) => LaneWidth / 2 + column * LaneWidth + 4;

    protected override Size MeasureOverride(Size availableSize) =>
        new(Math.Max(1, Item?.Lanes ?? 1) * LaneWidth + 8, 0);

    public override void Render(DrawingContext context)
    {
        var item = Item;
        if (item is null)
            return;
        var h = Bounds.Height;
        var mid = h / 2;

        if (item.IsWorking)
        {
            var x = X(item.HeadColumn);
            if (item.ConnectsToHead)
            {
                var dashed = new Pen(new SolidColorBrush(Palette.Muted), 2, new DashStyle([2, 2], 0));
                context.DrawLine(dashed, new Point(x, mid), new Point(x, h));
            }
            context.DrawEllipse(new SolidColorBrush(Palette.Background), new Pen(new SolidColorBrush(Palette.Muted), 2),
                new Point(x, mid), NodeRadius, NodeRadius);
            return;
        }

        var row = item.Row;
        if (row is null)
            return;
        foreach (var segment in row.Segments)
        {
            var pen = new Pen(new SolidColorBrush(Palette.Lane(segment.Color)), 2, lineCap: PenLineCap.Round);
            var (y0, y1) = segment.Half == SegmentHalf.Top ? (0.0, mid) : (mid, h);
            var x0 = X(segment.Start);
            var x1 = X(segment.End);
            if (segment.Half == SegmentHalf.Top && segment.Start == segment.End || segment.Half == SegmentHalf.Bottom && segment.Start == segment.End)
            {
                context.DrawLine(pen, new Point(x0, y0), new Point(x1, y1));
                continue;
            }
            // Curva suave entre colunas: de cima (start) até o nó, ou do nó até a raia de baixo (end).
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(new Point(x0, y0), false);
                ctx.CubicBezierTo(new Point(x0, (y0 + y1) / 2), new Point(x1, (y0 + y1) / 2), new Point(x1, y1));
                ctx.EndFigure(false);
            }
            context.DrawGeometry(null, pen, geometry);
        }
        if (item.ConnectsToHead)
        {
            var dashed = new Pen(new SolidColorBrush(Palette.Muted), 2, new DashStyle([2, 2], 0));
            context.DrawLine(dashed, new Point(X(row.Column), 0), new Point(X(row.Column), mid));
        }

        var center = new Point(X(row.Column), mid);
        var color = new SolidColorBrush(Palette.Lane(row.Color));
        if (row.IsMerge)
            context.DrawEllipse(new SolidColorBrush(Palette.Background), new Pen(color, 2), center, NodeRadius, NodeRadius);
        else
            context.DrawEllipse(color, null, center, NodeRadius, NodeRadius);
        if (item.IsHead)
            context.DrawEllipse(null, new Pen(new SolidColorBrush(Palette.Text), 1.5), center, NodeRadius + 2.5, NodeRadius + 2.5);
    }
}
