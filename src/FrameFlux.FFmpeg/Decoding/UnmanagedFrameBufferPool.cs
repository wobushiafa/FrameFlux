using System.Runtime.InteropServices;

namespace FrameFlux.FFmpeg;

internal sealed unsafe class UnmanagedFrameBufferPool : IDisposable
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
        RuntimeCompat.AtLeast(maximumBufferCount, 1, nameof(maximumBufferCount));
        RuntimeCompat.AtLeast(maximumRetainedBytes, 1, nameof(maximumRetainedBytes));
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
        RuntimeCompat.AtLeast(requiredSize, 1, nameof(requiredSize));
        lock (_sync)
        {
            RuntimeCompat.NotDisposed(_disposed, this);
            if (_buffersBySize.TryGetValue(requiredSize, out var buffers) &&
                buffers.TryPop(out var buffer))
            {
                _retainedBufferCount--;
                _retainedBytes -= requiredSize;
                return buffer;
            }
        }

        return AllocateNative(requiredSize);
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
                    if (_buffersBySize.Count >= 8)
                    {
                        PruneEmptyBucketsLocked();
                    }

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
            FreeNative(buffer);
        }
    }

    internal void StartAcceptingReturns()
    {
        lock (_sync)
        {
            RuntimeCompat.NotDisposed(_disposed, this);
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
            FreeNative(buffer);
        }
    }

    private void PruneEmptyBucketsLocked()
    {
        List<int>? emptyKeys = null;
        foreach (var entry in _buffersBySize)
        {
            if (entry.Value.Count == 0)
            {
                emptyKeys ??= [];
                emptyKeys.Add(entry.Key);
            }
        }

        if (emptyKeys is not null)
        {
            foreach (var key in emptyKeys)
            {
                _buffersBySize.Remove(key);
            }
        }
    }

    private static IntPtr AllocateNative(int size)
    {
#if NET8_0_OR_GREATER
        return (IntPtr)NativeMemory.AlignedAlloc(checked((nuint)size), 64);
#else
        return Marshal.AllocHGlobal(size);
#endif
    }

    private static void FreeNative(IntPtr buffer)
    {
#if NET8_0_OR_GREATER
        NativeMemory.AlignedFree((void*)buffer);
#else
        Marshal.FreeHGlobal(buffer);
#endif
    }
}
