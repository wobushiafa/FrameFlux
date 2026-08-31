using System;
using System.Collections.Generic;
using System.Diagnostics;
using FrameFlux.FFmpeg;
using Xunit;

namespace FrameFlux.FFmpeg.Tests;

public sealed class FfmpegPlaybackComponentTests
{
    [Theory]
    [InlineData(1920, 1080, 1280, 720, 1280, 720)]
    [InlineData(1920, 1080, 640, 0, 640, 360)]
    [InlineData(1280, 720, 1920, 1080, 1280, 720)]
    [InlineData(0, -1, 1280, 720, 1, 1)]
    public void OutputSize_PreservesAspectRatioAndDoesNotUpscale(
        int sourceWidth,
        int sourceHeight,
        int maxWidth,
        int maxHeight,
        int expectedWidth,
        int expectedHeight)
    {
        var actual = FfmpegPlaybackPolicy.CalculateOutputSize(
            sourceWidth,
            sourceHeight,
            maxWidth,
            maxHeight);

        Assert.Equal((expectedWidth, expectedHeight), actual);
    }

    [Fact]
    public void HardwareFailure_FallsBackOnlyWhenPolicyAllowsIt()
    {
        var failure = new FfmpegDecoderRuntimeException(
            "Hardware decoder failed.",
            innerException: null,
            isHardwareVideoDecodingActive: true);

        Assert.True(FfmpegPlaybackPolicy.ShouldFallbackToSoftware(
            new FfmpegPlaybackOptions
            {
                VideoDecodingMode = FfmpegVideoDecodingMode.HardwarePreferred
            },
            failure));
        Assert.False(FfmpegPlaybackPolicy.ShouldFallbackToSoftware(
            new FfmpegPlaybackOptions
            {
                VideoDecodingMode = FfmpegVideoDecodingMode.HardwareRequired
            },
            failure));
    }

    [Fact]
    public void SoftwareFallback_PreservesPlaybackSettings()
    {
        var source = new FfmpegPlaybackOptions
        {
            FrameDeliveryMode = FfmpegFrameDeliveryMode.DmaBuf,
            Transport = "udp",
            MaxFramesPerSecond = 24,
            MaxVideoWidth = 1280,
            MaxVideoHeight = 720,
            EnableAudio = false,
            Volume = 0.4,
            IsMuted = true,
            ScaleQuality = FfmpegScaleQuality.Bicubic
        };

        var fallback = FfmpegPlaybackPolicy.CreateSoftwareFallbackOptions(source);

        Assert.Equal(FfmpegVideoDecodingMode.SoftwareOnly, fallback.VideoDecodingMode);
        Assert.Equal(source.FrameDeliveryMode, fallback.FrameDeliveryMode);
        Assert.Equal(source.Transport, fallback.Transport);
        Assert.Equal(source.MaxFramesPerSecond, fallback.MaxFramesPerSecond);
        Assert.Equal(source.MaxVideoWidth, fallback.MaxVideoWidth);
        Assert.Equal(source.MaxVideoHeight, fallback.MaxVideoHeight);
        Assert.Equal(source.EnableAudio, fallback.EnableAudio);
        Assert.Equal(source.Volume, fallback.Volume);
        Assert.Equal(source.IsMuted, fallback.IsMuted);
        Assert.Equal(source.ScaleQuality, fallback.ScaleQuality);
    }

    [Fact]
    public void PerformanceTracker_PublishesEveryThirtySamples()
    {
        var snapshots = new List<FfmpegPerformanceSnapshot>();
        var tracker = new FfmpegPerformanceTracker(snapshots.Add);

        for (var index = 0; index < 29; index++)
        {
            tracker.Record(
                Stopwatch.Frequency,
                Stopwatch.Frequency,
                Stopwatch.Frequency,
                Stopwatch.Frequency,
                Stopwatch.Frequency,
                Stopwatch.Frequency);
        }

        Assert.Empty(snapshots);

        tracker.Record(
            Stopwatch.Frequency,
            Stopwatch.Frequency,
            Stopwatch.Frequency,
            Stopwatch.Frequency,
            Stopwatch.Frequency,
            Stopwatch.Frequency);

        var snapshot = Assert.Single(snapshots);
        Assert.Equal(30, snapshot.SampleCount);
        Assert.Equal(1000d, snapshot.ReadMilliseconds);
        Assert.Equal(1000d, snapshot.DecodeMilliseconds);
        Assert.Equal(1000d, snapshot.DispatchMilliseconds);
    }
}
