using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Aura.Core.Settings;
using static Aura.Core.Interop.NativeMethods;

namespace Aura.Notifications;

/// <summary>Что показать в уведомлении.</summary>
public sealed record ToastContent(
    string Title,
    string Subtitle,
    Geometry? Icon,
    Brush Tint,
    string? BusyTitle = null,
    string? Hint = null,
    bool WantsThumbnail = false);

/// <summary>
/// Окно уведомления поверх всех окон.
///
/// Не отбирает фокус у игры и пропускает клики насквозь: WS_EX_NOACTIVATE |
/// WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW (последний убирает окно из Alt-Tab).
/// Системная рамка гасится через DWMWA_BORDER_COLOR = DWMWA_COLOR_NONE — иначе
/// вокруг прозрачного окна остаётся светлая линия.
///
/// Ограничение платформы: поверх ИСКЛЮЧИТЕЛЬНОГО полноэкранного режима оверлеи
/// не рисуются в принципе; в оконном и безрамочном — работают.
/// </summary>
public partial class ToastWindow : Window
{
    private const double CompactWidth = 250, CompactHeight = 62;
    private const double WideWidth = 400, WideHeight = 74;

    private readonly Storyboard _spin;
    private DispatcherTimer? _expandTimer, _hideTimer;

    // Что показать, когда капсула развернётся: приходит либо по событию
    // «файл дописан», либо по страховочному таймеру, если события не будет.
    private ToastContent? _pending;
    private double _pendingSeconds = 3.5;
    private Func<ImageSource?>? _pendingThumb;

    public ToastWindow()
    {
        InitializeComponent();

        var rotate = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever };
        Storyboard.SetTarget(rotate, Spin);
        Storyboard.SetTargetProperty(rotate, new PropertyPath("RenderTransform.Angle"));
        _spin = new Storyboard();
        _spin.Children.Add(rotate);

        SourceInitialized += (_, _) => ApplyNativeStyles();
    }

    private void ApplyNativeStyles()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int ex = GetWindowLongW(hwnd, GWL_EXSTYLE);
        SetWindowLongW(hwnd, GWL_EXSTYLE,
            ex | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);

        // Рамку гасит композитор: у прозрачного окна иначе остаётся светлая линия
        uint none = DWMWA_COLOR_NONE;
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref none, sizeof(uint));
    }

    /// <summary>Показать уведомление. Повторный вызов заменяет содержимое.</summary>
    public void ShowToast(ToastContent content, NotificationPosition position, double seconds,
                          Func<ImageSource?>? thumbnailSource)
    {
        _expandTimer?.Stop();
        _hideTimer?.Stop();

        // Сбрасываем сдвиг от прошлого ухода: иначе каждое следующее уведомление
        // появлялось на 14 px правее, и отступы слева и справа переставали совпадать.
        CardShift.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
        CardShift.X = 0;

        Place(position);

        bool busy = content.BusyTitle is not null;
        TitleText.Text = content.BusyTitle ?? content.Title;
        SubText.Text = busy ? "секунду…" : content.Subtitle;
        HintText.Text = content.Hint ?? "";
        ArtIcon.Data = content.Icon;
        Art.Background = content.Tint;
        Shot.Source = null;
        Shot.Opacity = 0;

        // Компактное состояние
        Card.Width = CompactWidth;
        Card.Height = CompactHeight;
        Art.Width = 40; Art.Height = 40;
        SubText.Opacity = 0;
        HintText.Opacity = 0;
        LifeTrack.Opacity = 0;
        ArtIcon.Opacity = busy ? 0 : 1;
        Spin.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy) _spin.Begin();
        else _spin.Stop();

        if (!IsVisible)
        {
            Opacity = 0;
            Show();
        }
        AppearAnimation();

        _pending = content;
        _pendingSeconds = seconds;
        _pendingThumb = thumbnailSource;

        // Без ожидания карточка разворачивается сразу. С ожиданием ждём
        // CompleteToast, но не дольше десяти секунд: молча висеть нельзя.
        _expandTimer = StartTimer(busy ? 10 : 0.28, () => Expand(_pending!, seconds, thumbnailSource));
    }

    /// <summary>Работа закончена: капсула разворачивается в карточку с результатом.</summary>
    public void CompleteToast(string title, string subtitle)
    {
        if (_pending is null || !IsVisible) return;
        _expandTimer?.Stop();
        _pending = _pending with { Title = title, Subtitle = subtitle, BusyTitle = null };
        Expand(_pending, _pendingSeconds, _pendingThumb);
    }

    private void AppearAnimation()
    {
        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 };
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromSeconds(0.22)));
        CardShift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(18, 0, TimeSpan.FromSeconds(0.42)) { EasingFunction = ease });
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.94, 1, TimeSpan.FromSeconds(0.42)) { EasingFunction = ease });
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.94, 1, TimeSpan.FromSeconds(0.42)) { EasingFunction = ease });
    }

    private void Expand(ToastContent content, double seconds, Func<ImageSource?>? thumbnailSource)
    {
        _spin.Stop();
        Spin.Visibility = Visibility.Collapsed;

        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.22 };
        var grow = TimeSpan.FromSeconds(0.5);

        TitleText.Text = content.Title;
        SubText.Text = content.Subtitle;

        Card.BeginAnimation(WidthProperty, new DoubleAnimation(WideWidth, grow) { EasingFunction = ease });
        Card.BeginAnimation(HeightProperty, new DoubleAnimation(WideHeight, grow) { EasingFunction = ease });
        Art.BeginAnimation(WidthProperty, new DoubleAnimation(62, grow) { EasingFunction = ease });
        Art.BeginAnimation(HeightProperty, new DoubleAnimation(42, grow) { EasingFunction = ease });

        ArtIcon.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromSeconds(0.2)));
        SubText.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromSeconds(0.3)) { BeginTime = TimeSpan.FromSeconds(0.1) });
        if (!string.IsNullOrEmpty(HintText.Text))
            HintText.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromSeconds(0.3)) { BeginTime = TimeSpan.FromSeconds(0.12) });

        // Кадр из записи — если он есть, плитка с иконкой уступает ему место
        if (content.WantsThumbnail && thumbnailSource is not null)
        {
            var image = thumbnailSource();
            if (image is not null)
            {
                Shot.Source = image;
                Shot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromSeconds(0.35)));
                ArtIcon.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromSeconds(0.25)));
            }
        }

        // Полоска времени показа
        LifeTrack.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromSeconds(0.25)));
        Life.Width = WideWidth - 24;
        LifeScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1, 0, TimeSpan.FromSeconds(Math.Max(1, seconds))));

        _hideTimer = StartTimer(Math.Max(1, seconds), HideToast);
    }

    public void HideToast()
    {
        _hideTimer?.Stop();
        var fade = new DoubleAnimation(0, TimeSpan.FromSeconds(0.28));
        fade.Completed += (_, _) => { if (Opacity <= 0.01) Hide(); };
        BeginAnimation(OpacityProperty, fade);
        CardShift.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty,
            new DoubleAnimation(0, 14, TimeSpan.FromSeconds(0.28)));
    }

    /// <summary>Окно прижимается к нужному углу рабочего стола, карточка — к тому же углу внутри.</summary>
    private void Place(NotificationPosition position)
    {
        var area = SystemParameters.WorkArea;

        bool top = position is NotificationPosition.TopLeft or NotificationPosition.TopRight or NotificationPosition.TopCenter;
        bool left = position is NotificationPosition.TopLeft or NotificationPosition.BottomLeft;
        bool center = position is NotificationPosition.TopCenter;

        Left = center ? area.Left + (area.Width - Width) / 2 : left ? area.Left : area.Right - Width;
        Top = top ? area.Top : area.Bottom - Height;

        Card.VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        Card.HorizontalAlignment = center ? HorizontalAlignment.Center
                                 : left ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        // Отступ от края экрана: вплотную к рамке уведомление смотрится обрезанным.
        // По центру отступы обязаны быть симметричными — иначе карточка уезжает
        // вбок ровно на половину разницы.
        const double edge = 24;
        double leftMargin = center ? edge : left ? edge : 0;
        double rightMargin = center ? edge : left ? 0 : edge;
        Card.Margin = new Thickness(leftMargin, top ? edge : 0, rightMargin, top ? 0 : edge);
        Card.RenderTransformOrigin = new Point(left || center ? 0 : 1, top ? 0 : 1);
    }

    private static DispatcherTimer StartTimer(double seconds, Action action)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        timer.Tick += (_, _) => { timer.Stop(); action(); };
        timer.Start();
        return timer;
    }
}
