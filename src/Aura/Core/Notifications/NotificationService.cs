using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Aura.Core.Settings;
using Aura.Notifications;

namespace Aura.Core.Notifications;

public enum NotificationKind { ReplayOn, Saved, Stopped, Warning, Screenshot, Recording }

/// <summary>
/// Уведомления поверх игры. Само окно и анимации — в <see cref="ToastWindow"/>,
/// здесь только логика: что показывать, каким цветом и когда.
///
/// Сохранение показывается в два этапа: как только буфер снят, появляется
/// капсула «Сохраняю повтор…», а когда файл дописан на диск — она разворачивается
/// в карточку с кадром и весом. Пользователь видит, что работа идёт, и не жмёт
/// хоткей повторно.
/// </summary>
public sealed class NotificationService(SettingsManager settings, UiDispatcher dispatcher)
{
    private ToastWindow? _window;

    /// <summary>Кадр для превью берётся у работающего захвата; выключен — превью не будет.</summary>
    public Capture.LiveFrameProvider? PreviewSource { get; set; }

    /// <summary>Разовое уведомление: заголовок и необязательная вторая строка.</summary>
    public void Show(NotificationKind kind, string message, string? detail = null)
    {
        var s = settings.Current;
        if (kind is NotificationKind.Saved or NotificationKind.Screenshot)
            NotificationSounds.Play(s.SaveSound, s.CustomSaveSoundPath);
        if (!s.ShowNotifications) return;

        dispatcher.Enqueue(() =>
        {
            var (icon, tint) = Look(kind);
            Window().ShowToast(
                new ToastContent(message, detail ?? Hint(kind), icon, tint,
                                 WantsThumbnail: kind is NotificationKind.Saved or NotificationKind.Screenshot),
                s.NotificationPosition, s.NotificationDurationSeconds, Thumbnail);
        });
    }

    /// <summary>Первый этап сохранения: капсула с индикатором, без автозакрытия.</summary>
    public void ShowSaving(string busyTitle)
    {
        var s = settings.Current;
        if (!s.ShowNotifications) return;

        dispatcher.Enqueue(() =>
        {
            var (icon, tint) = Look(NotificationKind.Saved);
            Window().ShowToast(
                new ToastContent("Готово", "", icon, tint, BusyTitle: busyTitle, WantsThumbnail: true),
                s.NotificationPosition, s.NotificationDurationSeconds, Thumbnail);
        });
    }

    /// <summary>Второй этап: файл дописан — капсула разворачивается в карточку.</summary>
    public void CompleteSaving(string title, string? detail)
    {
        var s = settings.Current;
        NotificationSounds.Play(s.SaveSound, s.CustomSaveSoundPath);
        if (!s.ShowNotifications) return;
        dispatcher.Enqueue(() => _window?.CompleteToast(title, detail ?? ""));
    }

    private ToastWindow Window() => _window ??= new ToastWindow();

    /// <summary>Вторая строка. Коротко: в карточку влезает ~34 символа.</summary>
    private static string Hint(NotificationKind kind) => kind switch
    {
        NotificationKind.ReplayOn  => "буфер пишется",
        NotificationKind.Stopped   => "буфер остановлен",
        NotificationKind.Recording => "идёт запись в файл",
        _ => ""
    };

    /// <summary>Иконка и цвет плитки под каждый повод.</summary>
    private static (Geometry? Icon, Brush Tint) Look(NotificationKind kind)
    {
        string icon = kind switch
        {
            NotificationKind.Saved      => "Ico.Save",
            NotificationKind.Screenshot => "Ico.Camera",
            NotificationKind.Recording  => "Ico.Rec",
            NotificationKind.Stopped    => "Ico.Stop",
            NotificationKind.Warning    => "Ico.Alert",
            _                           => "Ico.Play"
        };
        string tint = kind switch
        {
            NotificationKind.Warning    => "OrangeBrush",
            NotificationKind.Recording  => "RecBrush",
            NotificationKind.Screenshot => "IndigoBrush",
            NotificationKind.Stopped    => "GrayBrush",
            _                           => "AccentBrush"
        };
        var app = Application.Current;
        return (app?.TryFindResource(icon) as Geometry,
                app?.TryFindResource(tint) as Brush ?? Brushes.Gray);
    }

    /// <summary>
    /// Кадр из работающего захвата → картинка для карточки. Пиксели уже BGRA,
    /// поэтому кодировать ничего не нужно: BitmapSource берёт их как есть.
    /// </summary>
    private ImageSource? Thumbnail()
    {
        var source = PreviewSource;
        if (source is null) return null;

        try
        {
            (byte[] Bgra, int W, int H)? frame = null;
            source((device, context, texture) =>
                frame = Capture.ScreenshotService.ReadPixels(device, context, texture));
            if (frame is null) return null;

            var (pixels, w, h) = Downscale(frame.Value.Bgra, frame.Value.W, frame.Value.H, 180);
            var bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
            bitmap.Freeze();
            return bitmap;
        }
        catch { return null; }
    }

    /// <summary>Уменьшение ближайшим соседом: для плитки 62 px этого более чем достаточно.</summary>
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
                Buffer.BlockCopy(src, srcRow + x * srcW / w * 4, dst, dstRow + x * 4, 4);
        }
        return (dst, w, h);
    }
}

/// <summary>Выполнение действия в потоке интерфейса (события конвейера приходят из фоновых).</summary>
public sealed class UiDispatcher(Dispatcher dispatcher)
{
    public void Enqueue(Action action)
    {
        if (dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }
}
