using System.Diagnostics;
using System.Runtime.InteropServices;
using Aura.Core.Logging;

namespace Aura.Core.Diagnostics;

/// <summary>
/// Дамп процесса со стеками всех потоков, когда энкодер встал.
///
/// ЗАЧЕМ. По логу видно только, что поток сидит внутри вызова NVENC. Чего именно он
/// там ждёт (замок устройства D3D11, вызов ядра, CUDA), показывает один стек, и
/// другого способа узнать причину зависаний с AQ нет.
///
/// Пишет ВТОРОЙ процесс (Aura.exe --dump): так советует Microsoft. Дамп из самого
/// процесса останавливает все его потоки, и если один из них держит замок кучи,
/// пишущий поток встаёт навсегда вместе с программой.
/// </summary>
internal static class StallDump
{
    private const int MaxPerRun = 2, KeepFiles = 3;
    private static int _written;

    /// <summary>Попросить дамп текущего процесса. Не ждёт и не бросает.</summary>
    public static void Request()
    {
        if (Interlocked.Increment(ref _written) > MaxPerRun) return;
        try
        {
            string dir = Path.Combine(Settings.SettingsManager.Dir, "logs");
            Directory.CreateDirectory(dir);
            foreach (var old in new DirectoryInfo(dir).GetFiles("stall-*.dmp")
                         .OrderByDescending(f => f.LastWriteTimeUtc).Skip(KeepFiles - 1))
                try { old.Delete(); } catch { }

            string path = Path.Combine(dir, $"stall-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.dmp");
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--dump");
            start.ArgumentList.Add(Environment.ProcessId.ToString());
            start.ArgumentList.Add(path);
            using var helper = Process.Start(start);
            Log.Warn("Engine", $"Дамп потоков для разбора зависания: {path}");
        }
        catch (Exception ex) { Log.Warn("Engine", $"Дамп потоков не запущен: {ex.Message}"); }
    }

    /// <summary>Тело помощника: записать дамп чужого процесса. Код выхода 0 — записан.</summary>
    public static int Write(int processId, string path)
    {
        try
        {
            using Process target = Process.GetProcessById(processId);
            using var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            // MiniDumpNormal (стеки и модули) + выгруженные модули + времена потоков
            const uint type = 0x00000020 | 0x00001000;
            return MiniDumpWriteDump(target.Handle, (uint)processId, file.SafeFileHandle, type, 0, 0, 0) ? 0 : 2;
        }
        catch { return 1; }
    }

    [DllImport("dbghelp.dll", SetLastError = true)]
    private static extern bool MiniDumpWriteDump(nint process, uint processId, SafeHandle file, uint type,
                                                 nint exception, nint userStream, nint callback);
}
