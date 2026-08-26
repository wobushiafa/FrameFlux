using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace FrameFlux.Avalonia;

internal sealed class SoftwareBitmapFrameRenderer : IRtspLeaseFrameRenderer
{
    private static readonly Vector BitmapDpi = new(96, 96);

    private readonly object _pendingFrameLock = new();
    private RtspVideoView? _owner;
    private WriteableBitmap? _bitmap;
    private RtspFrameLease? _pendingFrameLease;
    private IntPtr _pendingFrameBuffer;
    private int _pendingFrameBufferSize;
    private int _pendingFrameWidth;
    private int _pendingFrameHeight;
    private int _pendingFrameStride;
    private int _frameUpdatePending;

    public RtspRenderMode Mode => RtspRenderMode.SoftwareBitmap;

    public void Attach(RtspVideoView owner)
    {
        _owner = owner;
    }

    public void Detach()
    {
        _owner = null;
        _bitmap?.Dispose();
        _bitmap = null;
        ReleasePendingLease();
        FreePendingFrameBuffer();
    }

    public void UpdateFrame(IntPtr buffer, int width, int height, int stride)
    {
        if (Interlocked.CompareExchange(ref _frameUpdatePending, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var requiredBufferSize = stride * height;
            lock (_pendingFrameLock)
            {
                if (_pendingFrameBuffer == IntPtr.Zero || _pendingFrameBufferSize != requiredBufferSize)
                {
                    if (_pendingFrameBuffer != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(_pendingFrameBuffer);
                    }

                    _pendingFrameBuffer = Marshal.AllocHGlobal(requiredBufferSize);
                    _pendingFrameBufferSize = requiredBufferSize;
                }

                unsafe
                {
                    Buffer.MemoryCopy(
                        buffer.ToPointer(),
                        _pendingFrameBuffer.ToPointer(),
                        _pendingFrameBufferSize,
                        requiredBufferSize);
                }

                _pendingFrameWidth = width;
                _pendingFrameHeight = height;
                _pendingFrameStride = stride;
            }

            var owner = _owner;
            if (owner == null)
            {
                Interlocked.Exchange(ref _frameUpdatePending, 0);
                return;
            }

            owner.PostRendererUpdate(ProcessPendingFrame);
        }
        catch
        {
            Interlocked.Exchange(ref _frameUpdatePending, 0);
            throw;
        }
    }

    public void UpdateFrameLease(RtspFrameLease lease)
    {
        if (Interlocked.CompareExchange(ref _frameUpdatePending, 1, 0) != 0)
        {
            lease.Dispose();
            return;
        }

        try
        {
            lock (_pendingFrameLock)
            {
                ReleasePendingLease();
                _pendingFrameLease = lease;
                _pendingFrameWidth = lease.Width;
                _pendingFrameHeight = lease.Height;
                _pendingFrameStride = lease.Stride > 0 ? lease.Stride : lease.Plane0Stride;
            }

            var owner = _owner;
            if (owner == null)
            {
                ReleasePendingLease();
                Interlocked.Exchange(ref _frameUpdatePending, 0);
                return;
            }

            owner.PostRendererUpdate(ProcessPendingFrame);
        }
        catch
        {
            lease.Dispose();
            Interlocked.Exchange(ref _frameUpdatePending, 0);
            throw;
        }
    }

    public void Render(DrawingContext context, Rect bounds, Stretch stretch)
    {
        if (_bitmap == null)
        {
            context.FillRectangle(Brushes.Black, bounds);
            return;
        }

        var sourceRect = new Rect(0, 0, _bitmap.PixelSize.Width, _bitmap.PixelSize.Height);
        var destRect = RtspVideoView.CalculateDestinationRect(sourceRect.Size, bounds.Size, stretch);
        context.DrawImage(_bitmap, sourceRect, destRect);
    }

    public void Dispose()
    {
        Detach();
    }

    private void ProcessPendingFrame()
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            lock (_pendingFrameLock)
            {
                var lease = _pendingFrameLease;
                var sourceBuffer = lease != null
                    ? (lease.Buffer != IntPtr.Zero ? lease.Buffer : lease.Plane0Pointer)
                    : _pendingFrameBuffer;

                if (sourceBuffer == IntPtr.Zero || _pendingFrameWidth <= 0 || _pendingFrameHeight <= 0)
                {
                    return;
                }

                EnsureBitmap(_pendingFrameWidth, _pendingFrameHeight);
                var bitmap = _bitmap!;

                using var lockedBitmap = bitmap.Lock();
                var dstStride = lockedBitmap.RowBytes;
                
                for (int y = 0; y < _pendingFrameHeight; y++)
                {
                    var srcPtr = IntPtr.Add(sourceBuffer, y * _pendingFrameStride);
                    var dstPtr = IntPtr.Add(lockedBitmap.Address, y * dstStride);

                    unsafe
                    {
                        Buffer.MemoryCopy(
                            srcPtr.ToPointer(),
                            dstPtr.ToPointer(),
                            dstStride,
                            Math.Min(_pendingFrameStride, dstStride));
                    }
                }

                ReleasePendingLease();
            }

            _owner?.NotifyRendererFrameReady();
        }
        finally
        {
            _owner?.RecordRendererPresentation(Stopwatch.GetTimestamp() - start);
            Interlocked.Exchange(ref _frameUpdatePending, 0);
        }
    }

    private void EnsureBitmap(int width, int height)
    {
        if (_bitmap != null && _bitmap.PixelSize.Width == width && _bitmap.PixelSize.Height == height)
        {
            return;
        }

        _bitmap?.Dispose();
        _bitmap = CreateBitmap(width, height);
    }

    private static WriteableBitmap CreateBitmap(int width, int height)
    {
#if ANDROID
        return new WriteableBitmap(
            new PixelSize(width, height),
            BitmapDpi,
            PixelFormat.Rgba8888,
            AlphaFormat.Premul);
#else
        return new WriteableBitmap(
            new PixelSize(width, height),
            BitmapDpi,
            PixelFormat.Bgra8888,
            AlphaFormat.Opaque);
#endif
    }

    private void FreePendingFrameBuffer()
    {
        lock (_pendingFrameLock)
        {
            if (_pendingFrameBuffer == IntPtr.Zero)
            {
                return;
            }

            Marshal.FreeHGlobal(_pendingFrameBuffer);
            _pendingFrameBuffer = IntPtr.Zero;
            _pendingFrameBufferSize = 0;
            _pendingFrameWidth = 0;
            _pendingFrameHeight = 0;
            _pendingFrameStride = 0;
        }
    }

    private void ReleasePendingLease()
    {
        _pendingFrameLease?.Dispose();
        _pendingFrameLease = null;
    }
}
