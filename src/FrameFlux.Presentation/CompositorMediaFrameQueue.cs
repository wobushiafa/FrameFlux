namespace FrameFlux.Presentation;

// One frame of jitter headroom, not an unbounded playback buffer. Expired
// backlog is collapsed to the latest frame rather than increasing live latency.
internal sealed class CompositorMediaFrameQueue : IDisposable
{
    private readonly object _sync = new();
    private readonly TimeProvider _clock;
    private readonly TimeSpan _maximumBacklogAge;
    private readonly Entry[] _entries = new Entry[2];
    private int _head;
    private int _count;
    private bool _scheduled;
    private bool _disposed;
    private long _dropped;

    private readonly record struct Entry(IMediaFrameLease Frame, long Timestamp);

    internal CompositorMediaFrameQueue(TimeSpan maximumBacklogAge, TimeProvider? clock = null)
    {
        if (maximumBacklogAge <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maximumBacklogAge));
        _maximumBacklogAge = maximumBacklogAge;
        _clock = clock ?? TimeProvider.System;
    }

    internal bool HasPendingFrame
    {
        get { lock (_sync) return _count != 0; }
    }

    internal long DroppedFrames
    {
        get { lock (_sync) return _dropped; }
    }

    internal bool TrySubmit(IMediaFrameLease frame, out bool schedule)
    {
        ArgumentNullException.ThrowIfNull(frame);
        IMediaFrameLease? replaced = null;
        lock (_sync)
        {
            if (_disposed)
            {
                schedule = false;
                return false;
            }
            if (_count == _entries.Length)
            {
                replaced = Dequeue().Frame;
                _dropped++;
            }
            _entries[(_head + _count) % _entries.Length] = new Entry(frame, _clock.GetTimestamp());
            _count++;
            schedule = !_scheduled;
            _scheduled = true;
        }
        replaced?.Dispose();
        return true;
    }

    internal IMediaFrameLease? TakeForPresentation(out TimeSpan queuedFor)
    {
        IMediaFrameLease? expired = null;
        Entry taken;
        lock (_sync)
        {
            if (_count == 0)
            {
                queuedFor = TimeSpan.Zero;
                return null;
            }
            var now = _clock.GetTimestamp();
            if (_count == 2 && _clock.GetElapsedTime(_entries[_head].Timestamp, now) > _maximumBacklogAge)
            {
                expired = Dequeue().Frame;
                _dropped++;
            }
            taken = Dequeue();
            queuedFor = _clock.GetElapsedTime(taken.Timestamp, now);
        }
        expired?.Dispose();
        return taken.Frame;
    }

    internal bool CompletePresentation()
    {
        lock (_sync)
        {
            _scheduled = !_disposed && _count != 0;
            return _scheduled;
        }
    }

    internal void Clear() => ClearCore(false);
    public void Dispose() => ClearCore(true);

    private void ClearCore(bool dispose)
    {
        IMediaFrameLease? first = null;
        IMediaFrameLease? second = null;
        lock (_sync)
        {
            _disposed |= dispose;
            if (_count != 0)
                first = Dequeue().Frame;
            if (_count != 0)
                second = Dequeue().Frame;
            _scheduled = false;
        }
        try { first?.Dispose(); }
        finally { second?.Dispose(); }
    }

    private Entry Dequeue()
    {
        var result = _entries[_head];
        _entries[_head] = default;
        _head = (_head + 1) % _entries.Length;
        _count--;
        return result;
    }
}
