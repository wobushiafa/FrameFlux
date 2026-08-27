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

    [Fact]
    public void PresentationFailures_RequireFallbackAfterThreeConsecutiveAttempts()
    {
        var tracker = new MediaPresentationFailureTracker();

        Assert.False(tracker.Register(new InvalidOperationException("first")).RequiresSoftwareFallback);
        Assert.False(tracker.Register(new InvalidOperationException("second")).RequiresSoftwareFallback);
        var final = tracker.Register(new InvalidOperationException("third"));

        Assert.True(final.RequiresSoftwareFallback);
        Assert.True(tracker.IsExhausted);
        Assert.Equal(3, final.ConsecutiveFailureCount);
    }

    [Fact]
    public void SuccessfulPresentation_ResetsFailureBudget()
    {
        var tracker = new MediaPresentationFailureTracker();
        _ = tracker.Register(new InvalidOperationException("first"));
        _ = tracker.Register(new InvalidOperationException("second"));

        tracker.ReportSuccess();

        var next = tracker.Register(new InvalidOperationException("next"));
        Assert.Equal(1, next.ConsecutiveFailureCount);
        Assert.False(next.RequiresSoftwareFallback);
    }
}
