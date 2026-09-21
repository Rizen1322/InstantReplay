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

    private void StartRecordingLocked()
    {
        if (_recorder is not null) return;
        if (_state == EngineState.Stopped) StartWithFallbackLocked(preserveBuffers: false); // может бросить — наружу, UI покажет
        if (_encoder?.OutputMediaType is null) return;

        var s = _settings.Current;
        string game = GameDetector.DetectForegroundGame();
        // Тип видеопотока берётся не сейчас, а в момент создания файла (первый keyframe):
        // сразу после старта конвейера энкодер ещё не дописал в него заголовки кодека,
        // и файл, открытый с таким типом, не собирается на финализации.
        string file = ReserveFilePath(game, "recording", DateTime.Now);
        ManualRecorder recorder;
        try
        {
            recorder = new ManualRecorder(file, () => _encoder?.CloneOutputMediaType(),
                s.TrackMode, s.CaptureGameAudio, s.CaptureMicrophone);
        }
        catch
        {
            ReleaseFilePath(file);
            throw;
        }
        _encoder.FrameEncoded += recorder.OnFrame;
        _audio.BlockReady += recorder.OnAudio;
        _recorder = recorder;
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

    private string? StopRecordingLocked(bool wait)
    {
        var recorder = _recorder;
        if (recorder is null) return null;
        _recorder = null;
        // Одно чтение поля вместо двух: параллельный снос обнулял _encoder ровно
        // между проверкой и использованием
        var encoder = _encoder;
        if (encoder is not null) encoder.FrameEncoded -= recorder.OnFrame;
        _audio.BlockReady -= recorder.OnAudio;
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
