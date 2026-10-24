namespace Aura.Core.Capture;

public enum CaptureFailureKind
{
    DeviceLost,
    BackendUnavailable,
    BackendStalled,
    CaptureFormatChanged,
    BackendTransitionStorm,
    CaptureTargetClosed
}

public readonly record struct CaptureFailure(
    CaptureFailureKind Kind,
    Exception Error,
    string Reason,
    long Generation,
    long TargetRevision = 0);

/// <summary>
/// Мониторный кадр не совпал по размеру с текстурами брокера. CopyResource при
/// разных размерах молча ничего не копирует, и в запись шёл пустой (чёрный) слот
/// при «здоровых» счётчиках. Лечится только пересборкой конвейера под новый размер.
/// </summary>
internal sealed class CaptureFrameSizeMismatchException(int frameWidth, int frameHeight, int width, int height)
    : InvalidOperationException($"кадр {frameWidth}x{frameHeight}, а конвейер собран под {width}x{height}");

internal static class CaptureFailureClassifier
{
    public static bool IsDeviceLossHResult(int hresult) => hresult is
        unchecked((int)0x887A0005) or // DXGI_ERROR_DEVICE_REMOVED
        unchecked((int)0x887A0006) or // DXGI_ERROR_DEVICE_HUNG
        unchecked((int)0x887A0007) or // DXGI_ERROR_DEVICE_RESET
        unchecked((int)0x887A0020) or // DXGI_ERROR_DRIVER_INTERNAL_ERROR
        unchecked((int)0xC00D3E85);   // MF_E_D3D_DEVICE_LOSS
}
