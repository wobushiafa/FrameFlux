using System;

namespace FrameFlux.FFmpeg;

internal sealed class FfmpegMediaFrameLease : IMediaFrameLease
{
    private readonly Action<FfmpegMediaFrameLease>? _returnAction;
    private int _disposed;

    internal FfmpegMediaFrameLease(IntPtr buffer, int size, Action<FfmpegMediaFrameLease> returnAction)
    {
        Buffer = buffer;
        Size = size;
        _returnAction = returnAction;
    }

    public IntPtr Buffer { get; }

    public int Size { get; }

    public int Width { get; internal set; }

    public int Height { get; internal set; }

    public int Stride { get; internal set; }

    public RtspNativePixelFormat PixelFormat { get; internal set; } = RtspNativePixelFormat.Bgra32;

    public MediaFrameStorageKind StorageKind =>
        PixelFormat switch
        {
            RtspNativePixelFormat.D3D11Texture => MediaFrameStorageKind.D3D11Texture,
            RtspNativePixelFormat.DmaBuf => MediaFrameStorageKind.DmaBuf,
            _ => MediaFrameStorageKind.CpuMemory
        };

    MediaPixelFormat IMediaFrameLease.PixelFormat => PixelFormat switch
    {
        RtspNativePixelFormat.Yuv420P => MediaPixelFormat.Yuv420P,
        RtspNativePixelFormat.Nv12 => MediaPixelFormat.Nv12,
        RtspNativePixelFormat.Nv21 => MediaPixelFormat.Nv21,
        RtspNativePixelFormat.D3D11Texture => MediaPixelFormat.Unknown,
        RtspNativePixelFormat.DmaBuf => MediaPixelFormat.Unknown,
        _ => MediaPixelFormat.Bgra32
    };

    public bool TryGetCpuBuffer(out MediaCpuFrameBuffer buffer)
    {
        if (Volatile.Read(ref _disposed) != 0 ||
            PixelFormat == RtspNativePixelFormat.D3D11Texture ||
            Plane0Pointer == IntPtr.Zero)
        {
            buffer = default;
            return false;
        }

        buffer = new MediaCpuFrameBuffer(
            Buffer,
            Size,
            Plane0Pointer,
            Plane1Pointer,
            Plane2Pointer,
            Plane0Stride,
            Plane1Stride,
            Plane2Stride);
        return true;
    }

    public bool TryGetD3D11Texture(out MediaD3D11TextureBuffer texture)
    {
        if (Volatile.Read(ref _disposed) != 0 ||
            PixelFormat != RtspNativePixelFormat.D3D11Texture ||
            D3D11Texture == IntPtr.Zero)
        {
            texture = default;
            return false;
        }

        texture = new MediaD3D11TextureBuffer(D3D11Texture, D3D11ArraySlice);
        return true;
    }

    public bool TryGetDmaBuf(out MediaDmaBufFrameBuffer dmaBuf)
    {
        if (Volatile.Read(ref _disposed) != 0 ||
            PixelFormat != RtspNativePixelFormat.DmaBuf)
        {
            dmaBuf = default;
            return false;
        }

        dmaBuf = DmaBuf;
        return !dmaBuf.Objects.IsEmpty && !dmaBuf.Layers.IsEmpty;
    }

    public int Plane0Offset { get; internal set; }

    public int Plane1Offset { get; internal set; }

    public int Plane2Offset { get; internal set; }

    public int Plane0Stride { get; internal set; }

    public int Plane1Stride { get; internal set; }

    public int Plane2Stride { get; internal set; }

    public IntPtr Plane0Pointer { get; internal set; }

    public IntPtr Plane1Pointer { get; internal set; }

    public IntPtr Plane2Pointer { get; internal set; }

    public IntPtr NativeHandle { get; internal set; }

    public IntPtr D3D11Texture { get; internal set; }

    public int D3D11ArraySlice { get; internal set; }

    public MediaDmaBufFrameBuffer DmaBuf { get; internal set; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _returnAction?.Invoke(this);
    }

    internal void ResetBgra(int width, int height, int stride)
    {
        Volatile.Write(ref _disposed, 0);
        Width = width;
        Height = height;
        Stride = stride;
        PixelFormat = RtspNativePixelFormat.Bgra32;
        Plane0Offset = 0;
        Plane1Offset = 0;
        Plane2Offset = 0;
        Plane0Stride = stride;
        Plane1Stride = 0;
        Plane2Stride = 0;
        Plane0Pointer = Buffer;
        Plane1Pointer = IntPtr.Zero;
        Plane2Pointer = IntPtr.Zero;
        NativeHandle = IntPtr.Zero;
    }

    internal void ResetNative(
        int width,
        int height,
        RtspNativePixelFormat pixelFormat,
        int plane0Offset,
        int plane1Offset,
        int plane2Offset,
        int plane0Stride,
        int plane1Stride,
        int plane2Stride)
    {
        Volatile.Write(ref _disposed, 0);
        Width = width;
        Height = height;
        Stride = plane0Stride;
        PixelFormat = pixelFormat;
        Plane0Offset = plane0Offset;
        Plane1Offset = plane1Offset;
        Plane2Offset = plane2Offset;
        Plane0Stride = plane0Stride;
        Plane1Stride = plane1Stride;
        Plane2Stride = plane2Stride;
        Plane0Pointer = Buffer == IntPtr.Zero ? IntPtr.Zero : Buffer + plane0Offset;
        Plane1Pointer = plane1Stride <= 0 || Buffer == IntPtr.Zero ? IntPtr.Zero : Buffer + plane1Offset;
        Plane2Pointer = plane2Stride <= 0 || Buffer == IntPtr.Zero ? IntPtr.Zero : Buffer + plane2Offset;
        NativeHandle = IntPtr.Zero;
    }

    internal void ResetNativeDirect(
        int width,
        int height,
        RtspNativePixelFormat pixelFormat,
        IntPtr plane0Pointer,
        IntPtr plane1Pointer,
        IntPtr plane2Pointer,
        int plane0Stride,
        int plane1Stride,
        int plane2Stride)
    {
        Volatile.Write(ref _disposed, 0);
        Width = width;
        Height = height;
        Stride = plane0Stride;
        PixelFormat = pixelFormat;
        Plane0Offset = 0;
        Plane1Offset = 0;
        Plane2Offset = 0;
        Plane0Stride = plane0Stride;
        Plane1Stride = plane1Stride;
        Plane2Stride = plane2Stride;
        Plane0Pointer = plane0Pointer;
        Plane1Pointer = plane1Pointer;
        Plane2Pointer = plane2Pointer;
        NativeHandle = IntPtr.Zero;
        D3D11Texture = IntPtr.Zero;
        D3D11ArraySlice = 0;
    }

    internal void ResetD3D11(int width, int height, IntPtr texture, int arraySlice)
    {
        Volatile.Write(ref _disposed, 0);
        Width = width;
        Height = height;
        Stride = 0;
        PixelFormat = RtspNativePixelFormat.D3D11Texture;
        Plane0Offset = 0;
        Plane1Offset = 0;
        Plane2Offset = 0;
        Plane0Stride = 0;
        Plane1Stride = 0;
        Plane2Stride = 0;
        Plane0Pointer = IntPtr.Zero;
        Plane1Pointer = IntPtr.Zero;
        Plane2Pointer = IntPtr.Zero;
        NativeHandle = texture;
        D3D11Texture = texture;
        D3D11ArraySlice = arraySlice;
        DmaBuf = default;
    }

    internal void ResetDmaBuf(
        int width,
        int height,
        MediaDmaBufFrameBuffer dmaBuf)
    {
        Volatile.Write(ref _disposed, 0);
        Width = width;
        Height = height;
        Stride = 0;
        PixelFormat = RtspNativePixelFormat.DmaBuf;
        Plane0Offset = 0;
        Plane1Offset = 0;
        Plane2Offset = 0;
        Plane0Stride = 0;
        Plane1Stride = 0;
        Plane2Stride = 0;
        Plane0Pointer = IntPtr.Zero;
        Plane1Pointer = IntPtr.Zero;
        Plane2Pointer = IntPtr.Zero;
        NativeHandle = IntPtr.Zero;
        D3D11Texture = IntPtr.Zero;
        D3D11ArraySlice = 0;
        DmaBuf = dmaBuf;
    }


}
