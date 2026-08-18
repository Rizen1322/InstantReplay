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
    private static byte[]? _pixels;

    private readonly BitmapSource _shot;
    private readonly int _monitorX, _monitorY, _pixelWidth, _pixelHeight;
    private readonly string _screenshotFolder;

    private InkTool _tool = InkTool.None;
    private Color _color = Palette[0].Color;

    /// <summary>Толщина линии: тонко / средне / толсто. От неё же считается кегль подписи.</summary>
    private static readonly double[] Thicknesses = [2.5, 5, 9];
    private int _thickness;

    /// <summary>Поле ввода подписи, пока она набирается.</summary>
    private TextBox? _caption;
    private Point _captionAt;

    /// <summary>Отменённые фигуры: Ctrl+Y возвращает их обратно, пока не нарисовано новое.</summary>
    private readonly Stack<InkShape> _undone = new();
    private Rect _selection;
    private bool _hasSelection, _selecting, _drawing, _resizing, _moving;
    private Grip _grip;
    private Point _dragStart;
    private Rect _startRect;

    /// <summary>
    /// Снап по окнам: список собирается один раз при открытии оверлея,
    /// см. <see cref="WindowProbe"/>.
    /// </summary>
    private List<PixelRect>? _windows;
    private Rect? _candidate;
    private Point _lastProbePoint = new(double.NaN, double.NaN);

    /// <summary>
    /// Что было подсвечено в момент нажатия кнопки.
    ///
    /// ЗАЧЕМ ОТДЕЛЬНОЕ ПОЛЕ. Подсветка гаснет сразу на нажатии — иначе она висит
    /// поверх протяжки. Но решение «клик без протяжки = взять подсвеченное»
    /// принимается только на ОТПУСКАНИИ, и к тому моменту гасить уже нечего.
    /// Раньше здесь читалось <see cref="_candidate"/>, он был уже пуст, и клик
    /// по подсвеченной области просто сбрасывал выделение.
    /// </summary>
    private Rect? _pressedCandidate;

    private RegionCaptureWindow(BitmapSource shot, int x, int y, int width, int height, string folder)
    {
        InitializeComponent();
        _shot = shot;
        _monitorX = x; _monitorY = y; _pixelWidth = width; _pixelHeight = height;
        _screenshotFolder = folder;

        // Оверлей уже накрыл экран, картинка под ним застыла — список окон
        // собираем один раз, а не на каждое движение мыши
        _windows = WindowProbe.Collect(x, y, width, height);

        // Бинд области сам может содержать Ctrl — тогда снап включён уже при открытии,
        // и подсказка обязана говорить правду с первой секунды
        UpdateHint();

        Shot.Source = shot;
        BuildSwatches();
        ThicknessDot.Width = ThicknessDot.Height = 5 + Thicknesses[_thickness];
        ThicknessBtn.ToolTip = $"Толщина линии: {Thicknesses[_thickness]:0.#} px";

        PencilBtn.Click += (_, _) => SetTool(InkTool.Pencil);
        ArrowBtn.Click += (_, _) => SetTool(InkTool.Arrow);
        RectBtn.Click += (_, _) => SetTool(InkTool.Rect);
        BlurBtn.Click += (_, _) => SetTool(InkTool.Blur);
        TextBtn.Click += (_, _) => SetTool(InkTool.Text);
        EyedropperBtn.Click += (_, _) => SetTool(InkTool.Eyedropper);
        ColorBtn.Click += (_, _) => ShowColorPanel(ColorBtn.IsChecked == true);
        ThicknessBtn.Click += (_, _) => CycleThickness();
        UndoBtn.Click += (_, _) => Undo();
        RedoBtn.Click += (_, _) => Redo();
        CopyBtn.Click += (_, _) => Finish(copy: true, save: false);
        SaveBtn.Click += (_, _) => Finish(copy: false, save: true);
        CloseBtn.Click += (_, _) => Close();

        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseUp;
        KeyDown += OnKeyDown;
        // Ctrl включает снап, и реагировать надо на само нажатие, а не на
        // следующее движение мыши: человек метит в неподвижную карточку
        KeyDown += (_, e) => { if (IsCtrl(e.Key)) OnSnapKeyChanged(); };
        KeyUp += (_, e) => { if (IsCtrl(e.Key)) OnSnapKeyChanged(); };

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

        // Буфер кадра переживает закрытие оверлея: 14 МБ на 2560×1440, и выделять их
        // заново на каждое нажатие клавиши незачем. Оверлей всегда один (см. _open),
        // так что делить буфер не с кем.
        var (bgra, width, height) = await ScreenshotService.CapturePixelsAsync(monitorIndex, cursor, live, _pixels);
        _pixels = bgra;

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
            swatch.MouseLeftButtonDown += (s, e) =>
            {
                SetColor((Color)((Border)s).Tag);
                ShowColorPanel(false);   // цвет выбран — плашка своё отработала
                e.Handled = true;
            };
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

        // Кнопка палитры и значок пипетки показывают ТЕКУЩИЙ цвет. Без этого цвет,
        // взятый с кадра, вообще негде увидеть: среди кружков палитры его нет, а сама
        // палитра теперь свёрнута.
        var current = new SolidColorBrush(_color);
        current.Freeze();
        ColorDot.Fill = current;
        EyedropperIcon.Foreground = current;
    }

    /// <summary>Раскрыть или убрать плашку с цветами. Ставится под кнопкой цвета.</summary>
    private void ShowColorPanel(bool show)
    {
        ColorBtn.IsChecked = show;
        if (!show) { ColorPanel.Visibility = Visibility.Collapsed; return; }

        ColorPanel.Visibility = Visibility.Visible;
        ColorPanel.UpdateLayout();

        // Кнопка лежит внутри панели, а та — где угодно на холсте, поэтому её место
        // спрашиваем у самого дерева, а не считаем от координат выделения.
        var origin = ColorBtn.TransformToAncestor(Root).Transform(new Point(0, 0));
        double width = ColorPanel.ActualWidth > 0 ? ColorPanel.ActualWidth : 260;
        double height = ColorPanel.ActualHeight > 0 ? ColorPanel.ActualHeight : 36;

        double left = Math.Clamp(origin.X + ColorBtn.ActualWidth / 2 - width / 2,
                                 6, Math.Max(6, Root.ActualWidth - width - 6));
        // Под панелью, а если там край экрана — над ней
        double top = origin.Y + ColorBtn.ActualHeight + 8;
        if (top + height > Root.ActualHeight - 6) top = Math.Max(6, origin.Y - height - 8);

        Canvas.SetLeft(ColorPanel, left);
        Canvas.SetTop(ColorPanel, top);
    }

    // ---------------- Мышь ----------------

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Промах по пустому месту панели не должен начинать новое выделение:
        // сами кнопки событие гасят, а вот отступы вокруг них — нет.
        if (e.OriginalSource is DependencyObject source &&
            (Toolbar.IsAncestorOf(source) || ColorPanel.IsAncestorOf(source))) return;

        // Клик мимо раскрытой палитры её закрывает — как любое всплывающее меню
        if (ColorPanel.Visibility == Visibility.Visible) ShowColorPanel(false);

        // Клик мимо набираемой подписи её завершает — как в любом редакторе
        if (_caption is not null && !ReferenceEquals(e.OriginalSource, _caption)) CommitCaption();

        var point = e.GetPosition(Root);

        // Пипетка работает по ВСЕМУ кадру, а не только внутри выделения: подобрать
        // оттенок с соседнего окна — обычное дело, и выделять ради этого пол-экрана
        // никто не станет.
        if (_tool == InkTool.Eyedropper) { PickColor(point); return; }

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

            if (grip == Grip.Inside && _tool == InkTool.Text)
            {
                StartCaption(point);
                return;
            }

            if (grip == Grip.Inside && _tool != InkTool.None)
            {
                CommitCaption();
                _drawing = true;
                var shape = new InkShape
                {
                    Tool = _tool,
                    Color = _color,
                    Thickness = Thicknesses[_thickness],
                    Start = point,
                    End = point
                };
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

        // Подсветку гасим сразу, чтобы не висела поверх протяжки, но саму область
        // запоминаем: понадобится на отпускании, если протяжки так и не случилось
        _pressedCandidate = _candidate;
        SetCandidate(null);

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

        // Пипетка ведёт за курсором кружок с тем цветом, который возьмётся по клику:
        // без него приходится жать наугад и проверять по кнопке уже постфактум.
        if (_tool == InkTool.Eyedropper) { UpdateColorPeek(point); return; }

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
        UpdateCandidate(point);
    }

    /// <summary>
    /// Обновить подсветку области под курсором.
    ///
    /// Работает только до начала протяжки: как только человек зажал и потянул,
    /// он выделяет вручную, и подсказка обязана уйти с дороги. Ctrl глушит её
    /// совсем — на случай, когда область угадывается не так, как хочется.
    /// </summary>
    private void UpdateCandidate(Point point)
    {
        // Снап включает Ctrl. По умолчанию оверлей ведёт себя как раньше: протянул
        // рамку — получил область. Подсказка появляется только когда её позвали,
        // и обычному выделению не мешает вовсе.
        bool armed = SnapArmed
                     && !_hasSelection && !_selecting && !_drawing && !_resizing && !_moving
                     && _tool == InkTool.None;

        if (!armed)
        {
            SetCandidate(null);
            return;
        }

        // Поиск идёт по пикселям и стоит заметно дороже отрисовки, а WPF шлёт
        // MouseMove на каждый пиксель — считаем только когда курсор реально уехал
        if (!double.IsNaN(_lastProbePoint.X)
            && Math.Abs(point.X - _lastProbePoint.X) < 3
            && Math.Abs(point.Y - _lastProbePoint.Y) < 3)
            return;

        _lastProbePoint = point;
        RefreshCandidate(point);
    }

    private static bool IsCtrl(Key key) => key is Key.LeftCtrl or Key.RightCtrl;

    /// <summary>Зажат ли Ctrl — им включается поиск областей.</summary>
    private static bool SnapArmed => (Keyboard.Modifiers & ModifierKeys.Control) != 0;

    private void SetCandidate(Rect? value)
    {
        if (Nullable.Equals(_candidate, value)) return;
        _candidate = value;
        Ink.Candidate = value;

        // Размер показываем сразу: по нему видно, что именно заберёт клик.
        // Трогаем чип только когда своего выделения нет — иначе затрём его размер.
        if (!_hasSelection)
        {
            if (value is { } area) ShowSizeChip(area);
            else SizeChip.Visibility = Visibility.Collapsed;
        }

        Ink.Refresh();
    }

    /// <summary>
    /// Окно под курсором — самое верхнее по Z-порядку.
    ///
    /// Раньше здесь ещё искались блоки внутри окна по контрасту пикселей
    /// (карточки, панели). На реальных экранах это угадывало границы слишком
    /// ненадёжно, и от него отказались: Ctrl подсвечивает окно целиком, а
    /// произвольную область по-прежнему даёт обычная протяжка мышью.
    /// </summary>
    private void RefreshCandidate(Point point) => SetCandidate(TopWindowAt(point));

    /// <summary>
    /// Ctrl нажали или отпустили — подсветка обязана появиться и исчезнуть сразу,
    /// не дожидаясь, пока человек шевельнёт мышью.
    /// </summary>
    private void OnSnapKeyChanged()
    {
        _lastProbePoint = new Point(double.NaN, double.NaN);   // пересчитать на месте
        UpdateHint();
        UpdateCandidate(Mouse.GetPosition(Root));
    }

    /// <summary>Подсказка вверху экрана говорит то, что верно прямо сейчас.</summary>
    private void UpdateHint()
    {
        if (_hasSelection) return;

        Hint.Text = SnapArmed
            ? "Наведите на окно и щёлкните · Esc — отмена"
            : "Протяните область мышью · Ctrl — выделить окно целиком · Esc — отмена";
    }

    /// <summary>Самое верхнее окно под точкой; null — там ничего нет.</summary>
    private Rect? TopWindowAt(Point point)
    {
        if (_windows is null) return null;

        var (px, py) = ToPixels(point);
        foreach (var w in _windows)
            if (px >= w.X && px < w.X + w.Width && py >= w.Y && py < w.Y + w.Height)
                return ToDips(w);

        return null;
    }

    /// <summary>
    /// Из координат окна в пиксели снимка. Множитель считается от фактической
    /// ширины, а не от системного масштаба: окно оверлея растянуто ровно на монитор.
    /// </summary>
    private (int X, int Y) ToPixels(Point point)
    {
        double factor = Root.ActualWidth > 0 ? _pixelWidth / Root.ActualWidth : 1;
        return ((int)Math.Round(point.X * factor), (int)Math.Round(point.Y * factor));
    }

    private Rect ToDips(PixelRect r)
    {
        double factor = _pixelWidth > 0 ? Root.ActualWidth / _pixelWidth : 1;
        return new Rect(r.X * factor, r.Y * factor, r.Width * factor, r.Height * factor);
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

        // Клик без протяжки: если под курсором нашлась область — берём её целиком.
        // Это и есть весь снап со стороны человека: навёл, увидел рамку, щёлкнул.
        if (_selection.Width < 4 || _selection.Height < 4)
        {
            if (_pressedCandidate is { Width: >= 4, Height: >= 4 } area)
            {
                _selection = area;
                Ink.Selection = _selection;
                SetCandidate(null);
                // В протяжке это делает MouseMove; клик через него не проходит,
                // и без этих двух вызовов область выглядела бы незатемнённой и безразмерной
                UpdateDim();
                UpdateSizeChip();
            }
            else
            {
                ResetSelection();
                return;
            }
        }
        else SetCandidate(null);

        _pressedCandidate = null;
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
        _lastProbePoint = new Point(double.NaN, double.NaN);   // искать заново с любого места
        Ink.Refresh();
        UpdateHint();
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

        // Пока набирается подпись, клавиши принадлежат ей: Esc отменяет ввод,
        // Enter заканчивает, всё остальное — обычный набор текста.
        if (_caption is not null)
        {
            if (e.Key == Key.Escape) { CancelCaption(); e.Handled = true; }
            else if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                CommitCaption();
                e.Handled = true;
            }
            return;
        }

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
        CommitCaption();
        _tool = _tool == tool ? InkTool.None : tool;

        PencilBtn.IsChecked = _tool == InkTool.Pencil;
        ArrowBtn.IsChecked = _tool == InkTool.Arrow;
        RectBtn.IsChecked = _tool == InkTool.Rect;
        BlurBtn.IsChecked = _tool == InkTool.Blur;
        TextBtn.IsChecked = _tool == InkTool.Text;
        EyedropperBtn.IsChecked = _tool == InkTool.Eyedropper;

        // Размытая копия готовится один раз и только если её попросили: это полный
        // проход по кадру, платить за него тем, кто размытием не пользуется, незачем.
        if (_tool == InkTool.Blur) EnsureBlurred();

        // Пипетке затемнение мешает: она про НАСТОЯЩИЙ цвет кадра, а поверх него
        // лежит полупрозрачная чёрная заливка. Снимаем её на время выбора и
        // возвращаем, как только инструмент сменился.
        UpdateDim();
        if (_tool != InkTool.Eyedropper) HideColorPeek();
        if (_tool == InkTool.Eyedropper) ShowColorPanel(false);

        Cursor = _tool switch
        {
            InkTool.None => Cursors.Arrow,
            InkTool.Text => Cursors.IBeam,
            InkTool.Eyedropper => Cursors.Cross,
            _ => Cursors.Pen
        };
    }

    /// <summary>
    /// Взять цвет из кадра под курсором.
    ///
    /// Читаем из <see cref="_shot"/>, а не с экрана: на экране поверх кадра лежат
    /// затемнение, пометки и сама панель, и пипетка брала бы их, а не картинку.
    /// Координаты окна переводим в пиксели снимка тем же множителем, что и экспорт, —
    /// иначе на мониторе с масштабом попадёшь мимо.
    ///
    /// После выбора сразу берём карандаш: цвет выбирают, чтобы им рисовать, и лишний
    /// клик по инструменту здесь только мешает.
    /// </summary>
    /// <summary>Кружок с цветом под курсором и его код — рядом с самим курсором.</summary>
    private void UpdateColorPeek(Point at)
    {
        try
        {
            var color = ColorSampling.At(_shot, at, Root.ActualWidth);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            PeekDot.Fill = brush;
            PeekText.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";

            ColorPeek.Visibility = Visibility.Visible;
            ColorPeek.UpdateLayout();

            // Ниже-правее курсора, но у краёв экрана переворачиваем: подсказка,
            // уехавшая за границу, бесполезна ровно там, где нужнее всего.
            double width = ColorPeek.ActualWidth, height = ColorPeek.ActualHeight;
            double left = at.X + 18, top = at.Y + 18;
            if (left + width > Root.ActualWidth - 4) left = at.X - width - 12;
            if (top + height > Root.ActualHeight - 4) top = at.Y - height - 12;

            Canvas.SetLeft(ColorPeek, Math.Max(4, left));
            Canvas.SetTop(ColorPeek, Math.Max(4, top));
        }
        catch { HideColorPeek(); }
    }

    private void HideColorPeek() => ColorPeek.Visibility = Visibility.Collapsed;

    private void PickColor(Point at)
    {
        try
        {
            var picked = ColorSampling.At(_shot, at, Root.ActualWidth);
            _color = picked;
            HighlightSwatch();
            EyedropperBtn.ToolTip = $"Пипетка — взят #{picked.R:X2}{picked.G:X2}{picked.B:X2}";
            SetTool(InkTool.Pencil);
        }
        catch (Exception ex) { Log.Warn("Screenshot", $"Пипетка: {ex.Message}"); }
    }

    private void CycleThickness()
    {
        _thickness = (_thickness + 1) % Thicknesses.Length;
        double value = Thicknesses[_thickness];
        ThicknessDot.Width = ThicknessDot.Height = 5 + value;
        ThicknessBtn.ToolTip = $"Толщина линии: {value:0.#} px";
    }

    /// <summary>
    /// Размытая копия всего снимка. Делается через обычный эффект размытия WPF, а
    /// дальше используется как картинка: инструмент рисует её кусок, и в файл попадает
    /// ровно то же, что видно на экране. Разрешение сохраняем исходное — копия
    /// готовится в пикселях кадра, а не окна.
    /// </summary>
    private void EnsureBlurred()
    {
        if (Ink.Blurred is not null) return;

        try
        {
            double width = Root.ActualWidth, height = Root.ActualHeight;
            if (width <= 0 || height <= 0) return;

            double factor = _shot.PixelWidth / width;
            var image = new Image
            {
                Source = _shot,
                Width = width,
                Height = height,
                Effect = new System.Windows.Media.Effects.BlurEffect
                {
                    Radius = 16,
                    KernelType = System.Windows.Media.Effects.KernelType.Gaussian,
                    RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance
                }
            };
            image.Measure(new Size(width, height));
            image.Arrange(new Rect(0, 0, width, height));

            var bitmap = new RenderTargetBitmap(
                _shot.PixelWidth, _shot.PixelHeight, 96 * factor, 96 * factor, PixelFormats.Pbgra32);
            bitmap.Render(image);
            bitmap.Freeze();
            Ink.Blurred = bitmap;
        }
        catch (Exception ex) { Log.Warn("Screenshot", $"Размытие недоступно: {ex.Message}"); }
    }

    // ---------------- Подпись ----------------

    /// <summary>Поле ввода прямо на снимке: человек печатает там же, где появится текст.</summary>
    private void StartCaption(Point at)
    {
        CommitCaption();
        _captionAt = at;

        var brush = new SolidColorBrush(_color);
        brush.Freeze();
        _caption = new TextBox
        {
            MinWidth = 60,
            Background = System.Windows.Media.Brushes.Transparent,
            Foreground = brush,
            CaretBrush = brush,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            FontFamily = new FontFamily("Segoe UI"),
            FontWeight = FontWeights.SemiBold,
            FontSize = InkLayer.CaptionSize(Thicknesses[_thickness]),
            Padding = new Thickness(2, 0, 2, 0),
            Cursor = Cursors.IBeam
        };
        // Поле стоит так, чтобы буквы оказались там же, где их нарисует экспорт
        Canvas.SetLeft(_caption, at.X - 3);
        Canvas.SetTop(_caption, at.Y - 2);
        Layer.Children.Add(_caption);

        _caption.Focus();
        Keyboard.Focus(_caption);
    }

    /// <summary>Закончить ввод: пустую подпись выбрасываем, непустую превращаем в фигуру.</summary>
    private void CommitCaption()
    {
        if (_caption is null) return;

        string text = _caption.Text.Trim();
        Layer.Children.Remove(_caption);
        _caption = null;

        if (text.Length == 0) return;

        Ink.Shapes.Add(new InkShape
        {
            Tool = InkTool.Text,
            Color = _color,
            Thickness = Thicknesses[_thickness],
            Start = _captionAt,
            Text = text
        });
        _undone.Clear();
        Ink.Refresh();
    }

    private void CancelCaption()
    {
        if (_caption is null) return;
        Layer.Children.Remove(_caption);
        _caption = null;
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
        // Пока выбирают цвет — кадр показываем как есть: сквозь затемнение оттенок
        // не подобрать, а пипетка всё равно берёт цвет из исходного кадра.
        if (_tool == InkTool.Eyedropper) { Dim.Data = null; return; }

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

    private void UpdateSizeChip() => ShowSizeChip(_selection);

    /// <summary>Плашка с размером — и для своего выделения, и для подсвеченной области.</summary>
    private void ShowSizeChip(Rect area)
    {
        double factor = _shot.PixelWidth / Math.Max(1, Root.ActualWidth);
        SizeText.Text = $"{Math.Round(area.Width * factor)} × {Math.Round(area.Height * factor)}";
        Canvas.SetLeft(SizeChip, Math.Max(4, area.Left));
        Canvas.SetTop(SizeChip, Math.Max(4, area.Top - 26));
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
        // Недонабранная подпись должна попасть в снимок, а не пропасть по кнопке
        CommitCaption();
        if (!_hasSelection) return;

        try
        {
            // Кадр собираем здесь — он требует потока интерфейса, — а вот кодирование
            // PNG и запись файла уводим в фон: на выделении в пол-4K это заметная
            // пауза перед закрытием окна, и человек видит подвисший оверлей.
            var image = RenderSelection();
            image.Freeze();

            if (copy && !ImageClipboard.Copy(image))
            {
                Failed?.Invoke(new InvalidOperationException("буфер обмена занят другой программой"));
                Close();
                return;
            }

            if (!save)
            {
                Log.Info("Screenshot", $"Область скопирована в буфер ({image.PixelWidth}x{image.PixelHeight})");
                Saved?.Invoke(null, copy);
                Close();
                return;
            }

            string folder = _screenshotFolder;
            bool copied = copy;
            _ = Task.Run(() =>
            {
                try
                {
                    // Имя ищется по фактическому содержимому папки на каждый снимок
                    string file = ScreenshotService.NextFilePath(folder);

                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(image));
                    using (var stream = File.Create(file)) encoder.Save(stream);

                    Log.Info("Screenshot", $"Область сохранена: {file} ({image.PixelWidth}x{image.PixelHeight})");
                    Saved?.Invoke(file, copied);
                }
                catch (Exception ex)
                {
                    Log.Error("Screenshot", ex);
                    Failed?.Invoke(ex);
                }
            });
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
