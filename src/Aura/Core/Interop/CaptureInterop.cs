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
        return GraphicsCaptureItem.FromAbi(abi);
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
