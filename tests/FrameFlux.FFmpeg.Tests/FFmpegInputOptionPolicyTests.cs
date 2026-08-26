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
    public void DisabledLowLatencyDoesNotAddOptions()
    {
        Assert.Empty(FFmpegInputOptionPolicy.GetLowLatencyOptions(enabled: false));
    }
}
