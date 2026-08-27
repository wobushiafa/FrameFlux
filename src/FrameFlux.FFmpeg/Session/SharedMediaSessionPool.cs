using System.Buffers;

namespace FrameFlux.FFmpeg;

internal sealed class SharedMediaSessionPool
{
    private readonly object _sync = new();
    private readonly Dictionary<SharedMediaSessionKey, SharedMediaSessionEntry> _entries = [];

    internal IFfmpegMediaSession Acquire(
        MediaSource source,
        MediaOpenOptions options,
        double volume,
        bool isMuted,
        Func<IFfmpegMediaSession> sessionFactory,
        IMediaVideoOutput? videoOutput = null)
    {
        var key = SharedMediaSessionKey.Create(source, options);
        SharedMediaSessionEntry entry;
        lock (_sync)
        {
            if (!_entries.TryGetValue(key, out entry!))
            {
                entry = new SharedMediaSessionEntry(sessionFactory());
                _entries.Add(key, entry);
            }

            entry.AddReference();
        }

        return new SharedMediaSessionLease(
            entry,
            source,
            options,
            volume,
            isMuted,
            videoOutput,
            () => ReleaseAsync(key, entry));
    }

    private ValueTask ReleaseAsync(SharedMediaSessionKey key, SharedMediaSessionEntry entry)
    {
        var dispose = false;
        lock (_sync)
        {
            if (entry.ReleaseReference() == 0 &&
                _entries.TryGetValue(key, out var current) &&
                ReferenceEquals(current, entry))
            {
                _entries.Remove(key);
                dispose = true;
            }
        }

        return dispose ? entry.DisposeAsync() : ValueTask.CompletedTask;
    }

    private sealed record SharedMediaSessionKey(string Source, MediaOpenOptions Options)
    {
        internal static SharedMediaSessionKey Create(MediaSource source, MediaOpenOptions options) =>
            new(source.Uri.AbsoluteUri, options with { SessionSharing = MediaSessionSharingMode.Dedicated });
    }
}

internal sealed class SharedMediaSessionEntry : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly IFfmpegMediaSession _session;
    private readonly HashSet<SharedMediaSessionLease> _activeLeases = [];
    private int _references;
    private bool _disposed;

    internal SharedMediaSessionEntry(IFfmpegMediaSession session)
    {
        _session = session;
        _session.StateChanged += OnStateChanged;
        _session.Error += OnError;
        _session.FrameReceived += OnFrameReceived;
    }

    internal MediaDiagnostics Diagnostics => _session.Diagnostics;

    internal double Volume
    {
        get => _session.Volume;
        set => _session.Volume = value;
    }

    internal bool IsMuted
    {
        get => _session.IsMuted;
        set => _session.IsMuted = value;
    }

    internal void AddReference() => _references++;

    internal int ReleaseReference() => --_references;

    internal async ValueTask StartAsync(
        SharedMediaSessionLease lease,
        double volume,
        bool isMuted,
        CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            bool startPhysicalSession;
            lock (_sync)
            {
                if (!_activeLeases.Add(lease))
                {
                    return;
                }

                startPhysicalSession = _activeLeases.Count == 1;
            }

            Volume = volume;
            IsMuted = isMuted;
            try
            {
                if (startPhysicalSession)
                {
                    await _session.StartAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    lease.ForwardState(_session.State);
                }
            }
            catch
            {
                lock (_sync)
                {
                    _activeLeases.Remove(lease);
                }

                if (startPhysicalSession)
                {
                    try
                    {
                        await _session.StopAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                }

                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    internal async ValueTask StopAsync(
        SharedMediaSessionLease lease,
        CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool stopPhysicalSession;
            lock (_sync)
            {
                if (!_activeLeases.Remove(lease))
                {
                    return;
                }

                stopPhysicalSession = _activeLeases.Count == 0;
            }

            if (stopPhysicalSession)
            {
                await _session.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    internal ValueTask<MediaSnapshot?> CaptureSnapshotAsync(CancellationToken cancellationToken) =>
        _session.CaptureSnapshotAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _session.StateChanged -= OnStateChanged;
            _session.Error -= OnError;
            _session.FrameReceived -= OnFrameReceived;
            lock (_sync)
            {
                _activeLeases.Clear();
            }

            try
            {
                await _session.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                await _session.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _lifecycleGate.Release();
            _lifecycleGate.Dispose();
        }
    }

    private void OnStateChanged(object? sender, MediaPlaybackStateChangedEventArgs args)
    {
        foreach (var lease in SnapshotActiveLeases())
        {
            lease.ForwardState(args.NewState);
        }
    }

    private void OnError(object? sender, MediaPlaybackErrorEventArgs args)
    {
        foreach (var lease in SnapshotActiveLeases())
        {
            lease.ForwardError(args);
        }
    }

    private void OnFrameReceived(object? sender, MediaVideoFrame frame)
    {
        foreach (var lease in SnapshotActiveLeases())
        {
            lease.ForwardFrame(frame);
        }
    }

    private SharedMediaSessionLease[] SnapshotActiveLeases()
    {
        lock (_sync)
        {
            return [.. _activeLeases];
        }
    }
}

internal sealed class SharedMediaSessionLease : IFfmpegMediaSession
{
    private readonly SharedMediaSessionEntry _entry;
    private readonly Func<ValueTask> _release;
    private readonly IMediaVideoOutput? _videoOutput;
    private double _volume;
    private bool _isMuted;
    private int _started;
    private int _disposed;
    private MediaPlaybackState _state = MediaPlaybackState.Idle;

    internal SharedMediaSessionLease(
        SharedMediaSessionEntry entry,
        MediaSource source,
        MediaOpenOptions options,
        double volume,
        bool isMuted,
        IMediaVideoOutput? videoOutput,
        Func<ValueTask> release)
    {
        _entry = entry;
        _release = release;
        Source = source;
        Options = options;
        _volume = volume;
        _isMuted = isMuted;
        _videoOutput = videoOutput;
    }

    public MediaSource Source { get; }

    public MediaOpenOptions Options { get; }

    public MediaPlaybackState State => _state;

    public MediaDiagnostics Diagnostics => _entry.Diagnostics;

    public double Volume
    {
        get => Volatile.Read(ref _started) == 1 ? _entry.Volume : _volume;
        set
        {
            if (value is < 0d or > 1d || double.IsNaN(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Volume must be between 0.0 and 1.0.");
            }

            ThrowIfDisposed();
            _volume = value;
            if (Volatile.Read(ref _started) == 1)
            {
                _entry.Volume = value;
            }
        }
    }

    public bool IsMuted
    {
        get => Volatile.Read(ref _started) == 1 ? _entry.IsMuted : _isMuted;
        set
        {
            ThrowIfDisposed();
            _isMuted = value;
            if (Volatile.Read(ref _started) == 1)
            {
                _entry.IsMuted = value;
            }
        }
    }

    public event EventHandler<MediaPlaybackStateChangedEventArgs>? StateChanged;

    public event EventHandler<MediaPlaybackErrorEventArgs>? Error;

    public event EventHandler<MediaVideoFrame>? FrameReceived;

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        try
        {
            await _entry.StartAsync(this, _volume, _isMuted, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Volatile.Write(ref _started, 0);
            throw;
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await StopCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<MediaSnapshot?> CaptureSnapshotAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return Volatile.Read(ref _started) == 1
            ? _entry.CaptureSnapshotAsync(cancellationToken)
            : ValueTask.FromResult<MediaSnapshot?>(null);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        try
        {
            await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await _release().ConfigureAwait(false);
        }

        GC.SuppressFinalize(this);
    }

    internal void ForwardState(MediaPlaybackState state)
    {
        if (Volatile.Read(ref _started) == 1)
        {
            SetState(state);
        }
    }

    internal void ForwardError(MediaPlaybackErrorEventArgs args)
    {
        if (Volatile.Read(ref _started) == 1)
        {
            Error?.Invoke(this, args);
        }
    }

    internal void ForwardFrame(MediaVideoFrame frame)
    {
        if (Volatile.Read(ref _started) != 1)
        {
            return;
        }

        FrameReceived?.Invoke(this, frame);
        var output = _videoOutput;
        if (output is null ||
            !output.Supports(MediaFrameStorageKind.CpuMemory, frame.PixelFormat))
        {
            return;
        }

        MediaFrameDelivery.Deliver(
            output,
            new ManagedMediaFrameLease(frame),
            exception => Error?.Invoke(
                this,
                new MediaPlaybackErrorEventArgs(
                    new MediaPlaybackError(
                        "VideoOutputFailed",
                        exception.Message,
                        IsRecoverable: true,
                        exception))));
    }

    private async ValueTask StopCoreAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 0) == 0)
        {
            return;
        }

        try
        {
            await _entry.StopAsync(this, cancellationToken).ConfigureAwait(false);
            SetState(MediaPlaybackState.Stopped);
        }
        catch
        {
            Volatile.Write(ref _started, 1);
            throw;
        }
    }

    private void SetState(MediaPlaybackState state)
    {
        var oldState = _state;
        if (oldState == state)
        {
            return;
        }

        _state = state;
        StateChanged?.Invoke(this, new MediaPlaybackStateChangedEventArgs(oldState, state));
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
}

internal sealed class ManagedMediaFrameLease : IMediaFrameLease
{
    private readonly MediaVideoFrame _frame;
    private MemoryHandle _memoryHandle;
    private readonly IntPtr _buffer;
    private int _disposed;

    internal unsafe ManagedMediaFrameLease(MediaVideoFrame frame)
    {
        _frame = frame;
        _memoryHandle = frame.Data.Pin();
        _buffer = (IntPtr)_memoryHandle.Pointer;
    }

    public int Width => _frame.Width;

    public int Height => _frame.Height;

    public MediaFrameStorageKind StorageKind => MediaFrameStorageKind.CpuMemory;

    public MediaPixelFormat PixelFormat => _frame.PixelFormat;

    public bool TryGetCpuBuffer(out MediaCpuFrameBuffer buffer)
    {
        if (Volatile.Read(ref _disposed) == 1 ||
            _buffer == IntPtr.Zero)
        {
            buffer = default;
            return false;
        }

        buffer = new MediaCpuFrameBuffer(
            _buffer,
            _frame.Data.Length,
            _buffer,
            IntPtr.Zero,
            IntPtr.Zero,
            _frame.Stride,
            0,
            0);
        return true;
    }

    public bool TryGetD3D11Texture(out MediaD3D11TextureBuffer texture)
    {
        texture = default;
        return false;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _memoryHandle.Dispose();
        _memoryHandle = default;
    }
}
