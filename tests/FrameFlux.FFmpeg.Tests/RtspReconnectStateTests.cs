using FrameFlux.FFmpeg;
using Xunit;

namespace FrameFlux.FFmpeg.Tests;

public sealed class RtspReconnectStateTests
{
    [Fact]
    public void RegisterFailure_AppliesAttemptLimitAndDeterministicBackoff()
    {
        var state = new RtspReconnectState(
            new RtspStreamOptions
            {
                ReconnectEnabled = true,
                MaximumReconnectAttempts = 2,
                ReconnectInitialDelayMilliseconds = 500,
                ReconnectMaximumDelayMilliseconds = 800
            },
            _ => 0);

        var first = state.RegisterFailure();
        var second = state.RegisterFailure();
        var rejected = state.RegisterFailure();

        Assert.Equal(new RtspReconnectDecision(true, 1, TimeSpan.FromMilliseconds(500)), first);
        Assert.Equal(new RtspReconnectDecision(true, 2, TimeSpan.FromMilliseconds(800)), second);
        Assert.Equal(new RtspReconnectDecision(false, 3, TimeSpan.Zero), rejected);
        Assert.Equal(new MediaReconnectDiagnostics(3, 2, 0, TimeSpan.Zero), state.Diagnostics);
    }

    [Fact]
    public void RegisterSuccess_CountsOneRecoveryAndResetsConsecutiveFailures()
    {
        var state = new RtspReconnectState(
            new RtspStreamOptions
            {
                ReconnectEnabled = true,
                ReconnectInitialDelayMilliseconds = 100,
                ReconnectMaximumDelayMilliseconds = 1000
            },
            _ => 0);

        state.RegisterFailure();

        Assert.True(state.RegisterSuccess());
        Assert.False(state.RegisterSuccess());
        Assert.Equal(new MediaReconnectDiagnostics(0, 1, 1, TimeSpan.Zero), state.Diagnostics);
    }

    [Fact]
    public void RegisterFailure_WhenDisabled_DoesNotCountRetryAttempt()
    {
        var state = new RtspReconnectState(
            new RtspStreamOptions { ReconnectEnabled = false },
            _ => 0);

        var decision = state.RegisterFailure();

        Assert.False(decision.RetryAllowed);
        Assert.Equal(1, state.Diagnostics.ConsecutiveFailures);
        Assert.Equal(0, state.Diagnostics.TotalAttempts);
    }
}
