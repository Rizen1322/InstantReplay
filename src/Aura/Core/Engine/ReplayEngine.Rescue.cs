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
/// ровно тогда, когда в игре что-то случилось. Теперь она сразу пишется в фоне во
/// временный файл рядом с буфером. Нажал «сохранить», пока эта часть ещё в окне
/// повтора — она ляжет в библиотеку отдельным клипом «(до сбоя)». Не нажал —
/// файл удаляется, как удалились бы и сами кадры, выйдя за окно.
/// </summary>
public sealed partial class ReplayEngine
{
    private sealed record RescuedPart(string Path, long EndTicks, int Seconds, string Game, DateTime CapturedAt);

    private readonly object _rescueSync = new();
    private RescuedPart? _rescued;

    private static string RescueDirectory() => Path.Combine(ReplayVideoBuffer.DiskDirectory, "rescue");

    private static long NowTicks() =>
        (long)(System.Diagnostics.Stopwatch.GetTimestamp() * (10_000_000.0 / System.Diagnostics.Stopwatch.Frequency));

    /// <summary>
    /// Забрать из буфера всё, что накоплено старым энкодером, и записать в фоне.
    /// Буфер после этого пустой и копит уже кадры нового энкодера.
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
        string game = GameForClip();
        DateTime capturedAt = DateTime.Now;

        Log.Warn("Engine", $"Энкодер сменился — {seconds} с повтора до сбоя сохраняю отдельно, чтобы не потерять");
        _ = Task.Run(() =>
        {
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
            try
            {
                string dir = RescueDirectory();
                Directory.CreateDirectory(dir);
                var audio = WaitAndSnapshotAudio(start, end);
                string written = ReplaySaver.Save(Path.Combine(dir, Guid.NewGuid().ToString("N") + ".mp4"),
                                                  video, format, audio, audioTracks, null, gentleIo: true);
                lock (_rescueSync)
                {
                    DeleteRescuedLocked();
                    _rescued = new RescuedPart(written, end, seconds, game, capturedAt);
                }
                Log.Info("Engine", $"Повтор до сбоя ждёт сохранения: {seconds} с");
            }
            catch (Exception ex)
            {
                Log.Warn("Engine", $"Повтор до сбоя не записался: {ex.Message}");
            }
            finally
            {
                lease.Dispose();
            }
        });
    }

    /// <summary>
    /// Человек сохраняет повтор: часть до сбоя, если она ещё в окне
    /// <paramref name="windowTicks"/>, уходит в библиотеку. Возвращает путь или null.
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
        if (NowTicks() - part.EndTicks > windowTicks)
        {
            TryDelete(part.Path);
            return null;
        }

        string target = ReserveFilePath(part.Game, "replay", part.CapturedAt);
        try
        {
            string named = Storage.FileNaming.NextAvailablePath(
                Path.Combine(Path.GetDirectoryName(target)!,
                             Path.GetFileNameWithoutExtension(target) + " (до сбоя)" + Path.GetExtension(target)),
                File.Exists);
            Directory.CreateDirectory(Path.GetDirectoryName(named)!);
            File.Move(part.Path, named);
            _storage.RegisterSaved(named);
            Log.Info("Engine", $"Повтор до сбоя сохранён: {named}");
            return (named, part.Seconds);
        }
        catch (Exception ex)
        {
            Log.Warn("Engine", $"Повтор до сбоя не перенесён в библиотеку: {ex.Message}");
            TryDelete(part.Path);
            return null;
        }
        finally
        {
            ReleaseFilePath(target);
        }
    }

    /// <summary>Часть до сбоя вышла за окно повтора или повтор выключен: удалить.</summary>
    private void ExpireRescued(bool force)
    {
        lock (_rescueSync)
        {
            if (_rescued is null) return;
            long window = TimeSpan.FromSeconds(_settings.Current.ReplayLengthSeconds).Ticks;
            if (force || NowTicks() - _rescued.EndTicks > window) DeleteRescuedLocked();
        }
    }

    private void DeleteRescuedLocked()
    {
        if (_rescued is null) return;
        TryDelete(_rescued.Path);
        _rescued = null;
    }

    /// <summary>Остатки прошлого запуска: их уже никто не сохранит.</summary>
    internal static void PurgeRescueLeftovers()
    {
        try
        {
            string dir = RescueDirectory();
            if (!Directory.Exists(dir)) return;
            foreach (string file in Directory.EnumerateFiles(dir)) TryDelete(file);
        }
        catch { }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
