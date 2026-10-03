using System.Diagnostics;
using System.Runtime.InteropServices;
using Aura.Core.Logging;

namespace Aura.Core.Diagnostics;

/// <summary>
/// Проверка в --dev: остановить все потоки своего процесса на время, как будто
/// система перестала давать ему работать (нехватка памяти, подкачка). Между
/// остановкой и пуском поток ничего не выделяет и не логирует: остановленный
/// поток мог держать замок кучи или сборщика.
/// </summary>
internal static class ProcessFreeze
{
    public static void Run(int delayMs, int freezeMs)
    {
        Thread.Sleep(delayMs);
        uint self = GetCurrentThreadId();
        var ids = Process.GetCurrentProcess().Threads.Cast<ProcessThread>().Select(t => (uint)t.Id)
            .Where(id => id != self).ToArray();
        var handles = new IntPtr[ids.Length];
        for (int i = 0; i < ids.Length; i++) handles[i] = OpenThread(0x0002 /* SUSPEND_RESUME */, false, ids[i]);
        Log.Info("SelfTest", $"останавливаю {ids.Length} потоков на {freezeMs} мс");

        for (int i = 0; i < handles.Length; i++) if (handles[i] != IntPtr.Zero) SuspendThread(handles[i]);
        Sleep((uint)freezeMs);
        for (int i = 0; i < handles.Length; i++) if (handles[i] != IntPtr.Zero) ResumeThread(handles[i]);

        foreach (var h in handles) if (h != IntPtr.Zero) CloseHandle(h);
        Log.Info("SelfTest", "потоки снова идут");
    }

    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] private static extern IntPtr OpenThread(uint access, bool inherit, uint id);
    [DllImport("kernel32.dll")] private static extern uint SuspendThread(IntPtr thread);
    [DllImport("kernel32.dll")] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] private static extern void Sleep(uint ms);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
