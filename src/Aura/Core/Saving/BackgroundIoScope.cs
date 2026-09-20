using Aura.Core.Interop;
using Aura.Core.Logging;

namespace Aura.Core.Saving;

/// <summary>
/// Переводит текущий поток в фоновый режим Windows на время ЗАЛПА данных на диск.
///
/// Зачем: сохранение клипа — это сотни мегабайт на диск за пару секунд. На пустом
/// рабочем столе это никому не мешает, а вот в игре такой залп ввода-вывода
/// конкурирует с подгрузкой ресурсов и даёт просадки кадров. THREAD_MODE_BACKGROUND_BEGIN
/// опускает потоку приоритет ДИСКОВЫХ операций (обычный ThreadPriority на них не влияет),
/// поэтому запись идёт «в щели» между обращениями игры к диску.
///
/// ГРАНИЦЫ РЕЖИМА — ТОЛЬКО ПОДАЧА СЭМПЛОВ.
///
/// Раньше фоновый режим включался на ВСЁ сохранение целиком, вместе с построением
/// писателя. Это стоило пользователю 27.6 секунды заморозки на живой машине:
///
///     [Saver] Запись файла заняла 28648 мс (открытие 27659, видео 100, аудио 0,
///             финализация 889)
///
/// Открытие — это MFCreateFile, MFCreateSinkWriterFromURL, создание AAC-энкодера и
/// BeginWriting: загрузка DLL кодеков, чтение реестра, активация COM-объектов. Данных
/// там нет вовсе, зато есть общие с остальным процессом блокировки Media Foundation
/// и загрузчика образов. Документация SetThreadPriority предупреждает ровно об этом:
/// «When a thread is in background processing mode, it should minimize sharing
/// resources such as critical sections, heaps, and handles with other threads in the
/// process, otherwise priority inversions can occur». Потоки захвата и кодирования
/// идут с AboveNormal и работают через ту же Media Foundation — получалась инверсия
/// приоритетов, и вставал весь конвейер, а не только сохранение.
///
/// Поэтому режим включается ТОЛЬКО вокруг цикла подачи сэмплов (см. ReplaySaver):
/// открытие писателя и финализация контейнера идут на обычном приоритете. Там счёт
/// идёт на сотни миллисекунд, игре это незаметно, а 27-секундная инверсия исключена.
///
/// Включаем только когда в фокусе игра: на рабочем столе тормозить сохранение незачем.
/// </summary>
internal static class BackgroundIoScope
{
    /// <summary>
    /// В фоновом ли режиме ТЕКУЩИЙ поток.
    ///
    /// Нужен по двум причинам. Первая: писатель выходит из режима досрочно
    /// (см. <see cref="ReleaseForCurrentThread"/>), и повторный выход обязан быть
    /// безвредным. Вторая: THREAD_MODE_BACKGROUND_BEGIN на потоке, который УЖЕ в
    /// фоновом режиме, возвращает ошибку ERROR_THREAD_MODE_ALREADY_BACKGROUND —
    /// вложенный вызов не должен ни падать, ни «съедать» чужой выход из режима.
    /// </summary>
    [ThreadStatic] private static bool _active;

    /// <summary>В фоновом ли режиме текущий поток прямо сейчас.</summary>
    public static bool IsActive => _active;

    /// <summary>
    /// Ввести текущий поток в фоновый режим. Возвращает true, если режим включился
    /// именно этим вызовом — значит, этот же код обязан его и выключить.
    ///
    /// Поток сохранения берётся из пула потоков и вернётся туда же, поэтому выход
    /// из режима обязателен на ЛЮБОМ пути, включая исключение.
    /// </summary>
    public static bool EnterIf(bool condition)
    {
        if (!condition || _active) return false;
        try
        {
            bool ok = NativeMethods.SetThreadPriority(
                NativeMethods.GetCurrentThread(), NativeMethods.THREAD_MODE_BACKGROUND_BEGIN);
            if (!ok) Log.Warn("Saver", "Фоновый режим ввода-вывода не включился");
            _active = ok;
            return ok;
        }
        catch (Exception ex)
        {
            Log.Warn("Saver", $"Фоновый режим ввода-вывода: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Выйти из фонового режима. Возвращает true, если поток в нём был.
    /// Безопасно звать сколько угодно раз, в том числе из finally.
    ///
    /// ЗАЧЕМ ДОСРОЧНЫЙ ВЫХОД. THREAD_MODE_BACKGROUND_BEGIN опускает не только приоритет
    /// дисковых операций, но и приоритет планировщика, и приоритет страниц памяти.
    /// В замерах на рабочем столе клип в 570 МБ писался 992 мс (575 МБ/с), а в игре
    /// клип в 347 МБ — 8249 мс (42 МБ/с). Разница больше чем в десять раз.
    ///
    /// Короткий залп в фоновом режиме игре не мешает и стоит недорого. Но когда
    /// сохранение затягивается, вежливость превращается в свою противоположность:
    /// пользователь ждёт клип секундами, а блоки арены остаются закреплёнными всё
    /// это время (см. UnpinWhenWriterDone). Поэтому первые полторы секунды пишем
    /// тихо, а дальше возвращаемся к обычному приоритету и дописываем в полную силу.
    ///
    /// Приоритет при выходе восстанавливается системой на тот, что был ДО входа:
    /// «The system restores the resource scheduling priorities of the thread as they
    /// were before the thread entered background processing mode». То есть поток
    /// сохранения вернётся к BelowNormal — процессор игре мы всё равно не отбираем.
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
}
