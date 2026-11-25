using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FrameFlux.Capture;

/// <summary>
/// Local USB camera and capture device implementation of <see cref="IMediaPlayer"/>.
/// </summary>
public sealed class CaptureMediaPlayer : IMediaPlayer
{
    private readonly CapturePlayerOptions _playerOptions;
    private readonly CaptureSessionPool _sessionPool;
    private readonly Func<CaptureFrameBufferPool, ICaptureBackend> _backendFactory;
    private readonly Func<string?, MediaOpenOptions, double, bool, ICaptureAudioLoopback?> _audioFactory;
    private readonly Func<int, string?>? _deviceIdResolver;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _commands = new(1, 1);
    private readonly Stopwatch _playbackStopwatch = new();
    private MediaSource? _source;
    private MediaOpenOptions _options = new();
    private MediaPlaybackState _state = MediaPlaybackState.Idle;
    private IMediaVideoOutput? _videoOutput;
    private CaptureSessionLease? _sessionLease;
    private CaptureSessionSubscriber? _subscriber;
    private MediaSnapshot? _latestSnapshot;
    private EventHandler<MediaVideoFrame>? _frameReceived;
    private TaskCompletionSource<MediaSnapshot?>? _pendingSnapshotTcs;
    private double _volume = 1d;
    private bool _isMuted;
    private long _frameSequence;
    private bool _disposed;

    public CaptureMediaPlayer(CapturePlayerOptions? options = null)
        : this(
            options,
            CaptureSessionPool.Shared,
            CreatePlatformBackend,
            CreatePlatformAudioLoopback,
            ResolvePlatformDeviceIdByIndex)
    {
    }

    internal CaptureMediaPlayer(
        CapturePlayerOptions? options,
        CaptureSessionPool sessionPool,
        Func<CaptureFrameBufferPool, ICaptureBackend> backendFactory,
        Func<string?, MediaOpenOptions, double, bool, ICaptureAudioLoopback?> audioFactory,
        Func<int, string?>? deviceIdResolver = null)
    {
        _playerOptions = options ?? new CapturePlayerOptions();
        _playerOptions.Validate();
        _sessionPool = sessionPool;
        _backendFactory = backendFactory;
        _audioFactory = audioFactory;
        _deviceIdResolver = deviceIdResolver;
    }

    public MediaSource? Source
    {
        get
        {
            lock (_sync)
            {
                return _source;
            }
        }
    }

    public MediaOpenOptions Options
    {
        get
        {
            lock (_sync)
            {
                return _options;
            }
        }
    }

    public MediaPlaybackState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    public MediaCapabilities Capabilities { get; } = new(
        IsLive: true,
        CanPause: false,
        CanSeek: false,
        CanChangePlaybackRate: false,
        CanCaptureSnapshots: true);

    public MediaDiagnostics Diagnostics
    {
        get
        {
            CaptureSessionLease? lease;
            lock (_sync)
            {
                lease = _sessionLease;
            }

            return lease?.Session.Diagnostics ?? MediaDiagnostics.Empty;
        }
    }

    public double PlaybackRate
    {
        get => 1d;
        set
        {
            if (value != 1d)
            {
                throw new NotSupportedException("Live capture sources do not support playback-rate changes.");
            }
        }
    }

    public double Volume
    {
        get
        {
            lock (_sync)
            {
                return _volume;
            }
        }
        set
        {
            if (value is < 0d or > 1d || double.IsNaN(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Volume must be between 0.0 and 1.0.");
            }

            CaptureSessionLease? lease;
            bool muted;
            lock (_sync)
            {
                RuntimeCompat.NotDisposed(_disposed, this);
                _volume = value;
                muted = _isMuted;
                lease = _sessionLease;
            }

            lease?.Session.UpdateAudioLevels(value, muted);
        }
    }

    public bool IsMuted
    {
        get
        {
            lock (_sync)
            {
                return _isMuted;
            }
        }
        set
        {
            CaptureSessionLease? lease;
            double volume;
            lock (_sync)
            {
                RuntimeCompat.NotDisposed(_disposed, this);
                _isMuted = value;
                volume = _volume;
                lease = _sessionLease;
            }

            lease?.Session.UpdateAudioLevels(volume, value);
        }
    }

    public IMediaVideoOutput? VideoOutput
    {
        get
        {
            lock (_sync)
            {
                return _videoOutput;
            }
        }
        set
        {
            lock (_sync)
            {
                RuntimeCompat.NotDisposed(_disposed, this);
                _videoOutput = value;
            }
        }
    }

    public TimeSpan Position => _playbackStopwatch.Elapsed;

    public TimeSpan? Duration => null;

    public event EventHandler<MediaPlaybackStateChangedEventArgs>? StateChanged;

    public event EventHandler<MediaPlaybackErrorEventArgs>? Error;

    public event EventHandler<MediaVideoFrame>? FrameReceived
    {
        add
        {
            lock (_sync)
            {
                _frameReceived += value;
            }
        }
        remove
        {
            lock (_sync)
            {
                _frameReceived -= value;
            }
        }
    }

    public async ValueTask OpenAsync(
        MediaSource source,
        MediaOpenOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        RuntimeCompat.NotNull(source, nameof(source));
        var resolvedOptions = options ?? new MediaOpenOptions();
        resolvedOptions.Validate();

        var parsed = CaptureSource.Parse(source.Uri);

        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RuntimeCompat.NotDisposed(_disposed, this);
            await ReleaseSessionAsync().ConfigureAwait(false);

            var subscriber = new CaptureSessionSubscriber(
                getVideoOutput: () => VideoOutput,
                getDecodingPolicy: () => Options.Video.DecodingPolicy,
                needsCpuFrame: NeedsCpuFrameCopy,
                onFrame: OnSessionFrame,
                onStateChanged: TransitionTo,
                onError: OnSessionError);

            CaptureSessionLease lease;
            if (resolvedOptions.SessionSharing == MediaSessionSharingMode.Shared)
            {
                string? resolvedDeviceId = parsed.DeviceId;
                if (string.IsNullOrWhiteSpace(resolvedDeviceId) && _deviceIdResolver is not null)
                {
                    try
                    {
                        resolvedDeviceId = _deviceIdResolver(parsed.DeviceIndex ?? 0);
                    }
                    catch
                    {
                        resolvedDeviceId = null;
                    }
                }

                var key = CaptureSessionKey.Create(parsed, resolvedOptions, resolvedDeviceId);
                lease = _sessionPool.Acquire(
                    key,
                    () => new CapturePhysicalSession(
                        parsed,
                        resolvedOptions,
                        _playerOptions,
                        _backendFactory,
                        _audioFactory));
            }
            else
            {
                var dedicatedSession = new CapturePhysicalSession(
                    parsed,
                    resolvedOptions,
                    _playerOptions,
                    _backendFactory,
                    _audioFactory);
                dedicatedSession.AddPoolReference();
                lease = new CaptureSessionLease(dedicatedSession, dedicatedSession.DisposeAsync);
            }

            lock (_sync)
            {
                _source = source;
                _options = resolvedOptions;
                _sessionLease = lease;
                _subscriber = subscriber;
                _latestSnapshot = null;
                _frameSequence = 0;
            }

            _playbackStopwatch.Reset();

            try
            {
                await lease.Session.OpenAsync(subscriber, _volume, _isMuted, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await ReleaseSessionAsync().ConfigureAwait(false);
                TransitionTo(MediaPlaybackState.Faulted);
                throw;
            }
        }
        finally
        {
            _commands.Release();
        }
    }

    public async ValueTask PlayAsync(CancellationToken cancellationToken = default)
    {
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RuntimeCompat.NotDisposed(_disposed, this);
            CaptureSessionLease lease;
            CaptureSessionSubscriber subscriber;
            double volume;
            bool muted;
            lock (_sync)
            {
                lease = _sessionLease ?? throw new InvalidOperationException("Open a capture source before calling PlayAsync.");
                subscriber = _subscriber ?? throw new InvalidOperationException("Capture session subscriber is missing.");
                volume = _volume;
                muted = _isMuted;
            }

            _playbackStopwatch.Start();
            await lease.Session.StartAsync(subscriber, volume, muted, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _commands.Release();
        }
    }

    public ValueTask PauseAsync(CancellationToken cancellationToken = default) =>
        RuntimeCompat.Failure(new NotSupportedException("Live capture sources do not support pausing."));

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _playbackStopwatch.Reset();
            await ReleaseSessionAsync().ConfigureAwait(false);
            TransitionTo(MediaPlaybackState.Stopped);
        }
        finally
        {
            _commands.Release();
        }
    }

    public ValueTask SeekAsync(TimeSpan position, CancellationToken cancellationToken = default) =>
        RuntimeCompat.Failure(new NotSupportedException("Live capture sources do not support seeking."));

    public async ValueTask<MediaSnapshot?> CaptureSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Task<MediaSnapshot?>? waitTask = null;
        lock (_sync)
        {
            if (_latestSnapshot is { } existing)
            {
                return existing with { Data = existing.Data.ToArray() };
            }

            if (_state != MediaPlaybackState.Playing)
            {
                return null;
            }

            _pendingSnapshotTcs ??= new TaskCompletionSource<MediaSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
            waitTask = _pendingSnapshotTcs.Task;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(500));
        try
        {
            var captured = await waitTask.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            return captured is null ? null : captured with { Data = captured.Data.ToArray() };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _pendingSnapshotTcs?.TrySetResult(null);
            _pendingSnapshotTcs = null;
        }

        await _commands.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            _playbackStopwatch.Reset();
            await ReleaseSessionAsync().ConfigureAwait(false);
        }
        finally
        {
            _commands.Release();
            _commands.Dispose();
        }
    }

    private bool NeedsCpuFrameCopy()
    {
        lock (_sync)
        {
            return _frameReceived is not null ||
                   _options.Video.SnapshotPolicy == MediaSnapshotPolicy.KeepLatestFrame ||
                   _pendingSnapshotTcs is not null;
        }
    }

    private void OnSessionFrame(CaptureMediaFrameLease? cpuLease, int width, int height)
    {
        if (cpuLease is null || !cpuLease.TryGetCpuBuffer(out var cpuBuffer) || cpuBuffer.Plane0 == IntPtr.Zero)
        {
            return;
        }

        var rowBytes = checked(width * 4);
        var totalBytes = checked(rowBytes * height);
        if (totalBytes <= 0 || cpuBuffer.Size < totalBytes)
        {
            return;
        }

        var managedCopy = new byte[totalBytes];
        unsafe
        {
            var src = (byte*)cpuBuffer.Plane0;
            for (var y = 0; y < height; y++)
            {
                Marshal.Copy((IntPtr)(src + ((long)y * cpuBuffer.Plane0Stride)), managedCopy, y * rowBytes, rowBytes);
            }
        }

        var capturedAt = DateTimeOffset.UtcNow;
        var sequence = Interlocked.Increment(ref _frameSequence);

        EventHandler<MediaVideoFrame>? handlers;
        TaskCompletionSource<MediaSnapshot?>? snapshotTcs = null;
        MediaSnapshot? snapshot = null;

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            handlers = _frameReceived;
            if (_options.Video.SnapshotPolicy == MediaSnapshotPolicy.KeepLatestFrame ||
                _pendingSnapshotTcs is not null)
            {
                snapshot = new MediaSnapshot(
                    managedCopy,
                    width,
                    height,
                    rowBytes,
                    MediaPixelFormat.Bgra32,
                    capturedAt);
                _latestSnapshot = snapshot;
                snapshotTcs = _pendingSnapshotTcs;
                _pendingSnapshotTcs = null;
            }
        }

        snapshotTcs?.TrySetResult(snapshot);

        if (handlers is not null)
        {
            handlers.Invoke(
                this,
                new MediaVideoFrame(
                    managedCopy,
                    width,
                    height,
                    rowBytes,
                    MediaPixelFormat.Bgra32,
                    sequence,
                    capturedAt));
        }
    }

    private async Task ReleaseSessionAsync()
    {
        CaptureSessionLease? lease;
        CaptureSessionSubscriber? subscriber;
        lock (_sync)
        {
            lease = _sessionLease;
            _sessionLease = null;
            subscriber = _subscriber;
            _subscriber = null;
            _latestSnapshot = null;
            _pendingSnapshotTcs?.TrySetResult(null);
            _pendingSnapshotTcs = null;
        }

        if (lease is not null)
        {
            if (subscriber is not null)
            {
                await lease.Session.DetachAndMaybeStopAsync(
                    subscriber,
                    stopIfEmpty: _options.SessionSharing == MediaSessionSharingMode.Dedicated)
                    .ConfigureAwait(false);
            }

            await lease.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void TransitionTo(MediaPlaybackState newState)
    {
        MediaPlaybackState oldState;
        lock (_sync)
        {
            oldState = _state;
            if (oldState == newState)
            {
                return;
            }

            _state = newState;
            if (newState is MediaPlaybackState.Stopped or MediaPlaybackState.Faulted)
            {
                _playbackStopwatch.Stop();
            }
        }

        StateChanged?.Invoke(this, new MediaPlaybackStateChangedEventArgs(oldState, newState));
    }

    private void OnSessionError(MediaPlaybackError error) =>
        Error?.Invoke(this, new MediaPlaybackErrorEventArgs(error));

    private static ICaptureBackend CreatePlatformBackend(CaptureFrameBufferPool pool)
    {
#if !ANDROID
        if (OperatingSystem.IsWindows())
        {
            return new WindowsMediaFoundationCaptureBackend(pool);
        }
#endif
        throw new PlatformNotSupportedException("Local USB capture devices are currently supported on Windows only.");
    }

    private static ICaptureAudioLoopback? CreatePlatformAudioLoopback(
        string? audioDeviceId,
        MediaOpenOptions options,
        double volume,
        bool isMuted)
    {
#if !ANDROID
        if (OperatingSystem.IsWindows())
        {
            return new WindowsWasapiAudioBridge(
                audioDeviceId,
                options.Audio.OutputDeviceId,
                options.Audio.BufferDuration,
                volume,
                isMuted,
                options.Audio.GainDecibels);
        }
#endif
        return null;
    }

    private static string? ResolvePlatformDeviceIdByIndex(int index)
    {
#if !ANDROID
        if (OperatingSystem.IsWindows() && index >= 0)
        {
            var devices = WindowsMediaFoundationEnumerator.GetVideoDevices(includeFormats: false);
            if (index < devices.Count)
            {
                return devices[index].DeviceId;
            }
        }
#endif
        return null;
    }
}
