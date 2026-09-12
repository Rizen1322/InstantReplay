namespace Aura.Core.Capture;

internal enum DdaLifecycleState
{
    Stable,
    TransitionHold,
    Storm
}

/// <summary>
/// Отделяет одиночную смену режима от цикла, в котором DDA постоянно теряет
/// duplication-сессию. Время передаёт вызывающий, поэтому политика детерминирована.
/// </summary>
internal sealed class DdaLifecycleMonitor
{
    private readonly TimeSpan _stormWindow;
    private readonly TimeSpan _stableWindow;
    private readonly int _stormThreshold;
    private readonly Queue<TimeSpan> _invalidations = new();
    private TimeSpan? _lastInvalidation;

    public DdaLifecycleMonitor(
        TimeSpan stormWindow,
        TimeSpan stableWindow,
        int stormThreshold)
    {
        if (stormWindow <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(stormWindow));
        if (stableWindow <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(stableWindow));
        if (stormThreshold < 2)
            throw new ArgumentOutOfRangeException(nameof(stormThreshold));

        _stormWindow = stormWindow;
        _stableWindow = stableWindow;
        _stormThreshold = stormThreshold;
    }

    public DdaLifecycleState State { get; private set; } = DdaLifecycleState.Stable;
    public int InvalidationsInWindow => _invalidations.Count;

    public DdaLifecycleState RecordInvalidation(TimeSpan timestamp)
    {
        EnsureMonotonic(timestamp);
        if (State == DdaLifecycleState.Storm) return State;

        while (_invalidations.Count > 0 &&
               timestamp - _invalidations.Peek() > _stormWindow)
        {
            _invalidations.Dequeue();
        }

        _invalidations.Enqueue(timestamp);
        _lastInvalidation = timestamp;
        State = _invalidations.Count >= _stormThreshold
            ? DdaLifecycleState.Storm
            : DdaLifecycleState.TransitionHold;
        return State;
    }

    public DdaLifecycleState ObserveUsefulFrame(TimeSpan timestamp)
    {
        EnsureMonotonic(timestamp);
        if (State is DdaLifecycleState.Stable or DdaLifecycleState.Storm)
            return State;

        if (_lastInvalidation is { } last && timestamp - last >= _stableWindow)
        {
            _invalidations.Clear();
            _lastInvalidation = null;
            State = DdaLifecycleState.Stable;
        }

        return State;
    }

    public void Reset()
    {
        _invalidations.Clear();
        _lastInvalidation = null;
        State = DdaLifecycleState.Stable;
    }

    private void EnsureMonotonic(TimeSpan timestamp)
    {
        if (_lastInvalidation is { } last && timestamp < last)
            throw new ArgumentOutOfRangeException(nameof(timestamp), "Время не может идти назад");
    }
}
