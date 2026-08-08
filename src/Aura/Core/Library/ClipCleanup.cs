using Aura.Core.Logging;

namespace Aura.Core.Library;

/// <summary>
/// Уборка хвостов, которые остаются от записи.
///
/// Один клип — это не один файл. Рядом с ним живут:
///   • миниатюра и метаданные в кэше (%AppData%\Aura\thumbs\*.thumb, *.meta);
///   • обрывки прерванного сохранения (.tmp, .part) и служебные спутники,
///     если запись открывали внешними программами (.srt, .json, .log);
///   • папка игры, в которой этот клип был последним.
///
/// Раньше удалялся только сам файл: кэш миниатюр рос без конца (его никто не
/// чистил вообще), пустые папки игр копились в панораме отдельными группами, а
/// незавершённые .part оставались на диске навсегда. Здесь всё это в одном месте
/// и зовётся из каждого пути удаления — из меню карточки, из пачки и из автоочистки.
/// </summary>
public static class ClipCleanup
{
    /// <summary>
    /// Спутники записи. Файлы библиотеки (.mp4/.png/.jpg) сюда НЕ входят намеренно:
    /// сжатая копия «клип (25 МБ).mp4» — самостоятельная запись, она видна в панораме,
    /// и уносить её вместе с оригиналом без спроса нельзя.
    /// </summary>
    private static readonly string[] SidecarExtensions =
        [".tmp", ".part", ".thumb", ".meta", ".srt", ".vtt", ".json", ".log", ".txt"];

    /// <summary>
    /// Убрать всё, что осталось от записи: кэш миниатюры, файлы-спутники рядом
    /// и опустевшую папку игры. Сам файл записи к этому моменту уже удалён.
    /// Ошибки не пробрасываются — уборка не должна ронять удаление.
    /// </summary>
    public static void RemoveTraces(ClipItem item, string saveRoot)
    {
        ClipThumbnails.Forget(item);
        RemoveTraces(item.FullPath, saveRoot);
    }

    /// <summary>То же самое для файла, о котором известен только путь (автоочистка).</summary>
    public static void RemoveTraces(string path, string saveRoot)
    {
        RemoveSidecars(path);
        RemoveEmptyFolder(Path.GetDirectoryName(path), saveRoot);
    }

    /// <summary>Файлы с тем же именем и служебным расширением: «клип.mp4.part», «клип.srt».</summary>
    private static void RemoveSidecars(string path)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (dir is null || !Directory.Exists(dir)) return;

            string bare = Path.GetFileNameWithoutExtension(path);   // клип
            string full = Path.GetFileName(path);                   // клип.mp4

            foreach (string candidate in Directory.EnumerateFiles(dir, bare + ".*"))
            {
                string name = Path.GetFileName(candidate);
                // «клип.mp4.part» и «клип.srt» — да; «клип 2.mp4» — нет, шаблон
                // и так его не поймает, но проверяем основу имени явно.
                bool ours = name.StartsWith(full + ".", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(Path.GetFileNameWithoutExtension(name), bare,
                                             StringComparison.OrdinalIgnoreCase);
                if (!ours) continue;

                string ext = Path.GetExtension(name);
                if (!SidecarExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) continue;

                try { File.Delete(candidate); }
                catch (Exception ex) { Log.Warn("Library", $"Хвост {name}: {ex.Message}"); }
            }
        }
        catch (Exception ex) { Log.Warn("Library", $"Уборка хвостов {path}: {ex.Message}"); }
    }

    /// <summary>
    /// Папка игры, из которой убрали последнюю запись. Корень папки записей не трогаем
    /// никогда — он должен существовать, даже когда в нём пусто.
    /// </summary>
    private static void RemoveEmptyFolder(string? dir, string saveRoot)
    {
        try
        {
            if (dir is null || saveRoot.Length == 0 || !Directory.Exists(dir)) return;

            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(saveRoot));
            string here = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));

            // Строго ВНУТРИ папки записей: сам корень и всё за его пределами не наше
            if (!here.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;

            if (Directory.EnumerateFileSystemEntries(here).Any()) return;
            Directory.Delete(here);
            Log.Info("Library", $"Убрал пустую папку {Path.GetFileName(here)}");
        }
        catch (Exception ex) { Log.Warn("Library", $"Пустая папка {dir}: {ex.Message}"); }
    }
}
