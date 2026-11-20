namespace FrameFlux.Unity.Native;

// FrameReceived supplies an owned managed copy, independent of the decoder's leases.
// Retain only the latest frame so a slow Unity Update never queues stale video.
internal sealed class UnityFrameMailbox
{
    private readonly object _sync = new();
    private MediaVideoFrame? _frame;
    private long _sequence;

    internal void Present(MediaVideoFrame frame)
    {
        if (frame.PixelFormat != MediaPixelFormat.Bgra32 || frame.Width <= 0 || frame.Height <= 0 ||
            frame.Stride < checked(frame.Width * 4) || frame.Data.Length < checked(frame.Stride * frame.Height))
            throw new ArgumentException("Expected a complete BGRA32 frame.", nameof(frame));
        lock (_sync)
        {
            _frame = frame;
            _sequence++;
        }
    }

    internal void Clear()
    {
        lock (_sync) _frame = null;
    }

    // 0: unchanged/empty; 1: copied; 2: buffer too small. Sequence changes only on success.
    internal unsafe int CopyRgba(byte* destination, int capacity, int* width, int* height, long* sequence)
    {
        if (width == null || height == null || sequence == null || capacity < 0)
            throw new ArgumentException("Invalid frame output arguments.");
        lock (_sync)
        {
            if (_frame is null || *sequence == _sequence) return 0;
            *width = _frame.Width;
            *height = _frame.Height;
            var required = checked(_frame.Width * _frame.Height * 4);
            if (destination == null || capacity < required) return 2;
            var source = _frame.Data.Span;
            // Unity texture rows start at the bottom. Strip row padding and swap B/R.
            for (var y = 0; y < _frame.Height; y++)
            {
                var row = source.Slice(y * _frame.Stride, _frame.Width * 4);
                var target = destination + (_frame.Height - 1 - y) * _frame.Width * 4;
                for (var x = 0; x < row.Length; x += 4)
                {
                    target[x] = row[x + 2];
                    target[x + 1] = row[x + 1];
                    target[x + 2] = row[x];
                    target[x + 3] = row[x + 3];
                }
            }
            *sequence = _sequence;
            return 1;
        }
    }
}
