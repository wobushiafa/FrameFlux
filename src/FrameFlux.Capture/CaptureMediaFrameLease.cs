namespace FrameFlux.Capture;

internal interface ICaptureD3D11SliceReleaser
{
    void ReleaseSlice(bool isNv12, int sliceIndex, IntPtr texturePtr);
}

/// <summary>
/// Reference-counted frame lease supporting CPU memory, D3D11 textures, and Linux DMA-BUF frames.
/// </summary>
internal sealed class CaptureMediaFrameLease : IMediaFrameLease
{
    private readonly CaptureFrameBufferPool? _cpuPool;
    private readonly ICaptureD3D11SliceReleaser? _d3d11Releaser;
    private readonly Action? _releaseCallback;
    private readonly bool _isNv12;
    private readonly MediaDmaBufFrameBuffer _dmaBuf;
    private int _remainingReferences = 1;

    private CaptureMediaFrameLease(
        int width,
        int height,
        MediaFrameStorageKind storageKind,
        MediaPixelFormat pixelFormat,
        IntPtr buffer,
        int size,
        int stride,
        IntPtr d3d11Texture,
        int d3d11ArraySlice,
        MediaDmaBufFrameBuffer dmaBuf,
        CaptureFrameBufferPool? cpuPool,
        ICaptureD3D11SliceReleaser? d3d11Releaser,
        bool isNv12,
        Action? releaseCallback)
    {
        Width = width;
        Height = height;
        StorageKind = storageKind;
        PixelFormat = pixelFormat;
        Buffer = buffer;
        Size = size;
        Stride = stride;
        D3D11Texture = d3d11Texture;
        D3D11ArraySlice = d3d11ArraySlice;
        _dmaBuf = dmaBuf;
        _cpuPool = cpuPool;
        _d3d11Releaser = d3d11Releaser;
        _isNv12 = isNv12;
        _releaseCallback = releaseCallback;
    }

    internal static CaptureMediaFrameLease CreateBgraCpu(
        int width,
        int height,
        int stride,
        IntPtr buffer,
        int size,
        CaptureFrameBufferPool pool)
    {
        return new CaptureMediaFrameLease(
            width,
            height,
            MediaFrameStorageKind.CpuMemory,
            MediaPixelFormat.Bgra32,
            buffer,
            size,
            stride,
            IntPtr.Zero,
            0,
            default,
            pool,
            d3d11Releaser: null,
            isNv12: false,
            releaseCallback: null);
    }

    internal static CaptureMediaFrameLease CreateD3D11(
        int width,
        int height,
        IntPtr texture,
        int arraySlice,
        ICaptureD3D11SliceReleaser releaser,
        bool isNv12,
        MediaPixelFormat pixelFormat = MediaPixelFormat.Bgra32)
    {
        return new CaptureMediaFrameLease(
            width,
            height,
            MediaFrameStorageKind.D3D11Texture,
            pixelFormat,
            IntPtr.Zero,
            0,
            0,
            texture,
            arraySlice,
            default,
            cpuPool: null,
            d3d11Releaser: releaser,
            isNv12: isNv12,
            releaseCallback: null);
    }

    internal static CaptureMediaFrameLease CreateD3D11(
        int width,
        int height,
        IntPtr texture,
        int arraySlice,
        Action? releaseCallback,
        MediaPixelFormat pixelFormat = MediaPixelFormat.Bgra32)
    {
        return new CaptureMediaFrameLease(
            width,
            height,
            MediaFrameStorageKind.D3D11Texture,
            pixelFormat,
            IntPtr.Zero,
            0,
            0,
            texture,
            arraySlice,
            default,
            cpuPool: null,
            d3d11Releaser: null,
            isNv12: false,
            releaseCallback: releaseCallback);
    }

    internal static CaptureMediaFrameLease CreateDmaBuf(
        int width,
        int height,
        MediaDmaBufFrameBuffer dmaBuf,
        Action? releaseCallback,
        MediaPixelFormat pixelFormat = MediaPixelFormat.Nv12)
    {
        return new CaptureMediaFrameLease(
            width,
            height,
            MediaFrameStorageKind.DmaBuf,
            pixelFormat,
            IntPtr.Zero,
            0,
            0,
            IntPtr.Zero,
            0,
            dmaBuf,
            cpuPool: null,
            d3d11Releaser: null,
            isNv12: pixelFormat == MediaPixelFormat.Nv12,
            releaseCallback: releaseCallback);
    }

    public int Width { get; }

    public int Height { get; }

    public MediaFrameStorageKind StorageKind { get; }

    public MediaPixelFormat PixelFormat { get; }

    internal IntPtr Buffer { get; }

    internal int Size { get; }

    internal int Stride { get; }

    internal IntPtr D3D11Texture { get; }

    internal int D3D11ArraySlice { get; }

    internal CaptureMediaFrameLease Retain()
    {
        while (true)
        {
            var current = Volatile.Read(ref _remainingReferences);
            if (current <= 0)
            {
                throw new ObjectDisposedException(nameof(CaptureMediaFrameLease));
            }

            if (Interlocked.CompareExchange(ref _remainingReferences, current + 1, current) == current)
            {
                return this;
            }
        }
    }

    public bool TryGetCpuBuffer(out MediaCpuFrameBuffer buffer)
    {
        if (Volatile.Read(ref _remainingReferences) <= 0 ||
            StorageKind != MediaFrameStorageKind.CpuMemory ||
            Buffer == IntPtr.Zero)
        {
            buffer = default;
            return false;
        }

        buffer = new MediaCpuFrameBuffer(
            Buffer,
            Size,
            Buffer,
            IntPtr.Zero,
            IntPtr.Zero,
            Stride,
            0,
            0);
        return true;
    }

    public bool TryGetD3D11Texture(out MediaD3D11TextureBuffer texture)
    {
        if (Volatile.Read(ref _remainingReferences) <= 0 ||
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
        if (Volatile.Read(ref _remainingReferences) <= 0 ||
            StorageKind != MediaFrameStorageKind.DmaBuf ||
            _dmaBuf.Objects.Length == 0 ||
            _dmaBuf.Layers.Length == 0)
        {
            dmaBuf = default;
            return false;
        }

        dmaBuf = _dmaBuf;
        return true;
    }

    public void Dispose()
    {
        var remaining = Interlocked.Decrement(ref _remainingReferences);
        if (remaining == 0)
        {
            if (_cpuPool is not null)
            {
                _cpuPool.Return(Buffer, Size);
            }
            else if (_d3d11Releaser is not null)
            {
                _d3d11Releaser.ReleaseSlice(_isNv12, D3D11ArraySlice, D3D11Texture);
            }
            else
            {
                _releaseCallback?.Invoke();
            }
        }
        else if (remaining < 0)
        {
            Interlocked.Exchange(ref _remainingReferences, 0);
        }
    }
}
