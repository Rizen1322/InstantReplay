using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using InstantReplay.Core.Logging;
using InstantReplay.Core.Storage;

namespace InstantReplay.Core.Library;

/// <summary>
/// Один файл библиотеки записей: клип (.mp4) или скриншот (.png/.jpg).
///
/// Свойства, которые догружаются в фоне (миниатюра, длительность, разрешение),
/// поднимают PropertyChanged — карточка в сетке дорисовывается сама, без пересборки
/// списка: пересборка сбрасывала бы позицию прокрутки при каждой загруженной
/// миниатюре.
/// </summary>
public sealed class ClipItem : INotifyPropertyChanged
{
    public ClipItem(string path, string root)
    {
        FullPath = path;
        var info = new FileInfo(path);
        SizeBytes = info.Length;
        // Время ЗАПИСИ файла, а не создания: при копировании коллекции на другой диск
        // CreationTime становится датой копирования, LastWriteTime остаётся исходным.
        Created = info.LastWriteTime;
        Title = Path.GetFileNameWithoutExtension(path);
        IsScreenshot = !path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);

        // Игра = имя папки, в которой лежит файл (раскладка Videos\InstantReplay\<Игра>\).
        // Файл прямо в корне — значит раскладка по играм была выключена.
        string? dir = Path.GetDirectoryName(path);
        Game = dir is null || PathsEqual(dir, root) ? "Без папки" : Path.GetFileName(dir);
    }

    private static bool PathsEqual(string a, string b) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
        StringComparison.OrdinalIgnoreCase);

    public string FullPath { get; }
    public string FileName => Path.GetFileName(FullPath);
    public string Title { get; }
    public string Game { get; }
    public DateTime Created { get; }
    public long SizeBytes { get; }
    public bool IsScreenshot { get; }

    public string SizeText => ByteSize.Format(SizeBytes);
    public string Subtitle => $"{Game} · {Created:HH:mm} · {SizeText}";
    public string PlaceholderGlyph => IsScreenshot ? "\uE722" : "\uE714";

    /// <summary>Строка для шапки просмотра: всё, что известно о файле.</summary>
    public string FullInfo
    {
        get
        {
            var parts = new List<string> { Game, Created.ToString("dd MMMM yyyy, HH:mm"), SizeText };
            if (Duration is TimeSpan d) parts.Add(FormatDuration(d));
            if (Resolution is { Length: > 0 } r) parts.Add(r);
            if (IsScreenshot) parts.Add("скриншот");
            return string.Join(" · ", parts);
        }
    }

    // ---------- Догружается в фоне ----------

    private ImageSource? _thumbnail;
    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set { _thumbnail = value; Raise(nameof(Thumbnail)); }
    }

    private TimeSpan? _duration;
    public TimeSpan? Duration
    {
        get => _duration;
        set
        {
            _duration = value;
            Raise(nameof(Duration)); Raise(nameof(DurationText));
            Raise(nameof(DurationVisibility)); Raise(nameof(FullInfo));
        }
    }

    private string? _resolution;
    public string? Resolution
    {
        get => _resolution;
        set { _resolution = value; Raise(nameof(Resolution)); Raise(nameof(FullInfo)); }
    }

    /// <summary>Миниатюру запрашиваем один раз на файл, даже если карточка переиспользована.</summary>
    internal bool ThumbRequested { get; set; }

    public string DurationText => IsScreenshot ? "PNG"
        : Duration is TimeSpan d ? FormatDuration(d) : "";

    public Visibility DurationVisibility =>
        DurationText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Плашка «новое» у свежих записей — сразу видно, что только что сохранилось.</summary>
    private bool _isNew;
    public bool IsNew
    {
        get => _isNew;
        set { _isNew = value; Raise(nameof(IsNew)); Raise(nameof(NewVisibility)); }
    }
    public Visibility NewVisibility => IsNew ? Visibility.Visible : Visibility.Collapsed;

    public static string FormatDuration(TimeSpan d) =>
        d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss") : d.ToString(@"m\:ss");

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Группа карточек с заголовком («Сегодня», «Вчера», «14 июля»).</summary>
public sealed class ClipGroup(string title, List<ClipItem> items)
{
    public string Title { get; } = title;
    public List<ClipItem> Items { get; } = items;

    public string Subtitle
    {
        get
        {
            long bytes = 0;
            foreach (var i in Items) bytes += i.SizeBytes;
            return $"{Items.Count} · {ByteSize.Format(bytes)}";
        }
    }
}

/// <summary>Чтение папки записей: список файлов, группировка по датам, служебные операции.</summary>
public static class ClipLibrary
{
    private static readonly string[] Extensions = [".mp4", ".png", ".jpg", ".jpeg"];

    /// <summary>
    /// Все записи и скриншоты в папке (рекурсивно), новые первыми.
    /// Вызывать из фонового потока: на большой коллекции это тысячи FileInfo.
    /// </summary>
    public static List<ClipItem> Scan(string root)
    {
        var list = new List<ClipItem>();
        try
        {
            if (!Directory.Exists(root)) return list;
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                string ext = Path.GetExtension(file);
                if (!Extensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) continue;
                // Файл может исчезнуть между перечислением и FileInfo (автоочистка) —
                // это не повод ронять весь список.
                try { list.Add(new ClipItem(file, root)); } catch { }
            }
        }
        catch (Exception ex) { Log.Warn("Library", $"Сканирование {root}: {ex.Message}"); }

        list.Sort((a, b) => b.Created.CompareTo(a.Created));
        return list;
    }

    /// <summary>Разбить упорядоченный список на группы по дате записи.</summary>
    public static List<ClipGroup> GroupByDate(List<ClipItem> items)
    {
        var groups = new List<ClipGroup>();
        string? currentTitle = null;
        List<ClipItem>? current = null;

        foreach (var item in items)
        {
            string title = ClipGrouping.DateTitle(item.Created, DateTime.Today);
            if (title != currentTitle)
            {
                current = [];
                currentTitle = title;
                groups.Add(new ClipGroup(title, current));
            }
            current!.Add(item);
        }
        return groups;
    }

}
