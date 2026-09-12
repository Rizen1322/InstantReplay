namespace Aura.Core.Capture;

public enum CaptureFailureKind
{
    DeviceLost,
    BackendUnavailable,
    BackendStalled,
    CaptureFormatChanged,
    BackendTransitionStorm
}

public readonly record struct CaptureFailure(
    CaptureFailureKind Kind,
    Exception Error,
    string Reason,
    long Generation);

internal static class CaptureFailureClassifier
{
    public static bool IsDeviceLossHResult(int hresult) => hresult is
        unchecked((int)0x887A0005) or // DXGI_ERROR_DEVICE_REMOVED
        unchecked((int)0x887A0006) or // DXGI_ERROR_DEVICE_HUNG
        unchecked((int)0x887A0007) or // DXGI_ERROR_DEVICE_RESET
        unchecked((int)0x887A0020) or // DXGI_ERROR_DRIVER_INTERNAL_ERROR
        unchecked((int)0xC00D3E85);   // MF_E_D3D_DEVICE_LOSS
}
