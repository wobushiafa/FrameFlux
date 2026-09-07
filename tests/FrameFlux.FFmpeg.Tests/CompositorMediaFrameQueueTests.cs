using FrameFlux.Presentation;
using Xunit;

namespace FrameFlux.FFmpeg.Tests;

public sealed class CompositorMediaFrameQueueTests
{
    [Fact]
    public void BurstOfTwoFrames_IsPresentedInOrderWithoutAnotherWakeup()
    {
        var clock = new Clock();
        using var queue = Create(clock);
        var first = new Frame();
        var second = new Frame();
        Assert.True(queue.TrySubmit(first, out var wake));
        Assert.True(wake);
        clock.Advance(2);
        Assert.True(queue.TrySubmit(second, out wake));
        Assert.False(wake);
        clock.Advance(14);
        Assert.Same(first, queue.TakeForPresentation(out var delay));
        Assert.Equal(TimeSpan.FromMilliseconds(16), delay);
        first.Dispose();
        Assert.True(queue.HasPendingFrame);
        clock.Advance(16);
        Assert.Same(second, queue.TakeForPresentation(out delay));
        Assert.Equal(TimeSpan.FromMilliseconds(30), delay);
        second.Dispose();
        Assert.False(queue.HasPendingFrame);
        Assert.Equal(0, queue.DroppedFrames);
        Assert.False(queue.CompletePresentation());
    }

    [Fact]
    public void FullQueue_RetainsOnlyNewestTwoAndDisposesOldest()
    {
        using var queue = Create();
        var frames = Enumerable.Range(0, 100).Select(_ => new Frame()).ToArray();
        foreach (var frame in frames) Assert.True(queue.TrySubmit(frame, out _));
        Assert.Equal(98, queue.DroppedFrames);
        Assert.All(frames.Take(98), f => Assert.Equal(1, f.DisposeCount));
        Assert.Same(frames[98], queue.TakeForPresentation(out _));
        frames[98].Dispose();
        Assert.Same(frames[99], queue.TakeForPresentation(out _));
        frames[99].Dispose();
        Assert.All(frames, f => Assert.Equal(1, f.DisposeCount));
    }

    [Fact]
    public void ExpiredBacklog_SkipsToLatestInsteadOfAccumulatingLatency()
    {
        var clock = new Clock();
        using var queue = Create(clock);
        var old = new Frame();
        var latest = new Frame();
        queue.TrySubmit(old, out _);
        clock.Advance(60);
        queue.TrySubmit(latest, out _);
        Assert.Same(latest, queue.TakeForPresentation(out var delay));
        Assert.Equal(TimeSpan.Zero, delay);
        Assert.Equal(1, old.DisposeCount);
        Assert.Equal(1, queue.DroppedFrames);
        latest.Dispose();
    }

    [Fact]
    public void SingleFrame_IsRetainedWhenNoNewerFrameExists()
    {
        var clock = new Clock();
        using var queue = Create(clock);
        var frame = new Frame();
        queue.TrySubmit(frame, out _);
        clock.Advance(500);
        Assert.Same(frame, queue.TakeForPresentation(out var delay));
        Assert.Equal(TimeSpan.FromMilliseconds(500), delay);
        Assert.Equal(0, queue.DroppedFrames);
        frame.Dispose();
    }

    [Fact]
    public void Clear_DrainsBothFramesAndAllowsRestart()
    {
        using var queue = Create();
        var first = new Frame();
        var second = new Frame();
        queue.TrySubmit(first, out _);
        queue.TrySubmit(second, out _);
        queue.Clear();
        Assert.False(queue.HasPendingFrame);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.DisposeCount);
        var resumed = new Frame();
        Assert.True(queue.TrySubmit(resumed, out var schedule));
        Assert.True(schedule);
        Assert.Same(resumed, queue.TakeForPresentation(out _));
        resumed.Dispose();
    }

    [Fact]
    public void Dispose_LeavesActiveFrameWithConsumerAndRejectsOwnership()
    {
        using var queue = Create();
        var active = new Frame();
        queue.TrySubmit(active, out _);
        Assert.Same(active, queue.TakeForPresentation(out _));
        var pending = new[] { new Frame(), new Frame() };
        foreach (var frame in pending) queue.TrySubmit(frame, out _);
        queue.Dispose();
        queue.Dispose();
        Assert.All(pending, f => Assert.Equal(1, f.DisposeCount));
        Assert.Equal(0, active.DisposeCount);
        Assert.False(queue.CompletePresentation());
        var rejected = new Frame();
        Assert.False(queue.TrySubmit(rejected, out var wake));
        Assert.False(wake);
        Assert.Equal(0, rejected.DisposeCount);
        rejected.Dispose();
        active.Dispose();
    }

    [Fact]
    public void CompletionRacingProducer_HasExactlyOneWakeupOwner()
    {
        for (var i = 0; i < 1000; i++)
        {
            using var queue = Create();
            var active = new Frame();
            queue.TrySubmit(active, out _);
            queue.TakeForPresentation(out _)!.Dispose();
            var next = new Frame();
            var completionWake = false;
            var submissionWake = false;
            Parallel.Invoke(() => completionWake = queue.CompletePresentation(),
                () => Assert.True(queue.TrySubmit(next, out submissionWake)));
            Assert.True(completionWake ^ submissionWake);
            Assert.Same(next, queue.TakeForPresentation(out _));
            next.Dispose();
        }
    }

    [Fact]
    public void ConcurrentSubmissions_RemainBoundedAndDisposeEveryLeaseOnce()
    {
        using var queue = Create();
        var frames = Enumerable.Range(0, 256).Select(_ => new Frame()).ToArray();
        var wakes = 0;
        Parallel.ForEach(frames, frame =>
        {
            Assert.True(queue.TrySubmit(frame, out var schedule));
            if (schedule) Interlocked.Increment(ref wakes);
        });
        Assert.Equal(1, wakes);
        Assert.Equal(254, frames.Sum(f => f.DisposeCount));
        queue.Dispose();
        Assert.All(frames, f => Assert.Equal(1, f.DisposeCount));
    }

    [Fact]
    public void PairedArrivalJitter_DoesNotDropFramesThatFitRefreshCapacity()
    {
        var clock = new Clock();
        using var queue = Create(clock);
        var frames = new List<Frame>();
        for (var ms = 0; ms < 1020; ms++)
        {
            if (ms % 34 == 0)
            {
                for (var n = 0; n < 2; n++)
                {
                    var frame = new Frame();
                    frames.Add(frame);
                    queue.TrySubmit(frame, out _);
                }
            }
            if (ms % 17 == 16)
                queue.TakeForPresentation(out _)?.Dispose();
            clock.Advance(1);
        }
        Assert.Equal(0, queue.DroppedFrames);
        Assert.All(frames, f => Assert.Equal(1, f.DisposeCount));
    }

    private static CompositorMediaFrameQueue Create(TimeProvider? clock = null) =>
        new(TimeSpan.FromMilliseconds(50), clock);

    private sealed class Clock : TimeProvider
    {
        private long _time;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _time;
        internal void Advance(long milliseconds) => _time += milliseconds;
    }

    private sealed class Frame : IMediaFrameLease
    {
        private int _disposeCount;
        internal int DisposeCount => Volatile.Read(ref _disposeCount);
        public int Width => 1;
        public int Height => 1;
        public MediaFrameStorageKind StorageKind => MediaFrameStorageKind.D3D11Texture;
        public MediaPixelFormat PixelFormat => MediaPixelFormat.Bgra32;
        public bool TryGetCpuBuffer(out MediaCpuFrameBuffer buffer) { buffer = default; return false; }
        public bool TryGetD3D11Texture(out MediaD3D11TextureBuffer texture) { texture = default; return false; }
        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }
}
