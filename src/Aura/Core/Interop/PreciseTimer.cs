using System.Runtime.InteropServices;

namespace Aura.Core.Interop;

/// <summary>
/// Точное ожидание без глобального timeBeginPeriod(1).
///
/// ЗАЧЕМ. Раньше точность Thread.Sleep добывалась вызовом timeBeginPeriod(1) на всё
/// время жизни процесса. Это заставляет систему тикать таймером 1 мс даже тогда,
/// когда повтор выключен и приложение просто висит в трее, — на ноутбуке это
/// заметный расход батареи. Высокоточный ожидаемый таймер
/// (CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, Windows 10 1803+) даёт ту же точность
/// только тому потоку, который ждёт, и только на время ожидания.
///
/// Экземпляр принадлежит одному потоку: ожидание на одном дескрипторе из двух
/// потоков сразу смысла не имеет.
/// </summary>
public sealed partial class PreciseTimer : IDisposable
{
    private const uint CreateWaitableTimerHighResolution = 0x00000002;
    private const uint TimerAllAccess = 0x1F0003;
    private const uint Infinite = 0xFFFFFFFF;

    private IntPtr _handle;

    public PreciseTimer()
    {
        _handle = CreateWaitableTimerExW(IntPtr.Zero, IntPtr.Zero,
                                         CreateWaitableTimerHighResolution, TimerAllAccess);
    }

    /// <summary>Есть ли высокоточный таймер. Нет — ожидание идёт через Thread.Sleep.</summary>
    public bool IsHighResolution => _handle != IntPtr.Zero;

    /// <summary>Подождать указанное число 100-нс тиков.</summary>
    public void Wait(long ticks)
    {
        if (ticks <= 0) return;
        if (_handle == IntPtr.Zero)
        {
            Thread.Sleep((int)Math.Max(1, ticks / 10_000));
            return;
        }

        long due = -ticks; // отрицательное — относительное время
        if (!SetWaitableTimerEx(_handle, ref due, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0))
        {
            Thread.Sleep((int)Math.Max(1, ticks / 10_000));
            return;
        }
        WaitForSingleObject(_handle, Infinite);
    }

    public void Wait(TimeSpan span) => Wait(span.Ticks);

    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (handle != IntPtr.Zero) CloseHandle(handle);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr CreateWaitableTimerExW(IntPtr attributes, IntPtr name, uint flags, uint access);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWaitableTimerEx(IntPtr timer, ref long dueTime, int period,
        IntPtr completionRoutine, IntPtr argToCompletionRoutine, IntPtr wakeContext, uint tolerableDelay);

    [LibraryImport("kernel32.dll")]
    private static partial uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}
