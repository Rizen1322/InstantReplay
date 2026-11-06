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
/// есть место, их нельзя подавлять: иначе 60-fps поток сам
/// превращается в 49-53 fps. Возраст очереди не освобождает слот временной сетки.
/// Если очередь заполнится, пришедший реальный кадр сначала вытеснит дубликат.
/// </summary>
internal static class EncoderQueueAdmissionPolicy
{
    public static EncoderQueueAdmission Decide(
        bool incomingDuplicate,
        int queueDepth,
        int maximumDepth,
        bool encoderBehind,
        int firstDuplicateIndex,
        double oldestAgeMs = 0)
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
