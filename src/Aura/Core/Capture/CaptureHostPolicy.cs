namespace Aura.Core.Capture;

/// <summary>
/// Когда мониторный DDA идёт через дочерний процесс <c>--capture-host</c>.
///
/// НА WINDOWS 10 — ПО УМОЛЧАНИЮ. Именно там нативный DDA не изолирован и
/// зависший вызов нельзя прервать иначе как выходом из процесса. На Windows 11
/// штатный путь — WGC, а DDA включается только диагностическим
/// <c>--capture dda</c>; изоляцию там можно включить переменной
/// <c>AURA_CAPTURE_HOST=1</c> (и выключить на Windows 10 через <c>=0</c>).
/// </summary>
internal static class CaptureHostPolicy
{
    internal static bool UseRemoteDda => UseRemoteDdaFor(
        Environment.OSVersion.Version.Build,
        Environment.GetEnvironmentVariable("AURA_CAPTURE_HOST"));

    internal static bool UseRemoteDdaFor(int windowsBuild, string? overrideValue) =>
        overrideValue?.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "on" => true,
            "0" or "false" or "off" => false,
            _ => windowsBuild < 22000
        };
}

/// <summary>
/// Воркер захвата не поднимается после серии перезапусков. Это не «ещё один
/// сбой»: бесконечно плодить процессы нельзя, поэтому движок обязан перейти в
/// честное терминальное состояние, сохранив накопленный RAM-повтор.
/// </summary>
internal sealed class RemoteCaptureHostUnavailableException : InvalidOperationException
{
    public RemoteCaptureHostUnavailableException(string reason)
        : base($"Захват экрана не восстановился: {reason}. Накопленный повтор можно сохранить. " +
               "Для продолжения перезапустите Aura.") { }
}

/// <summary>
/// Счётчик подряд идущих неудач изолированного захвата. Сбрасывается, как только
/// сессия реально отдала достаточно кадров: короткие падения при старте не должны
/// копиться вечно, а «поднимается и сразу умирает» — должны.
/// </summary>
internal static class RemoteCaptureFailureBudget
{
    public const int MaxConsecutiveFailures = 3;
    public const long HealthyFrames = 90;

    private static int s_consecutiveFailures;
    private static int s_unretiredWorkers;

    internal static int ConsecutiveFailures => Volatile.Read(ref s_consecutiveFailures);
    internal static bool Exhausted => ConsecutiveFailures >= MaxConsecutiveFailures;

    /// <summary>
    /// Воркер не подтвердил выход даже после принудительного завершения. Пока такой
    /// процесс жив, новый не запускаем: иначе плодятся зависшие воркеры и DDA-сессии.
    /// </summary>
    internal static bool HasUnretiredWorker => Volatile.Read(ref s_unretiredWorkers) > 0;

    internal static void NoteFailure() => Interlocked.Increment(ref s_consecutiveFailures);
    internal static void NoteUnretiredWorker() => Interlocked.Increment(ref s_unretiredWorkers);

    internal static void NoteHealthy(long framesCaptured)
    {
        if (framesCaptured >= HealthyFrames) Interlocked.Exchange(ref s_consecutiveFailures, 0);
    }

    internal static void ResetForTests()
    {
        Interlocked.Exchange(ref s_consecutiveFailures, 0);
        Interlocked.Exchange(ref s_unretiredWorkers, 0);
    }
}
