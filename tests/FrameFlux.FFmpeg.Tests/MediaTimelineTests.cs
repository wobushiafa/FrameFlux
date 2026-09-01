using Xunit;

namespace FrameFlux.FFmpeg.Tests;

public sealed class MediaTimelineTests
{
    [Fact]
    public void RelativePosition_SubtractsStreamStartTimestamp()
    {
        var stream = CreateStreamInfo(startTimestamp: 90_000);
        var frame = CreateFrameInfo(presentationTimestamp: 95_000);

        var position = FfmpegDecoder.GetRelativePosition(frame, stream);

        Assert.Equal(TimeSpan.FromSeconds(5), position);
    }

    [Fact]
    public void RelativePosition_ClampsFramesBeforeStreamStartToZero()
    {
        var stream = CreateStreamInfo(startTimestamp: 90_000);
        var frame = CreateFrameInfo(presentationTimestamp: 89_500);

        var position = FfmpegDecoder.GetRelativePosition(frame, stream);

        Assert.Equal(TimeSpan.Zero, position);
    }

    [Fact]
    public void SeekTimestamp_AddsStreamStartTimestamp()
    {
        var stream = CreateStreamInfo(startTimestamp: 90_000);

        var timestamp = FfmpegDecoder.GetSeekTimestamp(TimeSpan.FromSeconds(5), stream);

        Assert.Equal(95_000, timestamp);
    }

    [Fact]
    public void UnknownStreamStartTimestamp_FallsBackToZero()
    {
        var stream = CreateStreamInfo(startTimestamp: long.MinValue);
        var frame = CreateFrameInfo(presentationTimestamp: 5_000);

        Assert.Equal(
            TimeSpan.FromSeconds(5),
            FfmpegDecoder.GetRelativePosition(frame, stream));
        Assert.Equal(
            5_000,
            FfmpegDecoder.GetSeekTimestamp(TimeSpan.FromSeconds(5), stream));
    }

    [Fact]
    public void MissingPresentationTimestamp_DoesNotAdvancePosition()
    {
        var stream = CreateStreamInfo(startTimestamp: 90_000);
        var frame = CreateFrameInfo(presentationTimestamp: long.MinValue);

        Assert.Null(FfmpegDecoder.GetRelativePosition(frame, stream));
    }

    [Fact]
    public void MissingPresentationTimestamp_AdvancesUsingFrameRateFallback()
    {
        var stream = CreateStreamInfo(
            startTimestamp: 0,
            frameRateNumerator: 25,
            frameRateDenominator: 1);
        var frame = CreateFrameInfo(presentationTimestamp: long.MinValue);

        var position = FfmpegDecoder.ResolvePlaybackPosition(
            frame,
            stream,
            TimeSpan.FromSeconds(2),
            hasPlaybackPosition: true,
            FfmpegDecoder.GetFallbackFrameDuration(stream));

        Assert.Equal(TimeSpan.FromMilliseconds(2040), position);
    }

    [Fact]
    public void RepeatedPresentationTimestamp_AdvancesUsingFrameRateFallback()
    {
        var stream = CreateStreamInfo(
            startTimestamp: 0,
            frameRateNumerator: 50,
            frameRateDenominator: 1);
        var frame = CreateFrameInfo(presentationTimestamp: 1_000);

        var position = FfmpegDecoder.ResolvePlaybackPosition(
            frame,
            stream,
            TimeSpan.FromSeconds(1),
            hasPlaybackPosition: true,
            FfmpegDecoder.GetFallbackFrameDuration(stream));

        Assert.Equal(TimeSpan.FromMilliseconds(1020), position);
    }

    private static NativeStreamInfo CreateStreamInfo(
        long startTimestamp,
        int frameRateNumerator = 0,
        int frameRateDenominator = 0) => new()
    {
        TimeBaseNumerator = 1,
        TimeBaseDenominator = 1_000,
        StartTimestamp = startTimestamp,
        FrameRateNumerator = frameRateNumerator,
        FrameRateDenominator = frameRateDenominator
    };

    private static NativeFrameInfo CreateFrameInfo(long presentationTimestamp) => new()
    {
        PresentationTimestamp = presentationTimestamp,
        TimeBaseNumerator = 1,
        TimeBaseDenominator = 1_000
    };
}
