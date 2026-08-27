using FrameFlux;
using FrameFlux.Presentation;
using Xunit;

namespace FrameFlux.FFmpeg.Tests;

public sealed class LatestMediaFrameSlotTests
{
    [Fact]
    public void TrySubmit_FirstFrame_SchedulesPresentation()
    {
        using var slot = new LatestMediaFrameSlot();
        var frame = new TestFrameLease();

        var accepted = slot.TrySubmit(frame, out var schedulePresentation);

        Assert.True(accepted);
        Assert.True(schedulePresentation);
        Assert.Same(frame, slot.Take());
        Assert.Equal(0, frame.DisposeCount);
        frame.Dispose();
    }

    [Fact]
    public void TrySubmit_ReplacesAndDisposesPendingFrame_WithoutRescheduling()
    {
        using var slot = new LatestMediaFrameSlot();
        var first = new TestFrameLease();
        var latest = new TestFrameLease();
        Assert.True(slot.TrySubmit(first, out _));

        var accepted = slot.TrySubmit(latest, out var schedulePresentation);

        Assert.True(accepted);
        Assert.False(schedulePresentation);
        Assert.Equal(1, first.DisposeCount);
        Assert.Same(latest, slot.Take());
        latest.Dispose();
    }

    [Fact]
    public void Take_TransfersOwnershipAndAllowsAnotherSchedule()
    {
        using var slot = new LatestMediaFrameSlot();
        var first = new TestFrameLease();
        var second = new TestFrameLease();
        Assert.True(slot.TrySubmit(first, out _));

        var taken = slot.Take();
        var accepted = slot.TrySubmit(second, out var schedulePresentation);

        Assert.Same(first, taken);
        Assert.Equal(0, first.DisposeCount);
        Assert.True(accepted);
        Assert.True(schedulePresentation);
        taken!.Dispose();
        Assert.Same(second, slot.Take());
        second.Dispose();
    }

    [Fact]
    public void Clear_DisposesPendingFrameAndAllowsAnotherSchedule()
    {
        using var slot = new LatestMediaFrameSlot();
        var first = new TestFrameLease();
        var second = new TestFrameLease();
        Assert.True(slot.TrySubmit(first, out _));

        slot.Clear();
        var accepted = slot.TrySubmit(second, out var schedulePresentation);

        Assert.Equal(1, first.DisposeCount);
        Assert.True(accepted);
        Assert.True(schedulePresentation);
        Assert.Same(second, slot.Take());
        second.Dispose();
    }

    [Fact]
    public void ReleasePendingFrame_PreservesExistingPresentationSchedule()
    {
        using var slot = new LatestMediaFrameSlot();
        var stoppedFrame = new TestFrameLease();
        var restartedFrame = new TestFrameLease();
        var nextFrame = new TestFrameLease();
        Assert.True(slot.TrySubmit(stoppedFrame, out var initialSchedule));

        slot.ReleasePendingFrame();
        var accepted = slot.TrySubmit(restartedFrame, out var duplicateSchedule);

        Assert.True(initialSchedule);
        Assert.Equal(1, stoppedFrame.DisposeCount);
        Assert.True(accepted);
        Assert.False(duplicateSchedule);
        Assert.Same(restartedFrame, slot.Take());
        restartedFrame.Dispose();

        Assert.True(slot.TrySubmit(nextFrame, out var nextSchedule));
        Assert.True(nextSchedule);
        Assert.Same(nextFrame, slot.Take());
        nextFrame.Dispose();
    }

    [Fact]
    public void Dispose_DisposesPendingFrameAndRejectsWithoutTakingOwnership()
    {
        var slot = new LatestMediaFrameSlot();
        var pending = new TestFrameLease();
        var rejected = new TestFrameLease();
        Assert.True(slot.TrySubmit(pending, out _));

        slot.Dispose();
        slot.Dispose();
        var accepted = slot.TrySubmit(rejected, out var schedulePresentation);

        Assert.Equal(1, pending.DisposeCount);
        Assert.False(accepted);
        Assert.False(schedulePresentation);
        Assert.Equal(0, rejected.DisposeCount);
        rejected.Dispose();
    }

    [Fact]
    public void ConcurrentSubmissions_DisposeEveryFrameExceptLatest()
    {
        using var slot = new LatestMediaFrameSlot();
        var frames = Enumerable.Range(0, 64)
            .Select(_ => new TestFrameLease())
            .ToArray();
        var scheduledCount = 0;

        Parallel.ForEach(frames, frame =>
        {
            Assert.True(slot.TrySubmit(frame, out var schedulePresentation));
            if (schedulePresentation)
            {
                Interlocked.Increment(ref scheduledCount);
            }
        });

        var latest = slot.Take();
        Assert.NotNull(latest);
        Assert.Equal(1, scheduledCount);
        Assert.Equal(frames.Length - 1, frames.Sum(frame => frame.DisposeCount));
        latest.Dispose();
        Assert.All(frames, frame => Assert.Equal(1, frame.DisposeCount));
    }

    private sealed class TestFrameLease : IMediaFrameLease
    {
        private int _disposeCount;

        public int Width => 1;

        public int Height => 1;

        public MediaFrameStorageKind StorageKind => MediaFrameStorageKind.CpuMemory;

        public MediaPixelFormat PixelFormat => MediaPixelFormat.Bgra32;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public bool TryGetCpuBuffer(out MediaCpuFrameBuffer buffer)
        {
            buffer = default;
            return false;
        }

        public bool TryGetD3D11Texture(out MediaD3D11TextureBuffer texture)
        {
            texture = default;
            return false;
        }

        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }
}
