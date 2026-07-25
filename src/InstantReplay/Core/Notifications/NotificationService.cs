using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI;
using InstantReplay.Core.Settings;
using static InstantReplay.Core.Interop.NativeMethods;

namespace InstantReplay.Core.Notifications;

public enum NotificationKind { ReplayOn, Saved, Stopped, Warning, Screenshot, Recording }

/// <summary>
/// Кастомные полупрозрачные уведомления поверх ВСЕХ окон (включая borderless-игры).
/// Окно строится кодом (без XAML-страницы): Acrylic-подложка, плавное появление
/// (fade + slide), автоскрытие через 3.5 сек.
///
/// Фикс «белой рамки» (та самая проблема): у WinUI-окна DWM рисует однопиксельную
/// системную рамку даже после SetBorderAndTitleBar(false,false). Лечится ТОЛЬКО
/// через DwmSetWindowAttribute(DWMWA_BORDER_COLOR, DWMWA_COLOR_NONE) — рамка
/// отключается на уровне композитора. Плюс скругление углов DWMWCP_ROUND.
/// Окно click-through (WS_EX_TRANSPARENT|WS_EX_LAYERED) и не отбирает фокус
/// у игры (WS_EX_NOACTIVATE|WS_EX_TOOLWINDOW — также прячет его из Alt-Tab).
///
/// Ограничение платформы: поверх ИСКЛЮЧИТЕЛЬНОГО (exclusive) fullscreen оверлеи
/// ОС не рисуются в принципе (так же ведёт себя и ShadowPlay без своего hook-слоя);
/// в borderless/windowed — работает везде.
/// </summary>
public sealed class NotificationService
{
    private readonly SettingsManager _settings;
    private readonly DispatcherQueueHolder _dispatcher;
    private Window? _window;
    private TextBlock? _icon;
    private TextBlock? _text;
    private Grid? _root;
    private Border? _thumbBorder;
    private Image? _thumb;

    /// <summary>
    /// Источник кадра для превью в уведомлении. Задаётся приложением; если буфер
    /// выключен, вернёт false и превью просто не покажется.
    /// </summary>
    public Capture.LiveFrameProvider? PreviewSource { get; set; }
    private CancellationTokenSource? _hideCts;

    private const int WinWidth = 430, WinHeight = 76, Margin = 24;

    public NotificationService(SettingsManager settings, DispatcherQueueHolder dispatcher)
    {
        _settings = settings;
        _dispatcher = dispatcher;
    }

    public void Show(NotificationKind kind, string message)
    {
        var s = _settings.Current;
        // Звук сохранения — даже если визуальные уведомления выключены
        if (kind is NotificationKind.Saved or NotificationKind.Screenshot)
            NotificationSounds.Play(s.SaveSound, s.CustomSaveSoundPath);
        if (!s.ShowNotifications) return;
        _dispatcher.Enqueue(() => ShowCore(kind, message));
    }

    private void ShowCore(NotificationKind kind, string message)
    {
        EnsureWindow();

        _icon!.Text = kind switch
        {
            NotificationKind.ReplayOn   => "\uE768",  // Play
            NotificationKind.Saved      => "\uE74E",  // Save
            NotificationKind.Stopped    => "\uE71A",  // Stop
            NotificationKind.Screenshot => "\uE722",  // Camera
            NotificationKind.Recording  => "\uE7C8",  // Record
            _                           => "\uE7BA",  // Warning
        };
        _icon.Foreground = new SolidColorBrush(kind switch
        {
            NotificationKind.ReplayOn   => Color.FromArgb(255, 76, 217, 100),
            NotificationKind.Saved      => Color.FromArgb(255, 10, 132, 255),
            NotificationKind.Screenshot => Color.FromArgb(255, 10, 132, 255),
            NotificationKind.Recording  => Color.FromArgb(255, 255, 69, 58),
            NotificationKind.Stopped    => Color.FromArgb(255, 255, 69, 58),
            _                           => Color.FromArgb(255, 255, 214, 10),
        });
        _text!.Text = message;
        UpdateThumbnail(kind);

        PositionWindow();
        _window!.AppWindow.Show(activateWindow: false); // не отбираем фокус у игры

        // Плавное появление: fade-in + сдвиг
        var sb = new Storyboard();
        var fade = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(220), EnableDependentAnimation = true };
        Storyboard.SetTarget(fade, _root);
        Storyboard.SetTargetProperty(fade, "Opacity");
        sb.Children.Add(fade);

        _root!.RenderTransform = new TranslateTransform { Y = -14 };
        var slide = new DoubleAnimation
        {
            From = -14, To = 0,
            Duration = TimeSpan.FromMilliseconds(260),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(slide, _root.RenderTransform);
        Storyboard.SetTargetProperty(slide, "Y");
        sb.Children.Add(slide);
        sb.Begin();

        // Автоскрытие (длительность из настроек)
        _hideCts?.Cancel();
        _hideCts = new CancellationTokenSource();
        var token = _hideCts.Token;
        var duration = TimeSpan.FromSeconds(Math.Clamp(_settings.Current.NotificationDurationSeconds, 1, 15));
        Task.Delay(duration, token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            _dispatcher.Enqueue(HideAnimated);
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// Миниатюра последнего кадра для «сохранено» и «скриншот» — чтобы было видно,
    /// ЧТО записалось, а не только что файл куда-то лёг.
    ///
    /// Кадр берём у уже работающего буфера (тот же путь, что у скриншота), и всё
    /// делаем в фоне: уведомление показывается сразу, картинка догружается через
    /// миг. Если буфер выключен или что-то не получилось — просто нет превью,
    /// уведомление от этого не страдает.
    /// </summary>
    private void UpdateThumbnail(NotificationKind kind)
    {
        if (_thumbBorder is null) return;
        _thumbBorder.Visibility = Visibility.Collapsed;

        if (kind is not (NotificationKind.Saved or NotificationKind.Screenshot)) return;
        var source = PreviewSource;
        if (source is null) return;

        Task.Run(async () =>
        {
            try
            {
                (byte[] Bgra, int W, int H)? frame = null;
                source((device, context, texture) =>
                    frame = Capture.ScreenshotService.ReadPixels(device, context, texture));
                if (frame is null) return;

                var (pixels, w, h) = Downscale(frame.Value.Bgra, frame.Value.W, frame.Value.H, 152);

                var stream = new InMemoryRandomAccessStream();
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
                    (uint)w, (uint)h, 96, 96, pixels);
                await encoder.FlushAsync();
                stream.Seek(0);

                _dispatcher.Enqueue(async () =>
                {
                    try
                    {
                        var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                        await bitmap.SetSourceAsync(stream);
                        _thumb!.Source = bitmap;
                        _thumbBorder.Visibility = Visibility.Visible;
                    }
                    catch { }
                });
            }
            catch { }
        });
    }

    /// <summary>Уменьшение ближайшим соседом — для миниатюры 150 px этого достаточно.</summary>
    private static (byte[] Pixels, int W, int H) Downscale(byte[] src, int srcW, int srcH, int maxWidth)
    {
        int w = Math.Min(maxWidth, srcW);
        int h = Math.Max(1, (int)Math.Round(srcH * (w / (double)srcW)));
        var dst = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            int srcRow = y * srcH / h * srcW * 4;
            int dstRow = y * w * 4;
            for (int x = 0; x < w; x++)
                System.Buffer.BlockCopy(src, srcRow + x * srcW / w * 4, dst, dstRow + x * 4, 4);
        }
        return (dst, w, h);
    }

    private void HideAnimated()
    {
        if (_window is null || _root is null) return;
        var fade = new DoubleAnimation { From = 1, To = 0, Duration = TimeSpan.FromMilliseconds(200), EnableDependentAnimation = true };
        var sb = new Storyboard();
        Storyboard.SetTarget(fade, _root);
        Storyboard.SetTargetProperty(fade, "Opacity");
        sb.Children.Add(fade);
        sb.Completed += (_, _) => _window?.AppWindow.Hide();
        sb.Begin();
    }

    private void EnsureWindow()
    {
        if (_window is not null) return;

        _window = new Window();

        _icon = new TextBlock
        {
            // Fallback на Segoe MDL2 Assets: на Windows 10 шрифта Segoe Fluent Icons
            // нет, и без запасного иконка уведомления была «тофу»-квадратом.
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 22,
            VerticalAlignment = VerticalAlignment.Center
        };
        _text = new TextBlock
        {
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Colors.White),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 230 // чтобы длинный текст не выдавливал миниатюру за край окна
        };

        // Превью сохранённого кадра: скруглённая миниатюра справа от текста.
        // Пока кадра нет — блок скрыт и место не занимает.
        _thumb = new Image { Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill };
        _thumbBorder = new Border
        {
            Width = 76,
            Height = 44,
            CornerRadius = new CornerRadius(6),
            BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            Child = _thumb
        };

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(20, 0, 20, 0)
        };
        panel.Children.Add(_icon);
        panel.Children.Add(_text);
        panel.Children.Add(_thumbBorder);

        _root = new Grid
        {
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromArgb(120, 20, 20, 24)), // поверх акрила
            BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Opacity = 0
        };
        _root.Children.Add(panel);
        _window.Content = _root;

        // Acrylic-подложка (полупрозрачность + размытие)
        _window.SystemBackdrop = new DesktopAcrylicBackdrop();

        var appWindow = _window.AppWindow;
        appWindow.Resize(new Windows.Graphics.SizeInt32(WinWidth, WinHeight));
        appWindow.IsShownInSwitchers = false;

        if (appWindow.Presenter is OverlappedPresenter p)
        {
            p.SetBorderAndTitleBar(false, false);
            p.IsResizable = false;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
            p.IsAlwaysOnTop = true;
        }

        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);

        // === ФИКС БЕЛОЙ РАМКИ ===
        uint colorNone = DWMWA_COLOR_NONE;
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref colorNone, sizeof(uint));
        int round = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

        // Click-through + не активируется + нет в Alt-Tab + topmost
        int ex = GetWindowLongW(hwnd, GWL_EXSTYLE);
        SetWindowLongW(hwnd, GWL_EXSTYLE,
            ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOPMOST);
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private void PositionWindow()
    {
        var area = DisplayArea.GetFromWindowId(_window!.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var (x, y) = _settings.Current.NotificationPosition switch
        {
            NotificationPosition.TopLeft     => (area.X + Margin, area.Y + Margin),
            NotificationPosition.TopRight    => (area.X + area.Width - WinWidth - Margin, area.Y + Margin),
            NotificationPosition.BottomLeft  => (area.X + Margin, area.Y + area.Height - WinHeight - Margin),
            NotificationPosition.BottomRight => (area.X + area.Width - WinWidth - Margin, area.Y + area.Height - WinHeight - Margin),
            _ => (area.X + (area.Width - WinWidth) / 2, area.Y + Margin),
        };
        _window.AppWindow.Move(new Windows.Graphics.PointInt32(x, y));
    }
}

/// <summary>Обёртка над DispatcherQueue UI-потока, чтобы сервисы не зависели от окон.</summary>
public sealed class DispatcherQueueHolder(Microsoft.UI.Dispatching.DispatcherQueue queue)
{
    public void Enqueue(Action action)
    {
        if (queue.HasThreadAccess) action();
        else queue.TryEnqueue(() => action());
    }
}
