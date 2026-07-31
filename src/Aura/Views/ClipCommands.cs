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

    /// <summary>Библиотека изменилась — страницам пора перечитать список.</summary>
    public static event Action? LibraryChanged;

    /// <summary>Сообщить страницам, что в папке записей что-то поменялось.</summary>
    public static void NotifyLibraryChanged() => LibraryChanged?.Invoke();

    /// <summary>Оставлено для совместимости вызова из App: регистрация больше не нужна.</summary>
    public static void Register() { }

    private sealed class ClipAction(Action<ClipItem> action) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => parameter is ClipItem;
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
}
