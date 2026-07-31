namespace Aura.Core.Storage;

/// <summary>
/// Человекочитаемый размер файла. Отдельный файл без зависимостей: строкой пользуются
/// и UI, и логи, и тесты (тест-проект компилирует этот файл напрямую).
/// </summary>
public static class ByteSize
{
    public static string Format(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.##} ГБ",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.#} МБ",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0} КБ",
        _ => $"{bytes} Б"
    };
}
