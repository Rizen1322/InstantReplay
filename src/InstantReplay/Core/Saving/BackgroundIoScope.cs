using InstantReplay.Core.Interop;
using InstantReplay.Core.Logging;

namespace InstantReplay.Core.Saving;

/// <summary>
/// Переводит текущий поток в фоновый режим Windows на время записи файла.
///
/// Зачем: сохранение клипа — это сотни мегабайт на диск за пару секунд. На пустом
/// рабочем столе это никому не мешает, а вот в игре такой залп ввода-вывода
/// конкурирует с подгрузкой ресурсов и даёт просадки кадров. THREAD_MODE_BACKGROUND_BEGIN
/// опускает потоку приоритет ДИСКОВЫХ операций (обычный ThreadPriority на них не влияет),
/// поэтому запись идёт «в щели» между обращениями игры к диску.
///
/// Включаем только когда в фокусе игра: на рабочем столе тормозить сохранение незачем.
/// </summary>
internal readonly struct BackgroundIoScope : IDisposable
{
    private readonly bool _entered;

    private BackgroundIoScope(bool entered) => _entered = entered;

    public static BackgroundIoScope BeginIf(bool condition)
    {
        if (!condition) return new BackgroundIoScope(false);
        try
        {
            bool ok = NativeMethods.SetThreadPriority(
                NativeMethods.GetCurrentThread(), NativeMethods.THREAD_MODE_BACKGROUND_BEGIN);
            if (!ok) Log.Warn("Saver", "Фоновый режим ввода-вывода не включился");
            return new BackgroundIoScope(ok);
        }
        catch (Exception ex)
        {
            Log.Warn("Saver", $"Фоновый режим ввода-вывода: {ex.Message}");
            return new BackgroundIoScope(false);
        }
    }

    public void Dispose()
    {
        if (!_entered) return;
        // Выйти обязательно: поток возвращается в общий пул потоков приложения
        try
        {
            NativeMethods.SetThreadPriority(
                NativeMethods.GetCurrentThread(), NativeMethods.THREAD_MODE_BACKGROUND_END);
        }
        catch { }
    }
}
