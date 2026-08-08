using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using Aura.Core.Library;
using Aura.Core.Logging;

namespace Aura.Views;

/// <summary>
/// Действия над записью из контекстного меню карточки.
///
/// Команды, а не обработчики: карточка живёт в шаблоне из словаря ресурсов, у него
/// нет code-behind. Привязки зарегистрированы на PageBase, поэтому меню работает
/// одинаково и в «Клипах», и в «Последних записях» на обзоре.
/// </summary>
public static class ClipCommands
{
    // Обычные ICommand, а не RoutedCommand: маршрутизируемая команда из всплывающего
    // меню ищет обработчик по фокусу, а у popup своё дерево — пункты открывались
    // серыми и не нажимались. Здесь выполнение не зависит ни от фокуса, ни от дерева.
    public static ICommand Open { get; } = new ClipAction(item => OpenFile(item.FullPath));
    public static ICommand Reveal { get; } = new ClipAction(item => RevealFile(item.FullPath));
    public static ICommand CopyPath { get; } = new ClipAction(item => CopyToClipboard(item.FullPath));
    public static ICommand Rename { get; } = new ClipAction(RenameAsync);
    public static ICommand Delete { get; } = new ClipAction(DeleteAsync);

    /// <summary>Открыть в LosslessCut. Для скриншотов пункт неактивен.</summary>
    public static ICommand Trim { get; } = new ClipAction(TrimInLosslessCut, item => !item.IsScreenshot);

    /// <summary>Пережать под лимит вложения. Скриншоты и так лёгкие.</summary>
    public static ICommand Compress { get; } = new ClipAction(CompressAsync, item => !item.IsScreenshot);

    // ---------- Действия над ВЫДЕЛЕНИЕМ ----------
    //
    // Отдельные команды, а не те же самые: у групповых операций другой вопрос
    // пользователю («удалить 7 записей» вместо имени файла) и другая цена ошибки.

    /// <summary>Удалить все выделенные записи в корзину — один вопрос на всех.</summary>
    public static ICommand DeleteMany { get; } = new ClipsAction(DeleteManyAsync);

    /// <summary>Скопировать пути выделенных записей, по одному на строку.</summary>
    public static ICommand CopyPathsMany { get; } = new ClipsAction(items =>
        CopyToClipboard(string.Join(Environment.NewLine, items.Select(i => i.FullPath))));

    /// <summary>Библиотека изменилась — страницам пора перечитать список.</summary>
    public static event Action? LibraryChanged;

    /// <summary>Сообщить страницам, что в папке записей что-то поменялось.</summary>
    public static void NotifyLibraryChanged() => LibraryChanged?.Invoke();

    /// <summary>Оставлено для совместимости вызова из App: регистрация больше не нужна.</summary>
    public static void Register() { }

    private sealed class ClipsAction(Action<IReadOnlyList<ClipItem>> action) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => parameter is IReadOnlyList<ClipItem> { Count: > 0 };
        public void Execute(object? parameter)
        {
            if (parameter is IReadOnlyList<ClipItem> items && items.Count > 0) action(items);
        }
    }

    private sealed class ClipAction(Action<ClipItem> action, Func<ClipItem, bool>? enabled = null) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) =>
            parameter is ClipItem item && (enabled?.Invoke(item) ?? true);
        public void Execute(object? parameter)
        {
            if (parameter is ClipItem item) action(item);
        }
    }

    /// <summary>
    /// Открываем через explorer.exe, а не прямым запуском: приложение работает с
    /// правами администратора, а упакованные плееры Windows из такого процесса
    /// активируются криво и жалуются на «файл не найден».
    /// </summary>
    private static void OpenFile(string path) => Shell($"\"{path}\"");

    private static void RevealFile(string path) => Shell($"/select,\"{path}\"");

    private static void Shell(string args)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn("Library", $"explorer.exe {args}: {ex.Message}"); }
    }

    private static void CopyToClipboard(string text)
    {
        try { Clipboard.SetText(text); }
        catch (Exception ex) { Log.Warn("Library", $"Копирование пути: {ex.Message}"); }
    }

    private static async void RenameAsync(ClipItem item)
    {
        string? name = Dialogs.Prompt("Новое имя файла", item.Title, "Переименовать");
        if (name is null) return;

        name = name.Trim();
        if (name.Length == 0 || name == item.Title) return;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            Dialogs.Say("Недопустимое имя", "В имени файла есть символы, которые Windows не разрешает.");
            return;
        }

        string target = Path.Combine(Path.GetDirectoryName(item.FullPath)!, name + Path.GetExtension(item.FullPath));
        try
        {
            if (File.Exists(target))
            {
                Dialogs.Say("Файл уже есть", "В этой папке уже лежит файл с таким именем.");
                return;
            }
            await Task.Run(() => File.Move(item.FullPath, target));
            ClipThumbnails.Forget(item);                 // ключ кэша построен на старом пути
            Services.Storage.Rename(item.FullPath, target);
            LibraryChanged?.Invoke();
        }
        catch (Exception ex) { Dialogs.Say("Не удалось переименовать", ex.Message); }
    }

    // ---------------- Обрезка во внешней программе ----------------

    /// <summary>
    /// Открыть клип в LosslessCut — он режет без перекодирования, то есть быстро
    /// и без потери качества. Путь спрашиваем один раз: сначала ищем сами по
    /// обычным местам установки, и только если не нашли — просим показать файл.
    /// </summary>
    private static void TrimInLosslessCut(ClipItem item)
    {
        string? exe = Services.Settings.Current.LosslessCutPath;
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
        {
            exe = FindLosslessCut() ?? AskForLosslessCut();
            if (exe is null) return;
            Services.Settings.Current.LosslessCutPath = exe;
            Services.Settings.Save("tools");
        }

        try
        {
            Process.Start(new ProcessStartInfo(exe)
            {
                ArgumentList = { item.FullPath },
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exe)!
            });
        }
        catch (Exception ex)
        {
            Dialogs.Say("Не удалось открыть LosslessCut", ex.Message);
        }
    }

    /// <summary>Обычные места установки LosslessCut: установщик, portable, Scoop.</summary>
    public static string? FindLosslessCut()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programsX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var candidates = new List<string>
        {
            Path.Combine(local, "Programs", "losslesscut", "LosslessCut.exe"),
            Path.Combine(local, "Programs", "LosslessCut", "LosslessCut.exe"),
            Path.Combine(local, "LosslessCut", "LosslessCut.exe"),
            Path.Combine(programs, "LosslessCut", "LosslessCut.exe"),
            Path.Combine(programsX86, "LosslessCut", "LosslessCut.exe"),
            Path.Combine(profile, "scoop", "apps", "losslesscut", "current", "LosslessCut.exe"),
        };

        foreach (string path in candidates)
            if (File.Exists(path)) return path;

        // Портативная распаковка рядом: ...\LosslessCut-win-x64\LosslessCut.exe
        foreach (string root in new[] { Path.Combine(local, "Programs"), programs, profile })
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (string dir in Directory.EnumerateDirectories(root, "LosslessCut*"))
                {
                    string exe = Path.Combine(dir, "LosslessCut.exe");
                    if (File.Exists(exe)) return exe;
                }
            }
            catch { }

        return null;
    }

    /// <summary>Просим показать LosslessCut.exe и объясняем, зачем это нужно.</summary>
    private static string? AskForLosslessCut()
    {
        bool ok = Dialogs.Ask("Где лежит LosslessCut?",
            "Обрезка открывается в LosslessCut — он режет видео без перекодирования. " +
            "Покажи его файл LosslessCut.exe, дальше Aura запомнит путь.", "Выбрать");
        if (!ok) return null;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "LosslessCut.exe",
            Filter = "LosslessCut|LosslessCut.exe|Программы (*.exe)|*.exe"
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    // ---------------- Сжатие под вложение ----------------

    private static bool _compressing;

    /// <summary>
    /// Пережать клип так, чтобы он влез во вложение Discord. Считает битрейт по
    /// длительности и при нехватке опускает разрешение — 1440p на 400 кбит/с
    /// выглядит хуже, чем 720p на тех же килобитах.
    /// </summary>
    private static async void CompressAsync(ClipItem item)
    {
        if (_compressing)
        {
            Dialogs.Say("Уже сжимаю", "Дождись, пока закончится предыдущий клип.");
            return;
        }

        var settings = Services.Settings.Current;
        string? ffmpeg = Core.Tools.Ffmpeg.Find(settings.FfmpegPath, settings.LosslessCutPath);
        if (ffmpeg is null)
        {
            bool ok = Dialogs.Ask("Нужен ffmpeg",
                "Сжатие делает ffmpeg — он лежит рядом с LosslessCut. Покажи ffmpeg.exe, дальше Aura запомнит путь.",
                "Выбрать");
            if (!ok) return;

            var dialog = new Microsoft.Win32.OpenFileDialog { Title = "ffmpeg.exe", Filter = "ffmpeg|ffmpeg.exe" };
            if (dialog.ShowDialog() != true) return;
            ffmpeg = dialog.FileName;
            settings.FfmpegPath = ffmpeg;
            Services.Settings.Save("tools");
        }

        var duration = item.Duration ?? await ReadDurationAsync(item.FullPath);
        if (duration is null || duration.Value <= TimeSpan.Zero)
        {
            Dialogs.Say("Не получилось", "Не удалось определить длительность клипа.");
            return;
        }

        int limit = Math.Max(2, settings.AttachmentSizeMb);
        _compressing = true;
        // Сжатие идёт минутами — капсуле нельзя разворачиваться по десятисекундному таймеру
        Services.Notifications.ShowSaving($"Сжимаю до {limit} МБ…", maxWaitSeconds: 900);
        try
        {
            string output = await Core.Tools.Ffmpeg.CompressAsync(ffmpeg, item.FullPath, duration.Value, limit);
            long size = new FileInfo(output).Length;
            Services.Storage.RegisterSaved(output);
            Services.Notifications.CompleteSaving("Готово для отправки", Core.Storage.ByteSize.Format(size));
            LibraryChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Services.Notifications.Show(Core.Notifications.NotificationKind.Warning, "Не удалось сжать", ex.Message);
            Log.Warn("Ffmpeg", ex.Message);
        }
        finally { _compressing = false; }
    }

    /// <summary>Длительность файла у системы — если её ещё не подтянула карточка.</summary>
    private static async Task<TimeSpan?> ReadDurationAsync(string path)
    {
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            var props = await file.Properties.GetVideoPropertiesAsync();
            return props.Duration > TimeSpan.Zero ? props.Duration : null;
        }
        catch { return null; }
    }

    private static async void DeleteAsync(ClipItem item)
    {
        bool ok = Dialogs.Ask("Удалить запись?",
            $"«{item.FileName}» ({item.SizeText}) уедет в корзину — оттуда её можно вернуть.", "Удалить");
        if (!ok) return;

        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(item.FullPath);
            // Default вместо PermanentDelete — файл уходит в корзину
            await file.DeleteAsync(Windows.Storage.StorageDeleteOption.Default);

            ClipThumbnails.Forget(item);
            Services.Storage.Forget(item.FullPath);      // индекс папки и статистика
            LibraryChanged?.Invoke();
        }
        catch (Exception ex) { Dialogs.Say("Не удалось удалить", ex.Message); }
    }

    /// <summary>
    /// Удаление выделенного. Вопрос задаётся один раз на всю пачку, а список
    /// перечитывается тоже один раз — иначе страница перестраивалась бы на каждом
    /// файле, и на трёх десятках записей это заметная судорога.
    /// </summary>
    private static async void DeleteManyAsync(IReadOnlyList<ClipItem> items)
    {
        long bytes = items.Sum(i => i.SizeBytes);
        bool ok = Dialogs.Ask($"Удалить {Plural(items.Count)}?",
            $"{Core.Storage.ByteSize.Format(bytes)} уедут в корзину — оттуда их можно вернуть.", "Удалить");
        if (!ok) return;

        var failed = new List<string>();
        foreach (var item in items)
        {
            try
            {
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(item.FullPath);
                await file.DeleteAsync(Windows.Storage.StorageDeleteOption.Default);
                ClipThumbnails.Forget(item);
                Services.Storage.Forget(item.FullPath);
            }
            catch { failed.Add(item.FileName); }
        }

        LibraryChanged?.Invoke();
        if (failed.Count > 0)
            Dialogs.Say("Удалились не все",
                $"Осталось {failed.Count}: {string.Join(", ", failed.Take(5))}" +
                (failed.Count > 5 ? " и другие" : ""));
    }

    /// <summary>«7 записей» / «1 запись» / «2 записи» — по-русски.</summary>
    private static string Plural(int count) => (count % 10, count % 100) switch
    {
        (1, not 11) => $"{count} запись",
        (2 or 3 or 4, not (12 or 13 or 14)) => $"{count} записи",
        _ => $"{count} записей"
    };
}
