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
using Aura.Core.SystemIntegration;

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
    private readonly object _recoverySync = new();
    private CaptureFailure? _pendingCaptureFailure;

    private bool TryBeginCaptureRecovery(CaptureFailure? failure = null)
    {
        lock (_recoverySync)
        {
            // Переход в терминальную блокировку уже начат: новые попытки
            // восстановления не имеют смысла и только дёргали бы выход.
            if (Volatile.Read(ref _captureBlockRequested) != 0) return false;
            if (_recovering != 0)
            {
                // Новый источник может сломаться ещё до выхода из StartLocked.
                if (failure is { } next &&
                    (_pendingCaptureFailure is not { } pending || next.Generation >= pending.Generation))
                    _pendingCaptureFailure = next;
                return false;
            }
            _recovering = 1;
            return true;
        }
    }

    private void FinishCaptureRecovery(CancellationTokenSource? cancellation = null)
    {
        CaptureFailure? pending;
        lock (_recoverySync)
        {
            if (cancellation is not null)
            {
                if (ReferenceEquals(_recoveryCancellation, cancellation))
                    _recoveryCancellation = null;
                cancellation.Dispose();
            }
            _recovering = 0;
            pending = _pendingCaptureFailure;
            _pendingCaptureFailure = null;
        }
        if (pending is { } failure) OnCaptureFailed(failure, failure.Generation);
    }

    private void CancelCaptureRecovery()
    {
        // Cancel и Dispose принадлежат разным потокам: защищаем сам CTS, не lifecycle.
        lock (_recoverySync) _recoveryCancellation?.Cancel();
    }

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
        lock (_recoverySync)
        {
            _recoveryCancellation?.Cancel();
            _recoveryCancellation = cancellation;
            if (_stopRequested) cancellation.Cancel();
        }

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
                        if (candidate == CaptureBackend.Wgc && !ProbeDesktop().InteractiveDesktopActive)
                        {
                            Log.Info("Engine", "Пересборка WGC ждёт возврата пользовательского рабочего стола");
                            DesktopWaitResult wait = DesktopReadinessWaiter.WaitAsync(
                                ProbeDesktop,
                                requireShell: false,
                                shellTimeout: TimeSpan.Zero,
                                settleDuration: DesktopSettleDelay,
                                pollInterval: DesktopPollInterval,
                                settleIfInitiallyReady: false,
                                shouldAbort: () => _stopRequested ||
                                                   restartGeneration != Interlocked.Read(ref _captureGeneration),
                                wake: _desktopWake,
                                ct: cancellation.Token).GetAwaiter().GetResult();
                            if (wait.Aborted) return;
                        }

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
                            if (_continuousRecordingRequested) ResumeRecordingLocked();
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
                    catch (CaptureOutputHeldException held)
                    {
                        // Выход удерживает незавершившаяся сессия DDA этого же
                        // процесса: повторять старт бессмысленно и вредно — в логе
                        // друга так набралось 131 E_INVALIDARG. Переходим в честное
                        // терминальное состояние, сохраняя накопленный повтор.
                        Log.Error("Engine", $"Автовосстановление остановлено: {held.Message}");
                        EnterTerminalCaptureBlocked(held.Output);
                        return;
                    }
                    catch (RemoteCaptureHostUnavailableException unavailable)
                    {
                        // Изолированный воркер не поднимается после серии перезапусков:
                        // дальше плодить процессы и занимать чужие DDA-сессии нельзя.
                        Log.Error("Engine", $"Изолированный захват не восстановился: {unavailable.Message}");
                        EnterTerminalCaptureBlocked("воркер изолированного захвата не поднимается");
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
                FinishCaptureRecovery(cancellation);
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

    /// <summary>
    /// Перевести движок в терминальное состояние: захват в этом процессе не
    /// восстановится, но RAM-повтор цел и доступен для сохранения.
    ///
    /// Конвейер к этому моменту уже остановлен (StopLocked с CaptureRestart или
    /// CaptureBlocked), поэтому здесь только состояние и однократное сообщение.
    /// Самоперезапуск приложения не запускаем: RAM-буфер между процессами не
    /// переживёт, и человек должен сначала сохранить клип.
    /// </summary>
    private void EnterTerminalCaptureBlocked(in HeldCaptureOutput output) =>
        EnterTerminalCaptureBlocked($"выход {output.MonitorDeviceId} удерживает незавершившаяся сессия DDA " +
                                    $"(генерация {output.Generation})");

    /// <summary>
    /// Терминальное состояние по произвольной причине: изолированный воркер не
    /// поднимается, нативный вызов завис и т. п. Конвейер к этому моменту уже
    /// остановлен с сохранением буферов.
    /// </summary>
    private void EnterTerminalCaptureBlocked(string reason)
    {
        Interlocked.Exchange(ref _captureBlockRequested, 1);
        _captureBlockedReason = reason;
        SetState(EngineState.CaptureBlocked);
        NotifyCaptureBlockedOnce();
    }

    /// <summary>
    /// Один раз объяснить человеку, что случилось и что с этим делать. Повторные
    /// попытки включения не должны снова и снова начинать один и тот же DDA.
    /// </summary>
    private void NotifyCaptureBlockedOnce()
    {
        if (Interlocked.Exchange(ref _captureBlockedNotified, 1) != 0) return;
        Log.Warn("Engine", "Захват переведён в терминальную блокировку; накопленный RAM-повтор сохранён, " +
                           "автоматическое восстановление не повторяется до перезапуска Aura");
        Warning?.Invoke("Захват не восстановился. Накопленный повтор можно сохранить. " +
                        "Для продолжения перезапустите Aura.");
    }
}
