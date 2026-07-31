using Vortice.Direct3D11;
using Vortice.DXGI;
using Aura.Core.Logging;

namespace Aura.Core.Capture;

/// <summary>
/// Приоритет GPU-очереди нашего устройства.
///
/// Зачем: захват и энкодер живут на одном D3D-устройстве, и под 100% загрузкой GPU
/// игрой наши команды (BGRA→NV12 и копия в пул энкодера) стоят в общей очереди
/// последними. Пока они ждут, колбэк WGC не успевает выгребать кадры из пула, WGC
/// молча их дропает — в записи вместо реального движения остаются дубликаты пейсера
/// (в замерах 1600 настоящих кадров в минуту из 3600).
///
/// Приоритет НЕ добавляет работы: он меняет только очерёдность. Наша работа на кадр —
/// доли миллисекунды, поэтому берём +2, а не потолок +7: заметно поднять нас над
/// обычной очередью, но не отбирать у игры больше, чем она и так нам отдаёт.
///
/// Значения выше нуля система вправе проигнорировать у непривилегированного процесса —
/// поэтому ошибка здесь не фатальна, просто пишем в лог.
/// </summary>
internal static class GpuPriority
{
    private const int CapturePriority = 2;

    public static void TryRaise(ID3D11Device device)
    {
        try
        {
            using var dxgi = device.QueryInterface<IDXGIDevice>();
            dxgi.SetGPUThreadPriority(CapturePriority);
            dxgi.GetGPUThreadPriority(out int applied);
            if (applied == CapturePriority)
                Log.Info("Capture", $"Приоритет GPU-очереди захвата поднят до {applied}");
            else
                Log.Info("Capture", $"Система не приняла приоритет GPU-очереди (осталось {applied})");
        }
        catch (Exception ex)
        {
            Log.Warn("Capture", $"Приоритет GPU-очереди недоступен: {ex.Message}");
        }
    }
}
