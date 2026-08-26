using System;
using System.Runtime.InteropServices;

namespace FrameFlux.FFmpeg;

public sealed class RtspFrameLease : IDisposable
{
    private readonly Action<RtspFrameLease>? _returnAction;
    private bool _disposed;

    [DllImport("libc.so.6", EntryPoint = "close")]
    private static extern int posix_close(int fd);

    internal RtspFrameLease(IntPtr buffer, int size, Action<RtspFrameLease> returnAction)
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


    /// <summary>DMA-BUF file descriptor for VAAPI zero-copy path. -1 when unused.</summary>
    public int DmaBufFd { get; internal set; } = -1;

    /// <summary>DRM fourcc code (e.g. DRM_FORMAT_NV12).</summary>
    public uint DrmFourcc { get; internal set; }

    /// <summary>DRM buffer modifier.</summary>
    public ulong DrmModifier { get; internal set; }

    public int DmaBufYOffset { get; internal set; }

    public int DmaBufYPitch { get; internal set; }

    public int DmaBufUvOffset { get; internal set; }

    public int DmaBufUvPitch { get; internal set; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ReleaseNativeResources();
        _returnAction?.Invoke(this);
    }

    internal void ResetBgra(int width, int height, int stride)
    {
        _disposed = false;
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
        _disposed = false;
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
        _disposed = false;
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
        _disposed = false;
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
    }


    internal void ResetVaapiDmaBuf(
        int width,
        int height,
        int dmaBufFd,
        uint drmFourcc,
        ulong drmModifier,
        int yOffset,
        int yPitch,
        int uvOffset,
        int uvPitch)
    {
        _disposed = false;
        Width = width;
        Height = height;
        Stride = 0;
        PixelFormat = RtspNativePixelFormat.VaapiDmaBuf;
        Plane0Offset = 0;
        Plane1Offset = 0;
        Plane2Offset = 0;
        Plane0Stride = yPitch;
        Plane1Stride = uvPitch;
        Plane2Stride = 0;
        Plane0Pointer = IntPtr.Zero;
        Plane1Pointer = IntPtr.Zero;
        Plane2Pointer = IntPtr.Zero;
        NativeHandle = IntPtr.Zero;
        DmaBufFd = dmaBufFd;
        DrmFourcc = drmFourcc;
        DrmModifier = drmModifier;
        DmaBufYOffset = yOffset;
        DmaBufYPitch = yPitch;
        DmaBufUvOffset = uvOffset;
        DmaBufUvPitch = uvPitch;
    }

    private void ClearDmaBufFields()
    {
        DmaBufFd = -1;
        DrmFourcc = 0;
        DrmModifier = 0;
        DmaBufYOffset = 0;
        DmaBufYPitch = 0;
        DmaBufUvOffset = 0;
        DmaBufUvPitch = 0;
        D3D11Texture = IntPtr.Zero;
        D3D11ArraySlice = 0;
    }

    private void ReleaseNativeResources()
    {
        if (DmaBufFd >= 0)
        {
            posix_close(DmaBufFd);
        }

        ClearDmaBufFields();
    }
}
