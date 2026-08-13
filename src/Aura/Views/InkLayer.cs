using System.Windows;
using System.Windows.Media;

namespace Aura.Views;

/// <summary>Чем рисуют поверх выделенной области.</summary>
/// <summary>
/// Инструмент панели. <see cref="Eyedropper"/> ничего не рисует — он только берёт
/// цвет с кадра, поэтому в списке фигур не встречается.
/// </summary>
public enum InkTool { None, Pencil, Arrow, Rect, Blur, Text, Eyedropper }

/// <summary>Одна фигура: след карандаша, стрелка, прямоугольник, размытие или подпись.</summary>
public sealed class InkShape
{
    public InkTool Tool { get; init; }
    public List<Point> Points { get; } = [];   // карандаш
    public Point Start { get; set; }           // стрелка, прямоугольник, размытие, подпись
    public Point End { get; set; }
    public Color Color { get; init; }
    public double Thickness { get; init; } = 2.5;

    /// <summary>Текст подписи. Пустая подпись при завершении ввода не сохраняется.</summary>
    public string Text { get; set; } = "";
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

    /// <summary>
    /// Размытая копия всего снимка в координатах окна. Размытие рисуется не фильтром
    /// поверх кадра, а куском этой копии: тогда экспорт в файл идёт тем же кодом, что
    /// и показ на экране, и сохранённое совпадает с увиденным. Готовится один раз при
    /// первом обращении к инструменту (см. RegionCaptureWindow.EnsureBlurred).
    /// </summary>
    public ImageSource? Blurred { get; set; }

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

    /// <summary>Есть ли хоть одно размытие — по этому признаку готовится размытая копия.</summary>
    public bool NeedsBlurred => Shapes.Any(s => s.Tool == InkTool.Blur) || Current?.Tool == InkTool.Blur;

    private void Draw(DrawingContext dc, InkShape shape)
    {
        var brush = new SolidColorBrush(shape.Color);
        brush.Freeze();

        if (shape.Tool == InkTool.Blur)
        {
            var area = new Rect(shape.Start, shape.End);
            if (area.Width <= 0 || area.Height <= 0) return;
            if (Blurred is null) return;

            // Кусок размытой копии ровно на своём месте: рисуем всю копию, обрезав
            // её прямоугольником — тогда координаты совпадают с кадром сами собой.
            dc.PushClip(new RectangleGeometry(area));
            dc.DrawImage(Blurred, new Rect(0, 0, Blurred.Width, Blurred.Height));
            dc.Pop();
            return;
        }

        if (shape.Tool == InkTool.Text)
        {
            if (shape.Text.Length == 0) return;
            dc.DrawText(Caption(shape, brush), shape.Start);
            return;
        }
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
                DrawArrow(dc, brush, pen, shape);
                break;
        }
    }

    /// <summary>
    /// Подпись. Размер кегля привязан к выбранной толщине линии: человек уже выбрал
    /// «тонко/средне/толсто», и отдельная настройка размера текста была бы лишней.
    /// </summary>
    public static FormattedText Caption(InkShape shape, Brush brush) =>
        new(shape.Text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            CaptionSize(shape.Thickness), brush, 96)
        { TextAlignment = TextAlignment.Left };

    /// <summary>Кегль подписи по толщине линии: 2,5 → 18, 5 → 26, 9 → 38.</summary>
    public static double CaptionSize(double thickness) => 12 + thickness * 2.6;

    /// <summary>Разлёт сторон наконечника от оси стрелки, радианы (~24°).</summary>
    private const double HeadSpread = 0.42;

    /// <summary>Длина наконечника от кончика до боковых углов.</summary>
    private static double HeadLength(double thickness) => Math.Max(11, thickness * 4.5);

    /// <summary>
    /// Стрелка: древко и залитый наконечник.
    ///
    /// Древко НЕ доводится до кончика. У пера скруглённые концы, и линия, дотянутая
    /// до самой точки End, вылезала за наконечник полукруглым бугорком радиусом в
    /// половину толщины: на тонкой линии почти незаметно, а на толстой (9 px) это
    /// 4.5 px отростка прямо из острия. Останавливаем древко на основании
    /// треугольника — скругление уходит под заливку, и стыка не видно.
    /// </summary>
    private static void DrawArrow(DrawingContext dc, Brush brush, Pen pen, InkShape shape)
    {
        double dx = shape.End.X - shape.Start.X;
        double dy = shape.End.Y - shape.Start.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1) return;

        double angle = Math.Atan2(dy, dx);
        // Основание треугольника: проекция боковых углов на ось стрелки
        double shaft = length - HeadLength(shape.Thickness) * Math.Cos(HeadSpread);

        // Короткая стрелка — это уже один наконечник, древку в ней места нет
        if (shaft > 0.5)
            dc.DrawLine(pen, shape.Start, new Point(
                shape.Start.X + Math.Cos(angle) * shaft,
                shape.Start.Y + Math.Sin(angle) * shaft));

        DrawHead(dc, brush, shape);
    }

    /// <summary>Наконечник стрелки — залитый треугольник, чтобы был виден на пёстром фоне.</summary>
    private static void DrawHead(DrawingContext dc, Brush brush, InkShape shape)
    {
        var dx = shape.End.X - shape.Start.X;
        var dy = shape.End.Y - shape.Start.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1) return;

        double head = HeadLength(shape.Thickness);
        double angle = Math.Atan2(dy, dx);
        const double spread = HeadSpread;

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
