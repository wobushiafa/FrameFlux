namespace FrameFlux.Capture;

/// <summary>
/// Reference-counted frame lease supporting CPU memory and D3D11 textures.
/// </summary>
internal sealed class CaptureMediaFrameLease : IMediaFrameLease
{
    private readonly Action<CaptureMediaFrameLease>? _releaseAction;
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
        Action<CaptureMediaFrameLease>? releaseAction)
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
        _releaseAction = releaseAction;
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
            lease => pool.Return(lease.Buffer, lease.Size));
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
            _ => releaseCallback?.Invoke());
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
        dmaBuf = default;
        return false;
    }

    public void Dispose()
    {
        var remaining = Interlocked.Decrement(ref _remainingReferences);
        if (remaining == 0)
        {
            _releaseAction?.Invoke(this);
        }
        else if (remaining < 0)
        {
            Interlocked.Exchange(ref _remainingReferences, 0);
        }
    }
}
