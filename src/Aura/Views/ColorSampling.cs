using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Aura.Views;

/// <summary>
/// Взятие цвета из кадра снимка.
///
/// Отдельным файлом, а не методом окна: это единственная арифметика оверлея, которую
/// можно проверить без интерфейса, — перевод координат окна в пиксели кадра и порядок
/// байт. Ошибка здесь не падает и не видна в логе, она просто выдаёт не тот цвет.
/// </summary>
internal static class ColorSampling
{
    /// <summary>
    /// Цвет пикселя кадра под точкой окна.
    ///
    /// Берём именно из КАДРА, а не с экрана: поверх кадра лежат затемнение, уже
    /// нарисованные пометки и сама панель инструментов — с экрана пипетка хватала бы
    /// их. <paramref name="windowWidth"/> нужен, чтобы перевести координаты: на
    /// мониторе с масштабом 125% или на втором мониторе окно и кадр меряются
    /// в разных единицах.
    /// </summary>
    public static Color At(BitmapSource shot, Point at, double windowWidth)
    {
        double factor = shot.PixelWidth / Math.Max(1, windowWidth);
        int x = Math.Clamp((int)(at.X * factor), 0, shot.PixelWidth - 1);
        int y = Math.Clamp((int)(at.Y * factor), 0, shot.PixelHeight - 1);

        var pixel = new byte[4];
        shot.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);

        // Кадр в Bgr32: порядок байт B, G, R, четвёртый не используется
        return Color.FromRgb(pixel[2], pixel[1], pixel[0]);
    }
}
