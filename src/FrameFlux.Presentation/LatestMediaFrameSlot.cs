namespace FrameFlux.Presentation;

internal sealed class LatestMediaFrameSlot : IDisposable
{
    private readonly object _sync = new();
    private IMediaFrameLease? _pendingFrame;
    private bool _presentationScheduled;
    private bool _disposed;

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
