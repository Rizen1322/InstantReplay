using System.Security.Cryptography;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using InstantReplay.Core.Logging;
using InstantReplay.Core.Settings;

namespace InstantReplay.Core.Library;

/// <summary>
/// Миниатюры и метаданные (длительность, разрешение) для карточек библиотеки.
///
/// Кадр берём у системного поставщика миниатюр (та же картинка, что показывает
/// проводник) — это дешевле, чем поднимать свой декодер ради одного кадра, и
/// работает для любого контейнера, который система умеет читать.
///
/// Результат кладём в %LocalAppData%\InstantReplay\thumbs: при повторном открытии
/// вкладки сетка заполняется мгновенно и без обращений к shell. Ключ кэша включает
/// время изменения и размер файла — переименование/перезапись не отдаст старый кадр.
/// </summary>
public static class ClipThumbnails
{
    private static readonly string CacheDir = Path.Combine(SettingsManager.Dir, "thumbs");

    /// <summary>Ширина миниатюры в кэше: с запасом под карточку 288 px и HiDPI.</summary>
    private const uint ThumbWidth = 480;

    /// <summary>
    /// Не больше трёх одновременных запросов: shell-провайдер миниатюр для видео
    /// сам по себе не бесплатный, а сетка при быстрой прокрутке просит десятки штук.
    /// </summary>
    private static readonly SemaphoreSlim Gate = new(3);

    /// <summary>
    /// Догрузить миниатюру и метаданные элемента. Вызывать из UI-потока: продолжения
    /// возвращаются на него, и BitmapImage создаётся там, где положено.
    /// Ошибки не пробрасываются — карточка просто останется с заглушкой.
    /// </summary>
    public static async Task LoadAsync(ClipItem item)
    {
        if (item.ThumbRequested) return;
        item.ThumbRequested = true;

        try
        {
            string key = CacheKey(item);
            string thumbFile = Path.Combine(CacheDir, key + ".thumb");
            string metaFile = Path.Combine(CacheDir, key + ".meta");

            byte[]? bytes = null;
            TimeSpan? duration = null;
            string? resolution = null;

            await Gate.WaitAsync().ConfigureAwait(true);
            try
            {
                if (File.Exists(thumbFile))
                {
                    bytes = await File.ReadAllBytesAsync(thumbFile);
                    if (File.Exists(metaFile))
                        ParseMeta(await File.ReadAllTextAsync(metaFile), ref duration, ref resolution);
                }
                else
                {
                    var file = await StorageFile.GetFileFromPathAsync(item.FullPath);

                    if (item.IsScreenshot)
                    {
                        try
                        {
                            var props = await file.Properties.GetImagePropertiesAsync();
                            if (props.Width > 0) resolution = $"{props.Width}×{props.Height}";
                        }
                        catch { }
                    }
                    else
                    {
                        try
                        {
                            var props = await file.Properties.GetVideoPropertiesAsync();
                            if (props.Duration > TimeSpan.Zero) duration = props.Duration;
                            if (props.Width > 0) resolution = $"{props.Width}×{props.Height}";
                        }
                        catch { }
                    }

                    using var thumb = await file.GetThumbnailAsync(
                        item.IsScreenshot ? ThumbnailMode.PicturesView : ThumbnailMode.VideosView,
                        ThumbWidth, ThumbnailOptions.ResizeThumbnail);

                    // ThumbnailType.Icon — это generic-иконка файла (нет провайдера для
                    // кодека): в сетке она смотрится хуже нашей заглушки, поэтому не берём.
                    if (thumb is not null && thumb.Size > 0 && thumb.Type == ThumbnailType.Image)
                    {
                        using var source = thumb.AsStreamForRead();
                        using var buffer = new MemoryStream();
                        await source.CopyToAsync(buffer);
                        bytes = buffer.ToArray();
                    }

                    Directory.CreateDirectory(CacheDir);
                    if (bytes is not null) await File.WriteAllBytesAsync(thumbFile, bytes);
                    await File.WriteAllTextAsync(metaFile, BuildMeta(duration, resolution));
                }
            }
            finally { Gate.Release(); }

            if (duration is not null) item.Duration = duration;
            if (resolution is not null) item.Resolution = resolution;
            if (bytes is null || bytes.Length == 0) return;

            var image = new BitmapImage { DecodePixelWidth = (int)ThumbWidth };
            using var memory = new MemoryStream(bytes);
            await image.SetSourceAsync(memory.AsRandomAccessStream());
            item.Thumbnail = image;
        }
        catch (Exception ex)
        {
            Log.Warn("Library", $"Миниатюра «{item.FileName}»: {ex.Message}");
        }
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
