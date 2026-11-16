using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Aura.Core.Audio;

/// <summary>
/// Звук одной программы, а не всего компьютера: loopback процесса (Windows 10
/// 20348+ и Windows 11). В запись игры тогда не попадают Discord, браузер и
/// системные звуки, только сама игра и её дочерние процессы.
///
/// NAudio этого не умеет: нужен ActivateAudioInterfaceAsync на виртуальном
/// устройстве «VAD\Process_Loopback» с параметрами активации в PROPVARIANT.
/// </summary>
internal static class ProcessLoopback
{
    private const string VirtualDevice = @"VAD\Process_Loopback";
    private static readonly Guid IidAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");

    /// <summary>Клиент WASAPI, который слышит только процесс <paramref name="processId"/> и его детей.</summary>
    public static AudioClient Activate(int processId, TimeSpan timeout)
    {
        var parameters = new ActivationParams
        {
            ActivationType = 1,            // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
            TargetProcessId = (uint)processId,
            ProcessLoopbackMode = 0        // PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE
        };
        IntPtr paramsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<ActivationParams>());
        IntPtr variantPtr = Marshal.AllocHGlobal(Marshal.SizeOf<BlobVariant>());
        bool deferredFree = false;
        try
        {
            Marshal.StructureToPtr(parameters, paramsPtr, false);
            Marshal.StructureToPtr(new BlobVariant
            {
                Vt = 65,                   // VT_BLOB
                Size = (uint)Marshal.SizeOf<ActivationParams>(),
                Data = paramsPtr
            }, variantPtr, false);

            var handler = new CompletionHandler(variantPtr, paramsPtr);
            ActivateAudioInterfaceAsync(VirtualDevice, IidAudioClient, variantPtr, handler, out _);
            deferredFree = true;
            if (!handler.Wait(timeout, out int hr, out object? activated))
                throw new TimeoutException("система не ответила на запрос звука программы");
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            return new AudioClient((IAudioClient)activated!);
        }
        finally
        {
            if (!deferredFree)
            {
                Marshal.FreeHGlobal(variantPtr);
                Marshal.FreeHGlobal(paramsPtr);
            }
        }
    }

    /// <summary>
    /// Процесс для записи по имени exe (без «.exe»). У игр часто несколько процессов
    /// с одним именем (лаунчер, помощник), поэтому берём тот, у кого есть окно,
    /// а среди них самый ранний: его дети попадут в запись и так.
    /// </summary>
    public static int? FindProcess(string name)
    {
        Process[] all = [];
        try
        {
            all = Process.GetProcessesByName(name);
            return all
                .OrderByDescending(p => SafeHasWindow(p))
                .ThenBy(p => SafeStart(p))
                .Select(p => (int?)p.Id)
                .FirstOrDefault();
        }
        catch { return null; }
        finally
        {
            foreach (var p in all) p.Dispose();
        }
    }

    private static bool SafeHasWindow(Process p)
    {
        try { return p.MainWindowHandle != IntPtr.Zero; } catch { return false; }
    }

    private static DateTime SafeStart(Process p)
    {
        try { return p.StartTime; } catch { return DateTime.MaxValue; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ActivationParams
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    /// <summary>PROPVARIANT с VT_BLOB: тип, три резервных слова, затем BLOB.</summary>
    [StructLayout(LayoutKind.Explicit)]
    private struct BlobVariant
    {
        [FieldOffset(0)] public ushort Vt;
        [FieldOffset(8)] public uint Size;
        [FieldOffset(16)] public IntPtr Data;
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    /// <summary>Система зовёт обработчик из своего потока: объект обязан быть «agile».</summary>
    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject { }

    [ClassInterface(ClassInterfaceType.None)]
    [ComVisible(true)]
    private sealed class CompletionHandler(IntPtr variantPtr, IntPtr paramsPtr) : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        private readonly object _gate = new();
        private readonly ManualResetEventSlim _done = new(false);
        private IntPtr _variantPtr = variantPtr;
        private IntPtr _paramsPtr = paramsPtr;
        private bool _abandoned;
        private bool _completed;
        private int _result;
        private object? _interface;

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            int hr;
            object? iface = null;
            try
            {
                activateOperation.GetActivateResult(out hr, out iface);
            }
            catch (Exception ex) { hr = ex.HResult; }

            bool releaseCom = false;
            lock (_gate)
            {
                _completed = true;
                FreeBuffersLocked();
                if (_abandoned)
                {
                    releaseCom = iface is not null;
                }
                else
                {
                    _result = hr;
                    _interface = iface;
                    _done.Set();
                }
            }
            if (releaseCom && iface is not null && Marshal.IsComObject(iface))
                Marshal.ReleaseComObject(iface);
        }

        public bool Wait(TimeSpan timeout, out int result, out object? activated)
        {
            bool signaled = _done.Wait(timeout);
            object? toRelease = null;
            lock (_gate)
            {
                if (!signaled && !_completed)
                {
                    _abandoned = true;
                    result = 0;
                    activated = null;
                    return false;
                }
                FreeBuffersLocked();
                if (!signaled)
                {
                    toRelease = _interface;
                    _interface = null;
                    result = 0;
                    activated = null;
                }
                else
                {
                    result = _result;
                    activated = _interface;
                    _interface = null;
                }
            }
            _done.Dispose();
            if (toRelease is not null && Marshal.IsComObject(toRelease))
                Marshal.ReleaseComObject(toRelease);
            return signaled;
        }

        private void FreeBuffersLocked()
        {
            if (_variantPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_variantPtr);
                _variantPtr = IntPtr.Zero;
            }
            if (_paramsPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_paramsPtr);
                _paramsPtr = IntPtr.Zero;
            }
        }
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);
}
