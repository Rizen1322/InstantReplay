using System.Collections.Concurrent;

namespace Aura.Core.Logging;

/// <summary>
/// Простой потокобезопасный файловый логгер с фоновой записью.
/// Логи: %LocalAppData%\Aura\logs\app-YYYY-MM-DD.log (ротация 7 дней).
///
/// Три решения, оплаченных практикой:
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
///
/// 3. ФАЙЛ ПИШЕТ РОВНО ОДИН StreamWriter, И ДОСТУП К НЕМУ ИДЁТ ПОД ЛОКОМ.
///    Предсмертные сообщения (<see cref="Fatal"/>) раньше шли мимо очереди через
///    File.AppendAllText — и не доходили до диска НИКОГДА. Фоновый писатель держит
///    тот же файл открытым, оба открывали его с FileShare.Read, и второй получал
///    отказ в доступе; исключение глоталось пустым catch. То есть все три
///    глобальных обработчика падений (см. App.OnStartup) годами писали в пустоту —
///    ровно то, ради чего их и заводили. Теперь Fatal берёт тот же лок, что и
///    писатель, пишет в тот же поток и сбрасывает его на диск сам.
/// </summary>
public static class Log
{
    private static readonly BlockingCollection<string> Queue = new(4096);

    /// <summary>Через сколько выбрасывать накопленный счётчик повторов, даже если они продолжаются.</summary>
    private static readonly TimeSpan RepeatFlush = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Сколько строк из очереди дописать перед предсмертным сообщением. Последние
    /// записи ПЕРЕД падением — это и есть то, ради чего лог потом читают, а фоновый
    /// писатель их уже не разберёт: процесс умирает.
    /// </summary>
    private const int FatalTailLines = 500;

    /// <summary>Сколько ждать лока файла в Fatal, прежде чем уйти в запасной файл.</summary>
    private const int FatalLockTimeoutMs = 2000;

    // ---- Всё, что ниже, трогается ТОЛЬКО под FileSync ----
    private static readonly object FileSync = new();
    private static string _dir = "";
    private static StreamWriter? _file;
    private static string _openedFor = "";
    private static string _lastBody = "";
    private static int _repeats;
    private static DateTime _repeatSince = DateTime.MinValue;

    /// <summary>Писатель один на процесс, даже если Init позвали повторно (тесты).</summary>
    private static int _started;

    /// <summary>
    /// Поднять лог. <paramref name="directory"/> задаётся только тестами; приложение
    /// зовёт без аргумента и пишет в %LocalAppData%\Aura\logs.
    /// </summary>
    public static void Init(string? directory = null)
    {
        string dir = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Aura", "logs");
        Directory.CreateDirectory(dir);

        lock (FileSync)
        {
            try { _file?.Flush(); _file?.Dispose(); } catch { }
            _file = null;
            _openedFor = "";
            _dir = dir;
        }

        foreach (var f in Directory.EnumerateFiles(dir, "app-*.log"))
            try { if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-7)) File.Delete(f); } catch { }

        if (Interlocked.Exchange(ref _started, 1) == 0)
            new Thread(Writer) { IsBackground = true, Name = "LogWriter" }.Start();

        Info("App", $"===== Instant Replay запущен, PID {Environment.ProcessId} =====");
    }

    /// <summary>
    /// Предсмертное сообщение: пишется СИНХРОННО и сбрасывается на диск сразу.
    ///
    /// Обычные строки разбирает отдельный поток, и при аварийном завершении
    /// последние из них теряются — именно поэтому падения не оставляли следов.
    /// Здесь мы сначала дописываем накопленный хвост очереди (он хронологически
    /// раньше), затем само сообщение, затем flush.
    /// </summary>
    public static void Fatal(string tag, string msg)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [FTL] [{tag}] {msg}";
        System.Diagnostics.Debug.WriteLine(line);

        // Лок с таймаутом: писатель мог застрять на медленном диске, а мы в
        // предсмертном пути и ждать его бесконечно не имеем права.
        if (!Monitor.TryEnter(FileSync, FatalLockTimeoutMs)) { WriteAside(line); return; }
        try
        {
            var file = EnsureFile();
            if (file is null) { WriteAside(line); return; }

            for (int i = 0; i < FatalTailLines && Queue.TryTake(out var pending); i++)
                WriteCollapsed(file, pending);

            FlushRepeats(file);
            file.WriteLine(line);      // предсмертное сообщение не схлопываем никогда
            _lastBody = "";            // и не даём ему стать образцом для следующих
            file.Flush();
        }
        catch { WriteAside(line); }
        finally { Monitor.Exit(FileSync); }
    }

    /// <summary>
    /// Дописать хвост очереди и сбросить файл на диск. Зовётся при штатном выходе:
    /// Environment.Exit убивает фоновый поток писателя на месте, и последние строки
    /// (в том числе итоги остановки конвейера) до диска не доезжали.
    /// </summary>
    public static void Shutdown()
    {
        try { Queue.CompleteAdding(); } catch { }
        // Ждём недолго: выход приложения не должен подвисать из-за лога.
        for (int i = 0; i < 100 && Queue.Count > 0; i++) Thread.Sleep(10);
        lock (FileSync) { try { _file?.Flush(); } catch { } }
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
        // поток захвата на ожидании места. После Shutdown она закрыта — TryAdd на
        // закрытой коллекции бросает, и это не повод падать на выходе.
        try { Queue.TryAdd(line); } catch (InvalidOperationException) { }
    }

    private static void Writer()
    {
        try
        {
            foreach (var line in Queue.GetConsumingEnumerable())
                lock (FileSync)
                {
                    try
                    {
                        var file = EnsureFile();
                        if (file is null) continue;
                        WriteCollapsed(file, line);
                        // Сбрасываем на диск, когда очередь опустела: под нагрузкой строки
                        // копятся в буфере, в покое лог всегда актуален.
                        if (Queue.Count == 0) file.Flush();
                    }
                    catch { /* не роняем приложение из-за лога */ }
                }
        }
        finally
        {
            lock (FileSync)
            {
                try { _file?.Flush(); _file?.Dispose(); } catch { }
                _file = null;
                _openedFor = "";
            }
        }
    }

    /// <summary>
    /// Поток записи в сегодняшний файл; null — Init ещё не звали. Только под FileSync.
    ///
    /// FileShare.ReadWrite, а не Read по умолчанию: иначе лог нельзя открыть на
    /// чтение, пока приложение работает, — а разбирают его обычно именно так.
    /// </summary>
    private static StreamWriter? EnsureFile()
    {
        if (_dir.Length == 0) return null;

        string today = DateTime.Now.ToString("yyyy-MM-dd");
        if (_file is not null && today == _openedFor) return _file;

        try { _file?.Flush(); _file?.Dispose(); } catch { }
        _file = null;

        var stream = new FileStream(Path.Combine(_dir, $"app-{today}.log"),
                                    FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        // Полное имя кодировки: внутри Aura.Core короткое Encoding — это наше
        // пространство имён с кодировщиком видео, а не System.Text.
        _file = new StreamWriter(stream, System.Text.Encoding.UTF8) { AutoFlush = false };
        _openedFor = today;
        return _file;
    }

    /// <summary>Строка в файл со схлопыванием повторов. Только под FileSync.</summary>
    private static void WriteCollapsed(StreamWriter file, string line)
    {
        string body = Body(line);
        if (body == _lastBody)
        {
            _repeats++;
            if (DateTime.UtcNow - _repeatSince < RepeatFlush) return;
            FlushRepeats(file);
            _repeatSince = DateTime.UtcNow;
            return;
        }

        FlushRepeats(file);
        _lastBody = body;
        _repeatSince = DateTime.UtcNow;
        file.WriteLine(line);
    }

    private static void FlushRepeats(StreamWriter file)
    {
        if (_repeats == 0) return;
        file.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [INF] [Log] ↑ то же сообщение ещё {_repeats} раз");
        _repeats = 0;
    }

    /// <summary>
    /// Запасной путь для предсмертного сообщения: отдельный файл, если основной
    /// занят или недоступен. Строка не в том файле лучше, чем потерянная строка.
    /// </summary>
    private static void WriteAside(string line)
    {
        try
        {
            string dir = _dir;
            if (dir.Length == 0) return;
            File.AppendAllText(Path.Combine(dir, $"crash-{DateTime.Now:yyyy-MM-dd}.log"),
                               line + Environment.NewLine);
        }
        catch { }
    }

    /// <summary>Строка без метки времени — по ней и сравниваем повторы.</summary>
    private static string Body(string line) => line.Length > 13 ? line[13..] : line;
}
