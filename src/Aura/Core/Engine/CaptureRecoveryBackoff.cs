namespace Aura.Core.Engine;

internal static class CaptureRecoveryBackoff
{
    public static int DelayMilliseconds(int attempt, bool deviceLost)
    {
        if (attempt <= 1) return deviceLost ? 1_500 : 250;
        return attempt switch
        {
            2 => 3_000,
            3 => 5_000,
            4 => 10_000,
            _ => 15_000
        };
    }
}
