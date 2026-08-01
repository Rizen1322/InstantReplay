using System.Collections.Concurrent;

namespace Aura.Core.Logging;

/// <summary>
/// Простой потокобезопасный файловый логгер с фоновой записью.
/// Логи: %LocalAppData%\Aura\logs\app-YYYY-MM-DD.log (ротация 7 дней).
/// </summary>
public static class Log
{
    private static readonly BlockingCollection<string> Queue = new(4096);
    private static string _dir = "";

    public static void Init()
    {
        _dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "Aura", "logs");
        Directory.CreateDirectory(_dir);

        foreach (var f in Directory.EnumerateFiles(_dir, "app-*.log"))
            try { if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-7)) File.Delete(f); } catch { }

        var t = new Thread(Writer) { IsBackground = true, Name = "LogWriter" };
        t.Start();
        Info("App", $"===== Instant Replay запущен, PID {Environment.ProcessId} =====");
    }

    /// <summary>
    /// Запись НАПРЯМУЮ в файл, минуя фоновую очередь.
    ///
    /// Обычные строки пишет отдельный поток, и при аварийном завершении последние из
    /// них теряются — именно поэтому падения приложения не оставляли в логе ни следа.
    /// Для предсмертных сообщений это неприемлемо: пишем сразу, пусть и медленнее.
    /// </summary>
    public static void Fatal(string tag, string msg)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [FTL] [{tag}] {msg}";
        System.Diagnostics.Debug.WriteLine(line);
        try
        {
            if (_dir.Length > 0)
                File.AppendAllText(Path.Combine(_dir, $"app-{DateTime.Now:yyyy-MM-dd}.log"),
                                   line + Environment.NewLine);
        }
        catch { }
    }

    public static void Info(string tag, string msg)  => Enqueue("INF", tag, msg);
    public static void Warn(string tag, string msg)  => Enqueue("WRN", tag, msg);
    public static void Error(string tag, string msg) => Enqueue("ERR", tag, msg);
    public static void Error(string tag, Exception ex) => Enqueue("ERR", tag, ex.ToString());

    private static void Enqueue(string lvl, string tag, string msg)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{lvl}] [{tag}] {msg}";
        System.Diagnostics.Debug.WriteLine(line);
        Queue.TryAdd(line);
    }

    private static void Writer()
    {
        foreach (var line in Queue.GetConsumingEnumerable())
        {
            try
            {
                File.AppendAllText(Path.Combine(_dir, $"app-{DateTime.Now:yyyy-MM-dd}.log"), line + Environment.NewLine);
            }
            catch { /* не роняем приложение из-за лога */ }
        }
    }
}
