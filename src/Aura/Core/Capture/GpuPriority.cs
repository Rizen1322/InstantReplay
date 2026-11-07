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
/// доли миллисекунды, поэтому берём +2, а не потолок +7. Потолок (как у OBS)
/// пробовали в 1.1.111: заметной выгоды не было, а зависание в CS2 в ту сессию
/// поначалу списали на него — позже выяснилось, что виновата повторная блокировка
/// выхода NVENC (см. aura_nvenc_get). +2 проверен долгими прогонами под нагрузкой.
///
/// Значения выше нуля система вправе проигнорировать у непривилегированного процесса —
/// поэтому ошибка здесь не фатальна, просто пишем в лог.
/// </summary>
/// <summary>
/// Класс процесса не ниже Normal и без EcoQoS. Пойманный в игре процесс Aura был
/// в Idle с включённым режимом эффективности: копия кадра WGC шла 84 мс и до 40 к/с,
/// а отдельный процесс с теми же вызовами в тот же момент получал 97 к/с за 10 мс.
/// OBS работает в Normal. Запущенную из Планировщика Aura опускают в Idle уже после
/// старта (кто — не нашли), поэтому проверяем ещё и раз в минуту.
/// </summary>
internal static class ProcessQos
{
    public static void Ensure(bool atStart = false)
    {
        try
        {
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            var was = self.PriorityClass;
            bool low = was is System.Diagnostics.ProcessPriorityClass.Idle or System.Diagnostics.ProcessPriorityClass.BelowNormal;
            if (low) self.PriorityClass = System.Diagnostics.ProcessPriorityClass.Normal;
            var state = new Interop.NativeMethods.PROCESS_POWER_THROTTLING_STATE { Version = 1, ControlMask = 1, StateMask = 0 };
            bool eco = Interop.NativeMethods.SetProcessInformation(self.Handle, 4, ref state,
                System.Runtime.InteropServices.Marshal.SizeOf<Interop.NativeMethods.PROCESS_POWER_THROTTLING_STATE>());
            if (atStart || low)
                Log.Info("App", $"Класс процесса{(atStart ? "" : " опущен снаружи")}: {was} → {self.PriorityClass}, EcoQoS {(eco ? "выключен" : "не выключился")}");
        }
        catch (Exception ex) { Log.Warn("App", $"Класс процесса не поднят: {ex.Message}"); }
    }
}

/// <summary>
/// Приоритет GPU-очереди устройства: абсолютный 30, как у OBS (SetGPUThreadPriority(0x4000001E)
/// и SeIncreaseBasePriorityPrivilege при старте, frontend/obs-main.cpp). С +2 в RE4 копия
/// кадра ждала игру 130–160 мс (33–43 к/с); отдельный процесс с абсолютным 30 в ту же
/// минуту получал 97 к/с за 10 мс. Вылеты RE4 7–8 ноября вызывал не он, а убийство хоста
/// NVENC (см. RemoteNvencSession.Kill). Без прав администратора — прежний +2.
/// </summary>
internal static class GpuPriority
{
    private const int CapturePriority = 2;
    private const int FallbackPriority = 1;
    private const int AbsoluteRealtime = 0x4000001E;

    private static int _privilegeTried;
    private static void EnableIncreasePriorityPrivilege()
    {
        if (Interlocked.Exchange(ref _privilegeTried, 1) != 0) return;
        if (!OpenProcessToken(System.Diagnostics.Process.GetCurrentProcess().Handle, 0x0020 | 0x0008, out IntPtr token)) return;
        try
        {
            if (!LookupPrivilegeValueW(null, "SeIncreaseBasePriorityPrivilege", out long luid)) return;
            var tp = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 2 };   // SE_PRIVILEGE_ENABLED
            AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally { CloseHandle(token); }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 4)]
    private struct TokenPrivileges { public int Count; public long Luid; public int Attributes; }
    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, int access, out IntPtr token);
    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool LookupPrivilegeValueW(string? system, string name, out long luid);
    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges state, int length, IntPtr previous, IntPtr returnLength);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    public static void TryRaise(ID3D11Device device)
    {
        try
        {
            using var dxgi = device.QueryInterface<IDXGIDevice>();
            EnableIncreasePriorityPrivilege();
            if (dxgi.SetGPUThreadPriority(AbsoluteRealtime).Success)
            {
                Log.Info("Capture", "Приоритет GPU-очереди: абсолютный 30, как у OBS");
                return;
            }
            dxgi.SetGPUThreadPriority(CapturePriority);
            dxgi.GetGPUThreadPriority(out int applied);
            if (applied != CapturePriority)
            {
                // Без прав администратора +7 не дают — берём что дадут
                dxgi.SetGPUThreadPriority(FallbackPriority);
                dxgi.GetGPUThreadPriority(out applied);
            }
            if (applied > 0)
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
