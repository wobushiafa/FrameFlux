namespace FrameFlux.FFmpeg;

internal sealed class SharedRtspSessionPool
{
    private readonly object _sync = new();
    private readonly Dictionary<SharedRtspSessionKey, SharedRtspSessionEntry> _entries = [];

    internal IRtspSession Acquire(
        RtspSource source,
        RtspSessionOptions options,
        Func<IRtspSession> sessionFactory)
    {
        var key = SharedRtspSessionKey.Create(source, options);
        SharedRtspSessionEntry entry;
        lock (_sync)
        {
            if (!_entries.TryGetValue(key, out entry!))
            {
                entry = new SharedRtspSessionEntry(sessionFactory());
                _entries.Add(key, entry);
            }
            entry.AddReference();
        }

        return new SharedRtspSessionLease(
            entry,
            source,
            options,
            () => ReleaseAsync(key, entry));
    }

    private ValueTask ReleaseAsync(
        SharedRtspSessionKey key,
        SharedRtspSessionEntry entry)
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

    private sealed record SharedRtspSessionKey(
        string Source,
        RtspSessionOptions Options)
    {
        internal static SharedRtspSessionKey Create(
            RtspSource source,
            RtspSessionOptions options) =>
            new(
                source.Uri.AbsoluteUri,
                options with
                {
                    StreamSharing = RtspStreamSharingMode.Dedicated,
                    Volume = 1d,
                    IsMuted = false
                });
    }
}

internal sealed class SharedRtspSessionEntry : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly IRtspSession _session;
    private readonly HashSet<SharedRtspSessionLease> _activeLeases = [];
    private int _references;
    private bool _disposed;

    internal SharedRtspSessionEntry(IRtspSession session)
    {
        _session = session;
        _session.StateChanged += OnStateChanged;
        _session.Error += OnError;
        _session.FrameReceived += OnFrameReceived;
    }

    internal RtspSessionDiagnostics Diagnostics => _session.Diagnostics;

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
        SharedRtspSessionLease lease,
        double volume,
        bool muted,
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
            IsMuted = muted;
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
        SharedRtspSessionLease lease,
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

    internal ValueTask<RtspSnapshot?> CaptureSnapshotAsync(
        CancellationToken cancellationToken) =>
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

    private void OnStateChanged(object? sender, RtspSessionStateChangedEventArgs args)
    {
        foreach (var lease in SnapshotActiveLeases())
        {
            lease.ForwardState(args.NewState);
        }
    }

    private void OnError(object? sender, RtspSessionErrorEventArgs args)
    {
        foreach (var lease in SnapshotActiveLeases())
        {
            lease.ForwardError(args);
        }
    }

    private void OnFrameReceived(object? sender, RtspVideoFrame frame)
    {
        foreach (var lease in SnapshotActiveLeases())
        {
            lease.ForwardFrame(frame);
        }
    }

    private SharedRtspSessionLease[] SnapshotActiveLeases()
    {
        lock (_sync)
        {
            return [.. _activeLeases];
        }
    }
}

internal sealed class SharedRtspSessionLease : IRtspSession
{
    private readonly SharedRtspSessionEntry _entry;
    private readonly Func<ValueTask> _release;
    private double _volume;
    private bool _isMuted;
    private int _started;
    private int _disposed;
    private RtspSessionState _state = RtspSessionState.Idle;

    internal SharedRtspSessionLease(
        SharedRtspSessionEntry entry,
        RtspSource source,
        RtspSessionOptions options,
        Func<ValueTask> release)
    {
        _entry = entry;
        _release = release;
        Source = source;
        Options = options;
        _volume = options.Volume;
        _isMuted = options.IsMuted;
    }

    public RtspSource Source { get; }

    public RtspSessionOptions Options { get; }

    public RtspSessionState State => _state;

    public RtspSessionDiagnostics Diagnostics => _entry.Diagnostics;

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

    public event EventHandler<RtspSessionStateChangedEventArgs>? StateChanged;

    public event EventHandler<RtspSessionErrorEventArgs>? Error;

    public event EventHandler<RtspVideoFrame>? FrameReceived;

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        try
        {
            await _entry.StartAsync(this, _volume, _isMuted, cancellationToken)
                .ConfigureAwait(false);
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

    public ValueTask<RtspSnapshot?> CaptureSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return Volatile.Read(ref _started) == 1
            ? _entry.CaptureSnapshotAsync(cancellationToken)
            : ValueTask.FromResult<RtspSnapshot?>(null);
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

    internal void ForwardState(RtspSessionState state)
    {
        if (Volatile.Read(ref _started) == 0)
        {
            return;
        }
        SetState(state);
    }

    internal void ForwardError(RtspSessionErrorEventArgs args)
    {
        if (Volatile.Read(ref _started) == 1)
        {
            Error?.Invoke(this, args);
        }
    }

    internal void ForwardFrame(RtspVideoFrame frame)
    {
        if (Volatile.Read(ref _started) == 1)
        {
            FrameReceived?.Invoke(this, frame);
        }
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
            SetState(RtspSessionState.Stopped);
        }
        catch
        {
            Volatile.Write(ref _started, 1);
            throw;
        }
    }

    private void SetState(RtspSessionState state)
    {
        var oldState = _state;
        if (oldState == state)
        {
            return;
        }

        _state = state;
        StateChanged?.Invoke(this, new RtspSessionStateChangedEventArgs(oldState, state));
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
}
