namespace Aura.Core.Capture;

/// <summary>
/// Распознавание потери GPU-устройства: сброс драйвера (TDR), обновление драйвера
/// на ходу, переключение GPU, зависание. После такого события ВСЕ объекты D3D11
/// (устройство, VideoProcessor, MFT-энкодер) мертвы навсегда — их нельзя «починить»,
/// конвейер нужно собрать заново с нового устройства.
///
/// Раньше этого не было: захват молчал, а обработчик кадров бесконечно писал в лог
/// «кадр пропущен», и запись не возвращалась до ручного перезапуска приложения.
/// </summary>
internal static class DeviceLoss
{
    // HRESULT'ы DXGI. Держим числами, а не константами обёртки: одни и те же коды
    // прилетают и из SharpGen-исключений Vortice, и из COMException в WinRT-путях.
    private const int DxgiErrorDeviceRemoved = unchecked((int)0x887A0005);
    private const int DxgiErrorDeviceHung = unchecked((int)0x887A0006);
    private const int DxgiErrorDeviceReset = unchecked((int)0x887A0007);
    private const int DxgiErrorDriverInternalError = unchecked((int)0x887A0020);
    /// <summary>MF_E_D3D_DEVICE_LOSS — то же самое, но со стороны Media Foundation.</summary>
    private const int MfErrorD3DDeviceLoss = unchecked((int)0xC00D3E85);

    public static bool IsDeviceLost(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            int code = e is SharpGen.Runtime.SharpGenException sharpGen
                ? sharpGen.ResultCode.Code
                : e.HResult;
            if (code is DxgiErrorDeviceRemoved or DxgiErrorDeviceHung or DxgiErrorDeviceReset
                     or DxgiErrorDriverInternalError or MfErrorD3DDeviceLoss)
                return true;
        }
        return false;
    }
}
