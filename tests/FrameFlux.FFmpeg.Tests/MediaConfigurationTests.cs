using System.Reflection;
using FrameFlux;
using FrameFlux.FFmpeg;
using Xunit;

namespace FrameFlux.FFmpeg.Tests;

public sealed class MediaConfigurationTests
{
    [Theory]
    [InlineData(MediaVideoDecodingPolicy.SoftwareOnly, (int)FfmpegVideoDecodingMode.SoftwareOnly)]
    [InlineData(MediaVideoDecodingPolicy.HardwarePreferred, (int)FfmpegVideoDecodingMode.HardwarePreferred)]
    [InlineData(MediaVideoDecodingPolicy.HardwareRequired, (int)FfmpegVideoDecodingMode.HardwareRequired)]
    public void DecodingPolicy_MapsExplicitModes(
        MediaVideoDecodingPolicy policy,
        int expected)
    {
        Assert.Equal(
            (FfmpegVideoDecodingMode)expected,
            FfmpegPlaybackConfiguration.ResolveVideoDecodingMode(policy));
    }

    [Fact]
    public void DecodingPolicy_AutomaticUsesPlatformDefault()
    {
        var expected = OperatingSystem.IsWindows() || OperatingSystem.IsLinux()
            ? FfmpegVideoDecodingMode.HardwarePreferred
            : FfmpegVideoDecodingMode.SoftwareOnly;

        Assert.Equal(
            expected,
            FfmpegPlaybackConfiguration.ResolveVideoDecodingMode(
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
        Assert.Throws<ArgumentException>(() =>
            new MediaOpenOptions
            {
                Audio = new MediaAudioOptions { OutputDeviceId = " " }
            }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MediaOpenOptions
            {
                Audio = new MediaAudioOptions
                {
                    BufferDuration = TimeSpan.FromMilliseconds(9)
                }
            }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MediaOpenOptions
            {
                Audio = new MediaAudioOptions
                {
                    BufferDuration = TimeSpan.FromMilliseconds(2001)
                }
            }.Validate());
    }

    [Fact]
    public void ReconnectPolicy_RespectsDisableAttemptLimitAndDelayCap()
    {
        using var disabledClient = new FfmpegPlaybackClient(
            "rtsp://camera/disabled",
            new FfmpegPlaybackOptions { ReconnectEnabled = false });
        Assert.False(disabledClient.ShouldReconnect(1));

        using var limitedClient = new FfmpegPlaybackClient(
            "rtsp://camera/limited",
            new FfmpegPlaybackOptions
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
    public void PlayerFactoryOptions_ValidateAndCreateProcessSharedOpenLimit()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FfmpegMediaPlayerFactory(
                new FfmpegMediaPlayerFactoryOptions
                {
                    MaximumConcurrentOpenOperations = 0
                }));

        var firstFactory = new FfmpegMediaSessionFactory(
            options: new FfmpegMediaPlayerFactoryOptions
            {
                MaximumConcurrentOpenOperations = 3
            });
        var secondFactory = new FfmpegMediaSessionFactory(
            options: new FfmpegMediaPlayerFactoryOptions
            {
                MaximumConcurrentOpenOperations = 3
            });
        var semaphoreField = typeof(FfmpegMediaSessionFactory).GetField(
            "_openOperationSemaphore",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var firstSemaphore = Assert.IsType<SemaphoreSlim>(
            semaphoreField?.GetValue(firstFactory));
        var secondSemaphore = Assert.IsType<SemaphoreSlim>(
            semaphoreField?.GetValue(secondFactory));

        Assert.Same(firstSemaphore, secondSemaphore);
        Assert.Equal(3, firstSemaphore.CurrentCount);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(8, 2)]
    [InlineData(9, 3)]
    [InlineData(12, 3)]
    [InlineData(13, 4)]
    [InlineData(64, 4)]
    public void PlayerFactoryOptions_RecommendedOpenLimitScalesFromTwoToFour(
        int processorCount,
        int expected)
    {
        Assert.Equal(
            expected,
            FfmpegMediaPlayerFactoryOptions
                .CalculateRecommendedMaximumConcurrentOpenOperations(processorCount));
    }

    [Theory]
    [InlineData(MediaSnapshotPolicy.Disabled, false)]
    [InlineData(MediaSnapshotPolicy.KeepLatestFrame, true)]
    public void SnapshotPolicy_ControlsGpuSnapshotCopies(
        MediaSnapshotPolicy policy,
        bool expected)
    {
        var session = new FfmpegMediaSession(
            MediaSource.Parse("rtsp://camera/snapshot"),
            new MediaOpenOptions
            {
                Video = new MediaVideoOptions { SnapshotPolicy = policy }
            },
            volume: 1d,
            isMuted: false,
            videoOutput: null);
        var method = typeof(FfmpegMediaSession).GetMethod(
            "CreateEngineOptions",
            BindingFlags.Instance | BindingFlags.NonPublic);

        var options = Assert.IsType<FfmpegPlaybackOptions>(method?.Invoke(session, null));

        Assert.Equal(expected, options.CreateSnapshotFrames);
    }
}
