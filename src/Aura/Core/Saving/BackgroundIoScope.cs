using Aura.Core.Interop;
using Aura.Core.Logging;

namespace Aura.Core.Saving;

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
    /// <summary>
    /// В фоновом ли режиме ТЕКУЩИЙ поток. Нужно, чтобы писатель мог выйти из него
    /// досрочно (см. <see cref="ReleaseForCurrentThread"/>), а Dispose после этого
    /// не звал THREAD_MODE_BACKGROUND_END второй раз.
    /// </summary>
    [ThreadStatic] private static bool _active;

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
            _active = ok;
            return new BackgroundIoScope(ok);
        }
        catch (Exception ex)
        {
            Log.Warn("Saver", $"Фоновый режим ввода-вывода: {ex.Message}");
            return new BackgroundIoScope(false);
        }
    }

    /// <summary>
    /// Выйти из фонового режима досрочно. Возвращает true, если поток в нём был.
    ///
    /// ЗАЧЕМ. THREAD_MODE_BACKGROUND_BEGIN опускает не только приоритет дисковых
    /// операций, но и приоритет планировщика, и приоритет памяти. В замерах на
    /// рабочем столе клип в 570 МБ писался 992 мс (575 МБ/с), а в игре клип в
    /// 347 МБ — 8249 мс (42 МБ/с). Разница больше чем в десять раз.
    ///
    /// Короткий залп в фоновом режиме игре не мешает и стоит недорого. Но когда
    /// сохранение затягивается, вежливость превращается в свою противоположность:
    /// пользователь ждёт клип секундами, а блоки арены остаются закреплёнными всё
    /// это время (см. UnpinWhenWriterDone). Поэтому первые полторы секунды пишем
    /// тихо, а дальше возвращаемся к обычному приоритету и дописываем в полную силу.
    /// Приоритет потока при выходе возвращается к тому, что было до входа, то есть
    /// к BelowNormal, — процессор игре мы всё равно не отбираем.
    /// </summary>
    public static bool ReleaseForCurrentThread()
    {
        if (!_active) return false;
        _active = false;
        try
        {
            NativeMethods.SetThreadPriority(
                NativeMethods.GetCurrentThread(), NativeMethods.THREAD_MODE_BACKGROUND_END);
        }
        catch { }
        return true;
    }

    public void Dispose()
    {
        if (!_entered) return;
        // Выйти обязательно: поток возвращается в общий пул потоков приложения
        ReleaseForCurrentThread();
    }
}
