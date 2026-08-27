namespace FrameFlux.FFmpeg;

internal interface IMediaFrameLeaseSource
{
    void SetFrameLeaseConsumer(Action<IMediaFrameLease>? consumer);
}

internal sealed class ReferenceCountedMediaFrameLeaseOwner
{
    private readonly IMediaFrameLease _frame;
    private int _remainingReferences = 1;

    internal ReferenceCountedMediaFrameLeaseOwner(IMediaFrameLease frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        _frame = frame;
    }

    internal int Width => _frame.Width;

    internal int Height => _frame.Height;

    internal MediaFrameStorageKind StorageKind => _frame.StorageKind;

    internal MediaPixelFormat PixelFormat => _frame.PixelFormat;

    internal bool TryGetCpuBuffer(out MediaCpuFrameBuffer buffer) =>
        _frame.TryGetCpuBuffer(out buffer);

    internal bool TryGetD3D11Texture(out MediaD3D11TextureBuffer texture) =>
        _frame.TryGetD3D11Texture(out texture);

    internal ReferenceCountedMediaFrameLease CreateLease()
    {
        Interlocked.Increment(ref _remainingReferences);
        try
        {
            return new ReferenceCountedMediaFrameLease(this);
        }
        catch
        {
            Release();
            throw;
        }
    }

    internal void Release()
    {
        if (Interlocked.Decrement(ref _remainingReferences) == 0)
        {
            _frame.Dispose();
        }
    }
}

internal sealed class ReferenceCountedMediaFrameLease(
    ReferenceCountedMediaFrameLeaseOwner owner) : IMediaFrameLease
{
    private readonly ReferenceCountedMediaFrameLeaseOwner _owner = owner;
    private int _disposed;

    public int Width => _owner.Width;

    public int Height => _owner.Height;

    public MediaFrameStorageKind StorageKind => _owner.StorageKind;

    public MediaPixelFormat PixelFormat => _owner.PixelFormat;

    public bool TryGetCpuBuffer(out MediaCpuFrameBuffer buffer)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            buffer = default;
            return false;
        }

        return _owner.TryGetCpuBuffer(out buffer);
    }

    public bool TryGetD3D11Texture(out MediaD3D11TextureBuffer texture)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            texture = default;
            return false;
        }

        return _owner.TryGetD3D11Texture(out texture);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _owner.Release();
        }
    }
}
