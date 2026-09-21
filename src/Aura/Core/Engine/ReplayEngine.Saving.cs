using Vortice.MediaFoundation;
using Aura.Core.Audio;
using Aura.Core.Buffering;
using Aura.Core.Capture;
using Aura.Core.Encoding;
using Aura.Core.GameDetection;
using Aura.Core.Logging;
using Aura.Core.Saving;
using Aura.Core.Settings;
using Aura.Core.Storage;

namespace Aura.Core.Engine;

/// <summary>
/// Часть <see cref="ReplayEngine"/>. Сохранение повтора: снимок буферов под замком жизненного цикла и запись файла
/// в фоне, плюс резервирование имён файлов.
///
/// Движок разнесён по файлам по смыслу, без изменения поведения: те же поля, те же
/// замки, те же методы. Раньше это был один файл на 2200 строк, где сохранение,
/// диагностика и восстановление захвата шли вперемешку.
/// </summary>
public sealed partial class ReplayEngine
{
    /// <summary>
    /// Сохранить последние N секунд (по умолчанию — вся длина буфера из настроек).
    /// Снимок буферов мгновенный, remux — в фоне. После снимка буфер сбрасывается:
    /// каждый следующий повтор начинается с чистого листа.
    /// </summary>
    public void SaveReplay(int? secondsOverride = null)
    {
        lock (_lifecycle) SaveReplayLocked(secondsOverride);
    }

    private void SaveReplayLocked(int? secondsOverride)
    {
        if (_state != EngineState.Running || !_encodedStreamReady ||
            _encoder?.OutputMediaType is null) return;

        DumpStats("к моменту сохранения"); // короткий сеанс тоже должен оставить следы в логе

        var s = _settings.Current;
        long wanted = TimeSpan.FromSeconds(secondsOverride ?? s.ReplayLengthSeconds).Ticks;

        // Снимок с очисткой: буфер начинает копиться заново, владение массивами
        // кадров переходит нам — вернём их в пул после записи файла.
        var video = _videoBuffer.TakeSnapshot(wanted, out long snapshotToken);
        if (video.Count == 0) { SaveFailed?.Invoke("Буфер ещё пуст"); return; }
        SnapshotLease? lease = new(_videoBuffer, snapshotToken, video);
        try
        {
        // Аудио берём до очистки, затем его шкала начинается заново вместе с видео.
        var audio = _audioBuffer.Snapshot(video[0].PtsTicks, video[^1].PtsTicks);
        _audioBuffer.Clear();

        // Игра берётся по тому, что было на экране пока копился буфер (см. GameForClip).
        // Имя файла под неё подбирает уже фоновая задача: подбор упирается в File.Exists,
        // то есть в ДИСК, а этот метод держит _lifecycle — замок всего жизненного цикла
        // конвейера. Заснувший или занятый диск не должен останавливать запись.
        string game = GameForClip();
        DateTime capturedAt = DateTime.Now;
        // Копия, а не ссылка на поле энкодера: запись файла переживёт остановку
        // конвейера, а Dispose энкодера освободил бы тип прямо под SinkWriter.
        var mediaType = _encoder.CloneOutputMediaType();
        if (mediaType is null) { SaveFailed?.Invoke("Энкодер ещё не отдал тип видеопотока"); return; }
        lease.MediaType = mediaType;
        int seconds = (int)Math.Round(TimeSpan.FromTicks(video[^1].PtsTicks - video[0].PtsTicks).TotalSeconds);

        // Данные уже вырваны из кольцевого буфера и никуда не денутся — говорим об этом
        // пользователю сразу. Раньше уведомление ждало, пока сотни мегабайт доедут
        // до диска, и на длинном клипе это выглядело как «хоткей не сработал».
        SaveProgress = 0;
        ReplayCaptured?.Invoke(Math.Max(seconds, 1));

        SetState(EngineState.Saving);
        SnapshotLease saveLease = lease;
        _saveTask = Task.Run(() =>
        {
            _saveTaskId = Task.CurrentId;
            // Сохранение — пакетная фоновая работа: сотни МБ копий в нативные буферы
            // MF плюс сброс на диск. На обычном приоритете она конкурирует с потоками
            // захвата и кодирования (у тех AboveNormal), и входная очередь энкодера
            // успевает переполниться: в замерах пик очереди 66 из 66 и 523 дропнутых
            // кадра ровно в минуту сохранения, при этом сам ProcessInput не тормозил.
            // Лишние полсекунды на запись файла не заметит никто, потерянные кадры — да.
            Diagnostics.MemoryMap.Log("до сохранения");

            var self = Thread.CurrentThread;
            var previousPriority = self.Priority;
            self.Priority = ThreadPriority.BelowNormal;

            // В игре залп данных на диск уходит в фоновый режим Windows: приоритет
            // дисковых операций падает, и сотни мегабайт не отбирают ввод-вывод у игры.
            // На рабочем столе тормозить сохранение незачем — там пишем в полную силу.
            // Границы режима расставляет сам писатель: под ним идёт ТОЛЬКО подача
            // сэмплов, открытие и финализация — на обычном приоритете (см. BackgroundIoScope).
            bool inGame = !string.Equals(game, "Desktop", StringComparison.OrdinalIgnoreCase);
            string? file = null;
            try
            {
                file = ReserveFilePath(game, "replay", capturedAt);
                string publishedFile = ReplaySaver.Save(file, video, audio, mediaType, s.TrackMode,
                                                        s.CaptureGameAudio, s.CaptureMicrophone,
                                                        p => SaveProgress = p,
                                                        gentleIo: inGame);
                _storage.RegisterSaved(publishedFile); // индекс папки — без повторного обхода диска
                // Правку счётчика делает фоновый поток — идём через Update, чтобы она
                // не столкнулась с сохранением настроек из потока интерфейса.
                //
                // ГРУППА ОБЯЗАНА ОСТАВАТЬСЯ "stats". Обработчик Changed на группы
                // video/audio/replay берёт _lifecycle и перезапускает конвейер, а
                // Stop() как раз в это время ждёт завершения ЭТОЙ задачи, держа тот же
                // замок, — получился бы дедлок. Здесь мы внутри сохраняющего потока.
                _settings.Update(x => x.TotalReplaysSaved++, "stats");
                ReplaySaved?.Invoke(publishedFile, Math.Max(seconds, 1));
            }
            catch (Exception ex)
            {
                Log.Error("Engine", ex);
                FallBackToEightBitIfContainerRefused(ex);
                SaveFailed?.Invoke(ex.Message);
            }
            finally
            {
                // Файл записан — возвращаем буферу место, которое занимали кадры
                // клипа. Новой памяти на сохранение не тратилось вовсе: всё это
                // время клип лежал в той же арене, а запись шла в её свободную часть.
                saveLease.Dispose();
                if (file is not null) ReleaseFilePath(file);
                // Писатель закрыт — сводим освободившиеся нативные блоки вместе,
                // иначе память, занятая под клип, остаётся за процессом до выхода.
                Diagnostics.MemoryMap.Log("после сохранения");
                self.Priority = previousPriority;      // поток уходит обратно в пул потоков
                CompleteSavingState();
            }
        });
        lease = null; // владение ресурсами снимка перешло фоновой задаче
        }
        finally
        {
            // Любая ошибка между TakeSnapshot и успешным Task.Run раньше навсегда
            // оставляла арену зарезервированной, после чего новые кадры отбрасывались.
            lease?.Dispose();
        }
    }

    /// <summary>Транзакционное владение snapshot и клоном MediaType.</summary>
    private sealed class SnapshotLease(ReplayVideoBuffer owner, long token, List<EncodedFrame> frames) : IDisposable
    {
        private ReplayVideoBuffer? _owner = owner;
        private List<EncodedFrame>? _frames = frames;
        private IMFMediaType? _mediaType;

        public IMFMediaType? MediaType
        {
            set => _mediaType = value;
        }

        public void Dispose()
        {
            var currentOwner = Interlocked.Exchange(ref _owner, null);
            if (currentOwner is null) return;
            currentOwner.ReleaseSnapshot(token);
            Interlocked.Exchange(ref _frames, null)?.Clear();
            Interlocked.Exchange(ref _mediaType, null)?.Dispose();
        }
    }

    /// <summary>
    /// Путь файла по шаблону из настроек. {game} {date} {time} {preset} + раскладка по папкам игр.
    /// </summary>
    /// <summary>
    /// Занять имя файла под клип.
    ///
    /// Время в имени передаётся СНАРУЖИ, а не берётся здесь через DateTime.Now:
    /// повтор резервирует имя уже в фоновой задаче, и имя обязано помечать момент,
    /// когда клип сняли, а не момент, когда до записи дошли руки.
    /// </summary>
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
