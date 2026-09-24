using Xunit;

namespace FrameFlux.FFmpeg.Tests;

public sealed class FFmpegInputOptionPolicyTests
{
    [Fact]
    public void LowLatencyUsesReferenceFrameSafeOptions()
    {
        var options = FFmpegInputOptionPolicy.GetLowLatencyOptions(enabled: true);

        Assert.Equal("low_delay", Assert.Single(options, option => option.Key == "flags").Value);
        Assert.Equal("500000", Assert.Single(options, option => option.Key == "max_delay").Value);
        Assert.DoesNotContain(options, option =>
            option.Key == "fflags" && option.Value.Contains("nobuffer", StringComparison.Ordinal));
    }

    [Fact]
    public void HlsDoesNotUseRtspLowLatencyOptions()
    {
        Assert.Empty(FFmpegInputOptionPolicy.GetLowLatencyOptions(
            enabled: true,
            isHls: true));
    }

    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, true, false)]
    [InlineData(false, false, false, false)]
    public void PacketPrefetch_IncludesHlsAndHttpMedia(
        bool isHls,
        bool isHttpMedia,
        bool isPacketReader,
        bool expected)
    {
        Assert.Equal(
            expected,
            FFmpegInputOptionPolicy.ShouldPrefetchPackets(isHls, isHttpMedia, isPacketReader));
    }

    [Fact]
    public void HttpMediaDoesNotUseRtspLowLatencyOptions()
    {
        Assert.Empty(FFmpegInputOptionPolicy.GetLowLatencyOptions(
            enabled: true,
            isHls: false,
            isHttpMedia: true));
    }

    [Fact]
    public void DisabledLowLatencyDoesNotAddOptions()
    {
        Assert.Empty(FFmpegInputOptionPolicy.GetLowLatencyOptions(enabled: false));
    }
}
