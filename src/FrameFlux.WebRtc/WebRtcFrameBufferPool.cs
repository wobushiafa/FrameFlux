using System.Runtime.InteropServices;

namespace FrameFlux.WebRtc;

/// <summary>
/// Thread-safe unmanaged memory pool for WebRTC video frames.
/// Prevents GC pressure and memory spikes by recycling buffers of uniform sizes.
/// </summary>
public sealed unsafe class WebRtcFrameBufferPool : IDisposable
{
    public const int DefaultMaximumBufferCount = 8;
    public const long DefaultMaximumRetainedBytes = 64L * 1024 * 1024; // 64 MB
    private const int MaximumTrackedSizeBuckets = 8;

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
        RuntimeCompat.AtLeast(maximumBufferCount, 1, nameof(maximumBufferCount));
        RuntimeCompat.AtLeast(maximumRetainedBytes, 1, nameof(maximumRetainedBytes));
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
        RuntimeCompat.AtLeast(requiredSize, 1, nameof(requiredSize));
        lock (_sync)
        {
            RuntimeCompat.NotDisposed(_disposed, this);
            if (_buffersBySize.TryGetValue(requiredSize, out var buffers) &&
                buffers.TryPop(out var buffer))
            {
                _retainedBufferCount--;
                _retainedBytes -= requiredSize;
                if (buffers.Count == 0 && _buffersBySize.Count > MaximumTrackedSizeBuckets)
                {
                    _buffersBySize.Remove(requiredSize);
                }

                return buffer;
            }
        }

        return AllocateBuffer(requiredSize);
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
            var effectiveMaxBytes = _maximumRetainedBytes >= DefaultMaximumRetainedBytes
                ? Math.Max(_maximumRetainedBytes, (long)size * 2)
                : _maximumRetainedBytes;
            if (_disposed ||
                !_acceptingReturns ||
                _retainedBufferCount >= _maximumBufferCount ||
                size <= 0 ||
                size > effectiveMaxBytes ||
                _retainedBytes > effectiveMaxBytes - size)
            {
                release = true;
            }
            else
            {
                if (!_buffersBySize.TryGetValue(size, out var buffers))
                {
                    PruneEmptyBucketsLocked();
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
            FreeBuffer(buffer);
        }
    }

    public void StopAcceptingReturns() => ReleaseRetainedBuffers(dispose: false);

    public void StartAcceptingReturns()
    {
        lock (_sync)
        {
            RuntimeCompat.NotDisposed(_disposed, this);
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
            FreeBuffer(buffer);
        }
    }

    private void PruneEmptyBucketsLocked()
    {
        if (_buffersBySize.Count < MaximumTrackedSizeBuckets)
        {
            return;
        }

        List<int>? emptyKeys = null;
        foreach (var pair in _buffersBySize)
        {
            if (pair.Value.Count == 0)
            {
                emptyKeys ??= [];
                emptyKeys.Add(pair.Key);
            }
        }

        if (emptyKeys is null)
        {
            return;
        }

        foreach (var key in emptyKeys)
        {
            _buffersBySize.Remove(key);
        }
    }

    private static IntPtr AllocateBuffer(int size)
    {
#if NET8_0_OR_GREATER
        return (IntPtr)NativeMemory.AlignedAlloc((nuint)size, 64);
#else
        return Marshal.AllocHGlobal(size);
#endif
    }

    private static void FreeBuffer(IntPtr buffer)
    {
#if NET8_0_OR_GREATER
        NativeMemory.AlignedFree((void*)buffer);
#else
        Marshal.FreeHGlobal(buffer);
#endif
    }
}
