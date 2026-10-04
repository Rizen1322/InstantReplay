using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace Aura.Core.Encoding;

/// <summary>
/// Входные кадры MFT, которые сами сообщают, когда энкодер их отпустил
/// (IMFTrackedSample, mfidl.h).
///
/// ЗАЧЕМ. Раньше слот пула под входным кадром освобождался, когда из MFT выходил
/// очередной сжатый кадр: «вышел один — значит, отпустил самый старый вход». Это
/// не договор Media Foundation: энкодер вправе держать входной кадр дольше
/// (просмотр вперёд, B-кадры, своя очередь), и тогда пул переписал бы пиксели
/// кадра, который MFT ещё читает. MFT становится рабочим путём именно тогда,
/// когда NVENC отказал, — то есть в самый неудачный момент.
///
/// Теперь каждый слот пула получает свой отслеживаемый образец (sample) с
/// текстурой слота. Перед подачей ему ставится владелец (SetAllocator): образец
/// держит на себе лишнюю ссылку и, когда её отпустят все остальные — и мы, и MFT,
/// — вызывает наш <see cref="Released"/>. Только тогда слот возвращается в пул.
/// Образцы переиспользуются, как и задумано у Microsoft для пулов образцов.
///
/// Vortice не описывает IMFTrackedSample, поэтому здесь прямые вызовы по vtable.
/// </summary>
internal sealed unsafe class MftTrackedSamples : IDisposable
{
    // IUnknown: 0 QueryInterface, 1 AddRef, 2 Release
    // IMFTrackedSample: 3 SetAllocator
    // IMFSample = IMFAttributes (3 + 30) + …: 36 SetSampleTime, 38 SetSampleDuration, 42 AddBuffer
    // IMFAsyncResult: 6 GetObject
    private const int VtSetAllocator = 3, VtSetSampleTime = 36, VtSetSampleDuration = 38, VtAddBuffer = 42;
    private const int VtResultGetObject = 6;

    private static readonly Guid IidSample = new("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4");
    private static readonly Guid IidTrackedSample = new("245bf8e9-0755-40f7-88a5-ae0f18d55e17");
    private static readonly Guid IidUnknown = new("00000000-0000-0000-c000-000000000046");
    private static readonly Guid IidAsyncCallback = new("a27003cf-2354-4f2a-8d6a-ab7cff15437e");

    /// <summary>MFT отпустил входной кадр с этой текстурой: её слот можно переписывать.</summary>
    public event Action<ID3D11Texture2D>? Released;

    /// <summary>Сколько раз MFT вернул образец (для лога при закрытии).</summary>
    public long ReleasedCount => Interlocked.Read(ref _releasedCount);
    private long _releasedCount;

    /// <summary>Сколько образцов заведено (по числу слотов, через которые прошли кадры).</summary>
    public int SampleCount { get { lock (_sync) return _byTexture.Count; } }

    private sealed class Entry(IntPtr sample, IntPtr identity, ID3D11Texture2D texture)
    {
        public IntPtr Sample = sample;          // наша ссылка, пока образец не в MFT
        public readonly IntPtr Identity = identity;
        public readonly ID3D11Texture2D Texture = texture;
        public bool InFlight;
    }

    private readonly object _sync = new();
    private readonly Dictionary<IntPtr, Entry> _byTexture = [];
    private readonly Dictionary<IntPtr, Entry> _byIdentity = [];
    private readonly IntPtr _callback;
    private GCHandle _self;
    private bool _disposed;

    public MftTrackedSamples()
    {
        _self = GCHandle.Alloc(this, GCHandleType.Weak);
        _callback = Marshal.AllocHGlobal(sizeof(CallbackObject));
        var obj = (CallbackObject*)_callback;
        obj->Vtable = CallbackVtable;
        obj->Owner = GCHandle.ToIntPtr(_self);
    }

    /// <summary>
    /// Подать кадр: образец слота с временем и владельцем. Возвращает обёртку для
    /// ProcessInput; её нужно отпустить (Dispose) сразу после вызова, удачного или нет.
    /// Если MFT кадр не взял, обработчик сработает при этом же Dispose и вернёт слот.
    /// </summary>
    public IMFSample Prepare(ID3D11Texture2D texture, long time, long duration)
    {
        Entry entry;
        lock (_sync)
        {
            if (!_byTexture.TryGetValue(texture.NativePointer, out entry!))
            {
                entry = Create(texture);
                _byTexture[texture.NativePointer] = entry;
                _byIdentity[entry.Identity] = entry;
            }
            if (entry.InFlight) throw new InvalidOperationException("образец слота ещё в энкодере");
            entry.InFlight = true;
        }
        IntPtr sample = entry.Sample;
        Check(Call<long>(sample, VtSetSampleTime, time));
        Check(Call<long>(sample, VtSetSampleDuration, duration));

        IntPtr tracked;
        Guid iid = IidTrackedSample;
        Check(QueryInterface(sample, &iid, &tracked));
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int>)Vtable(tracked)[VtSetAllocator])(tracked, _callback, IntPtr.Zero);
        Marshal.Release(tracked);
        Check(hr);

        // Обёртка для ProcessInput держит свою ссылку; нашу отдаём: дальше образец
        // принадлежит MFT и вернётся к нам через обработчик
        Marshal.AddRef(sample);
        var wrapper = new IMFSample(sample);
        lock (_sync) entry.Sample = IntPtr.Zero;
        Marshal.Release(sample);
        return wrapper;
    }

    private static Entry Create(ID3D11Texture2D texture)
    {
        Check(MFCreateTrackedSample(out IntPtr tracked));
        IntPtr sample, identity;
        Guid iid = IidSample, unknown = IidUnknown;
        Check(QueryInterface(tracked, &iid, &sample));
        Marshal.Release(tracked);
        Check(QueryInterface(sample, &unknown, &identity));
        Marshal.Release(identity);   // только как ключ: объект жив, пока жив образец

        using var buffer = MediaFactory.MFCreateDXGISurfaceBuffer(typeof(ID3D11Texture2D).GUID, texture, 0, false);
        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Vtable(sample)[VtAddBuffer])(sample, buffer.NativePointer));
        return new Entry(sample, identity, texture);
    }

    /// <summary>Образец вернулся от MFT (обработчик владельца).</summary>
    private void OnInvoke(IntPtr asyncResult)
    {
        IntPtr obj;
        if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Vtable(asyncResult)[VtResultGetObject])(asyncResult, &obj) < 0 ||
            obj == IntPtr.Zero)
            return;
        IntPtr identity;
        Guid unknown = IidUnknown;
        QueryInterface(obj, &unknown, &identity);
        if (identity != IntPtr.Zero) Marshal.Release(identity);

        Entry? entry;
        IntPtr keep = IntPtr.Zero;
        lock (_sync)
        {
            if (!_byIdentity.TryGetValue(identity, out entry))
            {
                Marshal.Release(obj);
                return;
            }
            entry.InFlight = false;
            if (_disposed) Marshal.Release(obj);            // пул закрыт: образцу пора умереть
            else
            {
                // Ссылка из GetObject становится нашей: образец ждёт следующий кадр слота
                Guid iid = IidSample;
                QueryInterface(obj, &iid, &keep);
                Marshal.Release(obj);
                entry.Sample = keep;
            }
        }
        Interlocked.Increment(ref _releasedCount);
        Released?.Invoke(entry.Texture);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            // Свободные образцы отпускаем; те, что ещё в MFT, отпустятся при его
            // уничтожении и через обработчик (он видит _disposed)
            foreach (var entry in _byTexture.Values)
                if (entry.Sample != IntPtr.Zero) { Marshal.Release(entry.Sample); entry.Sample = IntPtr.Zero; }
        }
        // Обработчик и его память живут до конца процесса: MFT, брошенный с
        // висящим потоком, может отпустить образец когда угодно позже
    }

    // ---------------- обработчик IMFAsyncCallback ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct CallbackObject
    {
        public IntPtr* Vtable;
        public IntPtr Owner;   // GCHandle (Weak) на MftTrackedSamples
    }

    private static readonly IntPtr* CallbackVtable = CreateVtable();

    private static IntPtr* CreateVtable()
    {
        var vtable = (IntPtr*)NativeMemory.Alloc(5, (nuint)IntPtr.Size);
        vtable[0] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)&CbQueryInterface;
        vtable[1] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&CbAddRef;
        vtable[2] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&CbRelease;
        vtable[3] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint*, uint*, int>)&CbGetParameters;
        vtable[4] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)&CbInvoke;
        return vtable;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CbQueryInterface(IntPtr self, Guid* iid, IntPtr* result)
    {
        if (*iid == IidUnknown || *iid == IidAsyncCallback) { *result = self; return 0; }
        *result = IntPtr.Zero;
        return unchecked((int)0x80004002);   // E_NOINTERFACE
    }

    // Объект обработчика живёт до конца процесса: счёт ссылок не нужен
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint CbAddRef(IntPtr self) => 2;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint CbRelease(IntPtr self) => 1;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CbGetParameters(IntPtr self, uint* flags, uint* queue) => unchecked((int)0x80004001); // E_NOTIMPL

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CbInvoke(IntPtr self, IntPtr asyncResult)
    {
        try
        {
            var handle = GCHandle.FromIntPtr(((CallbackObject*)self)->Owner);
            if (handle.Target is MftTrackedSamples owner) owner.OnInvoke(asyncResult);
        }
        catch { /* исключение не должно уйти в код Media Foundation */ }
        return 0;
    }

    // ---------------- вызовы по vtable ----------------

    private static IntPtr* Vtable(IntPtr obj) => *(IntPtr**)obj;

    private static int QueryInterface(IntPtr obj, Guid* iid, IntPtr* result) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Vtable(obj)[0])(obj, iid, result);

    private static int Call<T>(IntPtr obj, int slot, T value) where T : unmanaged =>
        ((delegate* unmanaged[Stdcall]<IntPtr, T, int>)Vtable(obj)[slot])(obj, value);

    private static void Check(int hr) => Marshal.ThrowExceptionForHR(hr);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateTrackedSample(out IntPtr trackedSample);
}
