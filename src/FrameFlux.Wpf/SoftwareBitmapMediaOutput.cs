using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FrameFlux.Wpf;

internal sealed class SoftwareBitmapMediaOutput : FrameworkElement, IMediaVideoOutput, IDisposable
{
    private readonly object _frameSync = new();
    private IMediaFrameLease? _pendingFrame;
    private WriteableBitmap? _bitmap;
    private Stretch _stretch = Stretch.Uniform;
    private bool _renderScheduled;
    private bool _disposed;

    public MediaRenderPreference Preference => MediaRenderPreference.Software;

    public bool Supports(MediaFramePixelFormat pixelFormat) =>
        pixelFormat == MediaFramePixelFormat.Bgra32;

    internal Stretch Stretch
    {
        get => _stretch;
        set
        {
            _stretch = value;
            InvalidateVisual();
        }
    }

    internal event EventHandler? FramePresented;

    public bool TryPresent(IMediaFrameLease frame)
    {
        if (_disposed ||
            frame.PixelFormat != MediaFramePixelFormat.Bgra32 ||
            !frame.TryGetCpuBuffer(out _))
        {
            return false;
        }

        IMediaFrameLease? droppedFrame;
        var schedule = false;
        lock (_frameSync)
        {
            if (_disposed)
            {
                return false;
            }

            droppedFrame = _pendingFrame;
            _pendingFrame = frame;
            if (!_renderScheduled)
            {
                _renderScheduled = true;
                schedule = true;
            }
        }

        droppedFrame?.Dispose();
        if (schedule)
        {
            try
            {
                Dispatcher.BeginInvoke(
                    DispatcherPriority.Render,
                    new Action(RenderPendingFrame));
            }
            catch
            {
                ClearPendingFrame();
            }
        }

        return true;
    }

    internal void Clear()
    {
        ClearPendingFrame();
        _bitmap = null;
        InvalidateVisual();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Clear();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (_bitmap is null)
        {
            return;
        }

        drawingContext.DrawImage(
            _bitmap,
            CalculateDestinationRect(
                _bitmap.PixelWidth,
                _bitmap.PixelHeight,
                RenderSize,
                _stretch));
    }

    private void RenderPendingFrame()
    {
        IMediaFrameLease? frame;
        lock (_frameSync)
        {
            frame = _pendingFrame;
            _pendingFrame = null;
            _renderScheduled = false;
        }

        if (frame is null)
        {
            return;
        }

        try
        {
            if (!_disposed && frame.TryGetCpuBuffer(out var source))
            {
                RenderFrame(frame.Width, frame.Height, source);
                FramePresented?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError(
                "WPF software presentation failed: {0}",
                exception);
        }
        finally
        {
            frame.Dispose();
        }
    }

    private unsafe void RenderFrame(
        int width,
        int height,
        MediaCpuFrameBuffer frame)
    {
        if (_bitmap is null ||
            _bitmap.PixelWidth != width ||
            _bitmap.PixelHeight != height)
        {
            _bitmap = new WriteableBitmap(
                width,
                height,
                96,
                96,
                PixelFormats.Bgra32,
                null);
        }

        if (frame.Plane0 == IntPtr.Zero || frame.Plane0Stride <= 0)
        {
            return;
        }

        var rowBytes = Math.Min(checked(width * 4), frame.Plane0Stride);
        var requiredSourceBytes =
            checked((long)frame.Plane0Stride * (height - 1) + rowBytes);
        if (frame.Size < requiredSourceBytes)
        {
            return;
        }

        _bitmap.Lock();
        try
        {
            for (var row = 0; row < height; row++)
            {
                new ReadOnlySpan<byte>(
                    (byte*)frame.Plane0 + row * frame.Plane0Stride,
                    rowBytes).CopyTo(
                    new Span<byte>(
                        (byte*)_bitmap.BackBuffer + row * _bitmap.BackBufferStride,
                        rowBytes));
            }
            _bitmap.AddDirtyRect(new Int32Rect(0, 0, width, height));
        }
        finally
        {
            _bitmap.Unlock();
        }

        InvalidateVisual();
    }

    private void ClearPendingFrame()
    {
        IMediaFrameLease? frame;
        lock (_frameSync)
        {
            frame = _pendingFrame;
            _pendingFrame = null;
            _renderScheduled = false;
        }

        frame?.Dispose();
    }

    private static Rect CalculateDestinationRect(
        double sourceWidth,
        double sourceHeight,
        Size target,
        Stretch stretch)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || target.Width <= 0 || target.Height <= 0)
        {
            return Rect.Empty;
        }

        if (stretch == Stretch.Fill)
        {
            return new Rect(target);
        }

        if (stretch == Stretch.None)
        {
            return new Rect(
                (target.Width - sourceWidth) / 2,
                (target.Height - sourceHeight) / 2,
                sourceWidth,
                sourceHeight);
        }

        var scaleX = target.Width / sourceWidth;
        var scaleY = target.Height / sourceHeight;
        var scale = stretch == Stretch.UniformToFill
            ? Math.Max(scaleX, scaleY)
            : Math.Min(scaleX, scaleY);
        var width = sourceWidth * scale;
        var height = sourceHeight * scale;
        return new Rect(
            (target.Width - width) / 2,
            (target.Height - height) / 2,
            width,
            height);
    }
}
