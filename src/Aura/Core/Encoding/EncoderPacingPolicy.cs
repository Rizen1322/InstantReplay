namespace Aura.Core.Encoding;

/// <summary>Чистая часть решения, можно ли пейсеру заполнять паузу дубликатами.</summary>
internal static class EncoderPacingPolicy
{
    public static bool IsBehind(double requestsPerSecond, int targetFps,
                                bool inputRequestWaitingForFrame)
    {
        // Уже выданный NeedInput находится у FeedLoop, пока тот ждёт кадр. В этот
        // момент новых запросов закономерно нет: MFT готов принять кадр, а не занят.
        if (inputRequestWaitingForFrame) return false;

        return requestsPerSecond < targetFps * 0.75;
    }
}
