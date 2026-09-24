using Aura.Core.Buffering;
using Aura.Core.Logging;
using Aura.Core.Saving;
using Aura.Core.Saving.Mp4;
using Aura.Core.Settings;
using Aura.Core.Storage;

namespace Aura.Core.Engine;

/// <summary>
/// Часть <see cref="ReplayEngine"/>: сохранение повтора в файл.
/// </summary>
public sealed partial class ReplayEngine
{
    /// <summary>
    /// Нажатие сохранения, пришедшее во время записи прошлого клипа. Без замка
    /// жизненного цикла: его читает продолжение задачи сохранения, а Stop() держит
    /// этот замок, дожидаясь той же задачи.
    /// </summary>
    private int _pendingSave;
    private volatile int _pendingSaveSeconds; // 0 — длина из настроек

    public void SaveReplay(int? secondsOverride = null)
    {
        // Зовётся с потока интерфейса (хоткей, трей). Пока конвейер пересобирается,
        // замок жизненного цикла может быть занят надолго, и ждать его здесь значило
        // бы заморозить всё приложение. Тогда сохранение уходит в фон и ждёт там.
        if (Monitor.TryEnter(_lifecycle, 200))
        {
            try { SaveReplayLocked(secondsOverride); }
            finally { Monitor.Exit(_lifecycle); }
            return;
        }

        Log.Info("Engine", "Сохранение ждёт пересборку конвейера");
        _ = Task.Run(() =>
        {
            if (!Monitor.TryEnter(_lifecycle, TimeSpan.FromSeconds(20)))
            {
                Log.Warn("Engine", "Сохранение не дождалось пересборки конвейера за 20 секунд");
                SaveFailed?.Invoke("Захват перезапускается и не отвечает — клип не сохранён. Попробуйте ещё раз.");
                return;
            }
            try { SaveReplayLocked(secondsOverride); }
            catch (Exception ex)
            {
                Log.Error("Engine", ex);
                SaveFailed?.Invoke(ex.Message);
            }
            finally { Monitor.Exit(_lifecycle); }
        });
    }

    private void SaveReplayLocked(int? secondsOverride)
    {
        if (_state == EngineState.Stopped)
        {
            SaveFailed?.Invoke("Повтор выключен — сохранять нечего");
            return;
        }

        // Прошлый клип ещё пишется. Раньше нажатие здесь молча терялось, и человек
        // думал, что хоткей не сработал. Теперь оно ставится в очередь и выполнится
        // сразу после текущей записи.
        if (_state == EngineState.Saving || _saveTask is { IsCompleted: false })
        {
            _pendingSaveSeconds = secondsOverride ?? 0;
            if (Interlocked.Exchange(ref _pendingSave, 1) == 0)
                Warning?.Invoke("Предыдущий клип ещё сохраняется — этот сохранится сразу после него");
            return;
        }

        // Состояние Recovering сохранению не мешает: захват пересобирается, но буфер
        // со всеми кадрами и описанием потока лежит в памяти. Раньше нажатие во время
        // восстановления терялось молча — ровно в тот момент, когда игра дёрнулась.
        if (_videoBuffer.TotalBytes == 0 || _bufferSequenceHeader is null && _bufferCodec != VideoCodec.AV1)
        {
            SaveFailed?.Invoke(_state == EngineState.Recovering
                ? "Запись восстанавливается — в буфере пока нет кадров"
                : "Буфер ещё пуст");
            return;
        }

        DumpStats("к моменту сохранения"); // короткий сеанс тоже должен оставить следы в логе

        var s = _settings.Current;

        // Место проверяем ДО снимка: снимок очищает буфер, и если файл потом не
        // поместится, клип пропадёт. При нехватке буфер остаётся нетронутым.
        long estimate = _videoBuffer.TotalBytes + _audioBuffer.TotalBytes;
        try { DiskSpace.Require(s.SaveRootPath, estimate, "клипа"); }
        catch (InsufficientDiskSpaceException ex)
        {
            Log.Warn("Engine", ex.Message);
            SaveFailed?.Invoke(ex.Message);
            return;
        }

        long wanted = TimeSpan.FromSeconds(secondsOverride ?? s.ReplayLengthSeconds).Ticks;
        VideoSnapshot video = _videoBuffer.TakeSnapshot(wanted, out long snapshotToken);
        var lease = new SnapshotLease(_videoBuffer, snapshotToken, video);
        if (video.Count == 0)
        {
            lease.Dispose();
            SaveFailed?.Invoke("Буфер ещё пуст");
            return;
        }

        Mp4VideoFormat videoFormat;
        try
        {
            videoFormat = Mp4VideoFormat.FromBitstream(ContainerCodec(_bufferCodec), _bufferWidth, _bufferHeight,
                                                       _bufferSequenceHeader ?? [], video[0].Span)
                          with { FrameRate = _bufferFps };
        }
        catch (Exception ex)
        {
            lease.Dispose();
            Log.Error("Engine", ex);
            SaveFailed?.Invoke($"Не удалось разобрать поток кодека: {ex.Message}");
            return;
        }

        var audioTracks = new List<(AudioTrackKind, Mp4AudioFormat)>();
        foreach (var kind in ReplaySaver.TracksFor(s.TrackMode, s.CaptureGameAudio, s.CaptureMicrophone))
            if (_audio.FormatOf(kind) is { } format) audioTracks.Add((kind, format));

        // Игра — по тому, что было на экране, пока копился буфер (см. GameForClip).
        // Имя файла подбирает уже фоновая задача: подбор упирается в диск, а этот
        // метод держит замок всего жизненного цикла конвейера.
        string game = GameForClip();
        DateTime capturedAt = DateTime.Now;
        // Конец клипа — самое позднее время ПОКАЗА. С B-кадрами последний кадр в
        // порядке декодирования показывается раньше предыдущих, и по нему звук
        // обрезался бы на пару кадров раньше картинки.
        long lastPts = video[video.Count - 1].PtsTicks;
        for (int i = Math.Max(0, video.Count - 8); i < video.Count; i++)
            lastPts = Math.Max(lastPts, video[i].PtsTicks);
        int seconds = (int)Math.Round(TimeSpan.FromTicks(lastPts - video[0].PtsTicks).TotalSeconds);
        long clipStart = video[0].PtsTicks;
        long clipEnd = lastPts + Math.Max(0, video[video.Count - 1].DurationTicks);

        // Данные уже вырезаны из кольцевого буфера и никуда не денутся — говорим об
        // этом сразу, не дожидаясь диска.
        SaveProgress = 0;
        ReplayCaptured?.Invoke(Math.Max(seconds, 1));

        // Во время восстановления состояние не трогаем: его ведёт восстановление,
        // и «Сохранение» поверх него после записи файла превратилось бы в «Выключен».
        if (_state != EngineState.Recovering) SetState(EngineState.Saving);
        bool inGame = !string.Equals(game, "Desktop", StringComparison.OrdinalIgnoreCase);
        _saveTask = Task.Run(() =>
        {
            _saveTaskId = Task.CurrentId;
            // Пакетная фоновая работа не должна конкурировать с потоками захвата и
            // кодирования: в замерах сохранение на обычном приоритете давало дропы.
            var self = Thread.CurrentThread;
            var previousPriority = self.Priority;
            self.Priority = ThreadPriority.BelowNormal;
            string? file = null;
            try
            {
                var audio = WaitAndSnapshotAudio(clipStart, clipEnd);
                file = ReserveFilePath(game, "replay", capturedAt);
                string published = SaveWithRescue(file, video, videoFormat, audio, audioTracks, inGame);
                _storage.RegisterSaved(published);
                // ГРУППА ОБЯЗАНА ОСТАВАТЬСЯ "stats": обработчик групп video/audio/replay
                // берёт _lifecycle, а Stop() в это время ждёт ЭТУ задачу под тем же замком.
                _settings.Update(x => x.TotalReplaysSaved++, "stats");
                ReplaySaved?.Invoke(published, Math.Max(seconds, 1));
            }
            catch (Exception ex)
            {
                Log.Error("Engine", ex);
                SaveFailed?.Invoke(ex.Message);
            }
            finally
            {
                // Файл записан — возвращаем буферу место, которое занимали кадры клипа.
                lease.Dispose();
                if (file is not null) ReleaseFilePath(file);
                self.Priority = previousPriority;
                CompleteSavingState();
            }
        });
        // Очередное нажатие — строго ПОСЛЕ завершения этой задачи: внутри неё
        // _saveTask ещё не завершён, и новое сохранение снова встало бы в очередь.
        _saveTask.ContinueWith(_ => RunPendingSave(), TaskScheduler.Default);
    }

    /// <summary>Выполнить сохранение, которое ждало в очереди.</summary>
    private void RunPendingSave()
    {
        if (Interlocked.Exchange(ref _pendingSave, 0) == 0) return;
        int seconds = _pendingSaveSeconds;
        try { SaveReplay(seconds > 0 ? seconds : null); }
        catch (Exception ex) { Log.Error("Engine", ex); }
    }

    /// <summary>
    /// Дождаться, пока микшер сведёт звук до конца клипа, и снять его кадры.
    ///
    /// Звук сводится с отставанием в 0.2 с (см. AudioMixerEngine.MixLagTicks):
    /// снимок, взятый сразу, остался бы без последних долей секунды звука.
    /// </summary>
    private AudioSnapshot WaitAndSnapshotAudio(long clipStart, long clipEnd)
    {
        if (_audio.IsRunning)
        {
            long need = clipEnd + ReplayAudioBuffer.FrameTicks * 2;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (_audio.IsRunning && _audio.MixedUpToTicks < need && clock.ElapsedMilliseconds < 1000)
                Thread.Sleep(20);
        }
        return _audioBuffer.Snapshot(clipStart, clipEnd, Audio.AudioMixerEngine.SilentFrameOf);
    }

    /// <summary>
    /// Записать клип; если не вышло — попробовать запасную папку на другом диске.
    ///
    /// Буфер к этому моменту уже очищен снимком, поэтому неудача записи раньше
    /// означала потерянный клип. Кончилось место, отвалился внешний диск, папку
    /// заблокировал антивирус — клип всё равно должен где-то оказаться.
    /// </summary>
    private string SaveWithRescue(string file, VideoSnapshot video, Mp4VideoFormat videoFormat,
        AudioSnapshot audio, List<(AudioTrackKind, Mp4AudioFormat)> audioTracks, bool inGame)
    {
        try
        {
            return ReplaySaver.Save(file, video, videoFormat, audio, audioTracks,
                                    p => SaveProgress = p, gentleIo: inGame);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            long needed = video.TotalBytes + DiskSpace.SafetyMarginBytes;
            string? rescue = RescueDirectory(file, needed);
            if (rescue is null) throw;

            string target = FileNaming.NextAvailablePath(Path.Combine(rescue, Path.GetFileName(file)), File.Exists);
            Log.Warn("Engine", $"Клип не записался в папку записей ({ex.Message}) — сохраняю в {target}");
            string published = ReplaySaver.Save(target, video, videoFormat, audio, audioTracks,
                                                p => SaveProgress = p, gentleIo: false);
            Warning?.Invoke($"Папка записей недоступна ({ex.Message}). Клип сохранён в запасную папку: {published}");
            return published;
        }
    }

    /// <summary>Запасная папка на ДРУГОМ диске, где есть место; null — такой нет.</summary>
    private static string? RescueDirectory(string failedPath, long neededBytes)
    {
        string failedRoot;
        try { failedRoot = Path.GetPathRoot(Path.GetFullPath(failedPath)) ?? ""; }
        catch { failedRoot = ""; }

        var candidates = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Aura", "Rescued")
        };
        try
        {
            candidates.AddRange(DriveInfo.GetDrives()
                .Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
                .OrderByDescending(d => d.AvailableFreeSpace)
                .Select(d => Path.Combine(d.RootDirectory.FullName, "Aura (запасная папка)")));
        }
        catch { }

        foreach (string dir in candidates)
        {
            string root;
            try { root = Path.GetPathRoot(dir) ?? ""; } catch { continue; }
            if (string.Equals(root, failedRoot, StringComparison.OrdinalIgnoreCase)) continue;
            if (DiskSpace.FreeBytes(root) is long free && free < neededBytes) continue;
            try
            {
                Directory.CreateDirectory(dir);
                return dir;
            }
            catch { }
        }
        return null;
    }

    private static Mp4VideoCodec ContainerCodec(VideoCodec codec) => codec switch
    {
        VideoCodec.HEVC => Mp4VideoCodec.Hevc,
        VideoCodec.AV1 => Mp4VideoCodec.Av1,
        _ => Mp4VideoCodec.H264
    };

    /// <summary>Снимок и его место в арене: отпускаются вместе и ровно один раз.</summary>
    private sealed class SnapshotLease(ReplayVideoBuffer owner, long token, VideoSnapshot snapshot) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            owner.ReleaseSnapshot(token);
            snapshot.Dispose();
        }
    }

    private string ReserveFilePath(string game, string fallbackPrefix, DateTime capturedAt)
    {
        var s = _settings.Current;
        lock (_pathSync)
        {
            string path = FileNaming.BuildPath(s.SaveRootPath, s.GroupByGame, s.FileNameTemplate, game,
                capturedAt, $"{s.VerticalResolution}p{s.Fps}", fallbackPrefix,
                candidate => File.Exists(candidate) || _reservedPaths.Contains(candidate));
            _reservedPaths.Add(path);
            return path;
        }
    }

    private void ReleaseFilePath(string path)
    {
        lock (_pathSync) _reservedPaths.Remove(path);
    }
}
