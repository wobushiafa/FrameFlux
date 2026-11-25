using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using FrameFlux.Presentation;

namespace FrameFlux.Avalonia;

internal sealed class SoftwareBitmapMediaOutput : Image, IMediaVideoOutput, IDisposable
{
    private readonly LatestMediaFrameSlot _frameSlot = new();
    private readonly Action _renderLatestFrame;
    private WriteableBitmap? _bitmap;
    private bool _presentationNotified;
    private bool _disposed;

    internal SoftwareBitmapMediaOutput()
    {
        _renderLatestFrame = RenderLatestFrame;
    }

    public MediaFrameStorageKind PreferredFrameStorage => MediaFrameStorageKind.CpuMemory;

    public bool Supports(MediaFrameStorageKind storageKind, MediaPixelFormat pixelFormat) =>
        storageKind == MediaFrameStorageKind.CpuMemory &&
        pixelFormat == MediaPixelFormat.Bgra32;

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
        var bitmap = _bitmap;
        _bitmap = null;
        Source = null;
        bitmap?.Dispose();
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

    private void SchedulePresentation()
    {
        try
        {
            Dispatcher.UIThread.Post(_renderLatestFrame, DispatcherPriority.Render);
        }
        catch
        {
            ClearPendingFrame();
        }
    }

    private unsafe void RenderLatestFrame()
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
            if (_disposed ||
                !frame.TryGetCpuBuffer(out var source) ||
                source.Plane0 == IntPtr.Zero ||
                source.Plane0Stride <= 0)
            {
                return;
            }

            if (_bitmap is null ||
                _bitmap.PixelSize.Width != frame.Width ||
                _bitmap.PixelSize.Height != frame.Height)
            {
                _bitmap?.Dispose();
                _bitmap = new WriteableBitmap(
                    new PixelSize(frame.Width, frame.Height),
                    new Vector(96, 96),
                    PixelFormat.Bgra8888,
                    AlphaFormat.Opaque);
                Source = _bitmap;
            }

            using (var framebuffer = _bitmap.Lock())
            {
                var rowBytes = Math.Min(
                    checked(frame.Width * 4),
                    Math.Min(source.Plane0Stride, framebuffer.RowBytes));
                var requiredSourceBytes =
                    checked((long)source.Plane0Stride * (frame.Height - 1) + rowBytes);
                if (source.Size < requiredSourceBytes)
                {
                    return;
                }

                var srcPtr = (byte*)source.Plane0;
                var dstPtr = (byte*)framebuffer.Address;
                if (source.Plane0Stride == framebuffer.RowBytes && rowBytes == framebuffer.RowBytes)
                {
                    var totalBytes = (long)framebuffer.RowBytes * frame.Height;
                    Buffer.MemoryCopy(srcPtr, dstPtr, totalBytes, totalBytes);
                }
                else
                {
                    for (var row = 0; row < frame.Height; row++)
                    {
                        Buffer.MemoryCopy(
                            srcPtr + (nint)row * source.Plane0Stride,
                            dstPtr + (nint)row * framebuffer.RowBytes,
                            rowBytes,
                            rowBytes);
                    }
                }
            }

            // Updating pixels keeps Source unchanged, so Image does not invalidate
            // itself. Unlock first, then schedule a redraw for this video frame.
            InvalidateVisual();

            if (!_presentationNotified)
            {
                _presentationNotified = true;
                FramePresented?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError(
                "Avalonia software presentation failed: {0}",
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

    private void ClearPendingFrame()
    {
        _frameSlot.Clear();
    }
}
