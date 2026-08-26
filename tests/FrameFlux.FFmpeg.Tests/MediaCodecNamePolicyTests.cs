using Xunit;

namespace FrameFlux.FFmpeg.Tests;

public sealed class MediaCodecNamePolicyTests
{
    [Theory]
    [InlineData("OMX.google.h264.decoder")]
    [InlineData("c2.android.avc.decoder")]
    [InlineData("c2.google.hevc.decoder")]
    [InlineData("OMX.ffmpeg.video.decoder")]
    [InlineData("vendor.video.sw.decoder")]
    public void IsKnownSoftwareCodec_RecognizesSoftwareImplementations(string codecName)
    {
        Assert.True(MediaCodecNamePolicy.IsKnownSoftwareCodec(codecName));
    }

    [Theory]
    [InlineData("c2.qti.avc.decoder")]
    [InlineData("OMX.qcom.video.decoder.avc")]
    [InlineData("OMX.Exynos.HEVC.Decoder")]
    [InlineData("c2.mtk.hevc.decoder")]
    public void IsKnownSoftwareCodec_DoesNotRejectVendorHardwareImplementations(string codecName)
    {
        Assert.False(MediaCodecNamePolicy.IsKnownSoftwareCodec(codecName));
    }
}
