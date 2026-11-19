using System.Diagnostics;
using System.Runtime.InteropServices;
using Aura.Core.Interop;

namespace Aura.Core.SystemIntegration;

/// <summary>
/// Мгновенный снимок доступности интерактивного рабочего стола пользователя:
/// текущий рабочий стол ввода (<c>Default</c>, не <c>Winlogon</c>), флаг
/// блокировки сеанса WTS и наличие окна оболочки (<c>GetShellWindow</c>).
/// </summary>
internal readonly record struct DesktopSessionSnapshot(
    bool DefaultInputDesktop,
    bool? SessionLocked,
    bool ShellWindowPresent)
{
    /// <summary>
    /// На экране активный пользовательский рабочий стол, а сеанс не заперт.
    /// Неизвестный флаг WTS (<see langword="null"/>) не считается блокировкой,
    /// пока рабочий стол ввода точно <c>Default</c>.
    /// </summary>
    public bool InteractiveDesktopActive => DefaultInputDesktop && SessionLocked != true;
}

/// <summary>
/// Проверка текущего рабочего стола ввода и состояния блокировки сеанса Windows.
/// </summary>
internal static class DesktopSession
{
    private const int MinimumWtsInfoExHeaderBytes = 20;

    /// <summary>
    /// Ввод сейчас идёт на обычный рабочий стол (<c>Default</c>), а не на
    /// защищённый рабочий стол (<c>Winlogon</c>, экран блокировки, UAC, Ctrl+Alt+Del).
    /// При отказе <c>OpenInputDesktop</c> или ошибке чтения имени возвращает <see langword="false"/>.
    /// </summary>
    public static unsafe bool IsDefaultInputDesktop()
    {
        IntPtr desktop = NativeMethods.OpenInputDesktop(0, false, NativeMethods.DESKTOP_READOBJECTS);
        if (desktop == IntPtr.Zero) return false;
        try
        {
            char* buffer = stackalloc char[64];
            new Span<char>(buffer, 64).Clear();
            if (!NativeMethods.GetUserObjectInformation(desktop, NativeMethods.UOI_NAME, buffer, 64 * sizeof(char), out _))
                return false;

            ReadOnlySpan<char> span = new(buffer, 64);
            int nullIndex = span.IndexOf('\0');
            if (nullIndex >= 0) span = span[..nullIndex];
            return span.Equals("Default".AsSpan(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            NativeMethods.CloseDesktop(desktop);
        }
    }

    internal static bool? ParseWtsSessionLocked(uint level, int sessionState, int sessionFlags, uint bytesReturned)
    {
        if (bytesReturned < MinimumWtsInfoExHeaderBytes || level != 1) return null;
        if (sessionState != NativeMethods.WTS_ACTIVE) return true;
        if (sessionFlags == NativeMethods.WTS_SESSIONSTATE_LOCK) return true;
        if (sessionFlags == NativeMethods.WTS_SESSIONSTATE_UNLOCK) return false;
        return null;
    }

    /// <summary>
    /// Заперт ли текущий сеанс по данным WTS (<c>WTSSessionInfoEx</c>).
    /// Возвращает <see langword="null"/>, если состояние неизвестно или API недоступно.
    /// </summary>
    public static bool? QuerySessionLocked()
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (!NativeMethods.WTSQuerySessionInformationW(
                    NativeMethods.WTS_CURRENT_SERVER_HANDLE,
                    NativeMethods.WTS_CURRENT_SESSION,
                    NativeMethods.WTSSessionInfoEx,
                    out buffer,
                    out uint bytesReturned) ||
                buffer == IntPtr.Zero)
            {
                return null;
            }

            if (bytesReturned < MinimumWtsInfoExHeaderBytes) return null;
            var header = Marshal.PtrToStructure<NativeMethods.WTSINFOEX_HEADER>(buffer);
            return ParseWtsSessionLocked(header.Level, header.SessionState, header.SessionFlags, bytesReturned);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero) NativeMethods.WTSFreeMemory(buffer);
        }
    }

    public static DesktopSessionSnapshot Probe() =>
        new(IsDefaultInputDesktop(), QuerySessionLocked(), NativeMethods.GetShellWindow() != IntPtr.Zero);

    public static bool IsInteractiveDesktopActive() =>
        IsDefaultInputDesktop() && QuerySessionLocked() != true;
}

internal enum DesktopReadinessOutcome
{
    WaitingForInteractiveDesktop,
    WaitingForShell,
    Settling,
    Ready,
    ReadyWithoutShell
}

/// <summary>
/// Детерминированный автомат готовности рабочего стола перед запуском захвата WGC.
/// </summary>
internal sealed class DesktopReadinessTracker
{
    private readonly bool _requireShell;
    private readonly TimeSpan _shellTimeout;
    private readonly TimeSpan _settleDuration;
    private readonly bool _settleIfInitiallyReady;
    private bool _initialized;
    private bool _neededWait;
    private TimeSpan? _shellWaitStartedAt;
    private TimeSpan? _stableReadySince;

    public DesktopReadinessTracker(
        bool requireShell,
        TimeSpan shellTimeout,
        TimeSpan settleDuration,
        bool settleIfInitiallyReady = false)
    {
        _requireShell = requireShell;
        _shellTimeout = shellTimeout < TimeSpan.Zero ? TimeSpan.Zero : shellTimeout;
        _settleDuration = settleDuration < TimeSpan.Zero ? TimeSpan.Zero : settleDuration;
        _settleIfInitiallyReady = settleIfInitiallyReady;
    }

    public bool NeededWait => _neededWait;

    public TimeSpan? RemainingSettle(TimeSpan elapsed) =>
        _stableReadySince is { } since && _settleDuration > TimeSpan.Zero
            ? _settleDuration - (elapsed - since)
            : null;

    public DesktopReadinessOutcome Observe(in DesktopSessionSnapshot snapshot, TimeSpan elapsed)
    {
        bool firstObservation = !_initialized;
        _initialized = true;

        if (!snapshot.InteractiveDesktopActive)
        {
            _neededWait = true;
            _shellWaitStartedAt = null;
            _stableReadySince = null;
            return DesktopReadinessOutcome.WaitingForInteractiveDesktop;
        }

        bool readyWithoutShell = false;
        if (_requireShell && !snapshot.ShellWindowPresent)
        {
            _shellWaitStartedAt ??= elapsed;
            if (elapsed - _shellWaitStartedAt.Value < _shellTimeout)
            {
                _neededWait = true;
                _stableReadySince = null;
                return DesktopReadinessOutcome.WaitingForShell;
            }
            readyWithoutShell = true;
        }
        else
        {
            _shellWaitStartedAt = null;
        }

        if (firstObservation && !_settleIfInitiallyReady)
            return readyWithoutShell ? DesktopReadinessOutcome.ReadyWithoutShell : DesktopReadinessOutcome.Ready;

        if (_settleDuration > TimeSpan.Zero && (_neededWait || _settleIfInitiallyReady))
        {
            _stableReadySince ??= elapsed;
            if (elapsed - _stableReadySince.Value < _settleDuration)
            {
                _neededWait = true;
                return DesktopReadinessOutcome.Settling;
            }
        }

        return readyWithoutShell ? DesktopReadinessOutcome.ReadyWithoutShell : DesktopReadinessOutcome.Ready;
    }
}

/// <summary>
/// Потокобезопасный импульс пробуждения для ожиданий готовности рабочего стола.
/// </summary>
internal sealed class DesktopWakeSignal
{
    private volatile TaskCompletionSource _tcs = NewSignal();

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Capture() => _tcs.Task;

    public void Pulse()
    {
        TaskCompletionSource previous = Interlocked.Exchange(ref _tcs, NewSignal());
        previous.TrySetResult();
    }

    public Task WaitAsync(TimeSpan timeout, CancellationToken ct) =>
        WaitAsync(Capture(), timeout, ct);

    public async Task WaitAsync(Task signal, TimeSpan timeout, CancellationToken ct)
    {
        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task delayTask = Task.Delay(timeout, delayCts.Token);
        Task completed = await Task.WhenAny(signal, delayTask).ConfigureAwait(false);
        if (ReferenceEquals(completed, signal))
        {
            delayCts.Cancel();
            return;
        }

        await delayTask.ConfigureAwait(false);
    }
}

internal readonly record struct DesktopWaitResult(
    DesktopReadinessOutcome Outcome,
    TimeSpan Elapsed,
    bool Waited,
    bool Aborted);

internal static class DesktopReadinessWaiter
{
    public static async Task<DesktopWaitResult> WaitAsync(
        Func<DesktopSessionSnapshot> probe,
        bool requireShell,
        TimeSpan shellTimeout,
        TimeSpan settleDuration,
        TimeSpan pollInterval,
        bool settleIfInitiallyReady = false,
        Action<DesktopReadinessOutcome>? onFirstWait = null,
        Func<bool>? shouldAbort = null,
        DesktopWakeSignal? wake = null,
        Func<TimeSpan>? clock = null,
        CancellationToken ct = default)
    {
        Stopwatch? sw = clock is null ? Stopwatch.StartNew() : null;
        TimeSpan Now() => clock is not null ? clock() : sw!.Elapsed;

        var tracker = new DesktopReadinessTracker(requireShell, shellTimeout, settleDuration, settleIfInitiallyReady);
        DesktopReadinessOutcome lastOutcome = DesktopReadinessOutcome.WaitingForInteractiveDesktop;
        bool reportedWait = false;

        try
        {
            while (true)
            {
                TimeSpan now = Now();
                if (ct.IsCancellationRequested || shouldAbort?.Invoke() == true)
                    return new DesktopWaitResult(lastOutcome, now, tracker.NeededWait, Aborted: true);

                Task? wakeSignal = wake?.Capture();
                DesktopSessionSnapshot snapshot = probe();
                lastOutcome = tracker.Observe(in snapshot, now);
                if (lastOutcome is DesktopReadinessOutcome.Ready or DesktopReadinessOutcome.ReadyWithoutShell)
                    return new DesktopWaitResult(lastOutcome, now, tracker.NeededWait, Aborted: false);

                if (!reportedWait)
                {
                    reportedWait = true;
                    onFirstWait?.Invoke(lastOutcome);
                }

                TimeSpan step = pollInterval < TimeSpan.Zero ? TimeSpan.Zero : pollInterval;
                if (lastOutcome == DesktopReadinessOutcome.Settling)
                {
                    TimeSpan cap = TimeSpan.FromMilliseconds(150);
                    if (step > cap) step = cap;
                    if (tracker.RemainingSettle(now) is { } remaining && remaining > TimeSpan.Zero && remaining < step)
                        step = remaining;
                }

                if (wake is not null)
                    await wake.WaitAsync(wakeSignal!, step, ct).ConfigureAwait(false);
                else
                    await Task.Delay(step, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new DesktopWaitResult(lastOutcome, Now(), tracker.NeededWait, Aborted: true);
        }
    }
}
