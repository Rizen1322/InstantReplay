namespace Aura.Core.Capture;

/// <summary>Чистая проверка, что foreground-окно пригодно для game-only WGC.</summary>
internal static class GameWindowSelector
{
    private const double MinimumMonitorCoverage = 0.90;

    public static GameCaptureTarget? Select(
        in WindowCaptureSnapshot snapshot,
        GameCaptureTarget? previous)
    {
        if (snapshot.Hwnd == 0 ||
            snapshot.Hwnd != snapshot.ForegroundHwnd ||
            snapshot.Hwnd != snapshot.RootOwnerHwnd ||
            snapshot.ProcessId <= 0 ||
            snapshot.ProcessStartTicks <= 0 ||
            !snapshot.IsProcessAlive ||
            !snapshot.IsVisible ||
            snapshot.IsMinimized ||
            snapshot.IsCloaked ||
            snapshot.MonitorIndex != snapshot.SelectedMonitorIndex ||
            string.IsNullOrWhiteSpace(snapshot.ExecutableName) ||
            string.IsNullOrWhiteSpace(snapshot.GameName) ||
            snapshot.GameName.Equals("Desktop", StringComparison.OrdinalIgnoreCase) ||
            !HasMinimumCoverage(snapshot.ClientBounds, snapshot.MonitorBounds))
        {
            return null;
        }

        long revision = 1;
        if (previous is GameCaptureTarget old)
        {
            bool sameIdentity = old.Hwnd == snapshot.Hwnd &&
                                old.ProcessId == snapshot.ProcessId &&
                                old.ProcessStartTicks == snapshot.ProcessStartTicks;
            revision = sameIdentity ? old.Revision : checked(old.Revision + 1);
        }

        return new GameCaptureTarget(
            snapshot.Hwnd,
            snapshot.ProcessId,
            snapshot.ProcessStartTicks,
            snapshot.ExecutableName,
            snapshot.GameName,
            snapshot.MonitorIndex,
            snapshot.ClientBounds,
            snapshot.MonitorBounds,
            revision);
    }

    private static bool HasMinimumCoverage(in PixelRect client, in PixelRect monitor)
    {
        if (client.Width <= 0 || client.Height <= 0 ||
            monitor.Width <= 0 || monitor.Height <= 0)
        {
            return false;
        }

        long clientRight = (long)client.X + client.Width;
        long clientBottom = (long)client.Y + client.Height;
        long monitorRight = (long)monitor.X + monitor.Width;
        long monitorBottom = (long)monitor.Y + monitor.Height;

        long intersectionWidth = Math.Max(
            0,
            Math.Min(clientRight, monitorRight) - Math.Max(client.X, monitor.X));
        long intersectionHeight = Math.Max(
            0,
            Math.Min(clientBottom, monitorBottom) - Math.Max(client.Y, monitor.Y));
        long intersectionArea = intersectionWidth * intersectionHeight;
        long monitorArea = (long)monitor.Width * monitor.Height;

        return intersectionArea >= monitorArea * MinimumMonitorCoverage;
    }
}
