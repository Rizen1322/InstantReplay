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
/// Часть <see cref="ReplayEngine"/>. Восстановление захвата и автосмена источника: пересборка конвейера после сбоя,
/// выбор запасного backend, отсрочки между попытками.
///
/// Движок разнесён по файлам по смыслу, без изменения поведения: те же поля, те же
/// замки, те же методы. Раньше это был один файл на 2200 строк, где сохранение,
/// диагностика и восстановление захвата шли вперемешку.
/// </summary>
public sealed partial class ReplayEngine
{
    // ---------------- Восстановление и автосмена backend ----------------

    private int _recovering;

    private void RequestCaptureRestart(
        CaptureRecoveryDecision initialDecision,
        string reason,
        CaptureFailureKind failureKind,
        long observedGeneration)
    {
        CaptureBackend next = initialDecision.Backend;
        CaptureBackend previous = _captureBackend;
        if (initialDecision.Action == CaptureRecoveryAction.HoldForGameWindow)
        {
            Interlocked.CompareExchange(
                ref _windowHoldStartedTimestamp,
                System.Diagnostics.Stopwatch.GetTimestamp(),
                0);
        }
        var cancellation = new CancellationTokenSource();
        CancellationTokenSource? replaced = Interlocked.Exchange(
            ref _recoveryCancellation, cancellation);
        replaced?.Cancel();

        Task.Run(() =>
        {
            try
            {
                string recoveryMode = initialDecision.Action == CaptureRecoveryAction.HoldForGameWindow
                    ? "; держу последний игровой кадр"
                    : "";
                Log.Warn("Engine", $"Захват generation {observedGeneration}: {previous} → {next}; " +
                                   $"target r{initialDecision.TargetRevision}; {reason}{recoveryMode}");
                // Уведомление о начале восстановления не шлём вовсе. Человеку не
                // нужно знать, что захват переподключается: он узнает об этом только
                // если восстановиться не вышло. На альт-табе из полноэкранной игры
                // Desktop Duplication теряет дупликацию несколько раз подряд, и каждый
                // эпизод давал по два уведомления — отсюда спам.

                // При деградации оконного WGC не рвём конвейер сразу: пейсер
                // продолжает кодировать последний принятый игровой кадр на время
                // короткой выдержки. Мониторный кадр в этот эпизод admission-gate
                // всё равно не пропустит.
                bool delayConsumedWhileHolding =
                    initialDecision.Action == CaptureRecoveryAction.HoldForGameWindow &&
                    initialDecision.RetryDelay > TimeSpan.Zero;
                if (delayConsumedWhileHolding &&
                    cancellation.Token.WaitHandle.WaitOne(initialDecision.RetryDelay))
                    return;

                try
                {
                    lock (_lifecycle)
                    {
                        if (_stopRequested || observedGeneration != Interlocked.Read(ref _captureGeneration)) return;
                        StopLocked(PipelineStopIntent.CaptureRestart);
                        if (_state != EngineState.Recovering)
                            throw new InvalidOperationException("Остановка видеоконвейера не завершилась");
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("Engine", $"Автовосстановление отменено: конвейер не остановился ({ex.Message})");
                    Warning?.Invoke("Не удалось безопасно перезапустить захват");
                    return;
                }

                long restartGeneration = Interlocked.Read(ref _captureGeneration);
                CaptureBackend candidate = next;
                CaptureRecoveryDecision decision = initialDecision;
                for (int attempt = 1; ; attempt = attempt == int.MaxValue ? attempt : attempt + 1)
                {
                    int delay = attempt == 1 && delayConsumedWhileHolding
                        ? 0
                        : attempt == 1 && decision.RetryDelay > TimeSpan.Zero
                        ? Math.Max(1, (int)decision.RetryDelay.TotalMilliseconds)
                        : CaptureRecoveryBackoff.DelayMilliseconds(
                            attempt, failureKind == CaptureFailureKind.DeviceLost);
                    if (cancellation.Token.WaitHandle.WaitOne(delay)) return;

                    // Пока мы спали, человек мог выключить запись — тогда включать
                    // её обратно нельзя ни при каких обстоятельствах
                    if (_stopRequested ||
                        restartGeneration != Interlocked.Read(ref _captureGeneration))
                    {
                        Log.Info("Engine", "Восстановление отменено: состояние конвейера уже изменилось");
                        return;
                    }

                    try
                    {
                        GameCaptureTarget? candidateTarget = candidate is
                            CaptureBackend.WgcWindow or CaptureBackend.MinecraftOpenGl
                            ? _gameCaptureRecovery.Target
                            : null;
                        if (candidate == CaptureBackend.WgcWindow)
                            Interlocked.Increment(ref _windowRetryCount);
                        lock (_lifecycle)
                        {
                            if (_stopRequested ||
                                restartGeneration != Interlocked.Read(ref _captureGeneration)) return;
                            StartLocked(preserveBuffers: true, candidate, candidateTarget);
                            if (_stopRequested)
                            {
                                StopLocked(PipelineStopIntent.UserStop);
                                return;
                            }
                            // Читаем intent под тем же lifecycle-lock: если человек
                            // нажал «остановить» в промежутке, новый файл не создаём.
                            if (_continuousRecordingRequested) StartRecordingLocked();
                            if (_stopRequested)
                            {
                                StopLocked(PipelineStopIntent.UserStop);
                                return;
                            }
                        }
                        long holdStarted = Interlocked.Exchange(ref _windowHoldStartedTimestamp, 0);
                        string held = holdStarted == 0
                            ? ""
                            : $", удержание {ElapsedMilliseconds(holdStarted)} мс";
                        Log.Info("Engine", $"Конвейер восстановлен на {candidate} " +
                                           $"(попытка {attempt}{held})");
                        // О восстановлении говорим, только если оно было заметным: не с
                        // первой попытки. Обычная смена режима при альт-табе чинится с
                        // первой, и сообщать о ней значило бы сообщать о каждом альт-табе.
                        if (attempt > 1) Warning?.Invoke("Запись восстановлена");
                        return;
                    }
                    catch (OperationCanceledException) when (_stopRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("Engine", $"Восстановление {candidate}, попытка {attempt}: {ex.Message}");
                        restartGeneration = Interlocked.Read(ref _captureGeneration);
                        RefreshCaptureTarget();
                        if (!_gameCaptureRecovery.TryDecide(
                                candidate,
                                CaptureFailureKind.BackendUnavailable,
                                out decision))
                            return;
                        candidate = decision.Backend;
                    }
                }
            }
            finally
            {
                if (ReferenceEquals(
                        Interlocked.CompareExchange(ref _recoveryCancellation, null, cancellation),
                        cancellation))
                {
                    cancellation.Dispose();
                }
                Interlocked.Exchange(ref _recovering, 0);
            }
        });
    }

    private static long ElapsedMilliseconds(long startedTimestamp)
    {
        long elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - startedTimestamp;
        return elapsed <= 0
            ? 0
            : (long)(elapsed * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
    }
}
