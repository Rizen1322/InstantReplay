using Aura.Core.Logging;
using Aura.Core.Settings;

namespace Aura.Core.Storage;

public sealed record StorageStats(long FolderBytes, long FreeDiskBytes, int ClipCount, string RootPath);

/// <summary>
/// Свободное место, лимит размера папки записей и автоудаление самых старых клипов.
///
/// Всё считается по индексу папки (<see cref="ClipIndex"/>), который перестраивается
/// В ФОНЕ. Раньше и статистика, и автоочистка обходили папку рекурсивно прямо перед
/// сохранением клипа — то есть в самый неудачный момент. Теперь горячий путь
/// (<see cref="EnsureSpace"/>) складывает готовые числа.
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

    /// <summary>
    /// Гарантирует место перед сохранением: если включено автоудаление и
    /// (папка превысила лимит ИЛИ на диске мало места) — удаляем самые старые клипы.
    /// Удаляются только видео: скриншоты пользователь складывает сам, и молча
    /// подчищать их приложение не должно.
    /// </summary>
    public void EnsureSpace()
    {
        var s = _settings.Current;
        if (!s.AutoDeleteOldClips) return;

        string root = s.SaveRootPath;
        if (!Directory.Exists(root)) return;

        // Первый запуск за сеанс — индекса ещё нет, собираем здесь (один раз).
        if (!_index.IsBuilt || !_index.MatchesRoot(root)) _index.Rebuild(root);

        long maxFolder = s.MaxFolderSizeGb > 0 ? s.MaxFolderSizeGb * 1024L * 1024 * 1024 : long.MaxValue;
        long minFree = s.MinFreeSpaceGb * 1024L * 1024 * 1024;

        long folderBytes = _index.TotalBytes;
        long free = FreeSpace(root);
        if (folderBytes <= maxFolder && free >= minFree) return; // обычный случай — выходим сразу

        int deleted = 0;
        foreach (var file in _index.OldestFirst())
        {
            if (folderBytes <= maxFolder && free >= minFree) break;
            if (!file.IsClip) continue;
            try
            {
                File.Delete(file.Path);
                _index.Remove(file.Path);
                folderBytes -= file.SizeBytes;
                free += file.SizeBytes;
                deleted++;
            }
            catch (Exception ex)
            {
                // Файл открыт в плеере/проводнике — пропускаем, но из индекса не убираем
                Log.Warn("Storage", $"Не удалился {Path.GetFileName(file.Path)}: {ex.Message}");
            }
        }

        if (deleted > 0)
        {
            Log.Info("Storage", $"Автоочистка: удалено {deleted} старых клипов");
            CleanEmptyGameFolders(root);
            NotifyStats();
        }
    }

    private static void CleanEmptyGameFolders(string root)
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(root))
                try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
        }
        catch { }
    }

    private static long FreeSpace(string root)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!).AvailableFreeSpace; }
        catch { return 0; }
    }
}
