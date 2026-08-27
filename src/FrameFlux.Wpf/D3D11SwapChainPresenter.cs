using System.Runtime.InteropServices;
using System.Windows.Interop;
using FrameFlux.Presentation;
using FrameFlux.Rendering.Windows;

namespace FrameFlux.Wpf;

internal sealed class D3D11SwapChainPresenter : HwndHost, IMediaVideoOutput
{
    private readonly LatestMediaFrameSlot _frameSlot = new();
    private readonly WindowsD3D11Presenter _presenter = new();
    private readonly MediaPresentationFailureTracker _failureTracker = new();
    private int _targetWidth = 1;
    private int _targetHeight = 1;
    private int _stretchMode = (int)MediaStretchMode.Uniform;
    private bool _disposed;

    internal void SetStretch(System.Windows.Media.Stretch stretch) =>
        Volatile.Write(ref _stretchMode, (int)ToMediaStretchMode(stretch));

    public MediaFrameStorageKind PreferredFrameStorage => MediaFrameStorageKind.D3D11Texture;

    public bool Supports(MediaFrameStorageKind storageKind, MediaPixelFormat pixelFormat) =>
        storageKind == MediaFrameStorageKind.D3D11Texture;

    internal event Action<object?, MediaPresentationFailure>? PresentationFailed;

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
            try
            {
                Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Render,
                    new Action(PresentPendingFrame));
            }
            catch
            {
                Clear();
            }
        }

        return true;
    }

    internal void Clear()
    {
        _frameSlot.Clear();
        _failureTracker.Reset();
        _presenter.Reset();
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent) =>
        new(this, _presenter.CreateWindow(hwndParent.Handle));

    protected override void OnRenderSizeChanged(System.Windows.SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        Volatile.Write(
            ref _targetWidth,
            Math.Max(1, (int)Math.Ceiling(sizeInfo.NewSize.Width * dpi.DpiScaleX)));
        Volatile.Write(
            ref _targetHeight,
            Math.Max(1, (int)Math.Ceiling(sizeInfo.NewSize.Height * dpi.DpiScaleY)));
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        _frameSlot.Clear();
        _presenter.DestroyWindow();
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            _frameSlot.Dispose();
            if (disposing)
            {
                _presenter.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    private void PresentPendingFrame()
    {
        var lease = _frameSlot.Take();

        if (lease is null)
        {
            return;
        }

        try
        {
            if (!_disposed && lease.TryGetD3D11Texture(out var frame))
            {
                _presenter.Present(
                    lease.Width,
                    lease.Height,
                    frame,
                    Volatile.Read(ref _targetWidth),
                    Volatile.Read(ref _targetHeight),
                    (MediaStretchMode)Volatile.Read(ref _stretchMode));
                _failureTracker.ReportSuccess();
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError(
                "WPF D3D11 presentation failed: {0}",
                exception);
            _presenter.Reset();
            PresentationFailed?.Invoke(this, _failureTracker.Register(exception));
        }
        finally
        {
            lease.Dispose();
        }
    }

    private static MediaStretchMode ToMediaStretchMode(
        System.Windows.Media.Stretch stretch) => stretch switch
    {
        System.Windows.Media.Stretch.None => MediaStretchMode.None,
        System.Windows.Media.Stretch.Fill => MediaStretchMode.Fill,
        System.Windows.Media.Stretch.UniformToFill => MediaStretchMode.UniformToFill,
        _ => MediaStretchMode.Uniform
    };
}
