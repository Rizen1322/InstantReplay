using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Aura.Core.Interop;

// ВАЖНО (грабли из практики): CsWinRT-интероп с нативными COM-интерфейсами
// (IGraphicsCaptureItemInterop, IDirect3DDxgiInterfaceAccess) делается через
// RoGetActivationFactory + Marshal.GetObjectForIUnknown / GraphicsCaptureItem.FromAbi,
// а НЕ через "очевидные" .As<T>() — те падают или возвращают мусор.

[ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IGraphicsCaptureItemInterop
{
    IntPtr CreateForWindow(IntPtr window, ref Guid iid);
    IntPtr CreateForMonitor(IntPtr monitor, ref Guid iid);
}

[ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDirect3DDxgiInterfaceAccess
{
    IntPtr GetInterface(ref Guid iid);
}


internal static class CaptureInterop
{
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid ID3D11Texture2DIid = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
    private static readonly Guid IInspectableIid = new("AF86E2E0-B12D-4C6A-9C5A-D7AA65101E90");

    private static T GetActivationFactory<T>(string className)
    {
        Marshal.ThrowExceptionForHR(NativeMethods.WindowsCreateString(className, className.Length, out var hstr));
        try
        {
            var iid = typeof(T).GUID;
            Marshal.ThrowExceptionForHR(NativeMethods.RoGetActivationFactory(hstr, ref iid, out var factoryPtr));
            try { return (T)Marshal.GetObjectForIUnknown(factoryPtr); }
            finally { Marshal.Release(factoryPtr); }
        }
        finally { NativeMethods.WindowsDeleteString(hstr); }
    }

    /// <summary>Создаёт GraphicsCaptureItem для монитора (HMONITOR).</summary>
    public static GraphicsCaptureItem CreateItemForMonitor(IntPtr hMonitor)
    {
        var interop = GetActivationFactory<IGraphicsCaptureItemInterop>("Windows.Graphics.Capture.GraphicsCaptureItem");
        var iid = GraphicsCaptureItemIid;
        IntPtr abi = interop.CreateForMonitor(hMonitor, ref iid);
        // CreateFor* возвращает принадлежащую вызывающему COM-ссылку. FromAbi
        // создаёт свою managed-ссылку, поэтому исходную освобождаем ровно один раз.
        try { return GraphicsCaptureItem.FromAbi(abi); }
        finally { Marshal.Release(abi); }
    }

    /// <summary>Создаёт GraphicsCaptureItem для проверенного top-level HWND.</summary>
    public static GraphicsCaptureItem CreateItemForWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) throw new ArgumentException("HWND не задан", nameof(hwnd));
        var interop = GetActivationFactory<IGraphicsCaptureItemInterop>("Windows.Graphics.Capture.GraphicsCaptureItem");
        var iid = GraphicsCaptureItemIid;
        IntPtr abi = interop.CreateForWindow(hwnd, ref iid);
        try { return GraphicsCaptureItem.FromAbi(abi); }
        finally { Marshal.Release(abi); }
    }

    /// <summary>Оборачивает Vortice ID3D11Device в WinRT IDirect3DDevice для FramePool.</summary>
    public static IDirect3DDevice CreateWinRtDevice(ID3D11Device d3dDevice)
    {
        using var dxgi = d3dDevice.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(
            NativeMethods.CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var inspectable));
        var device = WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        Marshal.Release(inspectable);
        return device;
    }

    /// <summary>
    /// IID пятой версии интерфейса сессии захвата — единственное, что она добавляет,
    /// это минимальный интервал между кадрами. Сверено с windows.graphics.capture.h
    /// (MIDL_INTERFACE("67C0EA62-1F85-5061-925A-239BE0AC09CB") IGraphicsCaptureSession5).
    /// </summary>
    private static readonly Guid IGraphicsCaptureSession5Iid = new("67C0EA62-1F85-5061-925A-239BE0AC09CB");

    /// <summary>
    /// Слоты vtable: 3 метода IUnknown, 3 метода IInspectable, затем get и put
    /// в порядке объявления в заголовке.
    /// </summary>
    private const int GetMinUpdateIntervalSlot = 6;
    private const int PutMinUpdateIntervalSlot = 7;

    /// <summary>
    /// Ограничить частоту, с которой система делает кадры для этой сессии.
    ///
    /// ЗАЧЕМ ЧЕРЕЗ VTABLE, А НЕ СВОЙСТВОМ КЛАССА. Свойство MinUpdateInterval появилось
    /// в Windows 11 22H2, и проекция WinRT в нашей целевой версии SDK (22621) его ещё не
    /// объявляет — сборка падает с CS1061. Поднимать целевую версию SDK ради одного
    /// свойства значит менять требования ко всей сборке и упаковке. Прямой запрос
    /// интерфейса честно отвечает E_NOINTERFACE там, где свойства нет (Windows 10).
    ///
    /// Вызов идёт указателем на функцию, а не через [ComImport]-интерфейс: встроенная
    /// обёртка COM в современном .NET не обещает правильной раскладки для интерфейсов,
    /// унаследованных от IInspectable, а промах на один слот здесь означал бы вызов
    /// чужого метода с чужими аргументами. После записи значение читается обратно —
    /// по нему видно, что попали в нужный метод и система его приняла.
    ///
    /// Возвращает принятый системой интервал; null — интерфейса нет.
    /// </summary>
    public static unsafe TimeSpan? TrySetMinUpdateInterval(GraphicsCaptureSession session, TimeSpan interval)
    {
        ArgumentNullException.ThrowIfNull(session);
        IntPtr inspectable = WinRT.MarshalInspectable<GraphicsCaptureSession>.FromManaged(session);
        try
        {
            Guid iid = IGraphicsCaptureSession5Iid;
            if (Marshal.QueryInterface(inspectable, in iid, out IntPtr session5) != 0 || session5 == IntPtr.Zero)
                return null;
            try
            {
                var vtable = *(IntPtr**)session5;
                var put = (delegate* unmanaged[Stdcall]<IntPtr, long, int>)vtable[PutMinUpdateIntervalSlot];
                var get = (delegate* unmanaged[Stdcall]<IntPtr, long*, int>)vtable[GetMinUpdateIntervalSlot];

                Marshal.ThrowExceptionForHR(put(session5, interval.Ticks));
                long accepted;
                Marshal.ThrowExceptionForHR(get(session5, &accepted));
                return TimeSpan.FromTicks(accepted);
            }
            finally { Marshal.Release(session5); }
        }
        finally { Marshal.Release(inspectable); }
    }

    /// <summary>Достаёт нативную ID3D11Texture2D из кадра Direct3D11CaptureFrame.Surface.</summary>
    public static ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        var accessPtr = WinRT.MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        try
        {
            var access = (IDirect3DDxgiInterfaceAccess)Marshal.GetObjectForIUnknown(accessPtr);
            var iid = ID3D11Texture2DIid;
            IntPtr texPtr = access.GetInterface(ref iid);
            return new ID3D11Texture2D(texPtr); // Vortice берёт владение ссылкой
        }
        finally { Marshal.Release(accessPtr); }
    }
}
