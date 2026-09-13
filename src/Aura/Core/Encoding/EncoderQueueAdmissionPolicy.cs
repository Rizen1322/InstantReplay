namespace Aura.Core.Encoding;

internal enum EncoderQueueAdmission
{
    Append,
    RejectDuplicate,
    EvictDuplicate,
    EvictOldestReal
}

/// <summary>
/// Дубликаты поддерживают CFR и никогда не вытесняют реальный кадр. Пока в очереди
/// есть место, их нельзя подавлять: иначе 60-fps поток сам превращается в 49-53 fps.
/// Если очередь заполнится, пришедший реальный кадр сначала вытеснит дубликат.
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
            return queueDepth < maximumDepth
                ? EncoderQueueAdmission.Append
                : EncoderQueueAdmission.RejectDuplicate;

        if (queueDepth < maximumDepth) return EncoderQueueAdmission.Append;
        return firstDuplicateIndex >= 0
            ? EncoderQueueAdmission.EvictDuplicate
            : EncoderQueueAdmission.EvictOldestReal;
    }
}
