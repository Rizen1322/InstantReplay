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
public sealed class StorageManager
{
    /// <summary>Через сколько индекс считается устаревшим (файлы могли поменяться извне).</summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(2);

    private readonly SettingsManager _settings;
    private readonly ClipIndex _index = new();
    private int _rebuilding;

    public event Action<StorageStats>? StatsChanged;

    public StorageManager(SettingsManager settings)
    {
        _settings = settings;
        _settings.Changed += group =>
        {
            if (group is "" or "storage")
            {
                Directory.CreateDirectory(_settings.Current.SaveRootPath);
                RequestRebuild(force: true); // папка могла смениться — считаем по новой
            }
        };
    }

    private string Root => _settings.Current.SaveRootPath;

    /// <summary>
    /// Перестроить индекс в фоне, если он устарел или собран по другой папке.
    /// force — перестроить обязательно (смена папки записей).
    /// </summary>
    public void RequestRebuild(bool force = false)
    {
        string root = Root;
        bool stale = force || !_index.IsBuilt || !_index.MatchesRoot(root)
                     || DateTime.UtcNow - _index.BuiltUtc > StaleAfter;
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
        _index.Add(path);
        NotifyStats();
    }

    /// <summary>Файл удалён из панорамы или извне.</summary>
    public void Forget(string path)
    {
        _index.Remove(path);
        NotifyStats();
    }

    public void Rename(string from, string to)
    {
        _index.Rename(from, to);
        NotifyStats();
    }

    private static long FreeSpace(string root)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!).AvailableFreeSpace; }
        catch { return 0; }
    }
}
