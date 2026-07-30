using System.Security.Cryptography;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;
using InstantReplay.Core.Logging;
using InstantReplay.Core.Settings;

namespace InstantReplay.Core.Library;

/// <summary>
/// Миниатюры и метаданные (длительность, разрешение) для карточек библиотеки.
///
/// Кадр берём у системного поставщика миниатюр (та же картинка, что показывает
/// проводник) — это дешевле, чем поднимать свой декодер ради одного кадра.
///
/// Два правила, выстраданных на коллекции из полутора сотен клипов:
///
/// 1. ВСЯ работа с диском и системой — в фоновом потоке. Раньше продолжения
///    возвращались в UI-поток, и прокрутка панорамы намертво вешала окно: каждый
///    видимый клип тянул за собой обращение к shell, чтение кэша и декодирование.
///    В UI-поток возвращаемся только чтобы создать BitmapImage.
///
/// 2. В кэше храним JPEG, а не то, что отдала система. Системный поставщик отдаёт
///    несжатый BMP — 600+ КБ на кадр; на 150 клипов это сотни мегабайт чтения и
///    декодирования. JPEG того же размера весит ~40 КБ.
/// </summary>
public static class ClipThumbnails
{
    private static readonly string CacheDir = Path.Combine(SettingsManager.Dir, "thumbs");

    /// <summary>Ширина миниатюры в кэше: с запасом под карточку 288 px и HiDPI.</summary>
    private const uint ThumbWidth = 420;

    /// <summary>
    /// Не больше двух одновременных обращений к системе: поставщик миниатюр для видео
    /// сам по себе не бесплатный, а сетка при быстрой прокрутке просит десятки штук.
    /// </summary>
    private static readonly SemaphoreSlim Gate = new(2);

    /// <summary>
    /// Сколько запросов ждёт очереди. При быстрой прокрутке заявок набегает больше,
    /// чем есть смысла выполнять: экран уже уехал. Лишние отбрасываем — карточка
    /// попросит кадр заново, когда снова окажется в поле зрения.
    /// </summary>
    private static int _pending;
    private const int MaxPending = 16;

    /// <summary>Чем закончилась попытка получить кадр.</summary>
    public enum ThumbResult
    {
        /// <summary>Уже запрашивали для этого файла или очередь переполнена.</summary>
        Skipped,
        /// <summary>Кадр получен.</summary>
        Ok,
        /// <summary>Система вернула иконку файла вместо кадра — обычно нет декодера (HEVC).</summary>
        NoDecoder,
        Failed
    }

    /// <summary>Результат фоновой части: байты картинки и метаданные.</summary>
    private sealed record Payload(byte[]? Image, TimeSpan? Duration, string? Resolution, bool NoDecoder);

    /// <summary>
    /// Догрузить миниатюру и метаданные элемента. Вызывать из UI-потока: тяжёлая часть
    /// уедет в фон сама, а вернувшись, метод создаст BitmapImage там, где положено.
    /// Ошибки не пробрасываются — карточка просто останется с заглушкой.
    /// </summary>
    public static async Task<ThumbResult> LoadAsync(ClipItem item)
    {
        if (item.ThumbRequested) return ThumbResult.Skipped;

        // Очередь переполнена — не помечаем элемент запрошенным, попросит позже
        if (Volatile.Read(ref _pending) >= MaxPending) return ThumbResult.Skipped;

        item.ThumbRequested = true;
        Interlocked.Increment(ref _pending);

        Payload payload;
        try
        {
            payload = await Task.Run(() => FetchAsync(item));
        }
        catch (Exception ex)
        {
            Log.Warn("Library", $"Миниатюра «{item.FileName}»: {ex.Message}");
            return ThumbResult.Failed;
        }
        finally
        {
            Interlocked.Decrement(ref _pending);
        }

        if (payload.Duration is not null) item.Duration = payload.Duration;
        if (payload.Resolution is not null) item.Resolution = payload.Resolution;

        if (payload.Image is null || payload.Image.Length == 0)
            return payload.NoDecoder ? ThumbResult.NoDecoder : ThumbResult.Failed;

        try
        {
            // Единственная часть, которой нужен UI-поток
            var image = new BitmapImage { DecodePixelWidth = (int)ThumbWidth };
            using var memory = new MemoryStream(payload.Image);
            await image.SetSourceAsync(memory.AsRandomAccessStream());
            item.Thumbnail = image;
            return ThumbResult.Ok;
        }
        catch (Exception ex)
        {
            Log.Warn("Library", $"Миниатюра «{item.FileName}»: {ex.Message}");
            return ThumbResult.Failed;
        }
    }

    /// <summary>Фоновая часть: кэш или система. UI-потока здесь нет вообще.</summary>
    private static async Task<Payload> FetchAsync(ClipItem item)
    {
        string key = CacheKey(item);
        string thumbFile = Path.Combine(CacheDir, key + ".thumb");
        string metaFile = Path.Combine(CacheDir, key + ".meta");

        TimeSpan? duration = null;
        string? resolution = null;

        if (File.Exists(thumbFile))
        {
            byte[] cached = await File.ReadAllBytesAsync(thumbFile).ConfigureAwait(false);
            if (File.Exists(metaFile))
                ParseMeta(await File.ReadAllTextAsync(metaFile).ConfigureAwait(false), ref duration, ref resolution);
            return new Payload(cached, duration, resolution, false);
        }

        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(item.FullPath).AsTask().ConfigureAwait(false);

            if (item.IsScreenshot)
            {
                try
                {
                    var props = await file.Properties.GetImagePropertiesAsync().AsTask().ConfigureAwait(false);
                    if (props.Width > 0) resolution = $"{props.Width}×{props.Height}";
                }
                catch { }
            }
            else
            {
                try
                {
                    var props = await file.Properties.GetVideoPropertiesAsync().AsTask().ConfigureAwait(false);
                    if (props.Duration > TimeSpan.Zero) duration = props.Duration;
                    if (props.Width > 0) resolution = $"{props.Width}×{props.Height}";
                }
                catch { }
            }

            byte[]? bytes = null;
            bool noDecoder = false;

            using (var thumb = await file.GetThumbnailAsync(
                       item.IsScreenshot ? ThumbnailMode.PicturesView : ThumbnailMode.VideosView,
                       ThumbWidth, ThumbnailOptions.ResizeThumbnail).AsTask().ConfigureAwait(false))
            {
                // ThumbnailType.Icon — generic-иконка файла: система не смогла декодировать
                // видео (у HEVC такое бывает без «HEVC Video Extensions»). В сетке иконка
                // смотрится хуже нашей заглушки, поэтому не берём её, но запоминаем причину.
                if (thumb is not null && thumb.Size > 0 && thumb.Type == ThumbnailType.Image)
                    bytes = await ToJpegAsync(thumb).ConfigureAwait(false);
                else if (!item.IsScreenshot)
                    noDecoder = true;
            }

            Directory.CreateDirectory(CacheDir);
            if (bytes is not null) await File.WriteAllBytesAsync(thumbFile, bytes).ConfigureAwait(false);
            await File.WriteAllTextAsync(metaFile, BuildMeta(duration, resolution)).ConfigureAwait(false);

            return new Payload(bytes, duration, resolution, noDecoder);
        }
        finally { Gate.Release(); }
    }

    /// <summary>Пережать кадр в JPEG: системный поставщик отдаёт несжатый BMP.</summary>
    private static async Task<byte[]> ToJpegAsync(IRandomAccessStream source)
    {
        source.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(source).AsTask().ConfigureAwait(false);
        var pixels = await decoder.GetPixelDataAsync().AsTask().ConfigureAwait(false);

        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output).AsTask().ConfigureAwait(false);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
            decoder.PixelWidth, decoder.PixelHeight,
            decoder.DpiX, decoder.DpiY, pixels.DetachPixelData());
        await encoder.FlushAsync().AsTask().ConfigureAwait(false);

        output.Seek(0);
        using var reader = output.AsStreamForRead();
        using var buffer = new MemoryStream();
        await reader.CopyToAsync(buffer).ConfigureAwait(false);
        return buffer.ToArray();
    }

    /// <summary>
    /// Выбросить из кэша миниатюры, к которым давно не обращались. Файлы удалённых
    /// клипов иначе лежали бы там вечно. Запускать в фоне, ошибки игнорируются.
    /// </summary>
    public static void PruneCache(TimeSpan olderThan)
    {
        try
        {
            if (!Directory.Exists(CacheDir)) return;
            var cutoff = DateTime.UtcNow - olderThan;
            foreach (var f in Directory.EnumerateFiles(CacheDir))
                try { if (File.GetLastWriteTimeUtc(f) < cutoff) File.Delete(f); } catch { }
        }
        catch { }
    }

    /// <summary>Забыть кэш конкретного файла (переименование, удаление).</summary>
    public static void Forget(ClipItem item)
    {
        try
        {
            string key = CacheKey(item);
            foreach (var ext in new[] { ".thumb", ".meta" })
            {
                string path = Path.Combine(CacheDir, key + ext);
                if (File.Exists(path)) File.Delete(path);
            }
        }
        catch { }
    }

    private static string CacheKey(ClipItem item)
    {
        // System.Text.Encoding явно: в проекте есть свой Core.Encoding (энкодер видео)
        byte[] hash = SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(item.FullPath.ToLowerInvariant()));
        return $"{Convert.ToHexString(hash)[..16]}_{item.Created.Ticks:x}_{item.SizeBytes:x}";
    }

    private static string BuildMeta(TimeSpan? duration, string? resolution) =>
        $"{(long)(duration?.TotalMilliseconds ?? 0)}|{resolution ?? ""}";

    private static void ParseMeta(string text, ref TimeSpan? duration, ref string? resolution)
    {
        var parts = text.Split('|');
        if (parts.Length > 0 && long.TryParse(parts[0], out long ms) && ms > 0)
            duration = TimeSpan.FromMilliseconds(ms);
        if (parts.Length > 1 && parts[1].Length > 0)
            resolution = parts[1];
    }
}
