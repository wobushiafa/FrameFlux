using System.Runtime.InteropServices;

namespace FrameFlux.WebRtc;

/// <summary>
/// Thread-safe unmanaged memory pool for WebRTC video frames.
/// Prevents GC pressure and memory spikes by recycling buffers of uniform sizes.
/// </summary>
public sealed class WebRtcFrameBufferPool : IDisposable
{
    public const int DefaultMaximumBufferCount = 8;
    public const long DefaultMaximumRetainedBytes = 64L * 1024 * 1024; // 64 MB

    private readonly object _sync = new();
    private readonly Dictionary<int, Stack<IntPtr>> _buffersBySize = [];
    private readonly int _maximumBufferCount;
    private readonly long _maximumRetainedBytes;
    private int _retainedBufferCount;
    private long _retainedBytes;
    private bool _acceptingReturns = true;
    private bool _disposed;

    public WebRtcFrameBufferPool(
        int maximumBufferCount = DefaultMaximumBufferCount,
        long maximumRetainedBytes = DefaultMaximumRetainedBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBufferCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumRetainedBytes, 1);
        _maximumBufferCount = maximumBufferCount;
        _maximumRetainedBytes = maximumRetainedBytes;
    }

    public int RetainedBufferCount
    {
        get
        {
            lock (_sync)
            {
                return _retainedBufferCount;
            }
        }
    }

    public long RetainedBytes
    {
        get
        {
            lock (_sync)
            {
                return _retainedBytes;
            }
        }
    }

    public IntPtr Rent(int requiredSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(requiredSize, 1);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_buffersBySize.TryGetValue(requiredSize, out var buffers) &&
                buffers.TryPop(out var buffer))
            {
                _retainedBufferCount--;
                _retainedBytes -= requiredSize;
                if (buffers.Count == 0)
                {
                    _buffersBySize.Remove(requiredSize);
                }

                return buffer;
            }
        }

        return Marshal.AllocHGlobal(requiredSize);
    }

    public void Return(IntPtr buffer, int size)
    {
        if (buffer == IntPtr.Zero)
        {
            return;
        }

        var release = false;
        lock (_sync)
        {
            if (_disposed ||
                !_acceptingReturns ||
                _retainedBufferCount >= _maximumBufferCount ||
                size <= 0 ||
                size > _maximumRetainedBytes ||
                _retainedBytes > _maximumRetainedBytes - size)
            {
                release = true;
            }
            else
            {
                if (!_buffersBySize.TryGetValue(size, out var buffers))
                {
                    buffers = new Stack<IntPtr>();
                    _buffersBySize.Add(size, buffers);
                }

                buffers.Push(buffer);
                _retainedBufferCount++;
                _retainedBytes += size;
            }
        }

        if (release)
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void StopAcceptingReturns() => ReleaseRetainedBuffers(dispose: false);

    public void StartAcceptingReturns()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _acceptingReturns = true;
        }
    }

    public void Dispose() => ReleaseRetainedBuffers(dispose: true);

    private void ReleaseRetainedBuffers(bool dispose)
    {
        List<IntPtr> buffers;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = dispose;
            _acceptingReturns = false;
            buffers = new List<IntPtr>(_retainedBufferCount);
            foreach (var sizePool in _buffersBySize.Values)
            {
                buffers.AddRange(sizePool);
            }

            _buffersBySize.Clear();
            _retainedBufferCount = 0;
            _retainedBytes = 0;
        }

        foreach (var buffer in buffers)
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
