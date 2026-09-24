using System.Windows.Media;
using System.Windows.Media.Imaging;
using Aura.Core.Engine;

namespace Aura.Views;

/// <summary>
/// Уменьшенный кадр того, что сейчас пишет повтор: для живого окна на главном
/// экране и для ленты буфера.
///
/// Кадр берётся у работающего конвейера (тот же путь, что у миниатюры в
/// уведомлении), своя сессия захвата ради картинки не поднимается никогда.
/// Вычитывание идёт в фоне, а зовут его редко: раз в пару секунд, пока открыт
/// главный экран, и раз в 15 секунд для ленты.
/// </summary>
public static class LivePreview
{
    public static Task<BitmapSource?> GrabAsync(int maxWidth) => Task.Run(() => Grab(maxWidth));

    private static BitmapSource? Grab(int maxWidth)
    {
        var engine = Services.Engine;
        if (engine.State == EngineState.Stopped) return null;
        try
        {
            (byte[] Bgra, int W, int H)? frame = null;
            engine.TryUseLiveFrame((device, context, texture) =>
                frame = Core.Capture.ScreenshotService.ReadPixels(device, context, texture), allowStale: true);
            if (frame is null) return null;

            var (pixels, w, h) = Downscale(frame.Value.Bgra, frame.Value.W, frame.Value.H, maxWidth);
            var bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
            bitmap.Freeze();
            return bitmap;
        }
        catch { return null; }
    }

    /// <summary>
    /// Уменьшение усреднением по площади: у ближайшего соседа мелкий текст и
    /// HUD игры рассыпались бы в «песок», а здесь картинка остаётся гладкой.
    /// </summary>
    public static (byte[] Pixels, int W, int H) Downscale(byte[] src, int srcW, int srcH, int maxWidth)
    {
        int w = Math.Min(maxWidth, srcW);
        int h = Math.Max(1, (int)Math.Round((double)srcH * w / srcW));
        var dst = new byte[w * h * 4];
        double sx = (double)srcW / w, sy = (double)srcH / h;
        for (int y = 0; y < h; y++)
        {
            int y0 = (int)(y * sy), y1 = Math.Max(y0 + 1, Math.Min(srcH, (int)((y + 1) * sy)));
            for (int x = 0; x < w; x++)
            {
                int x0 = (int)(x * sx), x1 = Math.Max(x0 + 1, Math.Min(srcW, (int)((x + 1) * sx)));
                // Шаг 2 по обеим осям: при уменьшении в 3–10 раз разницы на глаз нет,
                // а работы вчетверо меньше.
                int b = 0, g = 0, r = 0, n = 0;
                for (int yy = y0; yy < y1; yy += 2)
                {
                    int row = yy * srcW * 4;
                    for (int xx = x0; xx < x1; xx += 2)
                    {
                        int i = row + xx * 4;
                        b += src[i]; g += src[i + 1]; r += src[i + 2]; n++;
                    }
                }
                int o = (y * w + x) * 4;
                dst[o] = (byte)(b / n); dst[o + 1] = (byte)(g / n); dst[o + 2] = (byte)(r / n); dst[o + 3] = 255;
            }
        }
        return (dst, w, h);
    }
}

/// <summary>
/// Лента буфера: маленький кадр раз в 15 секунд, пока идёт повтор. По ней на
/// главном экране видно, что лежит в буфере, и выделяется кусок для сохранения.
/// Кадры старше длины буфера выбрасываются, при выключении повтора лента пустеет.
/// </summary>
public static class ReplayFilmstrip
{
    public const int IntervalSeconds = 15;

    public sealed record Frame(DateTime At, BitmapSource Image);

    private static readonly object Sync = new();
    private static readonly List<Frame> Frames = [];
    private static System.Threading.Timer? _timer;
    private static int _busy;

    public static event Action? Changed;

    public static void Start()
    {
        _timer ??= new System.Threading.Timer(_ => Tick(), null, TimeSpan.FromSeconds(3),
                                              TimeSpan.FromSeconds(IntervalSeconds));
        Services.Engine.StateChanged += state =>
        {
            if (state != EngineState.Stopped) return;
            lock (Sync) Frames.Clear();
            Changed?.Invoke();
        };
    }

    public static List<Frame> Snapshot()
    {
        lock (Sync) return [.. Frames];
    }

    private static async void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            if (Services.Engine.State == EngineState.Stopped) return;
            var image = await LivePreview.GrabAsync(240);
            if (image is null) return;
            var keep = TimeSpan.FromSeconds(Services.Settings.Current.ReplayLengthSeconds + IntervalSeconds);
            lock (Sync)
            {
                Frames.Add(new Frame(DateTime.UtcNow, image));
                Frames.RemoveAll(f => DateTime.UtcNow - f.At > keep);
            }
            Changed?.Invoke();
        }
        catch { }
        finally { Volatile.Write(ref _busy, 0); }
    }
}
