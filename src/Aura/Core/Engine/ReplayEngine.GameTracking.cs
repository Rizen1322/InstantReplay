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
/// Часть <see cref="ReplayEngine"/>. Какая игра была на экране, пока копился буфер: по ней называется клип,
/// и по ней же конвейер заранее переключается на захват игрового окна.
///
/// Движок разнесён по файлам по смыслу, без изменения поведения: те же поля, те же
/// замки, те же методы. Раньше это был один файл на 2200 строк, где сохранение,
/// диагностика и восстановление захвата шли вперемешку.
/// </summary>
public sealed partial class ReplayEngine
{
    // ---------------- Какая игра была в буфере ----------------

    /// <summary>
    /// Игра берётся не в момент нажатия хоткея, а по тому, что было на экране,
    /// ПОКА КОПИЛСЯ БУФЕР. Иначе достаточно свернуться в Discord, вспомнить про
    /// момент и нажать — и трёхминутный клип из игры уезжает в папку «Discord».
    /// Держим отметки за длину буфера и выбираем ту игру, что занимала больше всего
    /// времени; рабочий стол засчитывается только если другого не было вовсе.
    /// </summary>
    private readonly Queue<(long Ticks, string Game)> _gameSamples = new();
    private readonly object _gameSync = new();
    private System.Threading.Timer? _gameTimer;

    private GameCaptureTarget? RefreshCaptureTarget()
    {
        lock (_captureTargetSync)
        {
            GameCaptureTarget? selected = ForegroundGameWindowProbe.TrySelect(
                _settings.Current.MonitorIndex,
                _lastVerifiedCaptureTarget);
            if (selected is GameCaptureTarget target)
                _lastVerifiedCaptureTarget = target;
            _gameCaptureRecovery.ObserveTarget(selected);
            return selected;
        }
    }

    private GameCaptureTarget? ActiveCaptureTarget()
    {
        lock (_captureTargetSync) return _activeCaptureTarget;
    }

    private void StartGameTracker(bool preserveHistory = false)
    {
        if (!preserveHistory)
            lock (_gameSync) _gameSamples.Clear();
        _gameTimer?.Dispose();
        _gameTimer = new System.Threading.Timer(_ =>
        {
            if (!_pipelineOpen || _stopRequested) return;
            try
            {
                GameCaptureTarget? foregroundTarget = RefreshCaptureTarget();
                CaptureBackendTargetSelection targetSelection =
                    CaptureBackendPolicy.SelectForForeground(
                        _captureBackend,
                        ActiveCaptureTarget(),
                        foregroundTarget,
                        _captureBackendForced);
                if (targetSelection.RestartRequired)
                    RequestProactiveCaptureTransition(targetSelection);

                if (_captureBackend == CaptureBackend.WgcWindow &&
                    ActiveCaptureTarget() is GameCaptureTarget activeTarget &&
                    (foregroundTarget is not GameCaptureTarget currentTarget ||
                     !currentTarget.HasSameIdentity(activeTarget) ||
                     currentTarget.Revision != activeTarget.Revision))
                {
                    long generation = Interlocked.Read(ref _captureGeneration);
                    OnCaptureFailed(new CaptureFailure(
                        CaptureFailureKind.CaptureTargetClosed,
                        new InvalidOperationException("Foreground game target changed"),
                        "WGC window: игра вышла из fullscreen или сменила окно",
                        generation,
                        activeTarget.Revision), generation);
                }

                string game = GameDetector.DetectForegroundGame();
                long now = DateTime.UtcNow.Ticks;
                lock (_gameSync)
                {
                    _gameSamples.Enqueue((now, game));
                    long oldest = now - _videoBuffer.MaxDurationTicks;
                    while (_gameSamples.Count > 0 && _gameSamples.Peek().Ticks < oldest)
                        _gameSamples.Dequeue();
                }
            }
            catch (Exception ex)
            {
                // Молчание здесь означает клип, уехавший в папку не той игры,
                // и никаких следов, почему так вышло. Раз в 2 секунды — не спамим:
                // повторы схлопывает сам логгер.
                Log.Warn("Engine", $"Не удалось определить игру на экране: {ex.Message}");
            }
        }, null, TimeSpan.Zero, TimeSpan.FromSeconds(2));
    }

    private void RequestProactiveCaptureTransition(
        in CaptureBackendTargetSelection selection)
    {
        if (selection.Target is not GameCaptureTarget target ||
            !_pipelineOpen || _stopRequested ||
            Interlocked.CompareExchange(ref _recovering, 1, 0) != 0)
        {
            return;
        }

        long generation = Interlocked.Read(ref _captureGeneration);
        var decision = new CaptureRecoveryDecision(
            CaptureRecoveryAction.Restart,
            selection.Backend,
            target.Revision,
            TimeSpan.Zero,
            _gameCaptureRecovery.Episode.Align(target));
        RequestCaptureRestart(
            decision,
            $"обнаружен Minecraft fullscreen: PID {target.ProcessId}, revision {target.Revision}",
            CaptureFailureKind.BackendUnavailable,
            generation);
    }

    /// <summary>Игра, под которую сохранять клип: самая частая за время буфера.</summary>
    private string GameForClip()
    {
        lock (_gameSync)
        {
            if (_gameSamples.Count == 0) return GameDetector.DetectForegroundGame();

            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var (_, game) in _gameSamples)
                counts[game] = counts.GetValueOrDefault(game) + 1;

            // Рабочий стол — это «ничего не запущено», он не должен побеждать игру,
            // даже если её свернули на половину буфера.
            var best = counts
                .OrderByDescending(p => string.Equals(p.Key, "Desktop", StringComparison.OrdinalIgnoreCase) ? -1 : p.Value)
                .First();
            return best.Key;
        }
    }
}
