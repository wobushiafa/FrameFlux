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
    private WriteableBitmap? _bitmap;
    private bool _disposed;

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
            try
            {
                Dispatcher.UIThread.Post(RenderLatestFrame, DispatcherPriority.Render);
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
        _bitmap?.Dispose();
        _bitmap = null;
        Source = null;
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

    private unsafe void RenderLatestFrame()
    {
        var frame = _frameSlot.Take();

        if (frame is null)
        {
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
                    AlphaFormat.Unpremul);
                Source = _bitmap;
            }

            using var framebuffer = _bitmap.Lock();
            var rowBytes = Math.Min(
                checked(frame.Width * 4),
                Math.Min(source.Plane0Stride, framebuffer.RowBytes));
            var requiredSourceBytes =
                checked((long)source.Plane0Stride * (frame.Height - 1) + rowBytes);
            if (source.Size < requiredSourceBytes)
            {
                return;
            }

            for (var row = 0; row < frame.Height; row++)
            {
                var sourceRow = new ReadOnlySpan<byte>(
                    (byte*)source.Plane0 + row * source.Plane0Stride,
                    rowBytes);
                var destinationRow = new Span<byte>(
                    (byte*)framebuffer.Address + row * framebuffer.RowBytes,
                    rowBytes);
                sourceRow.CopyTo(destinationRow);
            }

            FramePresented?.Invoke(this, EventArgs.Empty);
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
        }
    }

    private void ClearPendingFrame()
    {
        _frameSlot.Clear();
    }
}
