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
    public static bool IsDeviceLost(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            int code = e is SharpGen.Runtime.SharpGenException sharpGen
                ? sharpGen.ResultCode.Code
                : e.HResult;
            if (CaptureFailureClassifier.IsDeviceLossHResult(code))
                return true;
        }
        return false;
    }
}
