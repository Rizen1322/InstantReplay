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
    /// <summary>Предел очереди: при шторме лучше потерять строку, чем притормозить захват.</summary>
    private const int QueueCapacity = 4096;

    /// <summary>
    /// Очередь, из которой писатель забирает строки. Не readonly: Shutdown закрывает
    /// BlockingCollection насовсем, а открыть закрытую коллекцию заново нельзя —
    /// поэтому следующий Init обязан завести новую. Меняется только под Lifecycle.
    /// </summary>
    private static BlockingCollection<string> _queue = new(QueueCapacity);

    /// <summary>Жизненный цикл очереди и писателя: Init/Shutdown.</summary>
    private static readonly object Lifecycle = new();

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

    /// <summary>
    /// Поток записи текущей очереди. После Shutdown он завершается, и следующий Init
    /// (тесты, перезапуск в том же процессе) обязан поднять нового писателя: иначе
    /// закрытая очередь осталась бы немой, а лог — без новых строк.
    /// </summary>
    private static Thread? _writer;

    /// <summary>Хвост имени файла: у копии --dev он свой, см. <see cref="Init"/>.</summary>
    private static string _suffix = "";

    /// <summary>
    /// Поднять лог. <paramref name="directory"/> задаётся только тестами; приложение
    /// зовёт без аргумента и пишет в %LocalAppData%\Aura\logs.
    ///
    /// <paramref name="fileSuffix"/> разводит копии по разным файлам. Это не украшение:
    /// файл открыт с FileShare.ReadWrite (иначе лог не прочитать, пока приложение
    /// работает), и две копии — обычная и запущенная с --dev — писали бы в один файл
    /// каждая своим буфером, затирая друг другу строки. Обычные копии разведены
    /// мьютексом единственного экземпляра, а --dev поднимается рядом намеренно.
    /// </summary>
    public static void Init(string? directory = null, string fileSuffix = "")
    {
        // AURA_LOG_DIR — только для диагностических прогонов из песочницы/CI, где
        // %LocalAppData% недоступен на запись. Обычная Aura переменную не задаёт.
        string dir = directory
                     ?? (Environment.GetEnvironmentVariable("AURA_LOG_DIR") is { Length: > 0 } custom
                         ? custom
                         : Path.Combine(Environment.GetFolderPath(
                             Environment.SpecialFolder.LocalApplicationData), "Aura", "logs"));
        Directory.CreateDirectory(dir);

        lock (FileSync)
        {
            try { _file?.Flush(); _file?.Dispose(); } catch { }
            _file = null;
            _openedFor = "";
            _dir = dir;
            _suffix = fileSuffix;
        }

        foreach (var f in Directory.EnumerateFiles(dir, "app-*.log"))
            try { if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-7)) File.Delete(f); } catch { }

        // Писатель живёт ровно одну очередь. Shutdown закрывает её окончательно,
        // поэтому после него заводим И очередь, И поток заново: закрытая коллекция
        // так и осталась бы пустой, и лог молчал бы до перезапуска процесса.
        lock (Lifecycle)
        {
            bool restart = _queue.IsAddingCompleted;
            if (restart) _queue = new BlockingCollection<string>(QueueCapacity);

            if (restart || _writer is not { IsAlive: true })
            {
                // Коллекцию передаём в поток явно: следующий Init может подменить поле
                // ещё до старта потока, а писатель обязан разбирать СВОЮ очередь.
                var queue = _queue;
                _writer = new Thread(() => Writer(queue)) { IsBackground = true, Name = "LogWriter" };
                _writer.Start();
            }
        }

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

            // Берём очередь один раз: под нами её уже никто не подменит (Init после
            // Shutdown зовут не в предсмертном пути), а лок Lifecycle тут не нужен.
            var queue = _queue;
            for (int i = 0; i < FatalTailLines && queue.TryTake(out var pending); i++)
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
        var (queue, writer) = CloseQueue();

        // Ждём недолго: выход приложения не должен подвисать из-за лога.
        for (int i = 0; i < 100 && queue.Count > 0; i++) Thread.Sleep(10);

        // Даём писателю завершиться и закрыть файл: следующий Init в том же процессе
        // (тесты) не должен пересечься с его finally на общем StreamWriter.
        try { writer?.Join(500); } catch { }

        lock (FileSync) { try { _file?.Flush(); } catch { } }
    }

    /// <summary>
    /// Закрыть текущую очередь и вернуть её вместе с писателем. Дальше ждать их
    /// нужно уже вне Lifecycle: под локом не место ожиданию диска.
    /// </summary>
    private static (BlockingCollection<string> Queue, Thread? Writer) CloseQueue()
    {
        lock (Lifecycle)
        {
            try { _queue.CompleteAdding(); } catch { }
            return (_queue, _writer);
        }
    }

    public static void Info(string tag, string msg)  => Enqueue("INF", tag, msg);
    public static void Warn(string tag, string msg)  => Enqueue("WRN", tag, msg);
    public static void Error(string tag, string msg) => Enqueue("ERR", tag, msg);
    public static void Error(string tag, Exception ex) => Enqueue("ERR", tag, ex.ToString());

    /// <summary>Сколько строк потеряно из-за переполнения очереди с прошлого отчёта.</summary>
    private static long _dropped;

    private static void Enqueue(string lvl, string tag, string msg)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{lvl}] [{tag}] {msg}";
        System.Diagnostics.Debug.WriteLine(line);
        // Очередь не бесконечна: при шторме лучше потерять строку, чем притормозить
        // поток захвата на ожидании места. После Shutdown она закрыта — TryAdd на
        // закрытой коллекции бросает, и это не повод падать на выходе.
        // Ссылку берём один раз: если между строкой и добавлением Init после
        // Shutdown успел подменить очередь, строка уйдёт в старую и потеряется —
        // это допустимо, главное не писать в закрытую и не ждать на логе.
        var queue = _queue;
        try
        {
            if (queue.TryAdd(line)) return;

            // Потеря строк молча означает лог с дырой, о которой никто не знает:
            // при разборе падения кажется, что события просто не было. Считаем
            // потери и докладываем о них степенями десятки, чтобы сам отчёт
            // не стал источником шторма.
            long lost = Interlocked.Increment(ref _dropped);
            if (lost is 1 or 100 or 10_000 or 1_000_000)
                queue.TryAdd($"{DateTime.Now:HH:mm:ss.fff} [WRN] [Log] " +
                             $"очередь лога переполнена, потеряно строк: {lost}");
        }
        catch (InvalidOperationException) { }
    }

    private static void Writer(BlockingCollection<string> queue)
    {
        try
        {
            // GetConsumingEnumerable ждёт на семафоре, а не крутит цикл: пока очередь
            // пуста, поток спит. После CompleteAdding он доразбирает хвост и выходит.
            foreach (var line in queue.GetConsumingEnumerable())
                lock (FileSync)
                {
                    try
                    {
                        var file = EnsureFile();
                        if (file is null) continue;
                        WriteCollapsed(file, line);
                        // Сбрасываем на диск, когда очередь опустела: под нагрузкой строки
                        // копятся в буфере, в покое лог всегда актуален.
                        if (queue.Count == 0) file.Flush();
                    }
                    catch (Exception ex)
                    {
                        // Не роняем приложение из-за лога — но и не молчим. Раньше здесь
                        // стоял пустой catch: если файл лога не открывался, приложение
                        // работало вообще без следов, и целый разбор пришлось вести
                        // вслепую. Строку кладём в аварийный файл, причину — туда же
                        // (не чаще раза в минуту, чтобы не залить диск).
                        WriteAside(line);
                        ReportLogFailure(ex);
                    }
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

    private static long _logFailures;
    private static DateTime _lastLogFailureReportUtc = DateTime.MinValue;

    /// <summary>Сколько раз запись лога сорвалась (для диагностики).</summary>
    public static long Failures => Interlocked.Read(ref _logFailures);

    private static void ReportLogFailure(Exception ex)
    {
        Interlocked.Increment(ref _logFailures);
        System.Diagnostics.Debug.WriteLine($"Лог не пишется: {ex.Message}");

        DateTime now = DateTime.UtcNow;
        if (now - _lastLogFailureReportUtc < TimeSpan.FromMinutes(1)) return;
        _lastLogFailureReportUtc = now;
        WriteAside($"{DateTime.Now:HH:mm:ss.fff} [ERR] [Log] лог не пишется " +
                   $"({ex.GetType().Name}): {ex.Message}");
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

        // Основной файл — общий. Если его не отдают (права, атрибут «только чтение»,
        // чужой эксклюзивный захват), пробуем СВОЙ файл с номером процесса: лог в
        // другом месте лучше, чем ни одного лога. Молчание здесь дорого стоит —
        // именно из-за него один раз не осталось ни одной строки о сбоях.
        Exception? firstError = null;
        foreach (string name in new[]
                 {
                     $"app-{today}{_suffix}.log",
                     $"app-{today}{_suffix}-{Environment.ProcessId}.log"
                 })
        {
            try
            {
                var stream = new FileStream(Path.Combine(_dir, name),
                                            FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                // Полное имя кодировки: внутри Aura.Core короткое Encoding — это наше
                // пространство имён с кодировщиком видео, а не System.Text.
                _file = new StreamWriter(stream, System.Text.Encoding.UTF8) { AutoFlush = false };
                _openedFor = today;
                return _file;
            }
            catch (Exception ex) { firstError ??= ex; }
        }

        throw new IOException($"ни один файл лога не открылся в «{_dir}»: {firstError?.Message}",
                              firstError);
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
            File.AppendAllText(Path.Combine(dir, $"crash-{DateTime.Now:yyyy-MM-dd}{_suffix}.log"),
                               line + Environment.NewLine);
        }
        catch { }
    }

    /// <summary>Строка без метки времени — по ней и сравниваем повторы.</summary>
    private static string Body(string line) => line.Length > 13 ? line[13..] : line;
}
