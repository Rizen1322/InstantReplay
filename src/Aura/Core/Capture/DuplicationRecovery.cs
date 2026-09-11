namespace Aura.Core.Capture;

internal enum DuplicationRecoveryStatus
{
    Restored,
    Stopped,
    Failed
}

internal readonly record struct DuplicationRecoveryResult(
    DuplicationRecoveryStatus Status,
    Exception? Error = null);

/// <summary>
/// Управляет повторным созданием Desktop Duplication. Вынесено из DXGI-цикла,
/// чтобы последовательность «временный отказ → повтор → успех/остановка» проверялась
/// без настоящего монитора и D3D-устройства.
/// </summary>
internal static class DuplicationRecovery
{
    public const int ModeChangeDelayMilliseconds = 200;
    public const int RetryDelayMilliseconds = 500;

    public static bool IsTemporaryHResult(int hresult) => hresult is
        unchecked((int)0x80070005) or // E_ACCESSDENIED
        unchecked((int)0x887A0004) or // DXGI_ERROR_UNSUPPORTED (desktop mode)
        unchecked((int)0x887A0022) or // DXGI_ERROR_NOT_CURRENTLY_AVAILABLE
        unchecked((int)0x887A0025) or // DXGI_ERROR_MODE_CHANGE_IN_PROGRESS
        unchecked((int)0x887A0026) or // DXGI_ERROR_ACCESS_LOST
        unchecked((int)0x887A0028);   // DXGI_ERROR_SESSION_DISCONNECTED

    public static DuplicationRecoveryResult Run(
        Func<bool> isRunning,
        Action resetCurrent,
        Action create,
        Action<int> delay,
        Func<Exception, bool> isTemporary,
        Action<Exception>? temporaryFailure = null)
    {
        while (isRunning())
        {
            resetCurrent();
            delay(ModeChangeDelayMilliseconds);
            if (!isRunning())
                return new(DuplicationRecoveryStatus.Stopped);

            try
            {
                create();
                return new(DuplicationRecoveryStatus.Restored);
            }
            catch (Exception ex)
            {
                if (!isTemporary(ex))
                    return new(DuplicationRecoveryStatus.Failed, ex);

                temporaryFailure?.Invoke(ex);
                delay(RetryDelayMilliseconds);
            }
        }

        return new(DuplicationRecoveryStatus.Stopped);
    }
}
