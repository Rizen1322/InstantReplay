namespace InstantReplay.Core.Storage;

/// <summary>Файл в папке записей. IsClip — видео (.mp4); остальное — скриншоты.</summary>
public sealed record ClipFile(string Path, long SizeBytes, DateTime CreatedUtc, bool IsClip);

/// <summary>
/// Индекс папки записей в памяти.
///
/// Зачем: раньше и статистика, и автоочистка перед КАЖДЫМ сохранением обходили папку
/// рекурсивно с FileInfo на каждый файл. На большой коллекции это заметная пауза ровно
/// в тот момент, когда идёт запись клипа (в логах — предупреждения «проверка места
/// заняла N мс»). Теперь обход делается в фоне, а горячий путь работает с индексом:
/// сложение готовых чисел и удаление самых старых.
///
/// Класс намеренно не знает ни про настройки, ни про UI — его можно тестировать.
/// </summary>
public sealed class ClipIndex
{
    private static readonly string[] Extensions = [".mp4", ".png", ".jpg", ".jpeg"];

    private readonly object _sync = new();
    private readonly Dictionary<string, ClipFile> _files = new(StringComparer.OrdinalIgnoreCase);
    private long _totalBytes;
    private int _clipCount;

    /// <summary>Папка, по которой собран индекс ("" — индекс ещё не собирали).</summary>
    public string Root { get; private set; } = "";
    public DateTime BuiltUtc { get; private set; } = DateTime.MinValue;
    public bool IsBuilt => BuiltUtc != DateTime.MinValue;

    public long TotalBytes { get { lock (_sync) return _totalBytes; } }
    public int Count { get { lock (_sync) return _files.Count; } }
    /// <summary>Сколько именно видеоклипов (для статистики на вкладке «Хранилище»).</summary>
    public int ClipCount { get { lock (_sync) return _clipCount; } }

    public bool MatchesRoot(string root) =>
        Root.Length > 0 && string.Equals(Normalize(Root), Normalize(root), StringComparison.OrdinalIgnoreCase);

    /// <summary>Обход папки. Чистая функция — вызывать из фонового потока.</summary>
    public static List<ClipFile> Enumerate(string root)
    {
        var result = new List<ClipFile>();
        if (!Directory.Exists(root)) return result;

        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var file = Describe(path);
            if (file is not null) result.Add(file);
        }
        return result;
    }

    /// <summary>Описать файл, если он относится к записям. null — чужое расширение или файл исчез.</summary>
    public static ClipFile? Describe(string path)
    {
        string ext = System.IO.Path.GetExtension(path);
        if (!Extensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) return null;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            // LastWriteTime, а не CreationTime: при копировании коллекции на другой
            // диск дата создания становится датой копирования, и «самый старый клип»
            // определялся бы неверно.
            return new ClipFile(info.FullName, info.Length, info.LastWriteTimeUtc,
                                ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase));
        }
        catch { return null; }
    }

    public void Rebuild(string root)
    {
        var files = Enumerate(root);
        lock (_sync)
        {
            _files.Clear();
            _totalBytes = 0;
            _clipCount = 0;
            foreach (var f in files) AddLocked(f);
            Root = root;
            BuiltUtc = DateTime.UtcNow;
        }
    }

    /// <summary>Добавить (или обновить) файл — после сохранения клипа/скриншота.</summary>
    public void Add(string path)
    {
        var file = Describe(path);
        if (file is null) return;
        lock (_sync) AddLocked(file);
    }

    public void Add(ClipFile file)
    {
        lock (_sync) AddLocked(file);
    }

    private void AddLocked(ClipFile file)
    {
        if (_files.TryGetValue(file.Path, out var existing))
        {
            _totalBytes -= existing.SizeBytes;
            if (existing.IsClip) _clipCount--;
        }
        // Describe уже отдаёт FullName, но ключ нормализуем явно — Remove ищет так же
        _files[Normalize(file.Path)] = file;
        _totalBytes += file.SizeBytes;
        if (file.IsClip) _clipCount++;
    }

    public void Remove(string path)
    {
        // Ключ — всегда нормализованный путь: «папка/файл.mp4» и «папка\файл.mp4»
        // должны находить одну и ту же запись.
        string key = Normalize(path);
        lock (_sync)
        {
            if (!_files.Remove(key, out var existing)) return;
            _totalBytes -= existing.SizeBytes;
            if (existing.IsClip) _clipCount--;
        }
    }

    public void Rename(string from, string to)
    {
        Remove(from);
        Add(to);
    }

    /// <summary>Снимок индекса, самые старые первыми — порядок автоочистки.</summary>
    public List<ClipFile> OldestFirst()
    {
        lock (_sync)
        {
            var list = new List<ClipFile>(_files.Values);
            list.Sort((a, b) => a.CreatedUtc.CompareTo(b.CreatedUtc));
            return list;
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _files.Clear();
            _totalBytes = 0;
            _clipCount = 0;
            Root = "";
            BuiltUtc = DateTime.MinValue;
        }
    }

    private static string Normalize(string path)
    {
        try { return System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path)); }
        catch { return path; }
    }
}
