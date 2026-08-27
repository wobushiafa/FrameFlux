using System.Runtime.InteropServices;

namespace FrameFlux.FFmpeg;

internal sealed class UnmanagedFrameBufferPool : IDisposable
{
    internal const int DefaultMaximumBufferCount = 4;
    internal const long DefaultMaximumRetainedBytes = 64L * 1024 * 1024;

    private readonly object _sync = new();
    private readonly Dictionary<int, Stack<IntPtr>> _buffersBySize = [];
    private readonly int _maximumBufferCount;
    private readonly long _maximumRetainedBytes;
    private int _retainedBufferCount;
    private long _retainedBytes;
    private bool _acceptingReturns = true;
    private bool _disposed;

    internal UnmanagedFrameBufferPool(
        int maximumBufferCount = DefaultMaximumBufferCount,
        long maximumRetainedBytes = DefaultMaximumRetainedBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBufferCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumRetainedBytes, 1);
        _maximumBufferCount = maximumBufferCount;
        _maximumRetainedBytes = maximumRetainedBytes;
    }

    internal int RetainedBufferCount
    {
        get
        {
            lock (_sync)
            {
                return _retainedBufferCount;
            }
        }
    }

    internal long RetainedBytes
    {
        get
        {
            lock (_sync)
            {
                return _retainedBytes;
            }
        }
    }

    internal IntPtr Rent(int requiredSize)
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

    internal void Return(IntPtr buffer, int size)
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

    internal void StartAcceptingReturns()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _acceptingReturns = true;
        }
    }

    internal void StopAcceptingReturns() => ReleaseRetainedBuffers(dispose: false);

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
