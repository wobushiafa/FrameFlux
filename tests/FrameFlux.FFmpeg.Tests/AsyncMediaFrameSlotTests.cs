using FrameFlux.Presentation;
using Xunit;

namespace FrameFlux.FFmpeg.Tests;

public sealed class AsyncMediaFrameSlotTests
{
    [Fact]
    public void RenderPump_KeepsOneReservationAcrossFramesAndRearmsAfterIdle()
    {
        using var slot = new LatestMediaFrameSlot();
        for (var i = 0; i < 300; i++)
        {
            var frame = new Frame();
            Assert.False(slot.HasPendingFrame);
            Assert.True(slot.TrySubmit(frame, out var schedule));
            Assert.Equal(i == 0, schedule);
            Assert.True(slot.HasPendingFrame);
            Assert.Same(frame, slot.TakeForPresentation());
            Assert.False(slot.HasPendingFrame);
            frame.Dispose();
            Assert.Equal(1, frame.DisposeCount);
        }
        Assert.False(slot.CompletePresentation());
        var resumed = new Frame();
        Assert.True(slot.TrySubmit(resumed, out var resumedSchedule));
        Assert.True(resumedSchedule);
        slot.Clear();
        Assert.Equal(1, resumed.DisposeCount);
    }

    [Fact]
    public void RenderPump_ClearReleasesPendingFrameAndAllowsRestart()
    {
        using var slot = new LatestMediaFrameSlot();
        var pending = new Frame();
        Assert.True(slot.TrySubmit(pending, out _));
        slot.Clear();
        Assert.False(slot.HasPendingFrame);
        Assert.Equal(1, pending.DisposeCount);
        var restarted = new Frame();
        Assert.True(slot.TrySubmit(restarted, out var schedule));
        Assert.True(schedule);
        Assert.Same(restarted, slot.TakeForPresentation());
        restarted.Dispose();
        Assert.False(slot.CompletePresentation());
    }

    [Fact]
    public void InFlightPresentation_CoalescesFramesWithoutAdditionalCallbacks()
    {
        using var slot = new LatestMediaFrameSlot();
        using var active = new Frame();
        Assert.True(slot.TrySubmit(active, out var scheduled));
        Assert.True(scheduled);
        Assert.Same(active, slot.TakeForPresentation());
        var frames = Enumerable.Range(0, 128).Select(_ => new Frame()).ToArray();
        Parallel.ForEach(frames, frame =>
        {
            Assert.True(slot.TrySubmit(frame, out var duplicate));
            Assert.False(duplicate);
        });
        Assert.Equal(127, frames.Sum(frame => frame.DisposeCount));
        Assert.True(slot.CompletePresentation());
        var latest = slot.TakeForPresentation();
        Assert.NotNull(latest);
        latest.Dispose();
        Assert.False(slot.CompletePresentation());
        Assert.All(frames, frame => Assert.Equal(1, frame.DisposeCount));
        Assert.Equal(0, active.DisposeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClearAndRestart_PreservesQueuedOrRunningCallback(bool running)
    {
        using var slot = new LatestMediaFrameSlot();
        var old = new Frame();
        var restarted = new Frame();
        slot.TrySubmit(old, out _);
        if (running)
        {
            Assert.Same(old, slot.TakeForPresentation());
        }
        slot.ReleasePendingFrame();
        Assert.True(slot.TrySubmit(restarted, out var duplicate));
        Assert.False(duplicate);
        if (running)
        {
            Assert.Equal(0, old.DisposeCount);
            old.Dispose();
            Assert.True(slot.CompletePresentation());
        }
        Assert.Same(restarted, slot.TakeForPresentation());
        restarted.Dispose();
        Assert.False(slot.CompletePresentation());
        Assert.Equal(1, old.DisposeCount);
        Assert.Equal(1, restarted.DisposeCount);
    }

    [Fact]
    public void DisposeDuringPresentation_DoesNotRescheduleOrDisposeActiveFrame()
    {
        using var slot = new LatestMediaFrameSlot();
        using var active = new Frame();
        var pending = new Frame();
        using var rejected = new Frame();
        slot.TrySubmit(active, out _);
        Assert.Same(active, slot.TakeForPresentation());
        slot.TrySubmit(pending, out _);
        slot.Dispose();
        Assert.False(slot.CompletePresentation());
        Assert.Equal(0, active.DisposeCount);
        Assert.Equal(1, pending.DisposeCount);
        Assert.False(slot.TrySubmit(rejected, out var scheduled));
        Assert.False(scheduled);
        Assert.Equal(0, rejected.DisposeCount);
    }

    [Fact]
    public void CompletionRacingSubmission_ProducesExactlyOneSchedule()
    {
        for (var i = 0; i < 1000; i++)
        {
            using var slot = new LatestMediaFrameSlot();
            using var active = new Frame();
            using var next = new Frame();
            slot.TrySubmit(active, out _);
            Assert.Same(active, slot.TakeForPresentation());
            var completionSchedule = false;
            var submissionSchedule = false;
            Parallel.Invoke(
                () => completionSchedule = slot.CompletePresentation(),
                () => Assert.True(slot.TrySubmit(next, out submissionSchedule)));
            Assert.True(completionSchedule ^ submissionSchedule);
            Assert.Same(next, slot.TakeForPresentation());
            Assert.False(slot.CompletePresentation());
        }
    }

    [Fact]
    public void EmptyCallback_CompletesAndAllowsFutureFrames()
    {
        using var slot = new LatestMediaFrameSlot();
        var cleared = new Frame();
        using var next = new Frame();
        slot.TrySubmit(cleared, out _);
        slot.ReleasePendingFrame();
        Assert.Null(slot.TakeForPresentation());
        Assert.False(slot.CompletePresentation());
        Assert.True(slot.TrySubmit(next, out var scheduled));
        Assert.True(scheduled);
        Assert.Same(next, slot.TakeForPresentation());
        Assert.False(slot.CompletePresentation());
        Assert.Equal(1, cleared.DisposeCount);
    }

    private sealed class Frame : IMediaFrameLease
    {
        private int _disposeCount;
        public int Width => 1;
        public int Height => 1;
        public MediaFrameStorageKind StorageKind => MediaFrameStorageKind.D3D11Texture;
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
