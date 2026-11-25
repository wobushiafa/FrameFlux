using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FrameFlux.Presentation;

namespace FrameFlux.Wpf;

internal sealed class SoftwareBitmapMediaOutput : FrameworkElement, IMediaVideoOutput, IDisposable
{
    private readonly LatestMediaFrameSlot _frameSlot = new();
    private readonly Action _renderPendingFrame;
    private WriteableBitmap? _bitmap;
    private Stretch _stretch = Stretch.Uniform;
    private bool _presentationNotified;
    private bool _disposed;

    internal SoftwareBitmapMediaOutput()
    {
        _renderPendingFrame = RenderPendingFrame;
    }

    public MediaFrameStorageKind PreferredFrameStorage => MediaFrameStorageKind.CpuMemory;

    public bool Supports(MediaFrameStorageKind storageKind, MediaPixelFormat pixelFormat) =>
        storageKind == MediaFrameStorageKind.CpuMemory &&
        pixelFormat == MediaPixelFormat.Bgra32;

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
            frame.StorageKind != MediaFrameStorageKind.CpuMemory ||
            frame.PixelFormat != MediaPixelFormat.Bgra32 ||
            !frame.TryGetCpuBuffer(out _))
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

    internal void Clear()
    {
        _frameSlot.ReleasePendingFrame();
        _presentationNotified = false;
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
        _frameSlot.Dispose();
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

    private void SchedulePresentation()
    {
        try
        {
            Dispatcher.BeginInvoke(
                DispatcherPriority.Render,
                _renderPendingFrame);
        }
        catch
        {
            ClearPendingFrame();
        }
    }

    private void RenderPendingFrame()
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
            if (!_disposed && frame.TryGetCpuBuffer(out var source))
            {
                RenderFrame(frame.Width, frame.Height, source);
                if (!_presentationNotified)
                {
                    _presentationNotified = true;
                    FramePresented?.Invoke(this, EventArgs.Empty);
                }
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
            if (_frameSlot.CompletePresentation())
            {
                SchedulePresentation();
            }
        }
    }

    private unsafe void RenderFrame(
        int width,
        int height,
        MediaCpuFrameBuffer frame)
    {
        var createdNewBitmap = false;
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
            createdNewBitmap = true;
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
            var dstStride = _bitmap.BackBufferStride;
            var dstPtr = (byte*)_bitmap.BackBuffer;
            var srcPtr = (byte*)frame.Plane0;
            if (frame.Plane0Stride == dstStride && rowBytes == dstStride)
            {
                var totalBytes = (long)dstStride * height;
                Buffer.MemoryCopy(srcPtr, dstPtr, totalBytes, totalBytes);
            }
            else
            {
                for (var row = 0; row < height; row++)
                {
                    Buffer.MemoryCopy(
                        srcPtr + (nint)row * frame.Plane0Stride,
                        dstPtr + (nint)row * dstStride,
                        rowBytes,
                        rowBytes);
                }
            }

            _bitmap.AddDirtyRect(new Int32Rect(0, 0, width, height));
        }
        finally
        {
            _bitmap.Unlock();
        }

        if (createdNewBitmap)
        {
            InvalidateVisual();
        }
    }

    private void ClearPendingFrame()
    {
        _frameSlot.Clear();
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
