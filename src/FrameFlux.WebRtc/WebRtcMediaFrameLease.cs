using System.Runtime.CompilerServices;

namespace FrameFlux.WebRtc;

/// <summary>
/// Reusable media frame lease for WebRTC video frames.
/// Implements <see cref="IMediaFrameLease"/> with memory pool reclamation.
/// </summary>
public sealed class WebRtcMediaFrameLease : IMediaFrameLease
{
    private readonly Action<WebRtcMediaFrameLease>? _returnAction;
    private int _disposed;

    public WebRtcMediaFrameLease(
        IntPtr buffer,
        int size,
        Action<WebRtcMediaFrameLease>? returnAction = null)
    {
        Buffer = buffer;
        Size = size;
        _returnAction = returnAction;
    }

    public IntPtr Buffer { get; private set; }

    public int Size { get; private set; }

    public int Width { get; internal set; }

    public int Height { get; internal set; }

    public int Stride { get; internal set; }

    public MediaPixelFormat PixelFormat { get; internal set; } = MediaPixelFormat.Bgra32;

    public MediaFrameStorageKind StorageKind { get; internal set; } = MediaFrameStorageKind.CpuMemory;

    public IntPtr Plane0 { get; internal set; }

    public IntPtr Plane1 { get; internal set; }

    public IntPtr Plane2 { get; internal set; }

    public int Plane0Stride { get; internal set; }

    public int Plane1Stride { get; internal set; }

    public int Plane2Stride { get; internal set; }

    public bool IsFullRange { get; internal set; }

    public IntPtr D3D11Texture { get; internal set; }

    public int D3D11ArraySlice { get; internal set; }

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public bool TryGetCpuBuffer(out MediaCpuFrameBuffer buffer)
    {
        if (IsDisposed ||
            StorageKind != MediaFrameStorageKind.CpuMemory ||
            Plane0 == IntPtr.Zero)
        {
            buffer = default;
            return false;
        }

        buffer = new MediaCpuFrameBuffer(
            Buffer,
            Size,
            Plane0,
            Plane1,
            Plane2,
            Plane0Stride,
            Plane1Stride,
            Plane2Stride);
        return true;
    }

    public bool TryGetD3D11Texture(out MediaD3D11TextureBuffer texture)
    {
        if (IsDisposed ||
            StorageKind != MediaFrameStorageKind.D3D11Texture ||
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
        dmaBuf = default;
        return false;
    }

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
        PixelFormat = MediaPixelFormat.Bgra32;
        StorageKind = MediaFrameStorageKind.CpuMemory;
        Plane0 = Buffer;
        Plane1 = IntPtr.Zero;
        Plane2 = IntPtr.Zero;
        Plane0Stride = stride;
        Plane1Stride = 0;
        Plane2Stride = 0;
        D3D11Texture = IntPtr.Zero;
        D3D11ArraySlice = 0;
    }

    internal void ResetYuv420P(int width, int height, int yStride, int uvStride)
    {
        Volatile.Write(ref _disposed, 0);
        Width = width;
        Height = height;
        Stride = yStride;
        PixelFormat = MediaPixelFormat.Yuv420P;
        StorageKind = MediaFrameStorageKind.CpuMemory;

        var ySize = yStride * height;
        var uvHeight = (height + 1) / 2;
        var uSize = uvStride * uvHeight;

        Plane0 = Buffer;
        Plane1 = Buffer + ySize;
        Plane2 = Buffer + ySize + uSize;
        Plane0Stride = yStride;
        Plane1Stride = uvStride;
        Plane2Stride = uvStride;
        D3D11Texture = IntPtr.Zero;
        D3D11ArraySlice = 0;
    }

    internal void ResetNv12(int width, int height, int yStride, int uvStride)
    {
        Volatile.Write(ref _disposed, 0);
        Width = width;
        Height = height;
        Stride = yStride;
        PixelFormat = MediaPixelFormat.Nv12;
        StorageKind = MediaFrameStorageKind.CpuMemory;

        var ySize = yStride * height;

        Plane0 = Buffer;
        Plane1 = Buffer + ySize;
        Plane2 = IntPtr.Zero;
        Plane0Stride = yStride;
        Plane1Stride = uvStride;
        Plane2Stride = 0;
        D3D11Texture = IntPtr.Zero;
        D3D11ArraySlice = 0;
    }

    internal void ResetD3D11(int width, int height, IntPtr texture, int arraySlice)
    {
        Volatile.Write(ref _disposed, 0);
        Width = width;
        Height = height;
        Stride = 0;
        PixelFormat = MediaPixelFormat.Unknown;
        StorageKind = MediaFrameStorageKind.D3D11Texture;
        Plane0 = IntPtr.Zero;
        Plane1 = IntPtr.Zero;
        Plane2 = IntPtr.Zero;
        Plane0Stride = 0;
        Plane1Stride = 0;
        Plane2Stride = 0;
        D3D11Texture = texture;
        D3D11ArraySlice = arraySlice;
    }

    internal void UpdateBuffer(IntPtr newBuffer, int newSize)
    {
        Buffer = newBuffer;
        Size = newSize;
    }
}
