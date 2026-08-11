using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Aura.Core.Capture;
using Aura.Core.Interop;
using Aura.Core.Logging;

namespace Aura.Views;

/// <summary>
/// Снимок выделенной области — как в Lightshot: затемнённый экран, выделение мышью,
/// карандаш/стрелка/прямоугольник, копирование в буфер и сохранение в файл.
///
/// Оверлей показывает ЗАСТЫВШИЙ кадр, а не прозрачное окно поверх живого экрана:
/// иначе выделять пришлось бы на движущейся картинке, а в кадр попадал бы сам
/// оверлей. Кадр берётся тем же путём, что и обычный скриншот, — у работающего
/// буфера, если он включён. Пока оверлей открыт, кадр один и тот же, поэтому
/// выделение можно перевыделять и менять сколько угодно раз.
///
/// Окно ставится и растягивается через SetWindowPos в ФИЗИЧЕСКИХ пикселях: WPF
/// считает Left/Top в своих единицах, и на мониторе с масштабом 125% или на втором
/// мониторе оверлей ложился бы мимо экрана.
/// </summary>
public partial class RegionCaptureWindow : Window
{
    private const int SwpNoZOrder = 0x0004;
    private const int SwpShowWindow = 0x0040;

    /// <summary>Порядок ручек совпадает с InkLayer.HandlePoints.</summary>
    private enum Grip { None, NW, N, NE, E, SE, S, SW, W, Inside }

    /// <summary>Красный первым — им пользуются чаще всего, он же цвет по умолчанию.</summary>
    private static readonly (string Name, Color Color)[] Palette =
    [
        ("Красный", Color.FromRgb(0xE0, 0x3B, 0x3B)),
        ("Оранжевый", Color.FromRgb(0xF5, 0xA5, 0x24)),
        ("Жёлтый", Color.FromRgb(0xF2, 0xE0, 0x4B)),
        ("Зелёный", Color.FromRgb(0x3B, 0xC4, 0x6A)),
        ("Синий", Color.FromRgb(0x3B, 0x82, 0xF6)),
        ("Белый", Colors.White),
        ("Чёрный", Color.FromRgb(0x10, 0x10, 0x14)),
    ];

    private static RegionCaptureWindow? _open;

    private readonly BitmapSource _shot;
    private readonly int _monitorX, _monitorY, _pixelWidth, _pixelHeight;
    private readonly string _screenshotFolder;

    private InkTool _tool = InkTool.None;
    private Color _color = Palette[0].Color;

    /// <summary>Отменённые фигуры: Ctrl+Y возвращает их обратно, пока не нарисовано новое.</summary>
    private readonly Stack<InkShape> _undone = new();
    private Rect _selection;
    private bool _hasSelection, _selecting, _drawing, _resizing, _moving;
    private Grip _grip;
    private Point _dragStart;
    private Rect _startRect;

    private RegionCaptureWindow(BitmapSource shot, int x, int y, int width, int height, string folder)
    {
        InitializeComponent();
        _shot = shot;
        _monitorX = x; _monitorY = y; _pixelWidth = width; _pixelHeight = height;
        _screenshotFolder = folder;

        Shot.Source = shot;
        BuildSwatches();

        PencilBtn.Click += (_, _) => SetTool(InkTool.Pencil);
        ArrowBtn.Click += (_, _) => SetTool(InkTool.Arrow);
        RectBtn.Click += (_, _) => SetTool(InkTool.Rect);
        UndoBtn.Click += (_, _) => Undo();
        RedoBtn.Click += (_, _) => Redo();
        CopyBtn.Click += (_, _) => Finish(copy: true, save: false);
        SaveBtn.Click += (_, _) => Finish(copy: false, save: true);
        CloseBtn.Click += (_, _) => Close();

        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseUp;
        KeyDown += OnKeyDown;

        SourceInitialized += (_, _) => PlaceOverMonitor();
        // Затемнение рисуется по фактическому размеру окна — до Loaded он ещё нулевой
        Loaded += (_, _) => UpdateDim();
        SizeChanged += (_, _) => UpdateDim();
        Closed += (_, _) => _open = null;
    }

    /// <summary>
    /// Снимает экран и показывает оверлей. Вызывать из потока интерфейса.
    /// Второй оверлей поверх первого не открывается: повторное нажатие клавиши
    /// просто вернёт фокус уже открытому.
    /// </summary>
    public static async Task ShowForAsync(int monitorIndex, bool cursor, LiveFrameProvider? live, string folder)
    {
        if (_open is not null) { _open.Activate(); return; }

        var (bgra, width, height) = await ScreenshotService.CapturePixelsAsync(monitorIndex, cursor, live);

        // Bgr32, а не Bgra32: альфа в кадре захвата недостоверна (рабочий стол отдаёт
        // её нулями), и картинка становится прозрачной. Обычный скриншот по той же
        // причине кодируется с BitmapAlphaMode.Ignore.
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, bgra, width * 4);
        bitmap.Freeze();

        var bounds = MonitorLayout.For(monitorIndex);
        int x = bounds?.X ?? 0, y = bounds?.Y ?? 0;
        int w = bounds?.Width ?? width, h = bounds?.Height ?? height;

        _open = new RegionCaptureWindow(bitmap, x, y, w, h, folder);
        _open.Show();
        _open.Activate();
        _open.Focus();
    }

    private void PlaceOverMonitor()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, _monitorX, _monitorY, _pixelWidth, _pixelHeight,
            SwpNoZOrder | SwpShowWindow);
    }

    // ---------------- Палитра ----------------

    private void BuildSwatches()
    {
        foreach (var (name, color) in Palette)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();

            var swatch = new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(9),
                Background = brush,
                Margin = new Thickness(3, 0, 3, 0),
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent,
                Cursor = Cursors.Arrow,
                ToolTip = name,
                Tag = color
            };
            swatch.MouseLeftButtonDown += (s, e) => { SetColor((Color)((Border)s).Tag); e.Handled = true; };
            Swatches.Children.Add(swatch);
        }
        HighlightSwatch();
    }

    private void SetColor(Color color)
    {
        _color = color;
        HighlightSwatch();
        // Выбор цвета — намерение рисовать: если инструмент ещё не взят, берём карандаш
        if (_tool == InkTool.None) SetTool(InkTool.Pencil);
    }

    private void HighlightSwatch()
    {
        foreach (var child in Swatches.Children)
            if (child is Border border)
                border.BorderBrush = (Color)border.Tag == _color ? Brushes.White : Brushes.Transparent;
    }

    // ---------------- Мышь ----------------

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Промах по пустому месту панели не должен начинать новое выделение:
        // сами кнопки событие гасят, а вот отступы вокруг них — нет.
        if (e.OriginalSource is DependencyObject source && Toolbar.IsAncestorOf(source)) return;

        var point = e.GetPosition(Root);

        if (_hasSelection)
        {
            var grip = HitGrip(point);

            // Край выделения — меняем размер, пометки остаются на своих местах
            if (grip is not Grip.None and not Grip.Inside)
            {
                _resizing = true;
                _grip = grip;
                _startRect = _selection;
                Toolbar.Visibility = Visibility.Collapsed;
                CaptureMouse();
                return;
            }

            if (grip == Grip.Inside && _tool != InkTool.None)
            {
                _drawing = true;
                var shape = new InkShape { Tool = _tool, Color = _color, Start = point, End = point };
                if (_tool == InkTool.Pencil) shape.Points.Add(point);
                Ink.Current = shape;
                CaptureMouse();
                return;
            }

            if (grip == Grip.Inside)
            {
                _moving = true;
                _dragStart = point;
                _startRect = _selection;
                Toolbar.Visibility = Visibility.Collapsed;
                CaptureMouse();
                return;
            }

            // Протяжка мимо выделения — начинаем новое на том же кадре
            ResetSelection();
        }

        _selecting = true;
        _dragStart = point;
        _selection = new Rect(point, point);
        Ink.Selection = _selection;
        Toolbar.Visibility = Visibility.Collapsed;
        CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var point = e.GetPosition(Root);

        if (_drawing && Ink.Current is { } shape)
        {
            if (shape.Tool == InkTool.Pencil) shape.Points.Add(Clamp(point));
            else shape.End = Clamp(point);
            Ink.Refresh();
            return;
        }

        if (_resizing) { Resize(point); return; }
        if (_moving) { Move(point); return; }

        if (_selecting)
        {
            _selection = new Rect(_dragStart, point);
            Ink.Selection = _selection;
            Ink.Refresh();
            UpdateDim();
            UpdateSizeChip();
            return;
        }

        UpdateCursor(point);
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();

        if (_drawing)
        {
            _drawing = false;
            if (Ink.Current is { } shape)
            {
                Ink.Shapes.Add(shape);
                Ink.Current = null;
                // История линейная: после новой фигуры возвращать нечего
                _undone.Clear();
                Ink.Refresh();
            }
            return;
        }

        if (_resizing || _moving)
        {
            _resizing = _moving = false;
            ShowToolbar();
            return;
        }

        if (!_selecting) return;
        _selecting = false;

        // Случайный клик без протяжки — не выделение
        if (_selection.Width < 4 || _selection.Height < 4)
        {
            ResetSelection();
            return;
        }

        _hasSelection = true;
        Ink.ShowHandles = true;
        Ink.Refresh();
        Hint.Visibility = Visibility.Collapsed;
        Cursor = Cursors.Arrow;
        ShowToolbar();
    }

    /// <summary>Сброс к состоянию «ничего не выделено»: кадр тот же, пометки старой области уходят вместе с ней.</summary>
    private void ResetSelection()
    {
        _hasSelection = false;
        _selection = Rect.Empty;
        Ink.Selection = null;
        Ink.ShowHandles = false;
        Ink.Shapes.Clear();
        Ink.Current = null;
        _undone.Clear();
        Ink.Refresh();
        UpdateDim();
        Toolbar.Visibility = Visibility.Collapsed;
        SizeChip.Visibility = Visibility.Collapsed;
        Hint.Visibility = Visibility.Visible;
        Cursor = Cursors.Cross;
    }

    private void Resize(Point point)
    {
        double left = _startRect.Left, top = _startRect.Top, right = _startRect.Right, bottom = _startRect.Bottom;
        switch (_grip)
        {
            case Grip.NW: left = point.X; top = point.Y; break;
            case Grip.N: top = point.Y; break;
            case Grip.NE: right = point.X; top = point.Y; break;
            case Grip.E: right = point.X; break;
            case Grip.SE: right = point.X; bottom = point.Y; break;
            case Grip.S: bottom = point.Y; break;
            case Grip.SW: left = point.X; bottom = point.Y; break;
            case Grip.W: left = point.X; break;
        }

        _selection = new Rect(
            new Point(Math.Min(left, right), Math.Min(top, bottom)),
            new Point(Math.Max(left, right), Math.Max(top, bottom)));
        ApplySelection();
    }

    private void Move(Point point)
    {
        double dx = point.X - _dragStart.X, dy = point.Y - _dragStart.Y;
        double x = Math.Clamp(_startRect.X + dx, 0, Math.Max(0, Root.ActualWidth - _startRect.Width));
        double y = Math.Clamp(_startRect.Y + dy, 0, Math.Max(0, Root.ActualHeight - _startRect.Height));
        _selection = new Rect(x, y, _startRect.Width, _startRect.Height);
        ApplySelection();
    }

    private void ApplySelection()
    {
        Ink.Selection = _selection;
        Ink.Refresh();
        UpdateDim();
        UpdateSizeChip();
    }

    private Grip HitGrip(Point point)
    {
        if (!_hasSelection) return Grip.None;

        var points = InkLayer.HandlePoints(_selection);
        for (int i = 0; i < points.Length; i++)
            if (Math.Abs(point.X - points[i].X) <= InkLayer.HandleSize &&
                Math.Abs(point.Y - points[i].Y) <= InkLayer.HandleSize)
                return (Grip)(i + 1);

        return _selection.Contains(point) ? Grip.Inside : Grip.None;
    }

    /// <summary>Курсор подсказывает, что произойдёт по нажатию: изменить край, двигать, рисовать или выделить заново.</summary>
    private void UpdateCursor(Point point)
    {
        Cursor = HitGrip(point) switch
        {
            Grip.NW or Grip.SE => Cursors.SizeNWSE,
            Grip.NE or Grip.SW => Cursors.SizeNESW,
            Grip.N or Grip.S => Cursors.SizeNS,
            Grip.E or Grip.W => Cursors.SizeWE,
            Grip.Inside => _tool == InkTool.None ? Cursors.SizeAll : Cursors.Pen,
            _ => Cursors.Cross
        };
    }

    /// <summary>Рисование не должно выходить за пределы выделения — в файл попадёт только оно.</summary>
    private Point Clamp(Point point) => new(
        Math.Clamp(point.X, _selection.Left, _selection.Right),
        Math.Clamp(point.Y, _selection.Top, _selection.Bottom));

    // ---------------- Клавиши ----------------

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        switch (e.Key)
        {
            // Esc закрывает сразу. Двухступенчатый выход (сначала снять выделение,
            // потом закрыть) выглядел так, будто клавиша не сработала: чтобы выделить
            // заново, достаточно протянуть мышью мимо текущей области.
            case Key.Escape: Close(); break;
            case Key.Enter: if (_hasSelection) Finish(copy: false, save: true); break;
            case Key.Z when ctrl: Undo(); break;
            case Key.Y when ctrl: Redo(); break;
            case Key.C when ctrl: if (_hasSelection) Finish(copy: true, save: false); break;
            case Key.S when ctrl: if (_hasSelection) Finish(copy: false, save: true); break;
        }
    }

    // ---------------- Интерфейс ----------------

    private void SetTool(InkTool tool)
    {
        _tool = _tool == tool ? InkTool.None : tool;
        PencilBtn.IsChecked = _tool == InkTool.Pencil;
        ArrowBtn.IsChecked = _tool == InkTool.Arrow;
        RectBtn.IsChecked = _tool == InkTool.Rect;
        Cursor = _tool == InkTool.None ? Cursors.Arrow : Cursors.Pen;
    }

    private void Undo()
    {
        if (Ink.Shapes.Count == 0) return;
        _undone.Push(Ink.Shapes[^1]);
        Ink.Shapes.RemoveAt(Ink.Shapes.Count - 1);
        Ink.Refresh();
    }

    /// <summary>Вернуть отменённое. Ветка истории одна: нарисовал новое — возвращать больше нечего.</summary>
    private void Redo()
    {
        if (_undone.Count == 0) return;
        Ink.Shapes.Add(_undone.Pop());
        Ink.Refresh();
    }

    /// <summary>Затемнение всего экрана с вырезанным выделением (правило EvenOdd).</summary>
    private void UpdateDim()
    {
        var full = new RectangleGeometry(new Rect(0, 0, Root.ActualWidth, Root.ActualHeight));
        if (_selection.IsEmpty || _selection.Width <= 0)
        {
            Dim.Data = full;
            return;
        }
        var group = new GeometryGroup { FillRule = FillRule.EvenOdd };
        group.Children.Add(full);
        group.Children.Add(new RectangleGeometry(_selection));
        Dim.Data = group;
    }

    private void UpdateSizeChip()
    {
        double factor = _shot.PixelWidth / Math.Max(1, Root.ActualWidth);
        SizeText.Text = $"{Math.Round(_selection.Width * factor)} × {Math.Round(_selection.Height * factor)}";
        Canvas.SetLeft(SizeChip, Math.Max(4, _selection.Left));
        Canvas.SetTop(SizeChip, Math.Max(4, _selection.Top - 26));
        SizeChip.Visibility = Visibility.Visible;
    }

    /// <summary>Панель под выделением, а если снизу места нет — над ним.</summary>
    private void ShowToolbar()
    {
        Toolbar.Visibility = Visibility.Visible;
        Toolbar.UpdateLayout();

        double width = Toolbar.ActualWidth > 0 ? Toolbar.ActualWidth : 380;
        double height = Toolbar.ActualHeight > 0 ? Toolbar.ActualHeight : 40;

        double left = Math.Clamp(_selection.Right - width, 6, Math.Max(6, Root.ActualWidth - width - 6));
        double top = _selection.Bottom + 10;
        if (top + height > Root.ActualHeight - 6) top = Math.Max(6, _selection.Top - height - 10);

        Canvas.SetLeft(Toolbar, left);
        Canvas.SetTop(Toolbar, top);
    }

    // ---------------- Результат ----------------

    private void Finish(bool copy, bool save)
    {
        if (!_hasSelection) return;

        try
        {
            var image = RenderSelection();

            if (copy && !ImageClipboard.Copy(image))
            {
                Failed?.Invoke(new InvalidOperationException("буфер обмена занят другой программой"));
                Close();
                return;
            }

            string? file = null;
            if (save)
            {
                // Имя ищется по фактическому содержимому папки на каждый снимок
                file = ScreenshotService.NextFilePath(_screenshotFolder);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using var stream = File.Create(file);
                encoder.Save(stream);

                Log.Info("Screenshot", $"Область сохранена: {file} ({image.PixelWidth}x{image.PixelHeight})");
            }
            else Log.Info("Screenshot", $"Область скопирована в буфер ({image.PixelWidth}x{image.PixelHeight})");

            Saved?.Invoke(file, copy);
        }
        catch (Exception ex)
        {
            Log.Error("Screenshot", ex);
            Failed?.Invoke(ex);
        }
        Close();
    }

    /// <summary>Файл (null — только буфер) и признак копирования. Уведомления показывает App.</summary>
    public static event Action<string?, bool>? Saved;
    public static event Action<Exception>? Failed;

    /// <summary>
    /// Выделение вместе с пометками в исходном разрешении: масштабируем координаты
    /// окна обратно в пиксели снимка, иначе на мониторе с масштабом картинка вышла бы
    /// мельче оригинала.
    /// </summary>
    private BitmapSource RenderSelection()
    {
        double factor = _shot.PixelWidth / Math.Max(1, Root.ActualWidth);
        int width = Math.Max(1, (int)Math.Round(_selection.Width * factor));
        int height = Math.Max(1, (int)Math.Round(_selection.Height * factor));

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(factor, factor));
            dc.PushTransform(new TranslateTransform(-_selection.X, -_selection.Y));
            dc.DrawImage(_shot, new Rect(0, 0, Root.ActualWidth, Root.ActualHeight));
            Ink.DrawShapes(dc);
            dc.Pop();
            dc.Pop();
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
