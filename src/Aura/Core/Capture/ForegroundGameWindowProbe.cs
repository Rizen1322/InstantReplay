using System.Diagnostics;
using Aura.Core.GameDetection;
using Aura.Core.Interop;
using Aura.Core.Logging;

namespace Aura.Core.Capture;

/// <summary>Собирает Win32-снимок foreground-окна и передаёт его чистому селектору.</summary>
internal static class ForegroundGameWindowProbe
{
    public static GameCaptureTarget? TrySelect(
        int selectedMonitorIndex,
        GameCaptureTarget? previous)
    {
        try
        {
            nint foreground = NativeMethods.GetForegroundWindow();
            if (foreground == 0) return null;

            nint root = NativeMethods.GetAncestor(foreground, NativeMethods.GA_ROOTOWNER);
            if (root == 0) root = foreground;
            NativeMethods.GetWindowThreadProcessId(root, out uint rawPid);
            if (rawPid == 0 || rawPid > int.MaxValue) return null;

            using Process process = Process.GetProcessById((int)rawPid);
            long processStartTicks = process.StartTime.ToUniversalTime().Ticks;
            string executableName = process.ProcessName;

            bool hasClient = TryGetClientBounds(root, out PixelRect clientBounds);
            nint monitor = NativeMethods.MonitorFromWindow(root, NativeMethods.MONITOR_DEFAULTTONEAREST);
            bool hasMonitor = TryGetMonitorBounds(monitor, out PixelRect monitorBounds);
            int monitorIndex = MatchesSelectedMonitor(selectedMonitorIndex, monitorBounds)
                ? selectedMonitorIndex
                : -1;

            bool cloaked = NativeMethods.DwmGetWindowAttributeInt(
                root,
                NativeMethods.DWMWA_CLOAKED,
                out int cloakedValue,
                sizeof(int)) == 0 && cloakedValue != 0;

            var snapshot = new WindowCaptureSnapshot(
                Hwnd: root,
                ForegroundHwnd: foreground,
                RootOwnerHwnd: root,
                ProcessId: (int)rawPid,
                ProcessStartTicks: processStartTicks,
                ExecutableName: executableName,
                GameName: GameDetector.DetectForegroundGame(),
                MonitorIndex: monitorIndex,
                SelectedMonitorIndex: selectedMonitorIndex,
                ClientBounds: hasClient ? clientBounds : default,
                MonitorBounds: hasMonitor ? monitorBounds : default,
                IsVisible: NativeMethods.IsWindowVisible(root),
                IsMinimized: NativeMethods.IsIconic(root),
                IsCloaked: cloaked,
                IsProcessAlive: !process.HasExited);

            GameCaptureTarget? selected = GameWindowSelector.Select(snapshot, previous);
            if (selected is null) return null;

            // HWND и PID могли измениться между чтением свойств и выбором.
            if (!NativeMethods.IsWindow(root) ||
                NativeMethods.GetForegroundWindow() != foreground ||
                NativeMethods.GetAncestor(foreground, NativeMethods.GA_ROOTOWNER) != root)
            {
                return null;
            }

            NativeMethods.GetWindowThreadProcessId(root, out uint verifiedPid);
            if (verifiedPid != rawPid || process.HasExited ||
                process.StartTime.ToUniversalTime().Ticks != processStartTicks)
            {
                return null;
            }

            return selected;
        }
        catch (Exception ex)
        {
            Log.Warn("Capture", $"Игровое окно не определено: {ex.Message}");
            return null;
        }
    }

    private static bool TryGetClientBounds(nint hwnd, out PixelRect bounds)
    {
        bounds = default;
        if (!NativeMethods.GetClientRect(hwnd, out NativeMethods.RECT client)) return false;

        var origin = new NativeMethods.POINT { X = client.Left, Y = client.Top };
        if (!NativeMethods.ClientToScreen(hwnd, ref origin)) return false;

        int width = client.Right - client.Left;
        int height = client.Bottom - client.Top;
        if (width <= 0 || height <= 0) return false;

        bounds = new PixelRect(origin.X, origin.Y, width, height);
        return true;
    }

    private static bool TryGetMonitorBounds(nint monitor, out PixelRect bounds)
    {
        bounds = default;
        if (monitor == 0) return false;

        var info = new NativeMethods.MONITORINFO
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>()
        };
        if (!NativeMethods.GetMonitorInfoW(monitor, ref info)) return false;

        bounds = new PixelRect(
            info.rcMonitor.Left,
            info.rcMonitor.Top,
            info.rcMonitor.Right - info.rcMonitor.Left,
            info.rcMonitor.Bottom - info.rcMonitor.Top);
        return bounds.Width > 0 && bounds.Height > 0;
    }

    private static bool MatchesSelectedMonitor(int selectedMonitorIndex, in PixelRect actual)
    {
        var selected = MonitorLayout.For(selectedMonitorIndex);
        return selected is { } expected &&
               expected.X == actual.X && expected.Y == actual.Y &&
               expected.Width == actual.Width && expected.Height == actual.Height;
    }
}
