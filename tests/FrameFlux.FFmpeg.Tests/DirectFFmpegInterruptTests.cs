using Xunit;

namespace FrameFlux.FFmpeg.Tests;

public sealed class DirectFFmpegInterruptTests
{
    [Theory]
    [InlineData(192, 196, 8, 200)]
    [InlineData(100, 104, 4, 108)]
    public void InterruptCallbackOffset_FollowsValidatedAdjacentOptions(
        int fpsProbeSizeOffset,
        int errorRecognitionOffset,
        int pointerSize,
        int expected)
    {
        Assert.Equal(
            expected,
            DirectRtspSession.CalculateInterruptCallbackOffset(
                fpsProbeSizeOffset,
                errorRecognitionOffset,
                pointerSize));
    }

    [Fact]
    public void InterruptCallbackOffset_RejectsUnexpectedFfmpegLayout()
    {
        Assert.Throws<InvalidOperationException>(() =>
            DirectRtspSession.CalculateInterruptCallbackOffset(192, 204, 8));
    }

    [Fact]
    public void InterruptCallbackOffset_RejectsUnsupportedPointerSize()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DirectRtspSession.CalculateInterruptCallbackOffset(192, 196, 16));
    }
}
