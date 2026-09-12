namespace Aura.Core.Capture;

internal sealed class CaptureFrameBroker<TSlot> : IDisposable where TSlot : class
{
    private const int Capacity = 3;

    private readonly object _gate = new();
    private readonly Action<TSlot> _disposeSlot;
    private readonly SlotEntry[] _slots;
    private long _generation;
    private long _nextToken;
    private long _nextReadySequence;
    private long _framesPublished;
    private long _framesDroppedNoSlot;
    private long _latestTimestamp;
    private bool _disposed;

    public CaptureFrameBroker(Func<TSlot> createSlot, Action<TSlot> disposeSlot)
    {
        ArgumentNullException.ThrowIfNull(createSlot);
        ArgumentNullException.ThrowIfNull(disposeSlot);

        _disposeSlot = disposeSlot;
        _slots = new SlotEntry[Capacity];
        int created = 0;
        try
        {
            for (; created < Capacity; created++)
                _slots[created] = new SlotEntry(createSlot());
        }
        catch
        {
            for (int i = 0; i < created; i++)
                disposeSlot(_slots[i].Slot);
            throw;
        }
    }

    public long FramesPublished
    {
        get { lock (_gate) return _framesPublished; }
    }

    public long FramesDroppedNoSlot
    {
        get { lock (_gate) return _framesDroppedNoSlot; }
    }

    public long LatestTimestamp
    {
        get { lock (_gate) return _latestTimestamp; }
    }

    public long Generation
    {
        get { lock (_gate) return _generation; }
    }

    public void Reset(long generation)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _generation = generation;
            _latestTimestamp = 0;

            foreach (SlotEntry slot in _slots)
            {
                if (slot.State == SlotState.Ready)
                    slot.State = SlotState.Free;
            }
        }
    }

    public bool Publish(long generation, long timestamp, Action<TSlot> write)
    {
        ArgumentNullException.ThrowIfNull(write);

        int index;
        long token;
        TSlot slotValue;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (generation != _generation)
                return false;

            index = FindWritableSlot();
            if (index < 0)
            {
                _framesDroppedNoSlot++;
                return false;
            }

            SlotEntry slot = _slots[index];
            token = ++_nextToken;
            slot.State = SlotState.Writing;
            slot.Generation = generation;
            slot.Timestamp = timestamp;
            slot.Token = token;
            slotValue = slot.Slot;
        }

        try
        {
            write(slotValue);
        }
        catch
        {
            TSlot? dispose = RollBackWrite(index, generation, token);
            if (dispose is not null) _disposeSlot(dispose);
            throw;
        }

        TSlot? disposeAfterWrite = null;
        bool published = false;
        lock (_gate)
        {
            SlotEntry slot = _slots[index];
            if (slot.State == SlotState.Writing &&
                slot.Generation == generation &&
                slot.Token == token)
            {
                if (!_disposed && generation == _generation)
                {
                    slot.State = SlotState.Ready;
                    slot.ReadySequence = ++_nextReadySequence;
                    _framesPublished++;
                    _latestTimestamp = timestamp;
                    published = true;
                }
                else
                {
                    slot.State = SlotState.Free;
                    disposeAfterWrite = TakeSlotForDisposalIfNeeded(slot);
                }
            }
        }

        if (disposeAfterWrite is not null) _disposeSlot(disposeAfterWrite);
        return published;
    }

    public bool TryLeaseLatest(long generation, out CaptureFrameLease<TSlot>? lease)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lease = null;
            if (generation != _generation)
                return false;

            int index = -1;
            long newestSequence = long.MinValue;
            for (int i = 0; i < _slots.Length; i++)
            {
                SlotEntry candidate = _slots[i];
                if (candidate.State == SlotState.Ready &&
                    candidate.Generation == generation &&
                    candidate.ReadySequence > newestSequence)
                {
                    index = i;
                    newestSequence = candidate.ReadySequence;
                }
            }

            if (index < 0)
                return false;

            SlotEntry slot = _slots[index];
            long leaseToken = ++_nextToken;
            slot.State = SlotState.Reading;
            slot.Token = leaseToken;
            lease = new CaptureFrameLease<TSlot>(
                this, index, generation, leaseToken, slot.Slot, slot.Timestamp);
            return true;
        }
    }

    public void Dispose()
    {
        List<TSlot>? dispose = null;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;

            foreach (SlotEntry slot in _slots)
            {
                if (slot.State is SlotState.Writing or SlotState.Reading)
                    continue;

                TSlot? value = TakeSlotForDisposalIfNeeded(slot);
                if (value is not null)
                    (dispose ??= []).Add(value);
            }
        }

        if (dispose is null) return;
        foreach (TSlot slot in dispose) _disposeSlot(slot);
    }

    internal void ReturnLease(int index, long generation, long leaseToken)
    {
        TSlot? dispose = null;
        lock (_gate)
        {
            if ((uint)index >= (uint)_slots.Length)
                return;

            SlotEntry slot = _slots[index];
            if (slot.State != SlotState.Reading ||
                slot.Generation != generation ||
                slot.Token != leaseToken)
            {
                return;
            }

            slot.State = SlotState.Free;
            dispose = TakeSlotForDisposalIfNeeded(slot);
        }

        if (dispose is not null) _disposeSlot(dispose);
    }

    private int FindWritableSlot()
    {
        for (int i = 0; i < _slots.Length; i++)
        {
            if (_slots[i].State == SlotState.Free)
                return i;
        }

        int oldestIndex = -1;
        long oldestSequence = long.MaxValue;
        for (int i = 0; i < _slots.Length; i++)
        {
            SlotEntry candidate = _slots[i];
            if (candidate.State == SlotState.Ready && candidate.ReadySequence < oldestSequence)
            {
                oldestIndex = i;
                oldestSequence = candidate.ReadySequence;
            }
        }
        return oldestIndex;
    }

    private TSlot? RollBackWrite(int index, long generation, long token)
    {
        lock (_gate)
        {
            SlotEntry slot = _slots[index];
            if (slot.State != SlotState.Writing ||
                slot.Generation != generation ||
                slot.Token != token)
            {
                return null;
            }

            slot.State = SlotState.Free;
            return TakeSlotForDisposalIfNeeded(slot);
        }
    }

    private TSlot? TakeSlotForDisposalIfNeeded(SlotEntry slot)
    {
        if (!_disposed || slot.Disposed)
            return null;

        slot.Disposed = true;
        return slot.Slot;
    }

    private enum SlotState
    {
        Free,
        Writing,
        Ready,
        Reading
    }

    private sealed class SlotEntry(TSlot slot)
    {
        public TSlot Slot { get; } = slot;
        public SlotState State { get; set; }
        public long Generation { get; set; }
        public long Timestamp { get; set; }
        public long Token { get; set; }
        public long ReadySequence { get; set; }
        public bool Disposed { get; set; }
    }
}

internal sealed class CaptureFrameLease<TSlot> : IDisposable where TSlot : class
{
    private CaptureFrameBroker<TSlot>? _owner;
    private readonly int _slotIndex;
    private readonly long _leaseToken;

    internal CaptureFrameLease(
        CaptureFrameBroker<TSlot> owner,
        int slotIndex,
        long generation,
        long leaseToken,
        TSlot slot,
        long timestamp)
    {
        _owner = owner;
        _slotIndex = slotIndex;
        _leaseToken = leaseToken;
        Generation = generation;
        Slot = slot;
        Timestamp = timestamp;
    }

    public TSlot Slot { get; }
    public long Timestamp { get; }
    public long Generation { get; }

    public void Dispose()
    {
        CaptureFrameBroker<TSlot>? owner = Interlocked.Exchange(ref _owner, null);
        owner?.ReturnLease(_slotIndex, Generation, _leaseToken);
    }
}
