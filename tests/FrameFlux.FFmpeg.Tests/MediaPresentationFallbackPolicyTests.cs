using FrameFlux.Presentation;
using Xunit;

namespace FrameFlux.Tests;

public sealed class MediaPresentationFallbackPolicyTests
{
    [Fact]
    public void NativeSurfaceFailure_UsesGpuCompositionWhenAvailable()
    {
        var result = MediaPresentationFallbackPolicy.Resolve(
            failedNativeSurface: true,
            gpuCompositionAvailable: true);

        Assert.Equal(MediaVideoPresentationMode.GpuComposition, result);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void OtherFailures_UseSoftwareBitmap(
        bool failedNativeSurface,
        bool gpuCompositionAvailable)
    {
        var result = MediaPresentationFallbackPolicy.Resolve(
            failedNativeSurface,
            gpuCompositionAvailable);

        Assert.Equal(MediaVideoPresentationMode.SoftwareBitmap, result);
    }
}
