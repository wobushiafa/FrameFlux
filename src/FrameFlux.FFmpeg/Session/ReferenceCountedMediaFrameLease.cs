using System.Collections.Concurrent;

namespace FrameFlux.FFmpeg;

internal interface IMediaFrameLeaseSource
{
    MediaFrameStorageKind NativeFrameStorageKind => MediaFrameStorageKind.CpuMemory;

    bool HandlesOutputNatively(IMediaVideoOutput output) => false;

    void SetFrameLeaseConsumer(Action<IMediaFrameLease>? consumer);

    void SetSupplementaryCpuFrameLeaseConsumer(Action<IMediaFrameLease>? consumer) { }

    void UpdateSharedFrameRequirements(bool requiresNativeFrames, bool requiresCpuFrames) { }

    void AttachSharedVideoOutput(IMediaVideoOutput output) { }

    void DetachSharedVideoOutput(IMediaVideoOutput output) { }
}

internal sealed class ReferenceCountedMediaFrameLeaseOwner
{
    private const int MaximumPooledOwners = 32;
    private static readonly ConcurrentQueue<ReferenceCountedMediaFrameLeaseOwner> Pool = new();
    private static int s_pooledCount;

    private IMediaFrameLease? _frame;
    private int _remainingReferences;

    private ReferenceCountedMediaFrameLeaseOwner()
    {
    }

    internal ReferenceCountedMediaFrameLeaseOwner(IMediaFrameLease frame)
    {
        RuntimeCompat.NotNull(frame, nameof(frame));
        _frame = frame;
        _remainingReferences = 1;
    }

    internal static ReferenceCountedMediaFrameLeaseOwner Rent(
        IMediaFrameLease frame,
        int initialReferenceCount)
    {
        RuntimeCompat.NotNull(frame, nameof(frame));
        RuntimeCompat.AtLeast(initialReferenceCount, 1, nameof(initialReferenceCount));

        ReferenceCountedMediaFrameLeaseOwner owner;
        if (Pool.TryDequeue(out var pooled))
        {
            Interlocked.Decrement(ref s_pooledCount);
            owner = pooled;
        }
        else
        {
            owner = new ReferenceCountedMediaFrameLeaseOwner();
        }

        owner._frame = frame;
        Volatile.Write(ref owner._remainingReferences, initialReferenceCount);
        return owner;
    }

    internal int Width => _frame!.Width;

    internal int Height => _frame!.Height;

    internal MediaFrameStorageKind StorageKind => _frame!.StorageKind;

    internal MediaPixelFormat PixelFormat => _frame!.PixelFormat;

    internal bool TryGetCpuBuffer(out MediaCpuFrameBuffer buffer)
    {
        var frame = _frame;
        if (frame is null)
        {
            buffer = default;
            return false;
        }

        return frame.TryGetCpuBuffer(out buffer);
    }

    internal bool TryGetD3D11Texture(out MediaD3D11TextureBuffer texture)
    {
        var frame = _frame;
        if (frame is null)
        {
            texture = default;
            return false;
        }

        return frame.TryGetD3D11Texture(out texture);
    }

    internal bool TryGetDmaBuf(out MediaDmaBufFrameBuffer dmaBuf)
    {
        var frame = _frame;
        if (frame is null)
        {
            dmaBuf = default;
            return false;
        }

        return frame.TryGetDmaBuf(out dmaBuf);
    }

    internal ReferenceCountedMediaFrameLease CreateLease()
    {
        Interlocked.Increment(ref _remainingReferences);
        try
        {
            return ReferenceCountedMediaFrameLease.Rent(this);
        }
        catch
        {
            Release();
            throw;
        }
    }

    internal ReferenceCountedMediaFrameLease CreatePreCountedLease() =>
        ReferenceCountedMediaFrameLease.Rent(this);

    internal void Release(int count = 1)
    {
        var remaining = count == 1
            ? Interlocked.Decrement(ref _remainingReferences)
            : Interlocked.Add(ref _remainingReferences, -count);
        if (remaining == 0)
        {
            var frame = _frame;
            _frame = null;
            try
            {
                frame?.Dispose();
            }
            finally
            {
                if (Interlocked.Increment(ref s_pooledCount) <= MaximumPooledOwners)
                {
                    Pool.Enqueue(this);
                }
                else
                {
                    Interlocked.Decrement(ref s_pooledCount);
                }
            }
        }
    }
}

internal sealed class ReferenceCountedMediaFrameLease : IMediaFrameLease
{
    private const int MaximumPooledLeases = 64;
    private static readonly ConcurrentQueue<ReferenceCountedMediaFrameLease> Pool = new();
    private static int s_pooledCount;

    private ReferenceCountedMediaFrameLeaseOwner? _owner;
    private int _width;
    private int _height;
    private MediaFrameStorageKind _storageKind;
    private MediaPixelFormat _pixelFormat;

    private ReferenceCountedMediaFrameLease()
    {
    }

    internal ReferenceCountedMediaFrameLease(ReferenceCountedMediaFrameLeaseOwner owner)
    {
        Initialize(owner);
    }

    internal static ReferenceCountedMediaFrameLease Rent(ReferenceCountedMediaFrameLeaseOwner owner)
    {
        ReferenceCountedMediaFrameLease lease;
        if (Pool.TryDequeue(out var pooled))
        {
            Interlocked.Decrement(ref s_pooledCount);
            lease = pooled;
        }
        else
        {
            lease = new ReferenceCountedMediaFrameLease();
        }

        lease.Initialize(owner);
        return lease;
    }

    private void Initialize(ReferenceCountedMediaFrameLeaseOwner owner)
    {
        _width = owner.Width;
        _height = owner.Height;
        _storageKind = owner.StorageKind;
        _pixelFormat = owner.PixelFormat;
        Volatile.Write(ref _owner, owner);
    }

    public int Width => _width;

    public int Height => _height;

    public MediaFrameStorageKind StorageKind => _storageKind;

    public MediaPixelFormat PixelFormat => _pixelFormat;

    public bool TryGetCpuBuffer(out MediaCpuFrameBuffer buffer)
    {
        var owner = Volatile.Read(ref _owner);
        if (owner is null)
        {
            buffer = default;
            return false;
        }

        return owner.TryGetCpuBuffer(out buffer);
    }

    public bool TryGetD3D11Texture(out MediaD3D11TextureBuffer texture)
    {
        var owner = Volatile.Read(ref _owner);
        if (owner is null)
        {
            texture = default;
            return false;
        }

        return owner.TryGetD3D11Texture(out texture);
    }

    public bool TryGetDmaBuf(out MediaDmaBufFrameBuffer dmaBuf)
    {
        var owner = Volatile.Read(ref _owner);
        if (owner is null)
        {
            dmaBuf = default;
            return false;
        }

        return owner.TryGetDmaBuf(out dmaBuf);
    }

    public void Dispose()
    {
        var owner = Interlocked.Exchange(ref _owner, null);
        if (owner is null)
        {
            return;
        }

        try
        {
            owner.Release();
        }
        finally
        {
            if (Interlocked.Increment(ref s_pooledCount) <= MaximumPooledLeases)
            {
                Pool.Enqueue(this);
            }
            else
            {
                Interlocked.Decrement(ref s_pooledCount);
            }
        }
    }
}
