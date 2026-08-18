using Aura.Views;
using Xunit;

namespace InstantReplay.Tests;

/// <summary>
/// Поиск прямоугольного блока под курсором по самой картинке.
///
/// ЗАЧЕМ ПО ПИКСЕЛЯМ. Карточка друга в Steam, панель в мессенджере, ячейка
/// таблицы — всё это нарисовано ВНУТРИ окна и отдельным окном не является:
/// ни EnumWindows, ни UI Automation их не отдадут (Steam рисует через Chromium).
/// Зато оверлей уже держит снимок экрана целиком, и границы там видны глазами.
///
/// Главная трудность — не спутать границу блока с буквой внутри него. Поэтому
/// граница обязана быть ВЕРТИКАЛЬНО (или горизонтально) УСТОЙЧИВОЙ: буква даёт
/// перепад на двух-трёх строках, рамка карточки — на всей своей высоте.
/// </summary>
public class RegionDetectorTests
{
    private const int W = 400, H = 300;

    [Fact]
    public void КарточкаНаФонеНаходитсяЦеликом()
    {
        var canvas = new Canvas(W, H, 0x20, 0x20, 0x20);
        canvas.Fill(50, 40, 200, 120, 0xE0, 0xE0, 0xE0);

        var found = RegionDetector.Detect(canvas.Pixels, W, H, canvas.Stride, 150, 100);

        Assert.NotNull(found);
        Assert.Equal(new PixelRect(50, 40, 200, 120), found!.Value);
    }

    [Fact]
    public void КрайБуквыОтвергаетсяАКрайКарточкиПринимается()
    {
        // Ровно тот случай, ради которого граница проверяется на устойчивость.
        // Курсор стоит правее полосок-«строк текста»; поиск влево сначала
        // упирается в правый край полоски (перепад всего на 8 строках из 13 —
        // отвергаем), и только потом находит край карточки (все 13 строк).
        var canvas = new Canvas(W, H, 0x20, 0x20, 0x20);
        canvas.Fill(50, 40, 200, 120, 0xE0, 0xE0, 0xE0);
        canvas.Fill(70, 60, 120, 8, 0x30, 0x30, 0x30);
        canvas.Fill(70, 80, 90, 8, 0x30, 0x30, 0x30);
        canvas.Fill(70, 100, 110, 8, 0x30, 0x30, 0x30);

        var found = RegionDetector.Detect(canvas.Pixels, W, H, canvas.Stride, 200, 64);

        Assert.Equal(new PixelRect(50, 40, 200, 120), found);
    }

    [Fact]
    public void БерётсяСамыйБлижнийБлокАНеВнешний()
    {
        // Вложенность: аватар внутри карточки. Курсор на аватаре — нужен аватар.
        var canvas = new Canvas(W, H, 0x20, 0x20, 0x20);
        canvas.Fill(50, 40, 200, 120, 0xE0, 0xE0, 0xE0);
        canvas.Fill(70, 60, 60, 60, 0x50, 0x70, 0x90);

        var found = RegionDetector.Detect(canvas.Pixels, W, H, canvas.Stride, 100, 90);

        Assert.Equal(new PixelRect(70, 60, 60, 60), found);
    }

    [Fact]
    public void ПанельПрижатаяККраюЭкранаНаходится()
    {
        // Боковые панели (проводник, список чатов, сайдбар браузера) вплотную
        // прилегают к краю экрана: с этой стороны контрастной границы просто нет.
        // Требовать её со всех четырёх сторон — значит не находить их никогда.
        var canvas = new Canvas(W, H, 0x20, 0x20, 0x20);
        canvas.Fill(0, 50, 120, 200, 0xE0, 0xE0, 0xE0);

        var found = RegionDetector.Detect(canvas.Pixels, W, H, canvas.Stride, 60, 150);

        Assert.Equal(new PixelRect(0, 50, 120, 200), found);
    }

    [Fact]
    public void ОднороднаяОбластьНеДаётНичего()
    {
        var canvas = new Canvas(W, H, 0x20, 0x20, 0x20);

        Assert.Null(RegionDetector.Detect(canvas.Pixels, W, H, canvas.Stride, 200, 150));
    }

    [Fact]
    public void СлабыйКонтрастНеСчитаетсяГраницей()
    {
        // Отличие на пару единиц — это шум градиента, а не край панели
        var canvas = new Canvas(W, H, 0x20, 0x20, 0x20);
        canvas.Fill(50, 40, 200, 120, 0x24, 0x24, 0x24);

        Assert.Null(RegionDetector.Detect(canvas.Pixels, W, H, canvas.Stride, 150, 100));
    }

    [Fact]
    public void СлишкомМелкийБлокНеПредлагается()
    {
        // Кнопка в пару пикселей — это не то, что человек хочет снять
        var canvas = new Canvas(W, H, 0x20, 0x20, 0x20);
        canvas.Fill(100, 100, 5, 5, 0xE0, 0xE0, 0xE0);

        Assert.Null(RegionDetector.Detect(canvas.Pixels, W, H, canvas.Stride, 102, 102));
    }

    [Fact]
    public void БлокВплотнуюККраюЭкранаНеПредлагается()
    {
        // Если границы упёрлись в край снимка, это «весь экран», а не блок:
        // для такого у человека есть обычное выделение
        var canvas = new Canvas(W, H, 0x20, 0x20, 0x20);
        canvas.Fill(0, 0, W, H, 0xE0, 0xE0, 0xE0);

        Assert.Null(RegionDetector.Detect(canvas.Pixels, W, H, canvas.Stride, 200, 150));
    }

    [Fact]
    public void КурсорНаСамойГраницеНеПадает()
    {
        var canvas = new Canvas(W, H, 0x20, 0x20, 0x20);
        canvas.Fill(50, 40, 200, 120, 0xE0, 0xE0, 0xE0);

        // Не проверяем результат — важно, что вызовы на краях не выходят за массив
        RegionDetector.Detect(canvas.Pixels, W, H, canvas.Stride, 50, 40);
        RegionDetector.Detect(canvas.Pixels, W, H, canvas.Stride, 0, 0);
        RegionDetector.Detect(canvas.Pixels, W, H, canvas.Stride, W - 1, H - 1);
    }

    /// <summary>Синтетический снимок экрана в BGRA — то же, что отдаёт захват.</summary>
    private sealed class Canvas
    {
        private readonly int _width, _height;

        public Canvas(int width, int height, byte b, byte g, byte r)
        {
            _width = width; _height = height;
            Stride = width * 4;
            Pixels = new byte[Stride * height];
            Fill(0, 0, width, height, b, g, r);
        }

        public byte[] Pixels { get; }
        public int Stride { get; }

        public void Fill(int x, int y, int w, int h, byte b, byte g, byte r)
        {
            for (int row = y; row < y + h && row < _height; row++)
                for (int col = x; col < x + w && col < _width; col++)
                {
                    int i = row * Stride + col * 4;
                    Pixels[i] = b; Pixels[i + 1] = g; Pixels[i + 2] = r; Pixels[i + 3] = 255;
                }
        }
    }
}
