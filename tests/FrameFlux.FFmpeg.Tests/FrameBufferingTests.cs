using FrameFlux.FFmpeg;
using Xunit;

namespace FrameFlux.FFmpeg.Tests;

public sealed class FrameBufferingTests
{
    [Fact]
    public void FrameLease_ConcurrentDispose_ReturnsExactlyOnce()
    {
        var returnCount = 0;
        var lease = new FfmpegMediaFrameLease(
            new IntPtr(1),
            16,
            _ => Interlocked.Increment(ref returnCount));
        lease.ResetBgra(2, 2, 8);

        Parallel.For(0, 128, _ => lease.Dispose());

        Assert.Equal(1, Volatile.Read(ref returnCount));
        Assert.False(lease.TryGetCpuBuffer(out _));
    }

    [Fact]
    public void FrameLease_DisposedD3D11Lease_DoesNotExposeTexture()
    {
        var lease = new FfmpegMediaFrameLease(IntPtr.Zero, 0, _ => { });
        lease.ResetD3D11(1920, 1080, new IntPtr(42), 3);
        Assert.True(lease.TryGetD3D11Texture(out _));

        lease.Dispose();

        Assert.False(lease.TryGetD3D11Texture(out _));
    }

    [Fact]
    public void AudioQueue_WhenFull_DropsOldestFrames()
    {
        var queue = new BoundedAudioFrameQueue(capacity: 3);
        for (var timestamp = 0; timestamp < 5; timestamp++)
        {
            queue.Enqueue(CreateAudioFrame(timestamp));
        }

        Assert.Equal(3, queue.Count);
        Assert.Equal(2, queue.DroppedCount);
        Assert.True(queue.TryDequeue(out var first));
        Assert.True(queue.TryDequeue(out var second));
        Assert.True(queue.TryDequeue(out var third));
        Assert.Equal(2, first!.PresentationTimestamp);
        Assert.Equal(3, second!.PresentationTimestamp);
        Assert.Equal(4, third!.PresentationTimestamp);
        Assert.False(queue.TryDequeue(out _));
    }

    [Fact]
    public void ReusableBuffer_ReusesCapacityAndGrowsOnDemand()
    {
        var buffer = new ReusableUnmanagedBuffer();
        buffer.EnsureCapacity(1024);
        var initialPointer = buffer.Pointer;
        var initialCapacity = buffer.Capacity;

        Assert.NotEqual(IntPtr.Zero, initialPointer);
        Assert.True(initialCapacity >= 1024);

        buffer.EnsureCapacity(initialCapacity);
        Assert.Equal(initialPointer, buffer.Pointer);
        Assert.Equal(initialCapacity, buffer.Capacity);

        buffer.EnsureCapacity(checked(initialCapacity + 1));
        Assert.NotEqual(IntPtr.Zero, buffer.Pointer);
        Assert.True(buffer.Capacity > initialCapacity);

        buffer.Dispose();
        Assert.Equal(IntPtr.Zero, buffer.Pointer);
        Assert.Equal(0, buffer.Capacity);
        Assert.Throws<ObjectDisposedException>(() => buffer.EnsureCapacity(1));
    }

    [Fact]
    public void FrameBufferPool_BoundsRetainedCountAndBytes()
    {
        using var pool = new UnmanagedFrameBufferPool(
            maximumBufferCount: 2,
            maximumRetainedBytes: 32);
        var first = pool.Rent(16);
        var second = pool.Rent(16);
        var excess = pool.Rent(16);

        pool.Return(first, 16);
        pool.Return(second, 16);
        pool.Return(excess, 16);

        Assert.Equal(2, pool.RetainedBufferCount);
        Assert.Equal(32, pool.RetainedBytes);

        var reused = pool.Rent(16);
        Assert.Equal(1, pool.RetainedBufferCount);
        Assert.Equal(16, pool.RetainedBytes);
        pool.Return(reused, 16);

        pool.StopAcceptingReturns();
        Assert.Equal(0, pool.RetainedBufferCount);
        Assert.Equal(0, pool.RetainedBytes);

        var unpooled = pool.Rent(16);
        pool.Return(unpooled, 16);
        Assert.Equal(0, pool.RetainedBufferCount);
    }

    private static NativeAudioFrame CreateAudioFrame(long timestamp) =>
        new([0], 48_000, 2, timestamp, 1, 1);
}
