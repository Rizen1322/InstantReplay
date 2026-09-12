namespace Aura.Core.Capture;

/// <summary>Прямоугольник в физических пикселях рабочего стола.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height);

/// <summary>Снимок свойств окна, собранный Win32-адаптером для чистой проверки.</summary>
internal readonly record struct WindowCaptureSnapshot(
    nint Hwnd,
    nint ForegroundHwnd,
    nint RootOwnerHwnd,
    int ProcessId,
    long ProcessStartTicks,
    string ExecutableName,
    string GameName,
    int MonitorIndex,
    int SelectedMonitorIndex,
    PixelRect ClientBounds,
    PixelRect MonitorBounds,
    bool IsVisible,
    bool IsMinimized,
    bool IsCloaked,
    bool IsProcessAlive);

/// <summary>Проверенное тождество fullscreen-окна игры.</summary>
internal readonly record struct GameCaptureTarget(
    nint Hwnd,
    int ProcessId,
    long ProcessStartTicks,
    string ExecutableName,
    string GameName,
    int MonitorIndex,
    PixelRect ClientBounds,
    PixelRect MonitorBounds,
    long Revision)
{
    public bool HasSameIdentity(in GameCaptureTarget other) =>
        Hwnd == other.Hwnd &&
        ProcessId == other.ProcessId &&
        ProcessStartTicks == other.ProcessStartTicks;
}
