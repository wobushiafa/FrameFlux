using Xunit;

namespace FrameFlux.FFmpeg.Tests;

public sealed class MediaTimelineTests
{
    [Fact]
    public void RelativePosition_SubtractsStreamStartTimestamp()
    {
        var stream = CreateStreamInfo(startTimestamp: 90_000);
        var frame = CreateFrameInfo(presentationTimestamp: 95_000);

        var position = RtspDecoder.GetRelativePosition(frame, stream);

        Assert.Equal(TimeSpan.FromSeconds(5), position);
    }

    [Fact]
    public void RelativePosition_ClampsFramesBeforeStreamStartToZero()
    {
        var stream = CreateStreamInfo(startTimestamp: 90_000);
        var frame = CreateFrameInfo(presentationTimestamp: 89_500);

        var position = RtspDecoder.GetRelativePosition(frame, stream);

        Assert.Equal(TimeSpan.Zero, position);
    }

    [Fact]
    public void SeekTimestamp_AddsStreamStartTimestamp()
    {
        var stream = CreateStreamInfo(startTimestamp: 90_000);

        var timestamp = RtspDecoder.GetSeekTimestamp(TimeSpan.FromSeconds(5), stream);

        Assert.Equal(95_000, timestamp);
    }

    [Fact]
    public void UnknownStreamStartTimestamp_FallsBackToZero()
    {
        var stream = CreateStreamInfo(startTimestamp: long.MinValue);
        var frame = CreateFrameInfo(presentationTimestamp: 5_000);

        Assert.Equal(
            TimeSpan.FromSeconds(5),
            RtspDecoder.GetRelativePosition(frame, stream));
        Assert.Equal(
            5_000,
            RtspDecoder.GetSeekTimestamp(TimeSpan.FromSeconds(5), stream));
    }

    [Fact]
    public void MissingPresentationTimestamp_DoesNotAdvancePosition()
    {
        var stream = CreateStreamInfo(startTimestamp: 90_000);
        var frame = CreateFrameInfo(presentationTimestamp: long.MinValue);

        Assert.Null(RtspDecoder.GetRelativePosition(frame, stream));
    }

    private static NativeStreamInfo CreateStreamInfo(long startTimestamp) => new()
    {
        TimeBaseNumerator = 1,
        TimeBaseDenominator = 1_000,
        StartTimestamp = startTimestamp
    };

    private static NativeFrameInfo CreateFrameInfo(long presentationTimestamp) => new()
    {
        PresentationTimestamp = presentationTimestamp,
        TimeBaseNumerator = 1,
        TimeBaseDenominator = 1_000
    };
}
