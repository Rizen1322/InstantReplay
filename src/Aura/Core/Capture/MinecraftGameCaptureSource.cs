using Aura.Core.Capture.GameHook;
using Aura.Core.Logging;
using Vortice.Direct3D11;

namespace Aura.Core.Capture;

/// <summary>Один monitor WGC + Minecraft OpenGL bridge на общем D3D11 device.</summary>
internal sealed class MinecraftGameCaptureSource : IScreenCapture
{
    private readonly GameCaptureTarget _target;
    private readonly ScreenCaptureSource _monitor;
    private MinecraftCaptureRouter? _router;
    private OpenGlGameFrameBridge? _bridge;
    private ForegroundWindowWatcher? _foregroundWatcher;
    private int _monitorIndex;
    private int _targetFps;
    private bool _captureCursor;
    private long _generation;
    private long _monitorSequence;
    private long _framesAccepted;
    private bool _prepared;
    private bool _started;
    private bool _disposed;

    public MinecraftGameCaptureSource(in GameCaptureTarget target)
    {
        _target = target;
        _monitor = new ScreenCaptureSource(target.MonitorIndex);
        _monitor.FrameArrived += OnMonitorFrame;
        _monitor.Failed += failure => Failed?.Invoke(failure);
    }

    public ID3D11Device D3DDevice => _monitor.D3DDevice;
    public ID3D11DeviceContext D3DContext => _monitor.D3DContext;
    public int Width => _monitor.Width;
    public int Height => _monitor.Height;
    public long FramesReceived => _monitor.FramesReceived + (_bridge?.FramesUploaded ?? 0);
    public long FramesAccepted => Interlocked.Read(ref _framesAccepted);
    public long InvalidCursorShapes => _monitor.InvalidCursorShapes + (_bridge?.InvalidCursorShapes ?? 0);

    public event Action<CapturedSurface>? FrameArrived;
    public event Action<CaptureFailure>? Failed;

    public void Prepare(int monitorIndex, int targetFps, bool captureCursor, long generation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_prepared) throw new InvalidOperationException("Minecraft capture уже подготовлен");
        if (monitorIndex != _target.MonitorIndex)
            throw new ArgumentException("Minecraft target находится на другом мониторе", nameof(monitorIndex));

        _monitorIndex = monitorIndex;
        _targetFps = targetFps;
        _captureCursor = captureCursor;
        _generation = generation;
        _monitor.Prepare(monitorIndex, targetFps, captureCursor, generation);

        bool foreground = IsTargetForeground();
        _router = new MinecraftCaptureRouter(_target.Revision, foreground);
        GameCaptureRouteState route = _router.Current;
        _bridge = new OpenGlGameFrameBridge(
            D3DDevice,
            D3DContext,
            _target,
            Width,
            Height,
            targetFps,
            captureCursor,
            generation,
            route.Epoch);
        _bridge.FrameArrived += OnGameFrame;
        _bridge.Failed += OnBridgeFailure;
        _bridge.SetRoute(route.Epoch, foreground);

        _foregroundWatcher = new ForegroundWindowWatcher(_target.Hwnd);
        _foregroundWatcher.ForegroundChanged += OnForegroundChanged;
        _foregroundWatcher.TargetClosed += OnTargetClosed;
        _prepared = true;
    }

    public void Start()
    {
        if (!_prepared || _router is null || _bridge is null || _foregroundWatcher is null)
            throw new InvalidOperationException("Minecraft capture не подготовлен");
        if (_started) throw new InvalidOperationException("Minecraft capture уже запущен");

        _started = true;
        try
        {
            _monitor.Start();
            _foregroundWatcher.Start();
            _bridge.Start();
        }
        catch
        {
            _started = false;
            throw;
        }
        Log.Info(
            "Capture",
            $"Гибридный Minecraft capture запущен: WGC-monitor + OpenGL hook, " +
            $"PID {_target.ProcessId}, revision {_target.Revision}");
    }

    private void OnMonitorFrame(CapturedSurface surface)
    {
        MinecraftCaptureRouter? router = _router;
        if (!_started || router is null) return;
        long sequence = Interlocked.Increment(ref _monitorSequence);
        if (!router.TryAdmitMonitor(sequence, out HybridCaptureFrameToken token)) return;

        Interlocked.Increment(ref _framesAccepted);
        FrameArrived?.Invoke(surface with
        {
            TargetRevision = 0,
            Scope = CaptureSurfaceScope.Monitor,
            RouteEpoch = token.RouteEpoch
        });
    }

    private void OnGameFrame(OpenGlGameFrame frame)
    {
        MinecraftCaptureRouter? router = _router;
        if (!_started || router is null) return;
        if (!router.TryAdmitGame(
                frame.RouteEpoch,
                frame.TargetRevision,
                frame.Sequence,
                out HybridCaptureFrameToken token))
        {
            return;
        }

        Interlocked.Increment(ref _framesAccepted);
        FrameArrived?.Invoke(new CapturedSurface(
            frame.Texture,
            frame.Timestamp100ns,
            _generation,
            frame.Cursor,
            CaptureSurfaceScope.GameWindow,
            _target.Revision,
            token.RouteEpoch));
    }

    private void OnForegroundChanged(bool foreground)
    {
        MinecraftCaptureRouter? router = _router;
        OpenGlGameFrameBridge? bridge = _bridge;
        if (!_started || router is null || bridge is null) return;

        GameCaptureRouteState route = router.ObserveForeground(
            foreground,
            foreground ? _target.Revision : 0);
        bridge.SetRoute(route.Epoch, foreground);
        Log.Info(
            "Capture",
            $"Minecraft route -> {route.Route}, epoch {route.Epoch}, foreground={foreground}");
    }

    private void OnBridgeFailure(Exception error)
    {
        _router?.ObserveHookFailure();
        Log.Warn("Capture", $"Minecraft OpenGL hook: {error.Message}");
        Failed?.Invoke(new CaptureFailure(
            CaptureFailureKind.BackendUnavailable,
            error,
            "Minecraft OpenGL hook недоступен",
            _generation,
            _target.Revision));
    }

    private void OnTargetClosed()
    {
        var error = new InvalidOperationException("Minecraft target закрыт");
        Failed?.Invoke(new CaptureFailure(
            CaptureFailureKind.CaptureTargetClosed,
            error,
            error.Message,
            _generation,
            _target.Revision));
    }

    private bool IsTargetForeground()
    {
        nint foreground = GameHookNativeMethods.GetForegroundWindow();
        nint root = foreground == 0
            ? 0
            : GameHookNativeMethods.GetAncestor(foreground, GameHookNativeMethods.GaRoot);
        if (root == 0) root = foreground;
        return root == _target.Hwnd;
    }

    public void Stop()
    {
        if (!_prepared) return;
        _started = false;
        _foregroundWatcher?.Dispose();
        _foregroundWatcher = null;
        _bridge?.Dispose();
        _bridge = null;
        _monitor.Stop();
        _prepared = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _monitor.FrameArrived -= OnMonitorFrame;
        _monitor.Dispose();
        _disposed = true;
    }
}
