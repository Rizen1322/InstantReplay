namespace Aura.Core.Capture.GameHook;

internal sealed class ForegroundWindowWatcher : IDisposable
{
    private readonly nint _targetHwnd;
    private readonly GameHookNativeMethods.WinEventProc _callback;
    private Timer? _pollTimer;
    private nint _hook;
    private int _lastForeground = -1;
    private int _closedReported;
    private bool _disposed;

    public ForegroundWindowWatcher(nint targetHwnd)
    {
        if (targetHwnd == 0) throw new ArgumentOutOfRangeException(nameof(targetHwnd));
        _targetHwnd = targetHwnd;
        _callback = OnWinEvent;
    }

    public event Action<bool>? ForegroundChanged;
    public event Action? TargetClosed;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pollTimer is not null) throw new InvalidOperationException("Watcher уже запущен");

        _hook = GameHookNativeMethods.SetWinEventHook(
            GameHookNativeMethods.EventSystemForeground,
            GameHookNativeMethods.EventSystemForeground,
            0,
            _callback,
            0,
            0,
            GameHookNativeMethods.WineventOutOfContext |
            GameHookNativeMethods.WineventSkipOwnProcess);
        _pollTimer = new Timer(_ => Poll(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(100));
    }

    private void OnWinEvent(
        nint hook,
        uint eventType,
        nint hwnd,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime) => Poll();

    private void Poll()
    {
        if (_disposed) return;
        if (!GameHookNativeMethods.IsWindow(_targetHwnd))
        {
            if (Interlocked.Exchange(ref _closedReported, 1) == 0)
                TargetClosed?.Invoke();
            return;
        }

        nint foreground = GameHookNativeMethods.GetForegroundWindow();
        nint root = foreground == 0
            ? 0
            : GameHookNativeMethods.GetAncestor(foreground, GameHookNativeMethods.GaRoot);
        if (root == 0) root = foreground;
        int current = root == _targetHwnd ? 1 : 0;
        int previous = Interlocked.Exchange(ref _lastForeground, current);
        if (previous != current) ForegroundChanged?.Invoke(current != 0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pollTimer?.Dispose();
        _pollTimer = null;
        if (_hook != 0) _ = GameHookNativeMethods.UnhookWinEvent(_hook);
        _hook = 0;
    }
}
