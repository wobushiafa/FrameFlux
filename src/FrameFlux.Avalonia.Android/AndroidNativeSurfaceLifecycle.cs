namespace FrameFlux.Avalonia;

internal sealed class AndroidNativeSurfaceLifecycle
{
    private readonly ManualResetEventSlim _surfaceReady = new(false);
    private bool _surfaceAcquired;
    private bool _releasing;
    private bool _disposed;

    internal bool IsDisposed => Volatile.Read(ref _disposed);

    internal Exception? Failure { get; private set; }

    internal void PrepareAcquire()
    {
        Failure = null;
        _surfaceReady.Reset();
    }

    internal void WaitForSurface(CancellationToken cancellationToken) =>
        _surfaceReady.Wait(cancellationToken);

    internal void MarkAcquired()
    {
        _surfaceAcquired = true;
        _releasing = false;
    }

    internal void MarkHostCreated() => _releasing = false;

    internal void MarkSurfaceReady()
    {
        Failure = null;
        _releasing = false;
        _surfaceReady.Set();
    }

    internal void MarkSurfaceFailure(Exception exception)
    {
        Failure = exception;
        _surfaceReady.Set();
    }

    internal void MarkReleased()
    {
        _releasing = true;
        _surfaceAcquired = false;
        Failure = null;
    }

    internal bool MarkHostDestroyed(Exception exception)
    {
        var reportFailure = ShouldReportUnexpectedLoss();
        _releasing = true;
        _surfaceAcquired = false;
        Failure = exception;
        _surfaceReady.Set();
        return reportFailure;
    }

    internal bool MarkSurfaceDestroyed()
    {
        var reportFailure = ShouldReportUnexpectedLoss();
        _surfaceAcquired = false;
        _surfaceReady.Reset();
        return reportFailure;
    }

    internal bool MarkDisposed(Exception exception)
    {
        if (_disposed)
        {
            return false;
        }

        Volatile.Write(ref _disposed, true);
        _releasing = true;
        _surfaceAcquired = false;
        Failure = exception;
        _surfaceReady.Set();
        return true;
    }

    private bool ShouldReportUnexpectedLoss() =>
        _surfaceAcquired && !_releasing && !_disposed;
}
