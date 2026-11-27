namespace Aura.Core.Audio;

/// <summary>
/// Защита шкалы звука от сдвига и выбросов QPC-меток (<c>u64QPCPosition</c>) у WASAPI.
/// При резком и устойчивом уходе аппаратных часов (USB-гарнитуры, Bluetooth, сброс
/// аудиодвижка) переводит сдвиг на системные часы, чтобы микшер не уходил за пределы окна.
/// </summary>
public sealed class WasapiTimestampGuard
{
    public const long MaxLagTicks = 1_600_000;          // 160 мс (внутри MixLagTicks = 200 мс)
    public const long MaxLeadTicks = 1_500_000;         // 150 мс
    public const long MildBoundsTicks = 2_500_000;      // 250 мс
    public const long ReturnToZeroLagTicks = 1_400_000; // 140 мс
    public const long ReturnToZeroLeadTicks = 1_000_000;// 100 мс
    public const long SyntheticGapTicks = 500_000;      // 50 мс
    public const int RebaseStreak = 5;

    private long _offset;
    private long _lastSyntheticQpc;
    private int _outOfWindowStreak;

    public int Rebases { get; private set; }
    public long OffsetTicks => _offset;
    public long LastSkewTicks { get; private set; }

    public readonly record struct ResolvedTimestamp(long QpcTicks, bool Discontinuity, bool Rebased, long DeltaMs);

    public ResolvedTimestamp Resolve(long rawQpc, bool badTime, bool discontinuity, long durationTicks, long nowTicks)
    {
        if (badTime || rawQpc <= 0)
        {
            _outOfWindowStreak = 0;
            long qpc = NextSyntheticQpc(discontinuity, durationTicks, nowTicks);
            LastSkewTicks = _lastSyntheticQpc - nowTicks;
            return new ResolvedTimestamp(qpc, discontinuity, false, 0);
        }

        long adjusted = rawQpc + _offset;
        long adjustedEnd = adjusted + durationTicks;
        bool inWindow = adjustedEnd >= nowTicks - MaxLagTicks && adjusted <= nowTicks + MaxLeadTicks;
        if (inWindow)
        {
            _outOfWindowStreak = 0;
            _lastSyntheticQpc = adjustedEnd;
            LastSkewTicks = adjustedEnd - nowTicks;
            return new ResolvedTimestamp(adjusted, discontinuity, false, 0);
        }

        _outOfWindowStreak++;
        if (_outOfWindowStreak >= RebaseStreak)
        {
            bool rawInNormalWindow = rawQpc + durationTicks >= nowTicks - ReturnToZeroLagTicks
                                     && rawQpc <= nowTicks + ReturnToZeroLeadTicks;
            long newOffset = rawInNormalWindow ? 0 : (nowTicks - durationTicks) - rawQpc;
            long deltaMs = (newOffset - _offset) / 10_000;
            _offset = newOffset;
            _outOfWindowStreak = 0;
            Rebases++;

            adjusted = rawQpc + _offset;
            _lastSyntheticQpc = adjusted + durationTicks;
            LastSkewTicks = _lastSyntheticQpc - nowTicks;
            return new ResolvedTimestamp(adjusted, Discontinuity: true, Rebased: true, DeltaMs: deltaMs);
        }

        if (adjustedEnd >= nowTicks - MildBoundsTicks && adjusted <= nowTicks + MildBoundsTicks)
        {
            _lastSyntheticQpc = adjustedEnd;
            LastSkewTicks = adjustedEnd - nowTicks;
            return new ResolvedTimestamp(adjusted, discontinuity, false, 0);
        }

        long fallbackQpc = NextSyntheticQpc(discontinuity, durationTicks, nowTicks);
        LastSkewTicks = _lastSyntheticQpc - nowTicks;
        return new ResolvedTimestamp(fallbackQpc, discontinuity, false, 0);
    }

    private long NextSyntheticQpc(bool discontinuity, long durationTicks, long nowTicks)
    {
        if (!discontinuity && _lastSyntheticQpc > 0 && Math.Abs(nowTicks - _lastSyntheticQpc) < SyntheticGapTicks)
        {
            long qpc = _lastSyntheticQpc;
            _lastSyntheticQpc += durationTicks;
            return qpc;
        }
        long start = nowTicks - durationTicks;
        _lastSyntheticQpc = nowTicks;
        return start;
    }
}

/// <summary>
/// Сторож «немого» loopback: если устройство воспроизведения реально играет звук
/// (пик в Windows выше порога и не выключено), а захваченный loopback отдаёт чистую
/// тишину несколько секунд подряд — источник завис и его нужно пересоздать.
/// </summary>
public sealed class LoopbackSilenceWatchdog
{
    public const float EndpointAudibleThreshold = 0.01f;
    public const float CapturedSilentThreshold = 0.0001f;
    public const int TriggerSeconds = 5;

    private int _streak;

    public bool ObserveSecond(float capturedPeak, float endpointPeak, bool endpointMuted)
    {
        if (capturedPeak >= CapturedSilentThreshold || endpointMuted || endpointPeak < EndpointAudibleThreshold)
        {
            _streak = 0;
            return false;
        }
        if (++_streak >= TriggerSeconds)
        {
            _streak = 0;
            return true;
        }
        return false;
    }

    public void Reset() => _streak = 0;
}

/// <summary>
/// Ограничитель автоперезапусков при немом loopback: защищает от бесконечного цикла,
/// если драйвер или DRM-поток намеренно отдают нули.
/// </summary>
public sealed class SilentLoopbackRecoveryPolicy
{
    public const int MaxConsecutiveRestarts = 3;

    private int _consecutiveRestarts;
    private long _nextAllowedMs;

    public void NoteAudible()
    {
        _consecutiveRestarts = 0;
        _nextAllowedMs = 0;
    }

    public bool TryBeginRestart(long nowMs, out bool budgetExhausted)
    {
        if (nowMs < _nextAllowedMs)
        {
            budgetExhausted = false;
            return false;
        }
        if (_consecutiveRestarts >= MaxConsecutiveRestarts)
        {
            budgetExhausted = true;
            return false;
        }

        long cooldown = _consecutiveRestarts switch
        {
            0 => 15_000,
            1 => 60_000,
            _ => 300_000
        };
        _consecutiveRestarts++;
        _nextAllowedMs = nowMs + cooldown;
        budgetExhausted = false;
        return true;
    }

    public void Reset()
    {
        _consecutiveRestarts = 0;
        _nextAllowedMs = 0;
    }
}

public readonly record struct AudioCaptureMinuteStats(
    long Packets,
    long SilentPackets,
    int QpcRebases,
    long LastQpcSkewMs,
    long QpcOffsetMs,
    float Peak);

internal static class AudioCaptureHealth
{
    private static readonly Guid DeviceFormatFmtId = new("f19f064d-082c-4e27-bc73-6882a1bb8e4c");
    private static readonly Guid PhysicalSpeakersFmtId = new("1da5d803-d492-4edd-8c23-e0c0ffee7f0e");

    /// <summary>
    /// Свойства устройства в MMDevice, изменение которых меняет формат микшера или число каналов
    /// (PKEY_AudioEngine_DeviceFormat и PKEY_AudioEndpoint_PhysicalSpeakers).
    /// </summary>
    public static bool IsFormatProperty(Guid formatId, int propertyId) =>
        (formatId == DeviceFormatFmtId && propertyId == 0) ||
        (formatId == PhysicalSpeakersFmtId && propertyId == 3);
}
