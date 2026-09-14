using System.IO;
using Aura.Core.Logging;

namespace Aura.Core.Storage;

/// <summary>
/// Следит за папкой записей и сообщает об изменениях, сделанных не нами.
///
/// ЗАЧЕМ. Индекс папки считался устаревшим через две минуты и после этого
/// перестраивался полным обходом всех подпапок. Свои файлы в индекс попадают
/// сразу (StorageManager.RegisterSaved), так что обход нужен был ровно для одного:
/// заметить, что человек удалил или переименовал запись в проводнике. На библиотеке
/// в сотни клипов это регулярное чтение каталога ради события, которого обычно
/// нет вовсе.
///
/// Теперь об этих событиях сообщает сама система, а полный обход остаётся только
/// как страховка на случай, когда слежение недоступно или сорвалось.
///
/// События приходят пачками и с задержкой: копирование файла даёт Created задолго
/// до того, как файл дописан, а переименование в некоторых программах выглядит как
/// удаление и создание. Поэтому здесь только гашение дребезга и один общий сигнал
/// «что-то поменялось»; разбираться, что именно, дешевле одним проходом по папке.
/// </summary>
public sealed class ClipFolderWatcher : IDisposable
{
    /// <summary>
    /// Сколько ждать тишины, прежде чем сообщить. Копирование большого файла
    /// сыплет событиями всё время записи, и реагировать на каждое незачем.
    /// </summary>
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(2);

    private readonly Action<IReadOnlyCollection<string>> _changed;
    private readonly object _sync = new();

    /// <summary>Пути, о которых сообщим следующим сигналом. Один путь считается один раз.</summary>
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);

    private FileSystemWatcher? _watcher;
    private System.Threading.Timer? _settleTimer;
    private string _root = "";
    private bool _disposed;

    /// <summary>Работает ли слежение. Пока нет — индекс обновляется по старинке.</summary>
    public bool Active { get { lock (_sync) return _watcher is not null; } }

    public ClipFolderWatcher(Action<IReadOnlyCollection<string>> changed) => _changed = changed;

    /// <summary>Начать следить за папкой. Повторный вызов с той же папкой ничего не делает.</summary>
    public void Watch(string root)
    {
        lock (_sync)
        {
            if (_disposed) return;
            if (_watcher is not null && string.Equals(_root, root, StringComparison.OrdinalIgnoreCase))
                return;

            StopCore();
            _root = root;

            try
            {
                if (!Directory.Exists(root)) return;

                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    // Имя и размер — всё, что нас интересует. Время последнего доступа
                    // меняется от одного лишь просмотра папки и только шумит.
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size
                };
                watcher.Created += OnChanged;
                watcher.Deleted += OnChanged;
                watcher.Renamed += OnChanged;
                watcher.Error += OnError;
                watcher.EnableRaisingEvents = true;
                _watcher = watcher;
                Log.Info("Storage", $"Слежение за папкой записей включено: {root}");
            }
            catch (Exception ex)
            {
                // Сетевая папка, права, слишком длинный путь — причин много, и ни одна
                // не повод ломать запись. Останемся на обходе по расписанию.
                Log.Warn("Storage", $"Слежение за папкой записей недоступно: {ex.Message}");
                StopCore();
            }
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        lock (_sync)
        {
            // Список путей не бесконечен. Массовая операция в проводнике (удалили
            // сотню записей разом) насыпала бы их тысячами, а решение всё равно
            // одно: раз путей слишком много, разбирать их поштучно незачем, дешевле
            // пройти папку. Пустой список именно это и означает.
            if (_pending.Count >= 512) return;

            // Переименование это два пути: и старый, и новый нас интересуют.
            if (e is RenamedEventArgs renamed && !string.IsNullOrEmpty(renamed.OldFullPath))
                _pending.Add(renamed.OldFullPath);
            if (!string.IsNullOrEmpty(e.FullPath)) _pending.Add(e.FullPath);
            if (_pending.Count >= 512) _pending.Clear();
        }
        Schedule();
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        // Буфер переполнился или папка стала недоступна. Часть событий потеряна,
        // поэтому сообщаем о переменах в любом случае и поднимаем слежение заново.
        Log.Warn("Storage", $"Слежение за папкой записей сорвалось: {e.GetException().Message}");
        string root;
        lock (_sync)
        {
            root = _root;
            StopCore();
        }
        // Часть событий потеряна — сообщаем пустым списком, это значит «проверь всё».
        Schedule();
        if (!string.IsNullOrEmpty(root)) Watch(root);
    }

    private void Schedule()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _settleTimer ??= new System.Threading.Timer(_ => Fire(), null,
                                                        Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _settleTimer.Change(Settle, Timeout.InfiniteTimeSpan);
        }
    }

    private void Fire()
    {
        if (_disposed) return;

        string[] paths;
        lock (_sync)
        {
            paths = [.. _pending];
            _pending.Clear();
        }

        try { _changed(paths); }
        catch (Exception ex) { Log.Warn("Storage", $"Обновление индекса папки: {ex.Message}"); }
    }

    private void StopCore()
    {
        if (_watcher is not null)
        {
            _watcher.Created -= OnChanged;
            _watcher.Deleted -= OnChanged;
            _watcher.Renamed -= OnChanged;
            _watcher.Error -= OnError;
            try { _watcher.EnableRaisingEvents = false; } catch { }
            _watcher.Dispose();
            _watcher = null;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            StopCore();
            _settleTimer?.Dispose();
            _settleTimer = null;
        }
    }
}
