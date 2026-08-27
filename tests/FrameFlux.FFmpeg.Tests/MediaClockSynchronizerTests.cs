using FrameFlux.FFmpeg;
using Xunit;

namespace FrameFlux.FFmpeg.Tests;

public sealed class MediaClockSynchronizerTests
{
    [Fact]
    public void LateVideoFrame_IsDropped()
    {
        var synchronizer = new MediaClockSynchronizer();
        _ = synchronizer.EvaluateVideo(10d, 10d);

        var decision = synchronizer.EvaluateVideo(10.05d, 10.20d);

        Assert.Equal(MediaVideoSynchronizationAction.Drop, decision.Action);
        Assert.Equal(1, synchronizer.GetDiagnostics(0).DroppedVideoFrames);
    }

    [Fact]
    public void EarlyVideoFrame_IsDelayedByBoundedAmount()
    {
        var synchronizer = new MediaClockSynchronizer();

        var decision = synchronizer.EvaluateVideo(10.25d, 10d);

        Assert.Equal(MediaVideoSynchronizationAction.Delay, decision.Action);
        Assert.Equal(TimeSpan.FromMilliseconds(250), decision.Delay);
        Assert.Equal(1, synchronizer.GetDiagnostics(0).DelayedVideoFrames);
    }

    [Fact]
    public void DifferentInitialTimestampOrigins_AreRebased()
    {
        var synchronizer = new MediaClockSynchronizer();

        var decision = synchronizer.EvaluateVideo(100d, 5d);
        var diagnostics = synchronizer.GetDiagnostics(0);

        Assert.Equal(MediaVideoSynchronizationAction.Render, decision.Action);
        Assert.Equal(TimeSpan.Zero, diagnostics.AudioVideoOffset);
        Assert.Equal(1, diagnostics.ClockResetCount);
    }

    [Fact]
    public void TimestampJump_RebasesInsteadOfPermanentDropOrDelay()
    {
        var synchronizer = new MediaClockSynchronizer();
        _ = synchronizer.EvaluateVideo(1d, 1d);

        var decision = synchronizer.EvaluateVideo(10d, 10d);

        Assert.Equal(MediaVideoSynchronizationAction.Render, decision.Action);
        Assert.Equal(1, synchronizer.GetDiagnostics(0).ClockResetCount);
    }
}
