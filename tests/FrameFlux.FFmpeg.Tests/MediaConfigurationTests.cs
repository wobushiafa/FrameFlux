using System.Reflection;
using FrameFlux;
using FrameFlux.FFmpeg;
using Xunit;

namespace FrameFlux.FFmpeg.Tests;

public sealed class MediaConfigurationTests
{
    [Theory]
    [InlineData(MediaVideoDecodingPolicy.SoftwareOnly, (int)RtspVideoDecodingMode.SoftwareOnly)]
    [InlineData(MediaVideoDecodingPolicy.HardwarePreferred, (int)RtspVideoDecodingMode.HardwarePreferred)]
    [InlineData(MediaVideoDecodingPolicy.HardwareRequired, (int)RtspVideoDecodingMode.HardwareRequired)]
    public void DecodingPolicy_MapsExplicitModes(
        MediaVideoDecodingPolicy policy,
        int expected)
    {
        Assert.Equal(
            (RtspVideoDecodingMode)expected,
            RtspPlaybackConfiguration.ResolveVideoDecodingMode(policy));
    }

    [Fact]
    public void DecodingPolicy_AutomaticUsesPlatformDefault()
    {
        var expected = OperatingSystem.IsWindows()
            ? RtspVideoDecodingMode.HardwarePreferred
            : RtspVideoDecodingMode.SoftwareOnly;

        Assert.Equal(
            expected,
            RtspPlaybackConfiguration.ResolveVideoDecodingMode(
                MediaVideoDecodingPolicy.Automatic));
    }

    [Fact]
    public void OpenOptions_RejectInvalidNestedConfiguration()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new MediaOpenOptions { Network = null! }.Validate());
        Assert.Throws<ArgumentNullException>(() =>
            new MediaOpenOptions { Video = null! }.Validate());
        Assert.Throws<ArgumentNullException>(() =>
            new MediaOpenOptions { Audio = null! }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MediaOpenOptions
            {
                SessionSharing = (MediaSessionSharingMode)int.MaxValue
            }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MediaOpenOptions
            {
                Network = new MediaNetworkOptions
                {
                    Transport = (MediaTransport)int.MaxValue
                }
            }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MediaOpenOptions
            {
                Network = new MediaNetworkOptions { OpenTimeout = TimeSpan.Zero }
            }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MediaOpenOptions
            {
                Network = new MediaNetworkOptions
                {
                    Reconnect = new MediaReconnectOptions
                    {
                        InitialDelay = TimeSpan.FromSeconds(2),
                        MaximumDelay = TimeSpan.FromSeconds(1)
                    }
                }
            }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MediaOpenOptions
            {
                Network = new MediaNetworkOptions
                {
                    Reconnect = new MediaReconnectOptions { MaximumAttempts = -1 }
                }
            }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MediaOpenOptions
            {
                Video = new MediaVideoOptions
                {
                    DecodingPolicy = (MediaVideoDecodingPolicy)int.MaxValue
                }
            }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MediaOpenOptions
            {
                Video = new MediaVideoOptions
                {
                    SnapshotPolicy = (MediaSnapshotPolicy)int.MaxValue
                }
            }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MediaOpenOptions
            {
                Video = new MediaVideoOptions { MaximumFrameRate = double.NaN }
            }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MediaOpenOptions
            {
                Audio = new MediaAudioOptions { GainDecibels = double.NaN }
            }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MediaOpenOptions
            {
                Audio = new MediaAudioOptions { GainDecibels = 24.1d }
            }.Validate());
    }

    [Fact]
    public void ReconnectPolicy_RespectsDisableAttemptLimitAndDelayCap()
    {
        using var disabledClient = new RtspStreamClient(
            "rtsp://camera/disabled",
            new RtspStreamOptions { ReconnectEnabled = false });
        Assert.False(disabledClient.ShouldReconnect(1));

        using var limitedClient = new RtspStreamClient(
            "rtsp://camera/limited",
            new RtspStreamOptions
            {
                ReconnectEnabled = true,
                MaximumReconnectAttempts = 2,
                ReconnectInitialDelayMilliseconds = 500,
                ReconnectMaximumDelayMilliseconds = 800
            });

        Assert.True(limitedClient.ShouldReconnect(1));
        Assert.True(limitedClient.ShouldReconnect(2));
        Assert.False(limitedClient.ShouldReconnect(3));
        for (var failureCount = 1; failureCount <= 8; failureCount++)
        {
            Assert.InRange(
                limitedClient.CalculateReconnectDelayMilliseconds(failureCount),
                500,
                800);
        }
    }

    [Fact]
    public void PlayerFactoryOptions_ValidateAndCreateFactoryScopedOpenLimit()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FfmpegMediaPlayerFactory(
                new FfmpegMediaPlayerFactoryOptions
                {
                    MaximumConcurrentOpenOperations = 0
                }));

        var factory = new FfmpegMediaSessionFactory(
            options: new FfmpegMediaPlayerFactoryOptions
            {
                MaximumConcurrentOpenOperations = 3
            });
        var semaphoreField = typeof(FfmpegMediaSessionFactory).GetField(
            "_openOperationSemaphore",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var semaphore = Assert.IsType<SemaphoreSlim>(semaphoreField?.GetValue(factory));

        Assert.Equal(3, semaphore.CurrentCount);
    }
}
