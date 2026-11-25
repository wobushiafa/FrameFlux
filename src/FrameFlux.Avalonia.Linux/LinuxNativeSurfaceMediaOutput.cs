using Avalonia;
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
    private readonly Action _presentPendingFrame;
    private TopLevel? _topLevel;
    private double _renderScaling = 1d;
    private Stretch _stretch = Stretch.Uniform;
    private bool _presentationNotified;
    private bool _disposed;

    internal LinuxNativeSurfaceMediaOutput()
    {
        _presentPendingFrame = PresentPendingFrame;
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
        storageKind == MediaFrameStorageKind.DmaBuf ||
        (storageKind == MediaFrameStorageKind.CpuMemory &&
         pixelFormat == MediaPixelFormat.Bgra32);

    public event EventHandler? FramePresented;

    public event Action<object?, MediaPresentationFailure>? PresentationFailed;

    public bool TryPresent(IMediaFrameLease frame)
    {
        if (_disposed || _failureTracker.IsExhausted ||
            !Supports(frame.StorageKind, frame.PixelFormat) ||
            (frame.StorageKind == MediaFrameStorageKind.DmaBuf && !frame.TryGetDmaBuf(out _)) ||
            (frame.StorageKind == MediaFrameStorageKind.CpuMemory && !frame.TryGetCpuBuffer(out _)))
        {
            return false;
        }

        if (!_frameSlot.TrySubmit(frame, out var schedulePresentation))
        {
            return false;
        }

        if (schedulePresentation)
        {
            SchedulePresentation();
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
        _presentationNotified = false;
        _presenter.DestroyWindow();
        base.DestroyNativeControlCore(control);
    }

    public void Clear()
    {
        _frameSlot.ReleasePendingFrame();
        _failureTracker.Reset();
        _presentationNotified = false;
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

    private void SchedulePresentation()
    {
        try
        {
            Dispatcher.UIThread.Post(_presentPendingFrame, DispatcherPriority.Render);
        }
        catch
        {
            _frameSlot.Clear();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        DetachTopLevel();
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel is not null)
        {
            _renderScaling = _topLevel.RenderScaling;
            _topLevel.ScalingChanged += OnTopLevelScalingChanged;
        }
        else
        {
            _renderScaling = 1d;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        DetachTopLevel();
        base.OnDetachedFromVisualTree(e);
    }

    private void DetachTopLevel()
    {
        if (_topLevel is not null)
        {
            _topLevel.ScalingChanged -= OnTopLevelScalingChanged;
            _topLevel = null;
        }
    }

    private void OnTopLevelScalingChanged(object? sender, EventArgs e)
    {
        if (_topLevel is not null)
        {
            _renderScaling = _topLevel.RenderScaling;
        }
    }

    private void PresentPendingFrame()
    {
        var frame = _frameSlot.TakeForPresentation();
        if (frame is null)
        {
            if (_frameSlot.CompletePresentation())
            {
                SchedulePresentation();
            }
            return;
        }

        try
        {
            if (_disposed)
            {
                return;
            }

            var scaling = _renderScaling;
            var targetWidth = Math.Max(1, (int)Math.Ceiling(Bounds.Width * scaling));
            var targetHeight = Math.Max(1, (int)Math.Ceiling(Bounds.Height * scaling));

            if (frame.StorageKind == MediaFrameStorageKind.DmaBuf)
            {
                if (!frame.TryGetDmaBuf(out var dmaBuf))
                {
                    return;
                }

                _presenter.Present(
                    dmaBuf,
                    frame.Width,
                    frame.Height,
                    targetWidth,
                    targetHeight,
                    _stretch);
            }
            else if (frame.StorageKind == MediaFrameStorageKind.CpuMemory &&
                     frame.PixelFormat == MediaPixelFormat.Bgra32)
            {
                if (!frame.TryGetCpuBuffer(out var cpuFrame))
                {
                    return;
                }

                _presenter.PresentCpuBgra(
                    cpuFrame,
                    frame.Width,
                    frame.Height,
                    targetWidth,
                    targetHeight,
                    _stretch);
            }
            else
            {
                return;
            }

            _failureTracker.ReportSuccess();
            if (!_presentationNotified)
            {
                _presentationNotified = true;
                FramePresented?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"[{DateTimeOffset.Now:O}] FrameFlux Linux NativeSurface presentation failed:\n{exception}");
            System.Diagnostics.Trace.TraceError(
                "FrameFlux Linux NativeSurface presentation failed: {0}", exception);
            _presentationNotified = false;
            PresentationFailed?.Invoke(this, _failureTracker.Register(exception));
        }
        finally
        {
            frame.Dispose();
            if (_frameSlot.CompletePresentation())
            {
                SchedulePresentation();
            }
        }
    }
}
