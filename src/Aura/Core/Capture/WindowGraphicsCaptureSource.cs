using System.Diagnostics;
using Vortice.Direct3D11;
using Aura.Core.Interop;

namespace Aura.Core.Capture;

/// <summary>WGC-захват одного проверенного foreground-окна игры.</summary>
internal sealed class WindowGraphicsCaptureSource : IScreenCapture
{
    private readonly GameCaptureTarget _target;
    private readonly WindowCursorSampler _cursorSampler;
    private readonly WgcCaptureSession _inner;
    private bool _captureCursor;

    internal WindowGraphicsCaptureSource(in GameCaptureTarget target)
    {
        _target = target;
        _cursorSampler = new WindowCursorSampler(target);
        _inner = new WgcCaptureSession(
            target.MonitorIndex,
            CreateCaptureItem,
            CaptureSurfaceScope.GameWindow,
            target.Revision,
            sourceName: $"WGC window {target.ExecutableName} r{target.Revision}",
            forceCursorDisabled: true,
            targetCanClose: true,
            sampleCursor: () => _cursorSampler.Sample(_captureCursor));
    }

    public ID3D11Device D3DDevice => _inner.D3DDevice;
    public ID3D11DeviceContext D3DContext => _inner.D3DContext;
    public int Width => _inner.Width;
    public int Height => _inner.Height;
    public long FramesReceived => _inner.FramesReceived;
    public long FramesAccepted => _inner.FramesAccepted;
    public long InvalidCursorShapes => _cursorSampler.InvalidShapes;
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

    public void Prepare(int monitorIndex, int targetFps, bool captureCursor, long generation)
    {
        if (monitorIndex != _target.MonitorIndex)
            throw new InvalidOperationException("Игровое окно больше не принадлежит выбранному монитору");
        _captureCursor = captureCursor;
        _cursorSampler.Reset(_target.Revision, force: true);
        _inner.Prepare(monitorIndex, targetFps, captureCursor, generation);
    }

    public void Start() => _inner.Start();
    public void Stop() => _inner.Stop();
    public void Dispose() => _inner.Dispose();

    private Windows.Graphics.Capture.GraphicsCaptureItem CreateCaptureItem()
    {
        GameCaptureTarget? verified = ForegroundGameWindowProbe.TrySelect(
            _target.MonitorIndex,
            _target);
        if (verified is not GameCaptureTarget current ||
            !current.HasSameIdentity(_target) ||
            current.Revision != _target.Revision)
        {
            throw new InvalidOperationException("Игровое окно изменилось до запуска оконного захвата");
        }

        NativeMethods.GetWindowThreadProcessId(_target.Hwnd, out uint pid);
        if (pid != _target.ProcessId) throw new InvalidOperationException("PID игрового окна изменился");
        using Process process = Process.GetProcessById(_target.ProcessId);
        if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != _target.ProcessStartTicks)
            throw new InvalidOperationException("Процесс игрового окна изменился");

        return CaptureInterop.CreateItemForWindow(_target.Hwnd);
    }
}
