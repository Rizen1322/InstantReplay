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
/// Часть <see cref="ReplayEngine"/>. Обычная запись в файл («Начать запись»): запуск, остановка, дописывание
/// хвоста и публикация частей.
///
/// Движок разнесён по файлам по смыслу, без изменения поведения: те же поля, те же
/// замки, те же методы. Раньше это был один файл на 2200 строк, где сохранение,
/// диагностика и восстановление захвата шли вперемешку.
/// </summary>
public sealed partial class ReplayEngine
{
    // ---------------- Обычная запись в файл ----------------

    /// <summary>
    /// Начать обычную запись в файл. Если буфер выключен — включает его.
    ///
    /// Под тем же замком, что и остальной жизненный цикл: метод трогает _encoder,
    /// _audio и _recorder, а параллельный Stop() обнуляет ровно их. Monitor
    /// реентерантен, поэтому вызов из StopLocked и из восстановления проходит.
    /// </summary>
    public void StartRecordingToFile()
    {
        lock (_lifecycle)
        {
            _continuousRecordingRequested = true;
            // Recovery уже владеет обязанностью поднять конвейер. Здесь достаточно
            // записать пользовательский intent; новый сегмент откроется после старта.
            if (_state == EngineState.Recovering) return;
            try
            {
                StartRecordingLocked();
                _continuousRecordingRequested = _recorder is not null;
            }
            catch
            {
                _continuousRecordingRequested = false;
                throw;
            }
        }
    }

    /// <summary>
    /// Продолжить запись в файл после пересборки конвейера. Неудача (например,
    /// кончилось место) не должна валить саму пересборку: конвейер к этому моменту
    /// уже работает, и ошибка, выпущенная наружу, запустила бы восстановление по
    /// кругу. Запись останавливается, человек видит причину, повтор пишется дальше.
    /// </summary>
    private void ResumeRecordingLocked()
    {
        try { StartRecordingLocked(); }
        catch (Exception ex)
        {
            _continuousRecordingRequested = false;
            Log.Warn("Recorder", $"Запись в файл не продолжена: {ex.Message}");
            Warning?.Invoke($"Запись в файл остановлена: {ex.Message}");
        }
    }

    /// <summary>Обработчик кадров энкодера, через который пишет текущая запись.</summary>
    private Action<EncodedFrame>? _recorderFrameHandler;

    /// <summary>Запись отцеплена от энкодера на время пересборки конвейера.</summary>
    private bool _recorderDetached;

    /// <summary>Заголовок кодека, с которым открыта запись: по нему решаем, продолжать ли файл.</summary>
    private byte[]? _recorderSequenceHeader;
    private (VideoCodec Codec, int Width, int Height, int Fps) _recorderStream;

    /// <summary>
    /// Пересборка конвейера: запись отцепляется только от старого энкодера. Файл
    /// остаётся открытым, звук продолжает в него писаться.
    /// </summary>
    private void DetachRecorderLocked()
    {
        if (_recorder is null || _recorderDetached) return;
        if (_encoder is { } encoder && _recorderFrameHandler is { } handler) encoder.FrameEncoded -= handler;
        _recorderFrameHandler = null;
        _recorderDetached = true;
    }

    /// <summary>
    /// Прицепить открытую запись к новому энкодеру. Файл один на всю запись:
    /// раньше каждая пересборка (например, WGC замолчал и захват перешёл на
    /// Desktop Duplication) начинала новый файл. Продолжать можно, только если
    /// поток тот же: кодек, размер, частота и заголовок кодека. Иначе файл
    /// закрывается и начинается следующая часть, как раньше.
    /// </summary>
    private void ReattachRecorderLocked()
    {
        var recorder = _recorder!;
        var encoder = _encoder;
        if (encoder is not { StreamReady: true }) return;   // энкодер ещё не готов — попробуем при следующем старте

        if (_recorderStream != (_bufferCodec, _bufferWidth, _bufferHeight, _bufferFps))
        {
            Log.Info("Recorder", "Формат видео после пересборки другой — запись продолжится новым файлом");
            SplitRecordingLocked();
            return;
        }

        recorder.ResumeAfterGap();
        byte[]? expected = _recorderSequenceHeader;
        bool checkedHeader = false;
        Action<EncodedFrame> handler = null!;
        handler = frame =>
        {
            if (!checkedHeader)
            {
                if (!frame.IsKeyframe) return;
                checkedHeader = true;
                byte[]? header = encoder.TryGetSequenceHeader();
                if (expected is not null && header is not null &&
                    !EncodedStreamCompatibility.SameSequenceHeader(expected, header))
                {
                    // Поток кодека другой — в тот же файл его не положить. Решаем
                    // вне потока энкодера: разделение берёт замок жизненного цикла.
                    encoder.FrameEncoded -= handler;
                    _ = Task.Run(() =>
                    {
                        lock (_lifecycle)
                        {
                            if (!ReferenceEquals(_recorder, recorder)) return;
                            Log.Info("Recorder", "Параметры кодека после пересборки другие — запись продолжится новым файлом");
                            SplitRecordingLocked();
                        }
                    });
                    return;
                }
                _recorderSequenceHeader ??= header;
            }
            recorder.OnFrame(frame);
        };
        encoder.FrameEncoded += handler;
        _recorderFrameHandler = handler;
        _recorderDetached = false;
        Log.Info("Recorder", "Запись продолжается в тот же файл после пересборки конвейера");
    }

    /// <summary>Закрыть текущий файл записи и сразу начать следующий.</summary>
    private void SplitRecordingLocked()
    {
        StopRecordingLocked(wait: false);
        if (_continuousRecordingRequested) StartRecordingLocked();
    }

    private void StartRecordingLocked()
    {
        if (_recorder is not null)
        {
            if (_recorderDetached) ReattachRecorderLocked();
            return;
        }
        if (_state == EngineState.Stopped) StartWithFallbackLocked(preserveBuffers: false); // может бросить — наружу, UI покажет
        if (_encoder is not { StreamReady: true }) return;

        var s = _settings.Current;
        // Места нет — запись не начинаем: файл оборвался бы через пару минут.
        DiskSpace.Require(s.SaveRootPath,
                          Math.Max(DiskSpace.RecordingMinimumBytes, s.BitrateBps / 8 * 120),
                          "записи в файл");

        string game = GameDetector.DetectForegroundGame();
        // Описание видеодорожки собирается на первом ключевом кадре записи: к нему
        // энкодер уже отдал заголовки кодека.
        var codec = ContainerCodec(_bufferCodec);
        int width = _bufferWidth, height = _bufferHeight, fps = _bufferFps;
        // Заголовок берём у движка, а не у энкодера: файл открывается в потоке
        // писателя, когда энкодер мог уже смениться при восстановлении захвата.
        Func<byte[], Saving.Mp4.Mp4VideoFormat> videoFormat = keyframe =>
            Saving.Mp4.Mp4VideoFormat.FromBitstream(codec, width, height,
                _bufferSequenceHeader ?? [], keyframe) with { FrameRate = fps };

        var audioTracks = new List<(AudioTrackKind, Saving.Mp4.Mp4AudioFormat)>();
        foreach (var kind in ReplaySaver.TracksFor(s.TrackMode, s.CaptureGameAudio, s.CaptureMicrophone))
            if (_audio.FormatOf(kind) is { } format) audioTracks.Add((kind, format));

        string file = ReserveFilePath(game, "recording", DateTime.Now);
        ManualRecorder recorder;
        try
        {
            recorder = new ManualRecorder(file, videoFormat, audioTracks, 10_000_000L / Math.Max(1, fps));
        }
        catch
        {
            ReleaseFilePath(file);
            throw;
        }
        recorder.Faulted += _ =>
        {
            // Только если это всё ещё текущая запись: пользователь мог уже нажать стоп.
            lock (_lifecycle)
            {
                if (!ReferenceEquals(_recorder, recorder)) return;
                _continuousRecordingRequested = false;
                StopRecordingLocked(wait: false);   // причину покажет обработчик завершения
            }
        };
        _recorderFrameHandler = recorder.OnFrame;
        _encoder.FrameEncoded += _recorderFrameHandler;
        _audio.FrameEncoded += recorder.OnAudio;
        _recorder = recorder;
        _recorderNvencStats = NvencStats.Take();
        _recorderDetached = false;
        _recorderSequenceHeader = _bufferSequenceHeader;
        _recorderStream = (_bufferCodec, _bufferWidth, _bufferHeight, _bufferFps);
        RecordingChanged?.Invoke(true);
    }

    /// <summary>
    /// Остановить обычную запись. Возвращает путь к файлу (null — если не писали).
    /// Дозапись хвоста и финализация контейнера идут в фоне: на десятиминутном файле
    /// это заметное время, и держать на нём UI-поток нельзя. Событие (RecordingSaved
    /// или SaveFailed) приходит, когда файл реально закрыт.
    /// </summary>
    public string? StopRecordingToFile(bool wait = false)
    {
        lock (_lifecycle)
        {
            // Важен даже вызов между двумя сегментами, когда _recorder уже null:
            // recovery не должен после него снова открыть файл.
            _continuousRecordingRequested = false;
            return StopRecordingLocked(wait);
        }
    }

    /// <summary>Счётчики NVENC в начале записи в файл: в конце печатаем разницу.</summary>
    private NvencStats.Snapshot _recorderNvencStats;

    private string? StopRecordingLocked(bool wait)
    {
        var recorder = _recorder;
        if (recorder is null) return null;
        _recorder = null;
        Log.Info("Encoder", $"NVENC за запись: {NvencStats.Take().Since(_recorderNvencStats)}");
        // Одно чтение поля вместо двух: параллельный снос обнулял _encoder ровно
        // между проверкой и использованием
        var encoder = _encoder;
        if (encoder is not null && _recorderFrameHandler is { } handler) encoder.FrameEncoded -= handler;
        _recorderFrameHandler = null;
        _recorderDetached = false;
        _audio.FrameEncoded -= recorder.OnAudio;
        RecordingChanged?.Invoke(false);

        var finish = Task.Run(() =>
        {
            try
            {
                var result = recorder.Finish();

                // Индекс наполняем ТОЛЬКО удачными файлами. Раньше RegisterSaved шёл до
                // проверки Ok, и незакрытые части попадали в библиотеку и в статистику
                // хранилища как полноценные записи — карточка есть, а файла нет.
                if (result.Ok)
                {
                    foreach (var file in result.Files) _storage.RegisterSaved(file);
                    RecordingSaved?.Invoke(result.Files[0], Math.Max(result.Seconds, 1));
                    // Записанное целое, но запись оборвалась раньше (кончилось место) —
                    // человек должен узнать, почему файл короче.
                    if (result.Error is { } error) Warning?.Invoke($"Запись остановилась раньше: {error}");
                }
                else SaveFailed?.Invoke(result.Error ?? "запись не закрылась");
            }
            catch (Exception ex)
            {
                Log.Error("Recorder", ex);
                SaveFailed?.Invoke(ex.Message);
            }
            finally
            {
                try { recorder.Dispose(); }
                finally { ReleaseFilePath(recorder.FilePath); }
            }
        });
        lock (_writerTasksSync) _writerTasks.Add(finish);
        _ = finish.ContinueWith(completed =>
        {
            lock (_writerTasksSync) _writerTasks.Remove(completed);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        if (wait) finish.Wait(TimeSpan.FromSeconds(70));
        return recorder.FilePath;
    }
}
