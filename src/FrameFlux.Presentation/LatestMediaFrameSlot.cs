namespace FrameFlux.Presentation;

internal sealed class LatestMediaFrameSlot : IDisposable
{
    private readonly object _sync = new();
    private IMediaFrameLease? _pendingFrame;
    private bool _presentationScheduled;
    private bool _disposed;

    public bool HasPendingFrame
    {
        get
        {
            lock (_sync)
                return _pendingFrame is not null;
        }
    }

    public bool TrySubmit(IMediaFrameLease frame, out bool schedulePresentation)
    {
        ArgumentNullException.ThrowIfNull(frame);

        IMediaFrameLease? replacedFrame;
        lock (_sync)
        {
            if (_disposed)
            {
                schedulePresentation = false;
                return false;
            }

            replacedFrame = _pendingFrame;
            _pendingFrame = frame;
            schedulePresentation = !_presentationScheduled;
            _presentationScheduled = true;
        }

        replacedFrame?.Dispose();
        return true;
    }

    public IMediaFrameLease? Take()
    {
        lock (_sync)
        {
            var frame = _pendingFrame;
            _pendingFrame = null;
            _presentationScheduled = false;
            return frame;
        }
    }

    // Retain the reservation until async GPU work completes. Pair with
    // CompletePresentation and use ReleasePendingFrame while work is scheduled.
    public IMediaFrameLease? TakeForPresentation()
    {
        lock (_sync)
        {
            var frame = _pendingFrame;
            _pendingFrame = null;
            return frame;
        }
    }

    public bool CompletePresentation()
    {
        lock (_sync)
        {
            _presentationScheduled = !_disposed && _pendingFrame is not null;
            return _presentationScheduled;
        }
    }

    public void Clear()
    {
        IMediaFrameLease? frame;
        lock (_sync)
        {
            frame = _pendingFrame;
            _pendingFrame = null;
            _presentationScheduled = false;
        }

        frame?.Dispose();
    }

    public void ReleasePendingFrame()
    {
        IMediaFrameLease? frame;
        lock (_sync)
        {
            frame = _pendingFrame;
            _pendingFrame = null;
        }

        frame?.Dispose();
    }

    public void Dispose()
    {
        IMediaFrameLease? frame;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            frame = _pendingFrame;
            _pendingFrame = null;
            _presentationScheduled = false;
        }

        frame?.Dispose();
    }
}
