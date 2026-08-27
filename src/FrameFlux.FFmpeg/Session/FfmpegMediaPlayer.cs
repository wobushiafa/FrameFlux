using Microsoft.Extensions.Logging;

namespace FrameFlux.FFmpeg;

public sealed class FfmpegMediaPlayer : IMediaPlayer
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _commands = new(1, 1);
    private readonly IFfmpegMediaSessionFactory _sessionFactory;
    private IFfmpegMediaSession? _session;
    private MediaSource? _source;
    private MediaOpenOptions _options = new();
    private MediaPlaybackState _state = MediaPlaybackState.Idle;
    private MediaCapabilities _capabilities = MediaCapabilities.None;
    private MediaDiagnostics _diagnostics = MediaDiagnostics.Empty;
    private double _volume = 1d;
    private bool _isMuted;
    private IMediaVideoOutput? _videoOutput;
    private bool _disposed;

    public FfmpegMediaPlayer(ILoggerFactory? loggerFactory = null)
        : this(new FfmpegMediaSessionFactory(loggerFactory))
    {
    }

    internal FfmpegMediaPlayer(IFfmpegMediaSessionFactory sessionFactory)
    {
        _sessionFactory = sessionFactory;
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

    public MediaCapabilities Capabilities
    {
        get
        {
            lock (_sync)
            {
                return _capabilities;
            }
        }
    }

    public MediaDiagnostics Diagnostics
    {
        get
        {
            IFfmpegMediaSession? session;
            MediaDiagnostics diagnostics;
            lock (_sync)
            {
                session = _session;
                diagnostics = _diagnostics;
            }

            return session is null ? diagnostics : session.Diagnostics;
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

            lock (_sync)
            {
                ThrowIfDisposed();
                _volume = value;
                if (_session is not null)
                {
                    _session.Volume = value;
                }
            }
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
            lock (_sync)
            {
                ThrowIfDisposed();
                _isMuted = value;
                if (_session is not null)
                {
                    _session.IsMuted = value;
                }
            }
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
                ThrowIfDisposed();
                if (_session is not null)
                {
                    throw new InvalidOperationException("Set the video output before starting playback.");
                }

                _videoOutput = value;
            }
        }
    }

    public TimeSpan Position => TimeSpan.Zero;

    public TimeSpan? Duration => null;

    public event EventHandler<MediaPlaybackStateChangedEventArgs>? StateChanged;

    public event EventHandler<MediaPlaybackErrorEventArgs>? Error;

    public event EventHandler<MediaVideoFrame>? FrameReceived;

    public async ValueTask OpenAsync(
        MediaSource source,
        MediaOpenOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var resolvedOptions = options ?? new MediaOpenOptions();
        resolvedOptions.Validate();

        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await StopSessionCoreAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (source.Uri.Scheme is not ("rtsp" or "rtsps"))
            {
                throw new NotSupportedException(
                    $"The current FFmpeg backend does not support the '{source.Uri.Scheme}' media scheme yet.");
            }

            lock (_sync)
            {
                var usesGpuFrames = _videoOutput?.Preference is
                    MediaRenderPreference.NativeSurface or
                    MediaRenderPreference.CompositedGpu;
                _source = source;
                _options = resolvedOptions;
                _capabilities = new MediaCapabilities(
                    IsLive: true,
                    CanPause: false,
                    CanSeek: false,
                    CanCaptureSnapshots: resolvedOptions.CaptureSnapshots && !usesGpuFrames);
                _diagnostics = MediaDiagnostics.Empty;
            }
            TransitionTo(MediaPlaybackState.Ready);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            PublishError(new MediaPlaybackError(
                "OpenFailed",
                exception.Message,
                IsRecoverable: false,
                exception));
            TransitionTo(MediaPlaybackState.Faulted);
            throw;
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
            ThrowIfDisposed();
            MediaSource source;
            MediaOpenOptions options;
            double volume;
            bool muted;
            IMediaVideoOutput? videoOutput;
            lock (_sync)
            {
                if (_state is MediaPlaybackState.Playing or
                    MediaPlaybackState.Opening or
                    MediaPlaybackState.Reconnecting)
                {
                    return;
                }

                source = _source ??
                    throw new InvalidOperationException("Open a media source before starting playback.");
                options = _options;
                volume = _volume;
                muted = _isMuted;
                videoOutput = _videoOutput;
            }

            await StopSessionCoreAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var session = _sessionFactory.Create(source, options, volume, muted, videoOutput);
            session.StateChanged += OnSessionStateChanged;
            session.Error += OnSessionError;
            session.FrameReceived += OnFrameReceived;
            lock (_sync)
            {
                _session = session;
            }

            TransitionTo(MediaPlaybackState.Opening);
            try
            {
                await session.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await StopSessionCoreAsync().ConfigureAwait(false);
                throw;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            PublishError(new MediaPlaybackError(
                "PlayFailed",
                exception.Message,
                IsRecoverable: false,
                exception));
            TransitionTo(MediaPlaybackState.Faulted);
            throw;
        }
        finally
        {
            _commands.Release();
        }
    }

    public ValueTask PauseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        throw new NotSupportedException("The current live RTSP source does not support pause.");
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            TransitionTo(MediaPlaybackState.Stopping);
            await StopSessionCoreAsync().ConfigureAwait(false);
            TransitionTo(MediaPlaybackState.Stopped);
        }
        finally
        {
            _commands.Release();
        }
    }

    public ValueTask SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (position < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(position), "Position cannot be negative.");
        }

        throw new NotSupportedException("The current live RTSP source does not support seeking.");
    }

    public async ValueTask<MediaSnapshot?> CaptureSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        IFfmpegMediaSession? session;
        lock (_sync)
        {
            ThrowIfDisposed();
            session = _session;
        }

        if (session is null || !Capabilities.CanCaptureSnapshots)
        {
            return null;
        }

        var snapshot = await session.CaptureSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return snapshot;
    }

    public async ValueTask DisposeAsync()
    {
        await _commands.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            await StopSessionCoreAsync().ConfigureAwait(false);
            lock (_sync)
            {
                _disposed = true;
                _state = MediaPlaybackState.Stopped;
            }
        }
        finally
        {
            _commands.Release();
        }

        _commands.Dispose();
        GC.SuppressFinalize(this);
    }

    private async ValueTask StopSessionCoreAsync()
    {
        IFfmpegMediaSession? session;
        lock (_sync)
        {
            session = _session;
            _session = null;
        }

        if (session is null)
        {
            return;
        }

        session.StateChanged -= OnSessionStateChanged;
        session.Error -= OnSessionError;
        session.FrameReceived -= OnFrameReceived;
        await session.StopAsync().ConfigureAwait(false);
        lock (_sync)
        {
            _diagnostics = session.Diagnostics;
        }
        await session.DisposeAsync().ConfigureAwait(false);
    }

    private void OnSessionStateChanged(object? sender, MediaPlaybackStateChangedEventArgs args)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_session, sender))
            {
                return;
            }
        }

        TransitionTo(args.NewState);
    }

    private void OnSessionError(object? sender, MediaPlaybackErrorEventArgs args)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_session, sender))
            {
                return;
            }
        }

        PublishError(args.Error);
    }

    private void OnFrameReceived(object? sender, MediaVideoFrame frame)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_session, sender))
            {
                return;
            }
        }

        FrameReceived?.Invoke(this, frame);
    }

    private void TransitionTo(MediaPlaybackState state)
    {
        MediaPlaybackState oldState;
        lock (_sync)
        {
            oldState = _state;
            if (oldState == state)
            {
                return;
            }
            _state = state;
        }

        StateChanged?.Invoke(this, new MediaPlaybackStateChangedEventArgs(oldState, state));
    }

    private void PublishError(MediaPlaybackError error)
    {
        lock (_sync)
        {
            _diagnostics = _diagnostics with { LastError = error.Message };
        }
        Error?.Invoke(this, new MediaPlaybackErrorEventArgs(error));
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(FfmpegMediaPlayer));
        }
    }

}

public sealed class FfmpegMediaPlayerFactory : IMediaPlayerFactory
{
    private readonly IFfmpegMediaSessionFactory _sessionFactory;

    public FfmpegMediaPlayerFactory(ILoggerFactory? loggerFactory = null)
    {
        _sessionFactory = new FfmpegMediaSessionFactory(loggerFactory);
    }

    public IMediaPlayer Create() => new FfmpegMediaPlayer(_sessionFactory);
}
