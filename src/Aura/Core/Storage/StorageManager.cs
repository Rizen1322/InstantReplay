using Aura.Core.Logging;
using Aura.Core.Settings;

namespace Aura.Core.Storage;

public sealed record StorageStats(long FolderBytes, long FreeDiskBytes, int ClipCount, string RootPath);

/// <summary>
/// Сколько занято в папке записей и сколько свободно на диске.
///
/// Считается по индексу папки (<see cref="ClipIndex"/>), который перестраивается
/// В ФОНЕ: раньше статистика обходила папку рекурсивно прямо перед сохранением
/// клипа — то есть в самый неудачный момент.
///
/// Автоудаления старых записей здесь больше нет: приложение не должно молча
/// стирать чужие файлы, а «лимит папки» ничего не гарантировал — место на диске
/// кончалось от чего угодно ещё.
///
/// Индекс правится инкрементально: <see cref="RegisterSaved"/> после записи файла,
/// <see cref="Forget"/> и <see cref="Rename"/> — при операциях из панорамы. Изменения
/// извне (проводник) подхватывает перестройка по устареванию.
/// </summary>
public sealed class StorageManager : IDisposable
{
    /// <summary>
    /// Через сколько индекс считается устаревшим без слежения за папкой.
    ///
    /// Обход всех подпапок нужен ровно для одного: заметить чужие правки, свои
    /// файлы попадают в индекс сразу (см. <see cref="RegisterSaved"/>). Пока за
    /// папкой следит система, обход держим редким — он остаётся страховкой.
    /// </summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(2);

    /// <summary>То же, но когда слежение работает и о чужих правках сообщают сразу.</summary>
    private static readonly TimeSpan StaleAfterWatched = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Сколько путь считается «нашим» после того, как мы его сами занесли в индекс.
    /// Система сообщает о файле с задержкой, и запас нужен, чтобы собственное
    /// сохранение не выглядело чужой правкой.
    /// </summary>
    private static readonly TimeSpan OwnChangeWindow = TimeSpan.FromSeconds(30);

    private readonly SettingsManager _settings;
    private readonly ClipIndex _index = new();
    private readonly ClipFolderWatcher _watcher;

    /// <summary>Что мы сами трогали и когда. По нему отсеиваем эхо своих же правок.</summary>
    private readonly Dictionary<string, DateTime> _ownChanges = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _ownSync = new();

    private int _rebuilding;

    public event Action<StorageStats>? StatsChanged;

    public StorageManager(SettingsManager settings)
    {
        _settings = settings;
        _watcher = new ClipFolderWatcher(OnFolderChanged);
        _settings.Changed += group =>
        {
            if (group is "" or "storage")
            {
                Directory.CreateDirectory(_settings.Current.SaveRootPath);
                RequestRebuild(force: true); // папка могла смениться — считаем по новой
                _watcher.Watch(_settings.Current.SaveRootPath);
            }
        };
        _watcher.Watch(Root);
    }

    private string Root => _settings.Current.SaveRootPath;

    /// <summary>
    /// Перестроить индекс в фоне, если он устарел или собран по другой папке.
    /// force — перестроить обязательно (смена папки записей).
    /// </summary>
    public void RequestRebuild(bool force = false)
    {
        string root = Root;
        TimeSpan staleAfter = _watcher.Active ? StaleAfterWatched : StaleAfter;
        bool stale = force || !_index.IsBuilt || !_index.MatchesRoot(root)
                     || DateTime.UtcNow - _index.BuiltUtc > staleAfter;
        if (!stale) return;
        if (Interlocked.Exchange(ref _rebuilding, 1) == 1) return; // уже строится

        Task.Run(() =>
        {
            try
            {
                _index.Rebuild(root);
                NotifyStats();
            }
            catch (Exception ex) { Log.Warn("Storage", $"Индекс папки: {ex.Message}"); }
            finally { Interlocked.Exchange(ref _rebuilding, 0); }
        });
    }

    /// <summary>
    /// Статистика по индексу. Если индекс ещё не собран, вернёт нули и запустит сборку —
    /// UI получит настоящие числа событием StatsChanged через мгновение.
    /// </summary>
    public StorageStats GetStats()
    {
        string root = Root;
        RequestRebuild();
        return new StorageStats(_index.TotalBytes, FreeSpace(root), _index.ClipCount, root);
    }

    public void NotifyStats() => StatsChanged?.Invoke(GetStats());

    /// <summary>Файл записан — сразу в индекс, без обхода папки.</summary>
    public void RegisterSaved(string path)
    {
        RememberOwnChange(path);
        _index.Add(path);
        NotifyStats();
    }

    /// <summary>Файл удалён из панорамы или извне.</summary>
    public void Forget(string path)
    {
        RememberOwnChange(path);
        _index.Remove(path);
        NotifyStats();
    }

    public void Rename(string from, string to)
    {
        RememberOwnChange(from);
        RememberOwnChange(to);
        _index.Rename(from, to);
        NotifyStats();
    }

    /// <summary>
    /// Отметить путь как свой, чтобы слежение за папкой не приняло его за чужую
    /// правку. Заодно чистим просроченные отметки: список растёт по одному пути на
    /// сохранение, и без уборки он жил бы всю сессию.
    /// </summary>
    private void RememberOwnChange(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        var now = DateTime.UtcNow;
        lock (_ownSync)
        {
            _ownChanges[path] = now;

            // Запись во время сохранения идёт через .part, и о нём система тоже
            // сообщит. Считаем его своим вместе с готовым файлом.
            _ownChanges[path + ".part"] = now;

            if (_ownChanges.Count > 64)
                foreach (var stale in _ownChanges.Where(x => now - x.Value > OwnChangeWindow).ToList())
                    _ownChanges.Remove(stale.Key);
        }
    }

    /// <summary>
    /// Система сообщила о переменах в папке. Полный обход запускаем, только если
    /// среди путей есть хоть один не наш.
    ///
    /// ЗАЧЕМ ФИЛЬТР. Каждое сохранение создаёт .part и переименовывает его, то есть
    /// порождает ровно те события, на которые мы подписаны. Без фильтра обход всей
    /// библиотеки шёл бы после каждого клипа, причём сразу вслед за записью сотен
    /// мегабайт, когда диск и так занят. Ради этого обход и убирали.
    ///
    /// Пустой список означает потерю событий (переполнение буфера слежения) —
    /// тогда проверяем всё.
    /// </summary>
    private void OnFolderChanged(IReadOnlyCollection<string> paths)
    {
        if (paths.Count > 0 && paths.All(IsOwnChange)) return;
        RequestRebuild(force: true);
    }

    private bool IsOwnChange(string path)
    {
        lock (_ownSync)
            return _ownChanges.TryGetValue(path, out DateTime when) &&
                   DateTime.UtcNow - when <= OwnChangeWindow;
    }

    private static long FreeSpace(string root)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!).AvailableFreeSpace; }
        catch { return 0; }
    }

    public void Dispose() => _watcher.Dispose();
}
