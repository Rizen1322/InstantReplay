using System.Collections.Concurrent;

namespace Aura.Core.Logging;

/// <summary>
/// Простой потокобезопасный файловый логгер с фоновой записью.
/// Логи: %LocalAppData%\Aura\logs\app-YYYY-MM-DD.log (ротация 7 дней).
///
/// Два решения, оплаченных практикой:
///
/// 1. ФАЙЛ ДЕРЖИТСЯ ОТКРЫТЫМ. Раньше каждая строка была отдельным
///    File.AppendAllText — то есть открытие, запись и закрытие файла на строку.
///    Пока лог редкий, это незаметно; в шторме ошибок захвата (двадцать записей в
///    секунду, каждая со стеком) — сотни файловых операций в секунду на ровном месте.
///
/// 2. ПОВТОРЫ СХЛОПЫВАЮТСЯ. Ошибка в цикле пишется один раз, а дальше копится
///    счётчик: «то же сообщение ещё N раз». Иначе один застрявший вызов DXGI
///    заполняет мегабайты и прячет всё остальное — ровно это и случилось, когда
///    дупликация начала отвечать DXGI_ERROR_INVALID_CALL на каждый кадр.
/// </summary>
public static class Log
{
    private static readonly BlockingCollection<string> Queue = new(4096);
    private static string _dir = "";

    /// <summary>Через сколько выбрасывать накопленный счётчик повторов, даже если они продолжаются.</summary>
    private static readonly TimeSpan RepeatFlush = TimeSpan.FromSeconds(5);

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
        // Очередь не бесконечна: при шторме лучше потерять строку, чем притормозить
        // поток захвата на ожидании места.
        Queue.TryAdd(line);
    }

    private static void Writer()
    {
        StreamWriter? writer = null;
        string openedFor = "";

        // Схлопывание повторов: сравниваем строку без метки времени
        string lastBody = "";
        int repeats = 0;
        DateTime repeatSince = DateTime.MinValue;

        try
        {
            foreach (var line in Queue.GetConsumingEnumerable())
            {
                try
                {
                    string today = DateTime.Now.ToString("yyyy-MM-dd");
                    if (writer is null || today != openedFor)
                    {
                        writer?.Dispose();
                        // Полное имя: внутри Aura.Core короткое Encoding — это наше
                        // пространство имён с кодировщиком видео, а не System.Text
                        writer = new StreamWriter(Path.Combine(_dir, $"app-{today}.log"), append: true,
                                                  System.Text.Encoding.UTF8)
                        {
                            AutoFlush = false
                        };
                        openedFor = today;
                    }

                    string body = Body(line);
                    if (body == lastBody)
                    {
                        repeats++;
                        if (DateTime.UtcNow - repeatSince < RepeatFlush) continue;
                        WriteRepeats(writer, ref repeats);
                        repeatSince = DateTime.UtcNow;
                        continue;
                    }

                    WriteRepeats(writer, ref repeats);
                    lastBody = body;
                    repeatSince = DateTime.UtcNow;
                    writer.WriteLine(line);

                    // Сбрасываем на диск, когда очередь опустела: под нагрузкой строки
                    // копятся в буфере, в покое лог всегда актуален.
                    if (Queue.Count == 0) writer.Flush();
                }
                catch { /* не роняем приложение из-за лога */ }
            }
        }
        finally { try { writer?.Dispose(); } catch { } }
    }

    private static void WriteRepeats(StreamWriter writer, ref int repeats)
    {
        if (repeats == 0) return;
        writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [INF] [Log] ↑ то же сообщение ещё {repeats} раз");
        repeats = 0;
    }

    /// <summary>Строка без метки времени — по ней и сравниваем повторы.</summary>
    private static string Body(string line) => line.Length > 13 ? line[13..] : line;
}
