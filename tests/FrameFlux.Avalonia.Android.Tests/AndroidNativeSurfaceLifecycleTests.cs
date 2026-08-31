using FrameFlux.Avalonia;
using Xunit;

namespace FrameFlux.Avalonia.Android.Tests;

public sealed class AndroidNativeSurfaceLifecycleTests
{
    [Fact]
    public async Task SurfaceReady_UnblocksWaitingAcquire()
    {
        var lifecycle = new AndroidNativeSurfaceLifecycle();
        lifecycle.PrepareAcquire();
        var waiting = Task.Run(
            () => lifecycle.WaitForSurface(CancellationToken.None));

        lifecycle.MarkSurfaceReady();

        await waiting.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Null(lifecycle.Failure);
    }

    [Fact]
    public void UnexpectedSurfaceDestruction_AfterAcquireRequestsFallback()
    {
        var lifecycle = new AndroidNativeSurfaceLifecycle();
        lifecycle.MarkSurfaceReady();
        lifecycle.MarkAcquired();

        Assert.True(lifecycle.MarkSurfaceDestroyed());
    }

    [Fact]
    public void ReleasedSurfaceDestruction_DoesNotRequestFallback()
    {
        var lifecycle = new AndroidNativeSurfaceLifecycle();
        lifecycle.MarkSurfaceReady();
        lifecycle.MarkAcquired();
        lifecycle.MarkReleased();

        Assert.False(lifecycle.MarkSurfaceDestroyed());
    }

    [Fact]
    public async Task Dispose_IsIdempotentAndUnblocksWaitingAcquire()
    {
        var lifecycle = new AndroidNativeSurfaceLifecycle();
        lifecycle.PrepareAcquire();
        var waiting = Task.Run(
            () => lifecycle.WaitForSurface(CancellationToken.None));
        var failure = new ObjectDisposedException(nameof(lifecycle));

        Assert.True(lifecycle.MarkDisposed(failure));
        Assert.False(lifecycle.MarkDisposed(failure));

        await waiting.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(lifecycle.IsDisposed);
        Assert.Same(failure, lifecycle.Failure);
    }

    [Fact]
    public void HostDestruction_PreservesFailureForWaitingAcquire()
    {
        var lifecycle = new AndroidNativeSurfaceLifecycle();
        lifecycle.MarkAcquired();
        var failure = new InvalidOperationException("host destroyed");

        Assert.True(lifecycle.MarkHostDestroyed(failure));
        Assert.Same(failure, lifecycle.Failure);
    }
}
