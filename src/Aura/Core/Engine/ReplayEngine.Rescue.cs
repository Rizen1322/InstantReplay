using Aura.Core.Buffering;
using Aura.Core.Logging;
using Aura.Core.Saving;
using Aura.Core.Saving.Mp4;
using Aura.Core.Settings;

namespace Aura.Core.Engine;

/// <summary>
/// Часть <see cref="ReplayEngine"/>: повтор, накопленный до смены энкодера, не теряется.
///
/// ЗАЧЕМ. Если NVENC по-настоящему умер и конвейер пересобран на MFT, новый поток
/// описан другим заголовком кодека, и склеить его со старыми кадрами в один файл
/// нельзя. Раньше старая часть буфера просто стиралась: минуты повтора пропадали
/// ровно тогда, когда в игре что-то случилось.
///
/// Теперь старая часть остаётся в ПАМЯТИ отдельным снимком со своим заголовком
/// кодека. На диск до нажатия «Сохранить» не пишется ничего: в 2.0.35 она сразу
/// писалась во временный файл, а повтор не должен оставлять видео на диске без
/// команды человека. Нажал «Сохранить», пока часть ещё в окне повтора, — она
/// ложится в библиотеку отдельным клипом «(до сбоя)». Вышла за окно или повтор
/// выключен — снимок отпускается вместе со старой ареной.
///
/// Цена: пока снимок жив, старая арена занимает память рядом с новой. Это одна
/// арена и не дольше длины повтора.
/// </summary>
public sealed partial class ReplayEngine
{
    private sealed record RescuedPart(
        VideoSnapshot Video,
        SnapshotLease Lease,
        Mp4VideoFormat Format,
        List<(AudioTrackKind, Mp4AudioFormat)> AudioTracks,
        long StartTicks,
        long EndTicks,
        int Seconds,
        string Game,
        DateTime CapturedAt);

    private readonly object _rescueSync = new();
    private RescuedPart? _rescued;

    private static long NowTicks() =>
        (long)(System.Diagnostics.Stopwatch.GetTimestamp() * (10_000_000.0 / System.Diagnostics.Stopwatch.Frequency));

    /// <summary>
    /// Отделить всё, что накоплено старым энкодером. Буфер после этого пустой и
    /// копит уже кадры нового энкодера; старые кадры живут в снимке.
    /// </summary>
    private void RescueBufferBeforeFormatChange(byte[]? sequenceHeader, VideoCodec codec, int width, int height, int fps)
    {
        VideoSnapshot video = _videoBuffer.TakeSnapshot(long.MaxValue, out long token);
        var lease = new SnapshotLease(_videoBuffer, token, video);
        // Новая арена под новый поток; старая живёт, пока её держит снимок
        _videoBuffer.Clear();
        if (video.Count == 0) { lease.Dispose(); return; }

        Mp4VideoFormat format;
        try
        {
            format = Mp4VideoFormat.FromBitstream(ContainerCodec(codec), width, height, sequenceHeader ?? [], video[0].Span)
                     with { FrameRate = fps };
        }
        catch (Exception ex)
        {
            lease.Dispose();
            Log.Warn("Engine", $"Старую часть повтора не разобрать ({ex.Message}) — она потеряна");
            return;
        }

        long start = video[0].PtsTicks;
        long lastPts = video[video.Count - 1].PtsTicks;
        for (int i = Math.Max(0, video.Count - 8); i < video.Count; i++) lastPts = Math.Max(lastPts, video[i].PtsTicks);
        long end = lastPts + Math.Max(0, video[video.Count - 1].DurationTicks);
        int seconds = Math.Max(1, (int)Math.Round(TimeSpan.FromTicks(lastPts - start).TotalSeconds));

        var s = _settings.Current;
        var audioTracks = new List<(AudioTrackKind, Mp4AudioFormat)>();
        foreach (var kind in ReplaySaver.TracksFor(s.TrackMode, s.CaptureGameAudio, s.CaptureMicrophone))
            if (_audio.FormatOf(kind) is { } audioFormat) audioTracks.Add((kind, audioFormat));

        var part = new RescuedPart(video, lease, format, audioTracks, start, end, seconds, GameForClip(), DateTime.Now);
        lock (_rescueSync)
        {
            _rescued?.Lease.Dispose();
            _rescued = part;
        }
        Log.Warn("Engine", $"Энкодер сменился — {seconds} с повтора до сбоя держу в памяти отдельно до сохранения " +
                           $"({video.TotalBytes / (1024 * 1024)} МБ)");
    }

    /// <summary>Есть ли часть до сбоя, ещё входящая в окно повтора.</summary>
    private bool HasRescued(long windowTicks)
    {
        lock (_rescueSync) return _rescued is { } part && NowTicks() - part.EndTicks <= windowTicks;
    }

    /// <summary>
    /// Человек сохраняет повтор: часть до сбоя, если она ещё в окне
    /// <paramref name="windowTicks"/>, записывается отдельным клипом. Зовётся из
    /// фоновой задачи сохранения: запись файла идёт здесь же.
    /// </summary>
    private (string Path, int Seconds)? PublishRescued(long windowTicks)
    {
        RescuedPart? part;
        lock (_rescueSync)
        {
            part = _rescued;
            _rescued = null;
        }
        if (part is null) return null;

        string? reserved = null;
        try
        {
            if (NowTicks() - part.EndTicks > windowTicks) return null;

            reserved = ReserveFilePath(part.Game, "replay", part.CapturedAt);
            string named = Storage.FileNaming.NextAvailablePath(
                Path.Combine(Path.GetDirectoryName(reserved)!,
                             Path.GetFileNameWithoutExtension(reserved) + " (до сбоя)" + Path.GetExtension(reserved)),
                File.Exists);
            var audio = WaitAndSnapshotAudio(part.StartTicks, part.EndTicks);
            bool inGame = !string.Equals(part.Game, "Desktop", StringComparison.OrdinalIgnoreCase);
            string published = SaveWithRescue(named, part.Video, part.Format, audio, part.AudioTracks, inGame);
            _storage.RegisterSaved(published);
            Log.Info("Engine", $"Повтор до сбоя сохранён: {published}");
            return (published, part.Seconds);
        }
        catch (Exception ex)
        {
            Log.Warn("Engine", $"Повтор до сбоя не сохранён: {ex.Message}");
            return null;
        }
        finally
        {
            part.Lease.Dispose();
            if (reserved is not null) ReleaseFilePath(reserved);
        }
    }

    /// <summary>Часть до сбоя вышла за окно повтора или повтор выключен: отпустить память.</summary>
    private void ExpireRescued(bool force)
    {
        RescuedPart? expired = null;
        lock (_rescueSync)
        {
            if (_rescued is null) return;
            long window = TimeSpan.FromSeconds(_settings.Current.ReplayLengthSeconds).Ticks;
            if (force || NowTicks() - _rescued.EndTicks > window)
            {
                expired = _rescued;
                _rescued = null;
            }
        }
        if (expired is null) return;
        expired.Lease.Dispose();
        Log.Info("Engine", "Повтор до сбоя вышел за окно повтора — память отпущена");
    }

    /// <summary>
    /// Остатки 2.0.35: тогда часть до сбоя писалась во временную папку на диске.
    /// Теперь там ничего не появляется, а старые файлы убираются.
    /// </summary>
    internal static void PurgeRescueLeftovers()
    {
        try
        {
            string dir = Path.Combine(ReplayVideoBuffer.DiskDirectory, "rescue");
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch { }
    }
}
