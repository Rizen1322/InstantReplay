namespace InstantReplay.Core.Storage;

/// <summary>
/// Имя и путь файла записи по шаблону из настроек.
/// Плейсхолдеры: {game} {date} {time} {preset}.
///
/// Вынесено из движка отдельной чистой функцией: это логика, которую легко сломать
/// незаметно (запрещённый символ в имени игры, коллизия имён при двух сохранениях
/// в одну секунду), и её проверяют тесты.
/// </summary>
public static class FileNaming
{
    public const string DefaultTemplate = "{game} {date} - {time}";

    /// <summary>Имя файла без расширения. Запрещённые символы заменяются на «_».</summary>
    public static string BuildName(string? template, string game, DateTime now, string preset, string fallbackPrefix)
    {
        string name = (string.IsNullOrWhiteSpace(template) ? DefaultTemplate : template)
            .Replace("{game}", game, StringComparison.OrdinalIgnoreCase)
            .Replace("{date}", now.ToString("yyyy-MM-dd"), StringComparison.OrdinalIgnoreCase)
            .Replace("{time}", now.ToString("HH-mm-ss"), StringComparison.OrdinalIgnoreCase)
            .Replace("{preset}", preset, StringComparison.OrdinalIgnoreCase)
            .Trim();

        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? $"{fallbackPrefix}_{now:yyyy-MM-dd_HH-mm-ss}" : name;
    }

    /// <summary>
    /// Полный путь с учётом раскладки по папкам игр и уникальности имени.
    /// exists — проверка существования файла (в тестах подменяется).
    /// </summary>
    public static string BuildPath(string root, bool groupByGame, string? template, string game,
                                   DateTime now, string preset, string fallbackPrefix,
                                   Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        string name = BuildName(template, game, now, preset, fallbackPrefix);
        string dir = groupByGame ? Path.Combine(root, game) : root;

        string path = Path.Combine(dir, name + ".mp4");
        for (int i = 2; exists(path); i++)
            path = Path.Combine(dir, $"{name} ({i}).mp4");
        return path;
    }
}
