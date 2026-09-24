using Aura.Core.Buffering;
using Aura.Core.Capture;
using Aura.Core.Logging;
using Aura.Core.Settings;
using Aura.Core.Storage;

namespace Aura.Core.Engine;

/// <summary>
/// Часть <see cref="ReplayEngine"/>: реакция на правку настроек записи.
///
/// КАК БЫЛО. Страница «Захват» сохраняет всё одной группой «video», и на любую
/// правку — сменил микрофон, передвинул громкость, поменял длину повтора —
/// конвейер останавливался и собирался заново, а накопленный повтор пропадал.
///
/// КАК СТАЛО. Движок помнит настройки, с которыми собран конвейер, и сравнивает:
/// • формат видео (кодек, разрешение, частота, глубина цвета, монитор) — полная
///   пересборка, старые кадры с новыми смешать нельзя;
/// • битрейт, курсор — пересборка захвата и энкодера с СОХРАНЕНИЕМ буфера:
///   совместимость проверяется по заголовку кодека на первом ключевом кадре;
/// • длина повтора, место буфера — арена меняет размер, кадры переписываются в новую;
/// • устройства и дорожки звука — перезапускается только звук;
/// • громкость, шумодав, режим дорожек — на лету, без перезапуска вовсе.
/// </summary>
public sealed partial class ReplayEngine
{
    /// <summary>Настройки, от которых зависит собранный конвейер.</summary>
    private sealed record PipelineConfig(
        VideoCodec Codec, int Height, int Fps, VideoBitDepth BitDepth, int Monitor,
        int BitrateMbps, bool Cursor,
        int ReplaySeconds, bool OnDisk,
        bool Game, bool Mic, string? RenderDevice, string? CaptureDevice)
    {
        public static PipelineConfig From(AppSettings s) => new(
            s.Codec, s.VerticalResolution, s.Fps, s.BitDepth, s.MonitorIndex,
            s.BitrateMbps, s.RecordCursor,
            s.ReplayLengthSeconds, s.ReplayBufferOnDisk,
            s.CaptureGameAudio, s.CaptureMicrophone, s.RenderDeviceId, s.CaptureDeviceId);

        public bool FormatDiffers(PipelineConfig o) =>
            Codec != o.Codec || Height != o.Height || Fps != o.Fps || BitDepth != o.BitDepth || Monitor != o.Monitor;

        public bool EncoderDiffers(PipelineConfig o) => BitrateMbps != o.BitrateMbps || Cursor != o.Cursor;

        public bool BufferDiffers(PipelineConfig o) =>
            ReplaySeconds != o.ReplaySeconds || OnDisk != o.OnDisk || BitrateMbps != o.BitrateMbps;

        public bool AudioDiffers(PipelineConfig o) =>
            Game != o.Game || Mic != o.Mic || RenderDevice != o.RenderDevice || CaptureDevice != o.CaptureDevice;
    }

    private PipelineConfig? _appliedConfig;

    private void OnSettingsChanged(string group)
    {
        // Громкость и шумодав — на лету, без всякого перезапуска
        if (group is "" or "video" or "audio" or "audio-live")
            ApplyLiveAudioSettings(_settings.Current);

        if (group is not ("" or "video" or "audio" or "replay")) return;
        if (_state == EngineState.Stopped) return;

        // Под одним замком: между остановом и стартом не должен вклиниться ни хоткей
        // сохранения, ни вотчдог со своим перезапуском.
        lock (_lifecycle)
        {
            if (_state == EngineState.Stopped) return;
            var now = PipelineConfig.From(_settings.Current);
            var was = _appliedConfig;
            if (was == now) return;

            try
            {
                if (was is null || was.FormatDiffers(now))
                {
                    Log.Info("Engine", "Формат видео изменился — пересобираю конвейер с нуля");
                    StopLocked();
                    StartWithFallbackLocked(preserveBuffers: false);
                    return;
                }

                if (_state == EngineState.Recovering)
                {
                    // Пересборку ведёт восстановление; новые значения возьмутся на его
                    // старте. Размер буфера и звук меняем сразу.
                    Log.Info("Engine", "Настройки изменились во время восстановления — применю по ходу");
                }

                if (was.BufferDiffers(now)) ApplyBufferSize(now);
                if (was.AudioDiffers(now)) RestartAudioLocked();
                if (was.EncoderDiffers(now) && _state != EngineState.Recovering) RestartEncoderLocked();
                _appliedConfig = now;
            }
            catch (Exception ex)
            {
                Log.Error("Engine", ex);
                Warning?.Invoke($"Настройки не применились: {ex.Message}");
            }
        }
    }

    private void ApplyLiveAudioSettings(AppSettings s)
    {
        _audio.MicNoiseGate = s.MicNoiseSuppression;
        _audio.MicNeuralDenoise = s.MicNeuralNoiseSuppression;
        _audio.MicGateThresholdDb = s.MicNoiseGateDb;
        _audio.GameVolume = s.GameVolumePercent / 100f;
        _audio.MicVolume = s.MicVolumePercent / 100f;
    }

    /// <summary>Длина повтора, которая реально помещается в арену.</summary>
    private int EffectiveReplaySeconds(AppSettings s, bool warn)
    {
        int max = ReplayVideoBuffer.MaximumDurationSeconds(s.BitrateBps, s.ReplayBufferOnDisk);
        int effective = Math.Min(s.ReplayLengthSeconds, max);
        if (warn && effective < s.ReplayLengthSeconds)
        {
            Log.Warn("Engine", $"Повтор {s.ReplayLengthSeconds} с не помещается в арену при " +
                               $"{s.BitrateMbps} Мбит/с — ограничен до {effective} с");
            Warning?.Invoke($"Длина повтора ограничена до {TimeSpan.FromSeconds(effective):m\\:ss}: " +
                            (s.ReplayBufferOnDisk ? "не хватает места под буфер" : "не хватает оперативной памяти") +
                            (s.ReplayBufferOnDisk ? "" : ". Включите «Буфер на диске», чтобы писать дольше"));
        }
        return effective;
    }

    /// <summary>Папка файла буфера, если он на диске; null — буфер в памяти.</summary>
    private static string? BufferDirectory(AppSettings s) =>
        s.ReplayBufferOnDisk ? ReplayVideoBuffer.DiskDirectory : null;

    /// <summary>
    /// Хватает ли места, чтобы включить повтор: под клип в папке записей, а для
    /// буфера на диске — ещё и под сам буфер.
    /// </summary>
    private static void RequireDiskSpaceForReplay(AppSettings s)
    {
        DiskSpace.Require(s.SaveRootPath, DiskSpace.ReplayClipBytes(s.BitrateBps, s.ReplayLengthSeconds),
                          "сохранения повтора");
        if (s.ReplayBufferOnDisk)
        {
            int seconds = Math.Min(s.ReplayLengthSeconds, ReplayVideoBuffer.MaximumDurationSeconds(s.BitrateBps, true));
            DiskSpace.Require(ReplayVideoBuffer.DiskDirectory,
                              ReplayVideoBuffer.AllocatedCapacityBytes(s.BitrateBps, seconds, onDisk: true),
                              "буфера повтора на диске");
        }
    }

    /// <summary>Новая длина или место буфера: арена меняет размер, кадры переезжают.</summary>
    private void ApplyBufferSize(PipelineConfig now)
    {
        var s = _settings.Current;
        if (now.OnDisk) RequireDiskSpaceForReplay(s);

        // Пока пишется клип, его кадры держат арену — ждём, потом меняем размер.
        WaitForPendingWrites();

        int seconds = EffectiveReplaySeconds(s, warn: true);
        if (!_videoBuffer.Resize(s.BitrateBps, seconds, BufferDirectory(s)))
        {
            Log.Warn("Engine", "Арену не удалось пересоздать с сохранением — начинаю буфер заново");
            _videoBuffer.Allocate(s.BitrateBps, seconds, BufferDirectory(s));
            _bufferSequenceHeader = null;
        }
        _videoBuffer.MaxDurationTicks = TimeSpan.FromSeconds(seconds).Ticks;
        _audioBuffer.MaxDurationTicks = _videoBuffer.MaxDurationTicks;
        _audioBuffer.Resize(seconds, s.CaptureGameAudio, s.CaptureMicrophone);
    }

    /// <summary>Сменились устройства или набор дорожек звука: перезапускается только звук.</summary>
    private void RestartAudioLocked()
    {
        var s = _settings.Current;
        Log.Info("Engine", "Настройки звука изменились — перезапускаю только звук, видео продолжает писаться");
        _audio.Stop();
        _audioBuffer.Resize(EffectiveReplaySeconds(s, warn: false), s.CaptureGameAudio, s.CaptureMicrophone);
        ApplyLiveAudioSettings(s);
        if (s.CaptureGameAudio || s.CaptureMicrophone)
            _audio.Start(s.CaptureGameAudio, s.CaptureMicrophone, s.RenderDeviceId, s.CaptureDeviceId);
    }

    /// <summary>
    /// Сменился битрейт или курсор: захват и энкодер собираются заново, но буфер
    /// остаётся — так же, как при автовосстановлении захвата. Если новый поток
    /// окажется несовместим со старым (другой заголовок кодека), буфер очистится
    /// сам на первом ключевом кадре.
    /// </summary>
    private void RestartEncoderLocked()
    {
        CaptureBackend backend = _captureBackend;
        GameCaptureTarget? target = ActiveCaptureTarget();
        Log.Info("Engine", "Битрейт или курсор изменились — пересобираю энкодер, повтор сохраняется");

        StopLocked(PipelineStopIntent.CaptureRestart);
        try
        {
            StartLocked(preserveBuffers: true, backend,
                        backend is CaptureBackend.WgcWindow or CaptureBackend.MinecraftOpenGl ? target : null);
            if (_continuousRecordingRequested) ResumeRecordingLocked();
        }
        catch (Exception ex)
        {
            // Тот же источник не поднялся (окно игры закрылось и т. п.) — обычный
            // полный старт с выбором источника.
            Log.Warn("Engine", $"Пересборка с сохранением не удалась ({ex.Message}) — запускаю заново");
            if (_state != EngineState.Stopped) StopLocked();
            StartWithFallbackLocked(preserveBuffers: false);
        }
    }
}
