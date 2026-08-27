using FrameFlux;
using FrameFlux.FFmpeg;
using Xunit;

namespace FrameFlux.FFmpeg.Tests;

public sealed class MediaFrameDeliveryTests
{
    [Fact]
    public void Deliver_AcceptedOutputOwnsLease()
    {
        var lease = new TestFrameLease();
        var output = new TestVideoOutput(OutputBehavior.Accept);

        MediaFrameDelivery.Deliver(output, lease);

        Assert.Equal(0, lease.DisposeCount);
        Assert.Same(lease, output.AcceptedFrame);
        lease.Dispose();
    }

    [Fact]
    public void Deliver_RejectedOutputDisposesLease()
    {
        var lease = new TestFrameLease();
        var output = new TestVideoOutput(OutputBehavior.Reject);

        MediaFrameDelivery.Deliver(output, lease);

        Assert.Equal(1, lease.DisposeCount);
    }

    [Fact]
    public void Deliver_ThrowingOutputDisposesLeaseAndReportsError()
    {
        var lease = new TestFrameLease();
        var output = new TestVideoOutput(OutputBehavior.Throw);
        Exception? reported = null;

        MediaFrameDelivery.Deliver(output, lease, exception => reported = exception);

        Assert.Equal(1, lease.DisposeCount);
        Assert.IsType<InvalidOperationException>(reported);
    }

    private enum OutputBehavior
    {
        Accept,
        Reject,
        Throw
    }

    private sealed class TestVideoOutput(OutputBehavior behavior) : IMediaVideoOutput
    {
        internal IMediaFrameLease? AcceptedFrame { get; private set; }

        public MediaFrameStorageKind PreferredFrameStorage => MediaFrameStorageKind.CpuMemory;

        public bool Supports(MediaFrameStorageKind storageKind, MediaPixelFormat pixelFormat) =>
            storageKind == MediaFrameStorageKind.CpuMemory &&
            pixelFormat == MediaPixelFormat.Bgra32;

        public bool TryPresent(IMediaFrameLease frame)
        {
            if (behavior == OutputBehavior.Throw)
            {
                throw new InvalidOperationException("Output failed.");
            }

            if (behavior == OutputBehavior.Reject)
            {
                return false;
            }

            AcceptedFrame = frame;
            return true;
        }
    }

    private sealed class TestFrameLease : IMediaFrameLease
    {
        internal int DisposeCount { get; private set; }

        public int Width => 2;

        public int Height => 2;

        public MediaFrameStorageKind StorageKind => MediaFrameStorageKind.CpuMemory;

        public MediaPixelFormat PixelFormat => MediaPixelFormat.Bgra32;

        public bool TryGetCpuBuffer(out MediaCpuFrameBuffer buffer)
        {
            buffer = new MediaCpuFrameBuffer(
                new IntPtr(1),
                16,
                new IntPtr(1),
                IntPtr.Zero,
                IntPtr.Zero,
                8,
                0,
                0);
            return true;
        }

        public bool TryGetD3D11Texture(out MediaD3D11TextureBuffer texture)
        {
            texture = default;
            return false;
        }

        public void Dispose() => DisposeCount++;
    }
}
