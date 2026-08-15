using Vortice.DXGI;
using Aura.Core.Interop;

namespace Aura.Core.Capture;

/// <summary>
/// Где физически находятся мониторы и с каким масштабом.
///
/// Нужно оверлею выделения области: кадр приходит в физических пикселях от DXGI,
/// а окно WPF живёт в аппаратно-независимых. Без границ монитора и его DPI оверлей
/// на втором мониторе или при масштабе 125% ложится мимо.
///
/// Перечисление выходов здесь своё, а не из источника захвата: оверлею нельзя
/// зависеть от работающего конвейера — скриншот области делается и при выключенном
/// буфере. Порядок обхода тот же (адаптеры, внутри — выходы), поэтому индекс
/// монитора совпадает с тем, что выбран в настройках записи.
///
/// Список ненадолго кэшируется: снимок области спрашивает и монитор под курсором, и
/// границы этого монитора, то есть обходил бы адаптеры дважды подряд. Мониторы за
/// две секунды не переезжают, а смена конфигурации всё равно догонит следующий вызов.
/// </summary>
public static class MonitorLayout
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(2);
    private static readonly object Sync = new();
    private static List<(IntPtr Handle, NativeMethods.RECT Bounds)>? _cache;
    private static DateTime _cachedAt;

    /// <summary>Границы в физических пикселях и масштаб (1.0 = 96 DPI). Пусто — монитор не найден.</summary>
    public static (int X, int Y, int Width, int Height, double Scale)? For(int monitorIndex)
    {
        var monitors = Monitors();
        if (monitors.Count == 0) return null;

        var (handle, bounds) = monitors[Math.Clamp(monitorIndex, 0, monitors.Count - 1)];

        double scale = 1.0;
        // MDT_EFFECTIVE_DPI: сбой не критичен — просто останемся на 100%
        if (NativeMethods.GetDpiForMonitor(handle, 0, out uint dpiX, out _) == 0 && dpiX > 0)
            scale = dpiX / 96.0;

        return (bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, scale);
    }

    /// <summary>
    /// Монитор, на котором сейчас курсор. Скриншот должен сниматься там, где человек
    /// работает, а не там, куда настроена запись: настройка записи привязана к игре,
    /// а скриншот делают где угодно.
    /// null — определить не удалось, вызывающий останется на своём мониторе.
    /// </summary>
    public static int? IndexUnderCursor()
    {
        try
        {
            if (!NativeMethods.GetCursorPos(out var point)) return null;

            var monitors = Monitors();
            for (int i = 0; i < monitors.Count; i++)
            {
                var r = monitors[i].Bounds;
                if (point.X >= r.Left && point.X < r.Right && point.Y >= r.Top && point.Y < r.Bottom)
                    return i;
            }
        }
        catch (Exception ex)
        {
            Logging.Log.Warn("Capture", $"Не удалось определить монитор под курсором: {ex.Message}");
        }
        return null;
    }

    private static List<(IntPtr Handle, NativeMethods.RECT Bounds)> Monitors()
    {
        lock (Sync)
        {
            if (_cache is not null && DateTime.UtcNow - _cachedAt < CacheLifetime) return _cache;

            var list = new List<(IntPtr, NativeMethods.RECT)>();
            try
            {
                using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
                for (uint a = 0; factory.EnumAdapters1(a, out IDXGIAdapter1 adapter).Success; a++)
                {
                    using (adapter)
                        for (uint o = 0; adapter.EnumOutputs(o, out IDXGIOutput output).Success; o++)
                            using (output)
                            {
                                var description = output.Description;
                                var c = description.DesktopCoordinates;
                                list.Add((description.Monitor, new NativeMethods.RECT
                                {
                                    Left = c.Left, Top = c.Top, Right = c.Right, Bottom = c.Bottom
                                }));
                            }
                }
            }
            catch (Exception ex)
            {
                // Неполный список молча уводит запись на другой монитор: индексы
                // сдвигаются, и «второй монитор» в настройках указывает не туда.
                Logging.Log.Warn("Capture",
                    $"Перечисление мониторов оборвалось на {list.Count}-м: {ex.Message}");
            }

            _cache = list;
            _cachedAt = DateTime.UtcNow;
            return list;
        }
    }
}
