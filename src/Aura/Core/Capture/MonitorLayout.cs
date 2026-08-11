using Vortice.DXGI;
using Aura.Core.Interop;

namespace Aura.Core.Capture;

/// <summary>
/// Где физически находится монитор с заданным индексом и с каким масштабом.
///
/// Нужно оверлею выделения области: кадр приходит в физических пикселях от DXGI,
/// а окно WPF живёт в аппаратно-независимых. Без границ монитора и его DPI оверлей
/// на втором мониторе или при масштабе 125% ложится мимо.
///
/// Перечисление выходов здесь своё, а не из источника захвата: оверлею нельзя
/// зависеть от работающего конвейера — скриншот области делается и при выключенном
/// буфере. Порядок обхода тот же (адаптеры, внутри — выходы), поэтому индекс
/// монитора совпадает с тем, что выбран в настройках записи.
/// </summary>
public static class MonitorLayout
{
    /// <summary>Границы в физических пикселях и масштаб (1.0 = 96 DPI). Пусто — монитор не найден.</summary>
    public static (int X, int Y, int Width, int Height, double Scale)? For(int monitorIndex)
    {
        IntPtr handle = HandleFor(monitorIndex);
        if (handle == IntPtr.Zero) return null;

        var info = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfoW(handle, ref info)) return null;

        double scale = 1.0;
        // MDT_EFFECTIVE_DPI: сбой не критичен — просто останемся на 100%
        if (NativeMethods.GetDpiForMonitor(handle, 0, out uint dpiX, out _) == 0 && dpiX > 0)
            scale = dpiX / 96.0;

        var r = info.rcMonitor;
        return (r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, scale);
    }

    private static IntPtr HandleFor(int monitorIndex)
    {
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            var monitors = new List<IntPtr>();
            for (uint a = 0; factory.EnumAdapters1(a, out IDXGIAdapter1 adapter).Success; a++)
            {
                using (adapter)
                    for (uint o = 0; adapter.EnumOutputs(o, out IDXGIOutput output).Success; o++)
                        using (output) monitors.Add(output.Description.Monitor);
            }
            if (monitors.Count == 0) return IntPtr.Zero;
            return monitors[Math.Clamp(monitorIndex, 0, monitors.Count - 1)];
        }
        catch { return IntPtr.Zero; }
    }
}
