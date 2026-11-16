using System.Diagnostics;
using System.Runtime.InteropServices;
using Aura.Core.Logging;

namespace Aura.Core.Diagnostics;

/// <summary>
/// Дамп процесса со стеками всех потоков, когда конвейер встал.
///
/// ЗАЧЕМ. По логу видно только, что поток сидит внутри вызова. Чего именно он
/// там ждёт (замок устройства D3D11, вызов ядра, CUDA), показывает один стек, и
/// другого способа узнать причину зависаний нет. Тот же дампер используется и
/// для зависшего потока Desktop Duplication: снимок фазы говорит, ГДЕ встал
/// поток, а стек — на чём именно внутри драйвера.
///
/// Пишет ВТОРОЙ процесс (Aura.exe --dump): так советует Microsoft. Дамп из самого
/// процесса останавливает все его потоки, и если один из них держит замок кучи,
/// пишущий поток встаёт навсегда вместе с программой.
/// </summary>
internal static class StallDump
{
    private const int MaxPerRun = 2, KeepFiles = 3;
    private static readonly TimeSpan MinIntervalBetweenRequests = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HelperTimeout = TimeSpan.FromSeconds(30);
    private static readonly object RequestSync = new();
    private static int _written;
    private static long _lastRequestTimestamp;

    public const int ExitOk = 0;
    public const int ExitTargetGone = 4;
    public const int ExitNotWritten = 3;

    /// <summary>
    /// Попросить дамп текущего процесса. Не ждёт и не бросает. Возвращает false,
    /// если запрос отклонён лимитом частоты или помощник не запустился.
    ///
    /// Факт записи файла проверяется в фоне по коду выхода помощника: сообщать
    /// «дамп записан» сразу после Process.Start нельзя — помощник может не успеть,
    /// упасть или получить отказ.
    /// </summary>
    public static bool Request(string reason, string? context = null)
    {
        // Помощник --dump есть только у Aura.exe. Из тестовых процессов (unity
        // self-check, xunit) запускать себя с этим аргументом нельзя: они начнут
        // выполнять свою программу вместо дампа.
        string exe = Environment.ProcessPath ?? "";
        if (!string.Equals(Path.GetFileNameWithoutExtension(exe), "Aura", StringComparison.OrdinalIgnoreCase))
        {
            Log.Warn("Engine", $"Дамп по причине «{reason}» не запрошен: помощник --dump есть только у Aura.exe");
            return false;
        }

        lock (RequestSync)
        {
            long now = Stopwatch.GetTimestamp();
            if (_lastRequestTimestamp != 0 &&
                Stopwatch.GetElapsedTime(_lastRequestTimestamp, now) < MinIntervalBetweenRequests)
            {
                Log.Info("Engine", $"Дамп по причине «{reason}» пропущен: предыдущий запрошен только что");
                return false;
            }
            if (_written >= MaxPerRun)
            {
                Log.Warn("Engine", $"Дамп по причине «{reason}» не запрошен: лимит {MaxPerRun} за запуск исчерпан");
                return false;
            }

            _lastRequestTimestamp = now;
            _written++;
        }

        try
        {
            string dir = Path.Combine(Settings.SettingsManager.Dir, "logs");
            Directory.CreateDirectory(dir);
            CleanupOldDumps(dir);

            string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            string path = Path.Combine(dir, $"stall-{stamp}.dmp");
            if (!string.IsNullOrWhiteSpace(context))
            {
                try { File.WriteAllText(Path.ChangeExtension(path, ".txt"), $"причина: {reason}\n{context}"); }
                catch (Exception ex) { Log.Warn("Engine", $"Контекст дампа не записан: {ex.Message}"); }
            }

            string helperExe = Environment.ProcessPath!;
            var start = new ProcessStartInfo(helperExe) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--dump");
            start.ArgumentList.Add(Environment.ProcessId.ToString());
            start.ArgumentList.Add(path);

            Process? helper = Process.Start(start);
            if (helper is null)
            {
                Log.Warn("Engine", $"Помощник дампа не запустился по причине «{reason}»");
                return false;
            }

            Log.Warn("Engine", $"Запрошен дамп потоков (причина: {reason}), PID помощника {helper.Id}: {path}");
            _ = Task.Run(() => AwaitHelper(helper, path, reason));
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("Engine", $"Дамп потоков не запущен: {ex.Message}");
            return false;
        }
    }

    private static void CleanupOldDumps(string dir)
    {
        try
        {
            var old = new DirectoryInfo(dir)
                .GetFiles("stall-*")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip((KeepFiles - 1) * 2)
                .ToList();
            foreach (var file in old)
                try { file.Delete(); } catch { }
        }
        catch { }
    }

    private static void AwaitHelper(Process helper, string path, string reason)
    {
        try
        {
            bool exited = helper.WaitForExit((int)HelperTimeout.TotalMilliseconds);
            int code;
            if (!exited)
            {
                code = -1;
                try { helper.Kill(); } catch { }
                Log.Warn("Engine", $"Помощник дампа не завершился за {HelperTimeout.TotalSeconds:F0} с по причине «{reason}»");
            }
            else
            {
                code = helper.ExitCode;
            }

            var file = new FileInfo(path);
            bool written = file.Exists && file.Length > 0;
            if (written)
                Log.Warn("Engine", $"Дамп потоков записан (причина: {reason}, код помощника {code}, " +
                                   $"{file.Length / (1024 * 1024)} МБ): {path}");
            else
                Log.Error("Engine", $"Дамп потоков НЕ записан (причина: {reason}, код помощника {code}, фаза: " +
                                    $"{(exited ? "помощник вышел" : "помощник убит")}): {path}");
        }
        catch (Exception ex)
        {
            Log.Warn("Engine", $"Не удалось проверить дамп: {ex.Message}");
        }
        finally
        {
            helper.Dispose();
        }
    }

    /// <summary>
    /// Тело помощника: записать дамп чужого процесса. Код выхода 0 — файл создан
    /// и не пуст; 2 — MiniDumpWriteDump отказал; 3 — файл не записался;
    /// 4 — процесс уже вышел; 5 — DbgHelp занят другим помощником.
    /// </summary>
    public static int Write(int processId, string path)
    {
        // MiniDumpWriteDump не потокобезопасен: два помощника одновременно (например,
        // DDA и NVENC) могут испортить дамп друг друга. Сериализуем через именованный
        // мьютекс текущего пользователя, а не глобальный: чужой сеанс сюда не нужен.
        using var dbgHelpGate = new Mutex(false, @"Local\Aura.StallDump.DbgHelp");
        bool owned = false;
        try
        {
            try { owned = dbgHelpGate.WaitOne(TimeSpan.FromSeconds(20)); }
            catch (AbandonedMutexException) { owned = true; }
            if (!owned) return 5;

            using Process target = Process.GetProcessById(processId);
            if (target.HasExited) return ExitTargetGone;
            using var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            // MiniDumpWithUnloadedModules (0x20) + MiniDumpWithProcessThreadData (0x100)
            // + MiniDumpWithThreadInfo (0x1000). Полный дамп (0x2) не пишем: он
            // занимает гигабайты, а для стека зависшего потока не нужен.
            const uint type = 0x00000020 | 0x00000100 | 0x00001000;
            if (!MiniDumpWriteDump(target.Handle, (uint)processId, file.SafeFileHandle, type, 0, 0, 0))
                return 2;
            file.Flush(flushToDisk: true);
            var info = new FileInfo(path);
            return info.Exists && info.Length > 0 ? ExitOk : ExitNotWritten;
        }
        catch
        {
            return 1;
        }
        finally
        {
            if (owned)
            {
                try { dbgHelpGate.ReleaseMutex(); } catch { }
            }
        }
    }

    [DllImport("dbghelp.dll", SetLastError = true)]
    private static extern bool MiniDumpWriteDump(nint process, uint processId, SafeHandle file, uint type,
                                                 nint exception, nint userStream, nint callback);
}
