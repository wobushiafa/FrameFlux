using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using FrameFlux.Presentation;

namespace FrameFlux.Avalonia;

internal sealed class LinuxNativeSurfaceMediaOutput :
    NativeControlHost,
    IAvaloniaPlatformMediaOutput,
    IDisposable
{
    private readonly LatestMediaFrameSlot _frameSlot = new();
    private readonly LinuxX11EglPresenter _presenter = new();
    private readonly MediaPresentationFailureTracker _failureTracker = new();
    private Stretch _stretch = Stretch.Uniform;
    private bool _disposed;

    internal LinuxNativeSurfaceMediaOutput()
    {
        ClipToBounds = true;
        IsHitTestVisible = false;
    }

    public MediaFrameStorageKind PreferredFrameStorage => MediaFrameStorageKind.DmaBuf;

    public Control Surface => this;

    public Stretch Stretch
    {
        get => _stretch;
        set => _stretch = value;
    }

    public bool Supports(MediaFrameStorageKind storageKind, MediaPixelFormat pixelFormat) =>
        storageKind == MediaFrameStorageKind.DmaBuf;

    public event EventHandler? FramePresented;

    public event Action<object?, MediaPresentationFailure>? PresentationFailed;

    public bool TryPresent(IMediaFrameLease frame)
    {
        if (_disposed || _failureTracker.IsExhausted ||
            frame.StorageKind != MediaFrameStorageKind.DmaBuf ||
            !frame.TryGetDmaBuf(out _))
        {
            return false;
        }

        if (!_frameSlot.TrySubmit(frame, out var schedulePresentation))
        {
            return false;
        }

        if (schedulePresentation)
        {
            try
            {
                Dispatcher.UIThread.Post(PresentPendingFrame, DispatcherPriority.Render);
            }
            catch
            {
                _frameSlot.Clear();
            }
        }
        return true;
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (!string.Equals(parent.HandleDescriptor, "XID", StringComparison.Ordinal))
        {
            throw new PlatformNotSupportedException(
                "Linux NativeSurface requires Avalonia's X11 or XWayland backend.");
        }

        return new PlatformHandle(_presenter.CreateWindow(parent.Handle), "XID");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        _presenter.DestroyWindow();
        base.DestroyNativeControlCore(control);
    }

    public void Clear()
    {
        _frameSlot.Clear();
        _failureTracker.Reset();
        _presenter.Clear();
    }

    public ValueTask ReleaseResourcesAsync()
    {
        Clear();
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _frameSlot.Dispose();
        _presenter.Dispose();
        GC.SuppressFinalize(this);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private void PresentPendingFrame()
    {
        var frame = _frameSlot.Take();
        if (frame is null)
        {
            return;
        }

        try
        {
            if (_disposed || !frame.TryGetDmaBuf(out var dmaBuf))
            {
                return;
            }

            var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1d;
            _presenter.Present(
                dmaBuf,
                frame.Width,
                frame.Height,
                Math.Max(1, (int)Math.Ceiling(Bounds.Width * scaling)),
                Math.Max(1, (int)Math.Ceiling(Bounds.Height * scaling)),
                _stretch);
            _failureTracker.ReportSuccess();
            FramePresented?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"[{DateTimeOffset.Now:O}] FrameFlux Linux NativeSurface presentation failed:\n{exception}");
            System.Diagnostics.Trace.TraceError(
                "FrameFlux Linux NativeSurface presentation failed: {0}", exception);
            PresentationFailed?.Invoke(this, _failureTracker.Register(exception));
        }
        finally
        {
            frame.Dispose();
        }
    }
}
