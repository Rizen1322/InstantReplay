namespace InstantReplay.Core.Library;

/// <summary>
/// Заголовки секций панорамы: «Сегодня», «Вчера», «14 июля», «14 июля 2025».
/// Отдельно от <see cref="ClipLibrary"/>, потому что тут нет ни файлов, ни UI —
/// чистая функция от даты, покрытая тестами.
/// </summary>
public static class ClipGrouping
{
    public static string DateTitle(DateTime when, DateTime today)
    {
        var day = when.Date;
        today = today.Date;

        if (day == today) return "Сегодня";
        if (day == today.AddDays(-1)) return "Вчера";
        return day.Year == today.Year
            ? day.ToString("d MMMM")
            : day.ToString("d MMMM yyyy");
    }
}
