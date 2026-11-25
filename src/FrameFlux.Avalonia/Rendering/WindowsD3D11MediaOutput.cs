#if !ANDROID
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using FrameFlux.Presentation;
using FrameFlux.Rendering.Windows;

namespace FrameFlux.Avalonia;

internal sealed class WindowsD3D11MediaOutput :
    NativeControlHost,
    IAvaloniaPlatformMediaOutput,
    IDisposable
{
    private readonly LatestMediaFrameSlot _frameSlot = new();
    private readonly WindowsD3D11Presenter _presenter = new();
    private readonly MediaPresentationFailureTracker _failureTracker = new();
    private readonly Action _presentPendingFrame;
    private TopLevel? _topLevel;
    private double _renderScaling = 1d;
    private Stretch _stretch = Stretch.Uniform;
    private bool _presentationNotified;
    private bool _disposed;

    internal WindowsD3D11MediaOutput()
    {
        _presentPendingFrame = PresentPendingFrame;
    }

    public MediaFrameStorageKind PreferredFrameStorage => MediaFrameStorageKind.D3D11Texture;

    public Control Surface => this;

    public Stretch Stretch
    {
        get => _stretch;
        set => _stretch = value;
    }

    public bool Supports(MediaFrameStorageKind storageKind, MediaPixelFormat pixelFormat) =>
        storageKind == MediaFrameStorageKind.D3D11Texture;

    public event EventHandler? FramePresented;

    public event Action<object?, MediaPresentationFailure>? PresentationFailed;

    public bool TryPresent(IMediaFrameLease frame)
    {
        if (_disposed || _failureTracker.IsExhausted ||
            !frame.TryGetD3D11Texture(out _))
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
        var window = _presenter.CreateWindow(parent.Handle);
        return new PlatformHandle(window, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        _presentationNotified = false;
        _presenter.DestroyWindow();
        base.DestroyNativeControlCore(control);
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
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    public void Clear()
    {
        _frameSlot.ReleasePendingFrame();
        _failureTracker.Reset();
        _presentationNotified = false;
        _presenter.Reset();
    }

    public ValueTask ReleaseResourcesAsync()
    {
        Clear();
        return ValueTask.CompletedTask;
    }

    private void SchedulePresentation()
    {
        try
        {
            global::Avalonia.Threading.Dispatcher.UIThread.Post(
                _presentPendingFrame,
                global::Avalonia.Threading.DispatcherPriority.Render);
        }
        catch
        {
            Clear();
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
            if (_disposed || !frame.TryGetD3D11Texture(out var texture))
            {
                return;
            }

            var scaling = _renderScaling;
            _presenter.Present(
                frame.Width,
                frame.Height,
                texture,
                (int)Math.Ceiling(Bounds.Width * scaling),
                (int)Math.Ceiling(Bounds.Height * scaling),
                ToMediaStretchMode(_stretch));
            _failureTracker.ReportSuccess();
            if (!_presentationNotified)
            {
                _presentationNotified = true;
                FramePresented?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError(
                "Avalonia D3D11 presentation failed: {0}",
                exception);
            _presenter.Reset();
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

    private static MediaStretchMode ToMediaStretchMode(Stretch stretch) => stretch switch
    {
        Stretch.None => MediaStretchMode.None,
        Stretch.Fill => MediaStretchMode.Fill,
        Stretch.UniformToFill => MediaStretchMode.UniformToFill,
        _ => MediaStretchMode.Uniform
    };
}
#endif
