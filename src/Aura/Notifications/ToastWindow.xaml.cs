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
                          Func<ImageSource?>? thumbnailSource, double maxWaitSeconds = 10)
    {
        _expandTimer?.Stop();
        _hideTimer?.Stop();

        // Сбрасываем сдвиг от прошлого ухода: иначе следующее уведомление
        // появлялось бы смещённым.
        CardShift.BeginAnimation(TranslateTransform.XProperty, null);
        CardShift.BeginAnimation(TranslateTransform.YProperty, null);
        CardShift.X = 0;
        CardShift.Y = 0;

        Place(position);
        bool busy = content.BusyTitle is not null;
        Fill(content, busy);

        bool wasVisible = IsVisible && Opacity > 0.5;
        if (!IsVisible)
        {
            Opacity = 0;
            Show();
        }
        if (!wasVisible) AppearAnimation();

        _pending = content;
        _pendingSeconds = seconds;
        _pendingThumb = thumbnailSource;

        if (busy)
            // Ждём CompleteToast, но не дольше отведённого срока: молча висеть нельзя
            _expandTimer = StartTimer(maxWaitSeconds, () => Finish(_pending!, seconds, thumbnailSource));
        else
            Finish(content, seconds, thumbnailSource);
    }

    /// <summary>Снимок вёрстки в режиме --dev: содержимое без показа и анимаций.</summary>
    internal void PrepareForSnapshot(ToastContent content)
    {
        Fill(content, busy: false);
        LifeTrack.Opacity = 1;
        LifeScale.ScaleX = 0.7;
        Life.Background = Tint(content);
    }

    /// <summary>Работа закончена: карточка показывает результат.</summary>
    public void CompleteToast(string title, string subtitle)
    {
        if (_pending is null || !IsVisible) return;
        _expandTimer?.Stop();
        _pending = _pending with { Title = title, Subtitle = subtitle, BusyTitle = null };
        Fill(_pending, busy: false);
        Finish(_pending, _pendingSeconds, _pendingThumb);
    }

    private static SolidColorBrush Tint(ToastContent content) =>
        new((content.Tint as SolidColorBrush)?.Color ?? Colors.White);

    /// <summary>Текст, значок и цвет. Размер карточки при этом не меняется.</summary>
    private void Fill(ToastContent content, bool busy)
    {
        var tint = Tint(content).Color;
        TitleText.Text = busy ? content.BusyTitle! : content.Title;
        SubText.Text = busy ? "секунду…" : content.Subtitle;
        SubText.Visibility = string.IsNullOrWhiteSpace(SubText.Text) ? Visibility.Collapsed : Visibility.Visible;
        TitleText.FontSize = SubText.Visibility == Visibility.Visible ? 13 : 13.5;
        HintText.Text = busy ? "" : content.Hint ?? "";

        // Цвет события на значке и полоске, подложка значка того же цвета, но
        // приглушённая: плотная цветная плитка кричала поверх игры.
        ArtIcon.Data = content.Icon;
        ArtIcon.Foreground = new SolidColorBrush(tint);
        Art.Background = new SolidColorBrush(busy ? Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)
                                                  : Color.FromArgb(0x22, tint.R, tint.G, tint.B));
        Art.Width = 36;
        Art.CornerRadius = new CornerRadius(10);
        Shot.Source = null;
        Shot.Opacity = 0;
        ArtIcon.Opacity = busy ? 0 : 1;
        Spin.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy) _spin.Begin();
        else _spin.Stop();
        LifeTrack.BeginAnimation(OpacityProperty, null);
        LifeTrack.Opacity = 0;
    }

    /// <summary>Итог: кадр из записи вместо значка, полоска времени и таймер ухода.</summary>
    private void Finish(ToastContent content, double seconds, Func<ImageSource?>? thumbnailSource)
    {
        if (content.BusyTitle is not null) Fill(content with { BusyTitle = null }, busy: false);

        if (content.WantsThumbnail && thumbnailSource?.Invoke() is { } image)
        {
            // Кадр шире значка: 16:9, как сама запись
            Art.Width = 64;
            Art.CornerRadius = new CornerRadius(7);
            Shot.Source = image;
            Shot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromSeconds(0.3)));
            ArtIcon.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromSeconds(0.2)));
        }

        Life.Background = Tint(content);
        LifeTrack.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromSeconds(0.2)));
        LifeScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1, 0, TimeSpan.FromSeconds(Math.Max(1, seconds))));

        _hideTimer = StartTimer(Math.Max(1, seconds), HideToast);
    }

    private void AppearAnimation()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var time = TimeSpan.FromSeconds(0.38);
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.22)));
        CardShift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(Card.VerticalAlignment == VerticalAlignment.Top ? -14 : 14, 0, time) { EasingFunction = ease });
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.97, 1, time) { EasingFunction = ease });
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.97, 1, time) { EasingFunction = ease });
    }

    public void HideToast()
    {
        _hideTimer?.Stop();
        var fade = new DoubleAnimation(0, TimeSpan.FromSeconds(0.28)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        fade.Completed += (_, _) => { if (Opacity <= 0.01) Hide(); };
        BeginAnimation(OpacityProperty, fade);
        CardShift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(0, Card.VerticalAlignment == VerticalAlignment.Top ? -8 : 8, TimeSpan.FromSeconds(0.28)));
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0.97, TimeSpan.FromSeconds(0.28)));
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 0.97, TimeSpan.FromSeconds(0.28)));
    }

    /// <summary>Окно прижимается к нужному углу рабочего стола, карточка к тому же углу внутри.</summary>

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
