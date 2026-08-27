using FrameFlux;
using FrameFlux.Presentation;
using Xunit;

namespace FrameFlux.FFmpeg.Tests;

public sealed class MediaPresentationPolicyTests
{
    [Fact]
    public void Automatic_UsesGpuCompositionWhenEligible()
    {
        var plan = MediaPresentationPolicy.Resolve(
            MediaVideoPresentationMode.Automatic,
            new MediaOpenOptions
            {
                SessionSharing = MediaSessionSharingMode.Dedicated,
                Video = new MediaVideoOptions
                {
                    DecodingPolicy = MediaVideoDecodingPolicy.HardwarePreferred
                }
            },
            platformGpuPresentationAvailable: true,
            hasOverlay: true);

        Assert.Equal(MediaVideoPresentationMode.GpuComposition, plan.EffectiveMode);
        Assert.True(plan.UsesGpuComposition);
        Assert.False(plan.UsesNativeSurface);
    }

    [Fact]
    public void Automatic_UsesSoftwareForSharedSession()
    {
        var plan = MediaPresentationPolicy.Resolve(
            MediaVideoPresentationMode.Automatic,
            new MediaOpenOptions
            {
                SessionSharing = MediaSessionSharingMode.Shared
            },
            platformGpuPresentationAvailable: true,
            hasOverlay: false);

        Assert.Equal(MediaVideoPresentationMode.SoftwareBitmap, plan.EffectiveMode);
    }

    [Fact]
    public void ExplicitGpuMode_RejectsUnavailableGpuPath()
    {
        Assert.Throws<InvalidOperationException>(() =>
            MediaPresentationPolicy.Resolve(
                MediaVideoPresentationMode.GpuComposition,
                new MediaOpenOptions(),
                platformGpuPresentationAvailable: false,
                hasOverlay: false));
    }

    [Fact]
    public void NativeSurface_RejectsOverlay()
    {
        Assert.Throws<InvalidOperationException>(() =>
            MediaPresentationPolicy.Resolve(
                MediaVideoPresentationMode.NativeSurface,
                new MediaOpenOptions(),
                platformGpuPresentationAvailable: true,
                hasOverlay: true));
    }
}
