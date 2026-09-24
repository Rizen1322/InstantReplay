using System.Runtime.InteropServices;
using Aura.Core.Logging;

namespace Aura.Core.Interop;

/// <summary>
/// Регистрация потока в Multimedia Class Scheduler Service (MMCSS).
///
/// ЗАЧЕМ. ThreadPriority.Highest — это всего лишь «высокий» в обычном классе
/// приоритетов, и под игрой, которая сама занимает все ядра, его поток всё равно
/// ждёт кванта наравне с потоками игры. MMCSS для этого и сделан: поток,
/// зарегистрированный под задачей «Pro Audio» или «Capture», планировщик поднимает
/// в диапазон реального времени на ограниченную долю процессора, и звук с кадрами
/// не рвутся, когда игра грузит систему. Так же поступают сама Windows (аудиодвижок)
/// и OBS.
///
/// Экземпляр живёт на потоке, который его создал, и снимает регистрацию в Dispose
/// на том же потоке.
/// </summary>
public sealed partial class Mmcss : IDisposable
{
    /// <summary>Задача для звука: минимальные задержки, приоритет выше обычного звука.</summary>
    public const string ProAudio = "Pro Audio";

    /// <summary>Задача для захвата и кодирования видео.</summary>
    public const string Capture = "Capture";

    private IntPtr _handle;

    private Mmcss(IntPtr handle) => _handle = handle;

    /// <summary>
    /// Зарегистрировать текущий поток. Неудача не ошибка: без MMCSS поток просто
    /// остаётся на своём обычном приоритете.
    /// </summary>
    public static Mmcss Join(string task, string threadName)
    {
        uint index = 0;
        IntPtr handle = IntPtr.Zero;
        try { handle = AvSetMmThreadCharacteristicsW(task, ref index); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // avrt.dll есть в любой поддерживаемой Windows, это страховка
        }

        if (handle == IntPtr.Zero)
            Log.Info("Mmcss", $"{threadName}: MMCSS «{task}» недоступен ({Marshal.GetLastWin32Error()})");
        return new Mmcss(handle);
    }

    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (handle != IntPtr.Zero) AvRevertMmThreadCharacteristics(handle);
    }

    [LibraryImport("avrt.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);

    [LibraryImport("avrt.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AvRevertMmThreadCharacteristics(IntPtr handle);
}
