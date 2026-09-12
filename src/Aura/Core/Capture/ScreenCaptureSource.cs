using Windows.Graphics.Capture;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Aura.Core.Interop;

namespace Aura.Core.Capture;

/// <summary>WGC-захват выбранного монитора.</summary>
internal sealed class ScreenCaptureSource : IScreenCapture
{
    private readonly WgcCaptureSession _inner;

    internal ScreenCaptureSource(int monitorIndex)
    {
        _inner = new WgcCaptureSession(
            monitorIndex,
            () => CaptureInterop.CreateItemForMonitor(GetMonitorHandle(monitorIndex)),
            CaptureSurfaceScope.Monitor,
            targetRevision: 0,
            sourceName: "WGC monitor",
            forceCursorDisabled: false,
            targetCanClose: false);
    }

    public ID3D11Device D3DDevice => _inner.D3DDevice;
    public ID3D11DeviceContext D3DContext => _inner.D3DContext;
    public int Width => _inner.Width;
    public int Height => _inner.Height;
    public long FramesReceived => _inner.FramesReceived;
    public long FramesAccepted => _inner.FramesAccepted;
    public long InvalidCursorShapes => _inner.InvalidCursorShapes;
    public event Action<CapturedSurface>? FrameArrived
    {
        add => _inner.FrameArrived += value;
        remove => _inner.FrameArrived -= value;
    }
    public event Action<CaptureFailure>? Failed
    {
        add => _inner.Failed += value;
        remove => _inner.Failed -= value;
    }

    public void Prepare(int monitorIndex, int targetFps, bool captureCursor, long generation) =>
        _inner.Prepare(monitorIndex, targetFps, captureCursor, generation);
    public void Start() => _inner.Start();
    public void Stop() => _inner.Stop();
    public void Dispose() => _inner.Dispose();

    private static nint GetMonitorHandle(int index)
    {
        using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        var monitors = new List<nint>();
        for (uint adapterIndex = 0;
             factory.EnumAdapters1(adapterIndex, out IDXGIAdapter1 adapter).Success;
             adapterIndex++)
        {
            using (adapter)
            {
                for (uint outputIndex = 0;
                     adapter.EnumOutputs(outputIndex, out IDXGIOutput output).Success;
                     outputIndex++)
                {
                    using (output) monitors.Add(output.Description.Monitor);
                }
            }
        }

        if (monitors.Count == 0) throw new InvalidOperationException("Мониторы не найдены");
        return monitors[Math.Clamp(index, 0, monitors.Count - 1)];
    }
}
