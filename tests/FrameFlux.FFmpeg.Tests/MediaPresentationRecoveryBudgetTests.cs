using FrameFlux.Presentation;
using Xunit;

namespace FrameFlux.FFmpeg.Tests;

public sealed class MediaPresentationRecoveryBudgetTests
{
    [Fact]
    public void Failures_UseBoundedBackendRestartsBeforeSoftwareFallback()
    {
        var tracker = new MediaPresentationFailureTracker(maximumAttempts: 3);

        var first = tracker.Register(new InvalidOperationException("first"));
        var second = tracker.Register(new InvalidOperationException("second"));
        var fallback = tracker.Register(new InvalidOperationException("third"));
        var exhausted = tracker.Register(new InvalidOperationException("fourth"));

        Assert.True(first.RequiresBackendRestart);
        Assert.Equal(1, first.RestartAttemptCount);
        Assert.True(second.RequiresBackendRestart);
        Assert.Equal(2, second.RestartAttemptCount);
        Assert.False(fallback.RequiresBackendRestart);
        Assert.True(fallback.RequiresSoftwareFallback);
        Assert.Equal(2, fallback.RestartAttemptCount);
        Assert.False(exhausted.RequiresBackendRestart);
        Assert.False(exhausted.RequiresSoftwareFallback);
    }

    [Fact]
    public void Success_RestoresTheBackendRestartBudget()
    {
        var tracker = new MediaPresentationFailureTracker(maximumAttempts: 3);
        tracker.Register(new InvalidOperationException("first"));
        tracker.Register(new InvalidOperationException("second"));

        tracker.ReportSuccess();
        var next = tracker.Register(new InvalidOperationException("next"));

        Assert.True(next.RequiresBackendRestart);
        Assert.Equal(1, next.RestartAttemptCount);
        Assert.Equal(1, next.ConsecutiveFailureCount);
    }
}
