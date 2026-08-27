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
    public void Automatic_ReconfiguresOverlayWhenEffectiveModeUsesNativeSurface()
    {
        Assert.True(MediaPresentationPolicy.RequiresOverlayReconfiguration(
            MediaVideoPresentationMode.Automatic,
            MediaVideoPresentationMode.NativeSurface));
    }

    [Fact]
    public void Automatic_DoesNotReconfigureOverlayForGpuComposition()
    {
        Assert.False(MediaPresentationPolicy.RequiresOverlayReconfiguration(
            MediaVideoPresentationMode.Automatic,
            MediaVideoPresentationMode.GpuComposition));
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

    [Fact]
    public void ExhaustedFailureBudget_RequestsFallbackOnlyOnce()
    {
        var tracker = new MediaPresentationFailureTracker(maximumAttempts: 3);

        var failures = Enumerable.Range(0, 5)
            .Select(index => tracker.Register(
                new InvalidOperationException(index.ToString())))
            .ToArray();

        Assert.Single(failures, failure => failure.RequiresSoftwareFallback);
        Assert.False(failures[^1].RequiresSoftwareFallback);
        Assert.True(tracker.IsExhausted);
    }

    [Fact]
    public async Task ConcurrentFailures_AreCountedAtomically()
    {
        const int failureCount = 64;
        var tracker = new MediaPresentationFailureTracker(failureCount);

        var failures = await Task.WhenAll(
            Enumerable.Range(0, failureCount)
                .Select(index => Task.Run(() =>
                    tracker.Register(new InvalidOperationException(index.ToString())))));

        Assert.Equal(
            Enumerable.Range(1, failureCount),
            failures.Select(failure => failure.ConsecutiveFailureCount).Order());
        Assert.Single(failures, failure => failure.RequiresSoftwareFallback);
        Assert.True(tracker.IsExhausted);
    }
}
