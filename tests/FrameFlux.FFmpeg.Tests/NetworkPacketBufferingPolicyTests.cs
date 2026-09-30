using FrameFlux.FFmpeg;
using Xunit;

namespace FrameFlux.FFmpeg.Tests;

public sealed class NetworkPacketBufferingPolicyTests
{
    [Theory]
    [InlineData(2.9, 3.0, false)]
    [InlineData(3.0, 3.0, true)]
    [InlineData(1.9, 2.0, false)]
    [InlineData(2.0, 2.0, true)]
    public void StartsOnlyAfterTargetMediaDuration(double bufferedSeconds, double targetSeconds, bool expected)
    {
        Assert.Equal(expected, NetworkPacketBufferingPolicy.IsReady(
            packetCount: 100,
            packetCapacity: 1024,
            firstVideoTimestamp: 10,
            lastVideoTimestamp: 10 + bufferedSeconds,
            targetSeconds,
            sourceEnded: false));
    }

    [Fact]
    public void ShortOrUnstampedMediaCanFinishWithoutWaitingForTarget()
    {
        Assert.True(NetworkPacketBufferingPolicy.IsReady(1, 1024, null, null, 0, sourceEnded: false));
        Assert.True(NetworkPacketBufferingPolicy.IsReady(1, 1024, null, null, 3, sourceEnded: true));
        Assert.True(NetworkPacketBufferingPolicy.IsReady(1024, 1024, null, null, 3, sourceEnded: false));
        Assert.False(NetworkPacketBufferingPolicy.IsReady(100, 1024, null, null, 3, sourceEnded: false));
    }
}
