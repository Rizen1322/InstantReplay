using InstantReplay.Core.Logging;
using InstantReplay.Core.Settings;

namespace InstantReplay.Core.Storage;

public sealed record StorageStats(long FolderBytes, long FreeDiskBytes, int ClipCount, string RootPath);

/// <summary>
/// Проверка свободного места, лимит размера папки записей и автоудаление
/// самых старых клипов. Статистика ВСЕГДА считается по актуальному
/// Settings.Current.SaveRootPath (грабли: после смены папки в настройках
/// статистика должна пересчитаться по новой папке, а не показывать старую —
/// поэтому путь не кэшируется, а событие Settings.Changed("storage")
/// дополнительно триггерит StatsChanged для UI).
/// </summary>
public sealed class StorageManager
{
    private readonly SettingsManager _settings;
    public event Action<StorageStats>? StatsChanged;

    public StorageManager(SettingsManager settings)
    {
        _settings = settings;
        _settings.Changed += g =>
        {
            if (g is "" or "storage")
            {
                Directory.CreateDirectory(_settings.Current.SaveRootPath);
                NotifyStats(); // немедленный пересчёт по НОВОЙ папке
            }
        };
    }

    public StorageStats GetStats()
    {
        string root = _settings.Current.SaveRootPath; // всегда актуальный путь
        long folderBytes = 0;
        int count = 0;
        try
        {
            if (Directory.Exists(root))
                foreach (var f in Directory.EnumerateFiles(root, "*.mp4", SearchOption.AllDirectories))
                {
                    folderBytes += new FileInfo(f).Length;
                    count++;
                }
        }
        catch (Exception ex) { Log.Warn("Storage", ex.Message); }

        long free = 0;
        try { free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!).AvailableFreeSpace; }
        catch { }

        return new StorageStats(folderBytes, free, count, root);
    }

    public void NotifyStats() => StatsChanged?.Invoke(GetStats());

    /// <summary>
    /// Гарантирует место перед сохранением: если включено автоудаление и
    /// (папка превысила лимит ИЛИ на диске мало места) — удаляем самые старые клипы.
    /// </summary>
    public void EnsureSpace()
    {
        var s = _settings.Current;
        if (!s.AutoDeleteOldClips) return;

        string root = s.SaveRootPath;
        if (!Directory.Exists(root)) return;

        long maxFolder = s.MaxFolderSizeGb > 0 ? s.MaxFolderSizeGb * 1024L * 1024 * 1024 : long.MaxValue;
        long minFree = s.MinFreeSpaceGb * 1024L * 1024 * 1024;

        var files = Directory.EnumerateFiles(root, "*.mp4", SearchOption.AllDirectories)
            .Select(f => new FileInfo(f))
            .OrderBy(f => f.CreationTimeUtc) // старые — первыми под нож
            .ToList();

        long folderBytes = files.Sum(f => f.Length);
        long free;
        try { free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!).AvailableFreeSpace; }
        catch { free = long.MaxValue; }

        int deleted = 0;
        foreach (var f in files)
        {
            if (folderBytes <= maxFolder && free >= minFree) break;
            try
            {
                long len = f.Length;
                f.Delete();
                folderBytes -= len;
                free += len;
                deleted++;
            }
            catch (Exception ex) { Log.Warn("Storage", $"Не удалился {f.Name}: {ex.Message}"); }
        }
        if (deleted > 0)
        {
            Log.Info("Storage", $"Автоочистка: удалено {deleted} старых клипов");
            NotifyStats();
        }

        // Пустые папки игр подчищаем
        foreach (var dir in Directory.EnumerateDirectories(root))
            try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
    }

    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.##} ГБ",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.#} МБ",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0} КБ",
        _ => $"{bytes} Б"
    };
}
