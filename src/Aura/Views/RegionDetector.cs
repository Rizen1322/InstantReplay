namespace Aura.Views;

/// <summary>Прямоугольник в пикселях снимка экрана.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height);

/// <summary>
/// Поиск прямоугольного блока под курсором прямо по пикселям снимка.
///
/// ЗАЧЕМ. Карточка друга в Steam, панель мессенджера, ячейка таблицы — всё это
/// нарисовано ВНУТРИ окна и отдельным окном не является. Ни EnumWindows, ни UI
/// Automation их не отдадут: Steam рисует интерфейс через Chromium, и дерева
/// доступности там обычно нет. Зато снимок экрана у оверлея уже есть целиком,
/// а границы блоков на нём видны глазами — значит, видны и по контрасту.
///
/// КАК ОТЛИЧИТЬ РАМКУ ОТ БУКВЫ. Наивный поиск ближайшего перепада яркости
/// останавливается на первом же символе текста. Поэтому граница обязана быть
/// УСТОЙЧИВОЙ поперёк направления поиска: рамка карточки даёт перепад по всей
/// своей высоте, а буква — на нескольких строках. Проверяем полосу вокруг
/// курсора и требуем перепад в подавляющем большинстве её строк.
///
/// Здесь намеренно нет ни одного типа WPF: чистые пиксели на входе, числа на
/// выходе. Благодаря этому логика проверяется тестами на синтетических картинках.
/// </summary>
public static class RegionDetector
{
    /// <summary>Насколько цвет должен отличаться, чтобы считаться краем (0..255).</summary>
    private const int EdgeThreshold = 24;

    /// <summary>Сколько пикселей поперёк направления поиска проверяем в обе стороны.</summary>
    private const int SampleReach = 6;

    /// <summary>Какая доля полосы обязана показать перепад, чтобы это была граница.</summary>
    private const double Consistency = 0.7;

    /// <summary>Блок мельче этого человеку не нужен — он метил не в него.</summary>
    private const int MinSize = 8;

    /// <summary>
    /// Блок, накрывающий почти весь снимок, — это «весь экран», а не блок:
    /// для такого случая есть обычное выделение рамкой.
    /// </summary>
    private const double MaxCoverage = 0.95;

    /// <summary>
    /// Найти блок под точкой (<paramref name="x"/>, <paramref name="y"/>).
    /// null — ничего внятного под курсором нет.
    /// </summary>
    public static PixelRect? Detect(byte[] bgra, int width, int height, int stride, int x, int y)
    {
        if (x < 0 || y < 0 || x >= width || y >= height) return null;

        // Границы ищем наружу от курсора: первым встретится ближайший блок,
        // то есть аватар внутри карточки выиграет у самой карточки.
        var left = ScanHorizontal(bgra, width, height, stride, x, y, step: -1);
        var right = ScanHorizontal(bgra, width, height, stride, x, y, step: +1);
        var top = ScanVertical(bgra, width, height, stride, x, y, step: -1);
        var bottom = ScanVertical(bgra, width, height, stride, x, y, step: +1);

        // По каждой оси нужна хотя бы одна НАСТОЯЩАЯ граница. Боковые панели
        // прилегают к краю экрана вплотную, и с той стороны контраста нет — но
        // с противоположной он есть, и этого достаточно. Если же настоящих границ
        // нет вовсе, мы просто упёрлись в края снимка: блока под курсором нет.
        if (!left.Real && !right.Real) return null;
        if (!top.Real && !bottom.Real) return null;

        var rect = new PixelRect(left.At, top.At,
                                 right.At - left.At + 1,
                                 bottom.At - top.At + 1);

        if (rect.Width < MinSize || rect.Height < MinSize) return null;
        if (rect.Width >= width * MaxCoverage && rect.Height >= height * MaxCoverage) return null;

        return rect;
    }

    /// <summary>
    /// Край блока. <paramref name="Real"/> — найден по контрасту; false означает,
    /// что поиск упёрся в край снимка и границу пришлось взять оттуда.
    /// </summary>
    private readonly record struct Edge(int At, bool Real);

    /// <summary>Идти по строке до вертикальной границы; возвращает крайний столбец блока.</summary>
    private static Edge ScanHorizontal(byte[] bgra, int width, int height, int stride, int x, int y, int step)
    {
        int c = x;
        for (; c + step >= 0 && c + step < width; c += step)
            if (IsEdgeColumn(bgra, height, stride, c, c + step, y))
                return new Edge(c, true);

        return new Edge(c, false);
    }

    /// <summary>То же по столбцу: возвращает крайнюю строку блока.</summary>
    private static Edge ScanVertical(byte[] bgra, int width, int height, int stride, int x, int y, int step)
    {
        int r = y;
        for (; r + step >= 0 && r + step < height; r += step)
            if (IsEdgeRow(bgra, width, stride, r, r + step, x))
                return new Edge(r, true);

        return new Edge(r, false);
    }

    /// <summary>
    /// Настоящая ли граница между столбцами <paramref name="inner"/> и
    /// <paramref name="outer"/> — проверяем полосу строк вокруг курсора.
    /// </summary>
    private static bool IsEdgeColumn(byte[] bgra, int height, int stride, int inner, int outer, int y)
    {
        int from = Math.Max(0, y - SampleReach);
        int to = Math.Min(height - 1, y + SampleReach);
        int total = to - from + 1;
        int hits = 0;

        for (int r = from; r <= to; r++)
            if (Differs(bgra, r * stride + inner * 4, r * stride + outer * 4))
                hits++;

        return hits >= total * Consistency;
    }

    /// <summary>То же для границы между строками.</summary>
    private static bool IsEdgeRow(byte[] bgra, int width, int stride, int inner, int outer, int x)
    {
        int from = Math.Max(0, x - SampleReach);
        int to = Math.Min(width - 1, x + SampleReach);
        int total = to - from + 1;
        int hits = 0;

        for (int c = from; c <= to; c++)
            if (Differs(bgra, inner * stride + c * 4, outer * stride + c * 4))
                hits++;

        return hits >= total * Consistency;
    }

    /// <summary>Отличаются ли два пикселя настолько, чтобы это был край.</summary>
    private static bool Differs(byte[] bgra, int i, int j)
    {
        int b = Math.Abs(bgra[i] - bgra[j]);
        int g = Math.Abs(bgra[i + 1] - bgra[j + 1]);
        int r = Math.Abs(bgra[i + 2] - bgra[j + 2]);
        return Math.Max(b, Math.Max(g, r)) > EdgeThreshold;
    }
}
