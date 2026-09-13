namespace Aura.Core.Encoding;

internal enum EncoderQueueAdmission
{
    Append,
    RejectDuplicate,
    EvictDuplicate,
    EvictOldestReal
}

/// <summary>
/// Дубликаты поддерживают CFR, но не несут новой картинки. Поэтому они используют
/// только малую часть очереди и никогда не вытесняют реальный кадр.
/// </summary>
internal static class EncoderQueueAdmissionPolicy
{
    public static EncoderQueueAdmission Decide(
        bool incomingDuplicate,
        int queueDepth,
        int maximumDepth,
        bool encoderBehind,
        int firstDuplicateIndex)
    {
        if (queueDepth < 0) throw new ArgumentOutOfRangeException(nameof(queueDepth));
        if (maximumDepth <= 0) throw new ArgumentOutOfRangeException(nameof(maximumDepth));
        if (queueDepth > maximumDepth) throw new ArgumentOutOfRangeException(nameof(queueDepth));

        if (incomingDuplicate)
        {
            int duplicatePressureLimit = Math.Max(1, maximumDepth / 4);
            return encoderBehind || queueDepth >= duplicatePressureLimit
                ? EncoderQueueAdmission.RejectDuplicate
                : EncoderQueueAdmission.Append;
        }

        if (queueDepth < maximumDepth) return EncoderQueueAdmission.Append;
        return firstDuplicateIndex >= 0
            ? EncoderQueueAdmission.EvictDuplicate
            : EncoderQueueAdmission.EvictOldestReal;
    }
}
