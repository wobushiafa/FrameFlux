using Android.Graphics;
using Android.Views;
using Android.Widget;
using Avalonia;
using Avalonia.Android;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using FrameFlux.FFmpeg.Android;
using FrameFlux.Presentation;

namespace FrameFlux.Avalonia;

internal sealed class AndroidNativeSurfaceMediaOutput :
    NativeControlHost,
    IAvaloniaPlatformMediaOutput,
    IAndroidVideoSurfaceOutput,
    IMediaVideoOutputFeatureProvider
{
    private readonly object _surfaceSync = new();
    private readonly AndroidNativeSurfaceLifecycle _lifecycle = new();
    private readonly MediaPresentationFailureTracker _failureTracker = new(maximumAttempts: 1);
    private readonly SurfaceCallback _surfaceCallback;
    private FrameLayout? _nativeHost;
    private SurfaceView? _surfaceView;
    private global::Android.Views.Surface? _decoderSurface;
    private Stretch _stretch = Stretch.Uniform;
    private int _sourceWidth;
    private int _sourceHeight;

    internal AndroidNativeSurfaceMediaOutput()
    {
        _surfaceCallback = new SurfaceCallback(this);
        ClipToBounds = true;
        IsHitTestVisible = false;
    }

    public Control Surface => this;

    public MediaFrameStorageKind PreferredFrameStorage => MediaFrameStorageKind.CpuMemory;

    public Stretch Stretch
    {
        get => _stretch;
        set
        {
            _stretch = value;
            ScheduleLayoutUpdate();
        }
    }

    public event EventHandler? FramePresented;

    public event Action<object?, MediaPresentationFailure>? PresentationFailed;

    public bool Supports(MediaFrameStorageKind storageKind, MediaPixelFormat pixelFormat) => false;

    public bool TryPresent(IMediaFrameLease frame) => false;

    public object? GetVideoOutputFeature(Type featureType)
    {
        ArgumentNullException.ThrowIfNull(featureType);
        return featureType.IsInstanceOfType(this) ? this : null;
    }

    public global::Android.Views.Surface AcquireDecoderSurface(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_lifecycle.IsDisposed, this);
        lock (_surfaceSync)
        {
            if (_decoderSurface?.IsValid == true)
            {
                _lifecycle.MarkAcquired();
                return _decoderSurface;
            }

            _lifecycle.PrepareAcquire();
        }

        _lifecycle.WaitForSurface(cancellationToken);
        lock (_surfaceSync)
        {
            ObjectDisposedException.ThrowIf(_lifecycle.IsDisposed, this);
            if (_lifecycle.Failure is { } failure)
            {
                throw new PlatformNotSupportedException(
                    "The Avalonia Android native Surface is unavailable.",
                    failure);
            }

            if (_decoderSurface?.IsValid != true)
            {
                throw new PlatformNotSupportedException(
                    "The Avalonia Android native Surface was not created.");
            }

            _lifecycle.MarkAcquired();
            return _decoderSurface;
        }
    }

    public void SetDecodedVideoSize(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        lock (_surfaceSync)
        {
            _sourceWidth = width;
            _sourceHeight = height;
        }
        ScheduleLayoutUpdate();
    }

    public void Clear() => _failureTracker.Reset();

    public ValueTask ReleaseResourcesAsync()
    {
        lock (_surfaceSync)
        {
            _lifecycle.MarkReleased();
        }
        _failureTracker.Reset();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        lock (_surfaceSync)
        {
            if (!_lifecycle.MarkDisposed(
                    new ObjectDisposedException(GetType().FullName)))
            {
                return ValueTask.CompletedTask;
            }
        }
        DetachSurfaceCallback();
        _surfaceCallback.Dispose();
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        FrameLayout? host = null;
        SurfaceView? surfaceView = null;
        try
        {
            if (parent is not AndroidViewControlHandle androidParent)
            {
                throw new PlatformNotSupportedException(
                    "Android NativeSurface requires Avalonia's Android native control host.");
            }

            var context = androidParent.View.Context ??
                throw new PlatformNotSupportedException(
                    "Avalonia did not provide an Android Context for NativeSurface.");
            host = new FrameLayout(context);
            host.SetClipChildren(true);
            host.SetClipToPadding(true);
            host.SetBackgroundColor(global::Android.Graphics.Color.Black);
            surfaceView = new SurfaceView(context);
            surfaceView.SetBackgroundColor(global::Android.Graphics.Color.Black);
            // Avalonia renders the application into its own SurfaceView. A media-overlay
            // Surface can still remain behind the Activity window and be covered by its
            // opaque buffer, so the decoder Surface must live above the window.
            surfaceView.SetZOrderOnTop(true);
            var holder = surfaceView.Holder ??
                throw new PlatformNotSupportedException(
                    "Android did not provide a SurfaceHolder for NativeSurface.");
            holder.SetFormat(Format.Opaque);
            holder.AddCallback(_surfaceCallback);
            host.AddView(
                surfaceView,
                new FrameLayout.LayoutParams(
                    ViewGroup.LayoutParams.MatchParent,
                    ViewGroup.LayoutParams.MatchParent,
                    GravityFlags.Center));

            lock (_surfaceSync)
            {
                ObjectDisposedException.ThrowIf(_lifecycle.IsDisposed, this);
                _nativeHost = host;
                _surfaceView = surfaceView;
                _lifecycle.MarkHostCreated();
            }
            UpdateNativeLayout();
            return new AndroidViewControlHandle(host);
        }
        catch (Exception exception)
        {
            surfaceView?.Holder?.RemoveCallback(_surfaceCallback);
            host?.Dispose();
            SignalSurfaceFailure(exception);
            throw;
        }
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        var exception = new InvalidOperationException(
            "The Android NativeSurface host was destroyed while MediaCodec was active.");
        var reportFailure = false;
        lock (_surfaceSync)
        {
            reportFailure = _lifecycle.MarkHostDestroyed(exception);
        }
        DetachSurfaceCallback();
        base.DestroyNativeControlCore(control);
        if (reportFailure)
        {
            ReportFailure(exception);
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var arranged = base.ArrangeOverride(finalSize);
        ScheduleLayoutUpdate();
        return arranged;
    }

    private void OnSurfaceCreated(ISurfaceHolder holder)
    {
        var surface = holder.Surface;
        Exception? failure = null;
        lock (_surfaceSync)
        {
            if (_lifecycle.IsDisposed)
            {
                return;
            }

            if (surface?.IsValid != true)
            {
                failure = new PlatformNotSupportedException(
                    "Android created an invalid NativeSurface.");
                _decoderSurface = null;
                _lifecycle.MarkSurfaceFailure(failure);
            }
            else
            {
                _decoderSurface = surface;
                _lifecycle.MarkSurfaceReady();
            }
        }
        if (failure is null)
        {
            _failureTracker.ReportSuccess();
            ScheduleLayoutUpdate();
        }
        else
        {
            ReportFailure(failure);
        }
    }

    private void OnSurfaceChanged(ISurfaceHolder holder)
    {
        OnSurfaceCreated(holder);
        FramePresented?.Invoke(this, EventArgs.Empty);
    }

    private void OnSurfaceDestroyed()
    {
        var reportFailure = false;
        lock (_surfaceSync)
        {
            _decoderSurface = null;
            reportFailure = _lifecycle.MarkSurfaceDestroyed();
        }

        if (reportFailure)
        {
            ReportFailure(new InvalidOperationException(
                "The Android NativeSurface was destroyed while MediaCodec was active."));
        }
    }

    private void ScheduleLayoutUpdate()
    {
        if (_lifecycle.IsDisposed)
        {
            return;
        }

        try
        {
            Dispatcher.UIThread.Post(UpdateNativeLayout, DispatcherPriority.Render);
        }
        catch (Exception exception)
        {
            ReportFailure(exception);
        }
    }

    private void UpdateNativeLayout()
    {
        FrameLayout? host;
        SurfaceView? surfaceView;
        int sourceWidth;
        int sourceHeight;
        lock (_surfaceSync)
        {
            host = _nativeHost;
            surfaceView = _surfaceView;
            sourceWidth = _sourceWidth;
            sourceHeight = _sourceHeight;
        }

        if (host is null || surfaceView is null)
        {
            return;
        }

        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1d;
        var targetWidth = Math.Max(1, (int)Math.Ceiling(Bounds.Width * scaling));
        var targetHeight = Math.Max(1, (int)Math.Ceiling(Bounds.Height * scaling));
        var layout = AndroidNativeSurfaceLayoutCalculator.Calculate(
            sourceWidth,
            sourceHeight,
            targetWidth,
            targetHeight,
            _stretch);
        var parameters = surfaceView.LayoutParameters as FrameLayout.LayoutParams ??
            new FrameLayout.LayoutParams(layout.Width, layout.Height, GravityFlags.Center);
        parameters.Width = layout.Width;
        parameters.Height = layout.Height;
        parameters.Gravity = GravityFlags.Center;
        surfaceView.LayoutParameters = parameters;
        host.RequestLayout();
    }

    private void DetachSurfaceCallback()
    {
        SurfaceView? surfaceView;
        lock (_surfaceSync)
        {
            surfaceView = _surfaceView;
            _surfaceView = null;
            _nativeHost = null;
            _decoderSurface = null;
        }
        surfaceView?.Holder?.RemoveCallback(_surfaceCallback);
    }

    private void SignalSurfaceFailure(Exception exception)
    {
        lock (_surfaceSync)
        {
            _lifecycle.MarkSurfaceFailure(exception);
        }
        ReportFailure(exception);
    }

    private void ReportFailure(Exception exception)
    {
        System.Diagnostics.Trace.TraceError(
            "Avalonia Android NativeSurface presentation failed: {0}",
            exception);
        var failure = _failureTracker.Register(exception);
        try
        {
            Dispatcher.UIThread.Post(
                () => PresentationFailed?.Invoke(this, failure),
                DispatcherPriority.Render);
        }
        catch
        {
        }
    }

    private sealed class SurfaceCallback(AndroidNativeSurfaceMediaOutput owner) :
        Java.Lang.Object,
        ISurfaceHolderCallback
    {
        public void SurfaceCreated(ISurfaceHolder holder) => owner.OnSurfaceCreated(holder);

        public void SurfaceChanged(
            ISurfaceHolder holder,
            Format format,
            int width,
            int height) => owner.OnSurfaceChanged(holder);

        public void SurfaceDestroyed(ISurfaceHolder holder) => owner.OnSurfaceDestroyed();
    }
}
