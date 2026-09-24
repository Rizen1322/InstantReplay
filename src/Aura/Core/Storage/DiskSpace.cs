using System.Runtime.InteropServices;

namespace Aura.Core.Storage;

/// <summary>На диске для записей не хватает места — запись не начинается.</summary>
public sealed class InsufficientDiskSpaceException(string message) : IOException(message);

/// <summary>
/// Свободное место на диске папки записей и проверка «хватит ли на запись».
///
/// ЗАЧЕМ ОТДЕЛЬНО. Повтор копится в памяти, а о нехватке места узнавали только в
/// момент сохранения: буфер к тому времени уже вырезан, файл обрывается на
/// полпути, и клип пропадает ровно тогда, когда человек его захотел. Честнее не
/// включать запись вовсе и сказать об этом сразу.
///
/// GetDiskFreeSpaceEx, а не DriveInfo: DriveInfo не понимает сетевые пути
/// (\\server\share) и точки монтирования, а папку записей нередко переносят именно
/// туда. Функция принимает любую существующую папку и учитывает квоты пользователя.
/// </summary>
public static partial class DiskSpace
{
    /// <summary>Запас сверх расчётного размера: файловой системе и системе тоже нужно место.</summary>
    public const long SafetyMarginBytes = 256L << 20;

    /// <summary>Минимум для обычной записи в файл: хотя бы пара минут на высоком битрейте.</summary>
    public const long RecordingMinimumBytes = 1L << 30;

    /// <summary>Свободно байт для текущего пользователя; null — узнать не удалось.</summary>
    public static long? FreeBytes(string path)
    {
        try
        {
            string? dir = Path.GetFullPath(path);
            // Папки может ещё не быть (её создадут при первом сохранении) — спрашиваем
            // ближайшую существующую выше по дереву, это тот же том.
            while (dir is not null && !Directory.Exists(dir))
                dir = Path.GetDirectoryName(dir);
            if (dir is null) return null;

            if (!GetDiskFreeSpaceExW(dir.EndsWith('\\') ? dir : dir + "\\",
                                     out ulong available, out _, out _))
                return null;
            return (long)Math.Min(available, long.MaxValue);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Сколько места нужно под один клип повтора заданной длины: видео по
    /// среднему битрейту, звук AAC и запас. VBR с потолком держит среднее, поэтому
    /// среднего и достаточно; на GOP сверх длины добавляем пару секунд.
    /// </summary>
    public static long ReplayClipBytes(long bitrateBps, int seconds)
    {
        const long AudioBytesPerSecond = 3 * 24_000; // до трёх дорожек AAC по 192 кбит/с
        long clipSeconds = Math.Max(1, seconds) + 2;
        return bitrateBps / 8 * clipSeconds + AudioBytesPerSecond * clipSeconds;
    }

    /// <summary>
    /// Бросить <see cref="InsufficientDiskSpaceException"/>, если свободно меньше
    /// <paramref name="neededBytes"/> плюс запас. Если свободное место узнать не
    /// удалось (сетевой диск без ответа), запись НЕ блокируем: ложный отказ хуже.
    /// </summary>
    public static void Require(string path, long neededBytes, string what)
    {
        long? free = FreeBytes(path);
        if (free is not long available) return;

        long required = neededBytes + SafetyMarginBytes;
        if (available >= required) return;

        string root;
        try { root = Path.GetPathRoot(Path.GetFullPath(path)) ?? path; }
        catch { root = path; }
        throw new InsufficientDiskSpaceException(
            $"Нет места на диске {root.TrimEnd('\\')}: свободно {ByteSize.Format(available)}, " +
            $"для {what} нужно {ByteSize.Format(required)}. Освободите место или выберите " +
            "другую папку записей на вкладке «Файлы».");
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpaceExW(string directory,
        out ulong freeBytesAvailable, out ulong totalBytes, out ulong totalFreeBytes);
}
