using System.Windows;
using System.Windows.Media;

namespace Aura.Views;

/// <summary>Чем рисуют поверх выделенной области.</summary>
public enum InkTool { None, Pencil, Arrow, Rect }

/// <summary>Одна фигура: карандашный след, стрелка или прямоугольник.</summary>
public sealed class InkShape
{
    public InkTool Tool { get; init; }
    public List<Point> Points { get; } = [];   // карандаш
    public Point Start { get; set; }           // стрелка и прямоугольник
    public Point End { get; set; }
    public Color Color { get; init; }
    public double Thickness { get; init; } = 2.5;
}

/// <summary>
/// Слой рисования поверх снимка.
///
/// Фигуры хранятся списком, а не элементами дерева: тот же самый метод отрисовки
/// используется и на экране, и при экспорте в файл — иначе сохранённая картинка
/// рано или поздно разойдётся с тем, что человек видел на экране.
/// </summary>
public sealed class InkLayer : FrameworkElement
{
    private static readonly Color SelectionColor = Color.FromRgb(0xE0, 0x3B, 0x3B);

    /// <summary>Сторона квадратика-ручки. Он же радиус захвата мышью по краю выделения.</summary>
    public const double HandleSize = 9;

    public List<InkShape> Shapes { get; } = [];
    public InkShape? Current { get; set; }
    public Rect? Selection { get; set; }

    /// <summary>Показывать ручки изменения размера — только когда выделение готово.</summary>
    public bool ShowHandles { get; set; }

    public void Refresh() => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        if (Selection is { Width: > 0, Height: > 0 } selection)
        {
            var pen = new Pen(new SolidColorBrush(SelectionColor), 1.5);
            pen.Freeze();
            dc.DrawRectangle(null, pen, selection);

            // Пометки не должны вылезать за выделение: при уменьшении области
            // нарисованное обрезается ровно так же, как обрежется в файле.
            dc.PushClip(new RectangleGeometry(selection));
            DrawShapes(dc);
            dc.Pop();

            if (ShowHandles) DrawHandles(dc, selection);
            return;
        }
        DrawShapes(dc);
    }

    /// <summary>Восемь ручек по углам и серединам сторон.</summary>
    private static void DrawHandles(DrawingContext dc, Rect selection)
    {
        var fill = new SolidColorBrush(Colors.White);
        fill.Freeze();
        var pen = new Pen(new SolidColorBrush(SelectionColor), 1.2);
        pen.Freeze();

        foreach (var point in HandlePoints(selection))
            dc.DrawRectangle(fill, pen, new Rect(
                point.X - HandleSize / 2, point.Y - HandleSize / 2, HandleSize, HandleSize));
    }

    /// <summary>Порядок совпадает с перечислением ручек в окне: NW, N, NE, E, SE, S, SW, W.</summary>
    public static Point[] HandlePoints(Rect r) =>
    [
        new(r.Left, r.Top), new(r.Left + r.Width / 2, r.Top), new(r.Right, r.Top),
        new(r.Right, r.Top + r.Height / 2), new(r.Right, r.Bottom),
        new(r.Left + r.Width / 2, r.Bottom), new(r.Left, r.Bottom),
        new(r.Left, r.Top + r.Height / 2)
    ];

    /// <summary>Только фигуры, без рамки выделения — рамка в файл попадать не должна.</summary>
    public void DrawShapes(DrawingContext dc)
    {
        foreach (var shape in Shapes) Draw(dc, shape);
        if (Current is not null) Draw(dc, Current);
    }

    private static void Draw(DrawingContext dc, InkShape shape)
    {
        var brush = new SolidColorBrush(shape.Color);
        brush.Freeze();
        var pen = new Pen(brush, shape.Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        pen.Freeze();

        switch (shape.Tool)
        {
            case InkTool.Pencil:
                if (shape.Points.Count < 2) return;
                var geometry = new StreamGeometry();
                using (var ctx = geometry.Open())
                {
                    ctx.BeginFigure(shape.Points[0], false, false);
                    ctx.PolyLineTo(shape.Points.GetRange(1, shape.Points.Count - 1), true, true);
                }
                geometry.Freeze();
                dc.DrawGeometry(null, pen, geometry);
                break;

            case InkTool.Rect:
                var rect = new Rect(shape.Start, shape.End);
                if (rect.Width > 0 && rect.Height > 0) dc.DrawRectangle(null, pen, rect);
                break;

            case InkTool.Arrow:
                dc.DrawLine(pen, shape.Start, shape.End);
                DrawHead(dc, brush, shape);
                break;
        }
    }

    /// <summary>Наконечник стрелки — залитый треугольник, чтобы был виден на пёстром фоне.</summary>
    private static void DrawHead(DrawingContext dc, Brush brush, InkShape shape)
    {
        var dx = shape.End.X - shape.Start.X;
        var dy = shape.End.Y - shape.Start.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1) return;

        double head = Math.Max(11, shape.Thickness * 4.5);
        double angle = Math.Atan2(dy, dx);
        const double spread = 0.42; // ~24°

        var left = new Point(
            shape.End.X - head * Math.Cos(angle - spread),
            shape.End.Y - head * Math.Sin(angle - spread));
        var right = new Point(
            shape.End.X - head * Math.Cos(angle + spread),
            shape.End.Y - head * Math.Sin(angle + spread));

        var triangle = new StreamGeometry();
        using (var ctx = triangle.Open())
        {
            ctx.BeginFigure(shape.End, true, true);
            ctx.LineTo(left, true, true);
            ctx.LineTo(right, true, true);
        }
        triangle.Freeze();
        dc.DrawGeometry(brush, null, triangle);
    }
}
