using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;

namespace FrameFlux.FFmpeg;

public sealed class FfmpegMediaPlayer : IMediaPlayer
{
    private readonly object _sync = new();
    private readonly object _disposeSync = new();
    private Task? _disposeTask;
    private readonly SemaphoreSlim _commands = new(1, 1);
    private readonly IFfmpegMediaSessionFactory _sessionFactory;
    private IFfmpegMediaSession? _session;
    private MediaSource? _source;
    private MediaOpenOptions _options = new();
    private MediaPlaybackState _state = MediaPlaybackState.Idle;
    private MediaCapabilities _capabilities = MediaCapabilities.None;
    private MediaDiagnostics _diagnostics = MediaDiagnostics.Empty;
    private TimeSpan _position;
    private TimeSpan? _duration;
    private double _playbackRate = 1d;
    private double _volume = 1d;
    private bool _isMuted;
    private IMediaVideoOutput? _videoOutput;
    private EventHandler<MediaVideoFrame>? _frameReceived;
    private volatile EventHandler<MediaVideoFrame>[] _frameReceivedSnapshot = [];
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

    public double PlaybackRate
    {
        get
        {
            lock (_sync)
            {
                return _playbackRate;
            }
        }
        set
        {
            MediaPlaybackClock.ValidateRate(value);
            IFfmpegMediaSession? session;
            lock (_sync)
            {
                ThrowIfDisposed();
                if (_source is not null && !FfmpegSource.IsSeekable(_source.Uri) && value != 1d)
                {
                    throw new NotSupportedException(
                        "Live sources do not support playback-rate changes.");
                }

                _playbackRate = value;
                session = _session;
            }

            if (session is not null)
            {
                session.PlaybackRate = value;
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

            IFfmpegMediaSession? session;
            lock (_sync)
            {
                ThrowIfDisposed();
                _volume = value;
                session = _session;
            }

            if (session is not null)
            {
                session.Volume = value;
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
            IFfmpegMediaSession? session;
            lock (_sync)
            {
                ThrowIfDisposed();
                _isMuted = value;
                session = _session;
            }

            if (session is not null)
            {
                session.IsMuted = value;
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

    public TimeSpan Position
    {
        get
        {
            IFfmpegMediaSession? session;
            TimeSpan position;
            lock (_sync)
            {
                session = _session;
                position = _position;
            }

            return session?.Position ?? position;
        }
    }

    public TimeSpan? Duration
    {
        get
        {
            IFfmpegMediaSession? session;
            TimeSpan? duration;
            lock (_sync)
            {
                session = _session;
                duration = _duration;
            }

            return session?.Duration ?? duration;
        }
    }

    public event EventHandler<MediaPlaybackStateChangedEventArgs>? StateChanged;

    public event EventHandler<MediaPlaybackErrorEventArgs>? Error;

    public event EventHandler<MediaVideoFrame>? FrameReceived
    {
        add
        {
            IFfmpegMediaSession? sessionToSubscribe = null;
            lock (_sync)
            {
                var subscribe = _frameReceived is null;
                _frameReceived += value;
                _frameReceivedSnapshot = BuildFrameSubscribersSnapshot(_frameReceived);
                if (subscribe &&
                    _frameReceived is not null &&
                    _session is not null)
                {
                    sessionToSubscribe = _session;
                }
            }

            if (sessionToSubscribe is not null)
            {
                sessionToSubscribe.FrameReceived += OnFrameReceived;
            }
        }
        remove
        {
            IFfmpegMediaSession? sessionToUnsubscribe = null;
            lock (_sync)
            {
                _frameReceived -= value;
                _frameReceivedSnapshot = BuildFrameSubscribersSnapshot(_frameReceived);
                if (_frameReceived is null && _session is not null)
                {
                    sessionToUnsubscribe = _session;
                }
            }

            if (sessionToUnsubscribe is not null)
            {
                sessionToUnsubscribe.FrameReceived -= OnFrameReceived;
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

        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await StopSessionCoreAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var isFile = source.Uri.IsFile;
            if (!FfmpegSource.IsSupported(source.Uri))
            {
                throw new NotSupportedException(
                    $"The FFmpeg backend does not support the '{source.Uri.Scheme}' media scheme.");
            }
            if (isFile && !File.Exists(source.Uri.LocalPath))
            {
                throw new FileNotFoundException("The media file does not exist.", source.Uri.LocalPath);
            }
            var isSeekable = FfmpegSource.IsSeekable(source.Uri);
            if (isSeekable && resolvedOptions.SessionSharing == MediaSessionSharingMode.Shared)
            {
                throw new NotSupportedException("Seekable media playback requires a dedicated media session.");
            }

            lock (_sync)
            {
                var usesGpuFrames =
                    _videoOutput?.PreferredFrameStorage == MediaFrameStorageKind.D3D11Texture;
                _source = source;
                _options = resolvedOptions;
                if (!isSeekable)
                {
                    _playbackRate = 1d;
                }
                _capabilities = new MediaCapabilities(
                    IsLive: !isSeekable,
                    CanPause: isSeekable,
                    CanSeek: isSeekable,
                    CanChangePlaybackRate: isSeekable,
                    CanCaptureSnapshots:
                        resolvedOptions.Video.SnapshotPolicy == MediaSnapshotPolicy.KeepLatestFrame &&
                        !usesGpuFrames);
                _diagnostics = MediaDiagnostics.Empty;
                _position = TimeSpan.Zero;
                _duration = null;
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
            IFfmpegMediaSession? pausedSession;
            lock (_sync)
            {
                pausedSession = _state == MediaPlaybackState.Paused ? _session : null;
            }
            if (pausedSession is not null)
            {
                await pausedSession.SetPausedAsync(false, cancellationToken).ConfigureAwait(false);
                return;
            }

            MediaSource source;
            MediaOpenOptions options;
            TimeSpan initialPosition;
            double volume;
            bool muted;
            double playbackRate;
            IMediaVideoOutput? videoOutput;
            lock (_sync)
            {
                if (_state is MediaPlaybackState.Playing or
                    MediaPlaybackState.Buffering or
                    MediaPlaybackState.Opening or
                    MediaPlaybackState.Reconnecting)
                {
                    return;
                }

                source = _source ??
                    throw new InvalidOperationException("Open a media source before starting playback.");
                options = _options;
                initialPosition = _position;
                volume = _volume;
                muted = _isMuted;
                playbackRate = _playbackRate;
                videoOutput = _videoOutput;
            }

            await StopSessionCoreAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            // Avoid forcing per-frame GPU->CPU readback + LOH copies when snapshots are disabled on GPU output.
            var sessionOptions =
                !_capabilities.CanCaptureSnapshots &&
                options.Video.SnapshotPolicy == MediaSnapshotPolicy.KeepLatestFrame
                    ? options with
                    {
                        Video = options.Video with { SnapshotPolicy = MediaSnapshotPolicy.Disabled }
                    }
                    : options;
            var session = _sessionFactory.Create(source, sessionOptions, volume, muted, videoOutput);
            session.PlaybackRate = playbackRate;
            session.StateChanged += OnSessionStateChanged;
            session.Error += OnSessionError;
            lock (_sync)
            {
                if (_frameReceived is not null)
                {
                    session.FrameReceived += OnFrameReceived;
                }
                _session = session;
            }

            TransitionTo(MediaPlaybackState.Opening);
            try
            {
                await session.StartAsync(cancellationToken).ConfigureAwait(false);
                if (initialPosition > TimeSpan.Zero && FfmpegSource.IsSeekable(source.Uri))
                {
                    await session.SeekAsync(initialPosition, cancellationToken).ConfigureAwait(false);
                }
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

    public async ValueTask PauseAsync(CancellationToken cancellationToken = default)
    {
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            IFfmpegMediaSession? session;
            bool canPause;
            lock (_sync)
            {
                canPause = _capabilities.CanPause;
                session = _session;
            }
            if (!canPause)
            {
                throw new NotSupportedException("The current media source does not support pause.");
            }
            if (session is null || State == MediaPlaybackState.Paused)
            {
                return;
            }

            await session.SetPausedAsync(true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _commands.Release();
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            TransitionTo(MediaPlaybackState.Stopping);
            try
            {
                await StopSessionCoreAsync().ConfigureAwait(false);
            }
            finally
            {
                TransitionTo(MediaPlaybackState.Stopped);
            }
        }
        finally
        {
            _commands.Release();
        }
    }

    public async ValueTask SeekAsync(
        TimeSpan position,
        CancellationToken cancellationToken = default)
    {
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            IFfmpegMediaSession? session;
            TimeSpan? duration;
            bool canSeek;
            lock (_sync)
            {
                canSeek = _capabilities.CanSeek;
                duration = _session?.Duration ?? _duration;
                session = _session;
            }

            if (!canSeek)
            {
                throw new NotSupportedException("The current media source does not support seeking.");
            }
            if (position < TimeSpan.Zero || duration is { } knownDuration && position > knownDuration)
            {
                throw new ArgumentOutOfRangeException(nameof(position));
            }

            if (session is not null)
            {
                try
                {
                    await session.SeekAsync(position, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    _source is { Uri: { } uri } && FfmpegSource.IsHttpMedia(uri) &&
                    exception is not OperationCanceledException)
                {
                    lock (_sync)
                    {
                        _capabilities = _capabilities with { CanSeek = false };
                    }
                    PublishError(new MediaPlaybackError(
                        "SeekFailed",
                        $"HTTP media seeking failed: {exception.Message}",
                        IsRecoverable: true,
                        exception));
                    throw;
                }
            }
            lock (_sync)
            {
                _position = position;
                _duration ??= session?.Duration;
            }
        }
        finally
        {
            _commands.Release();
        }
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

    public ValueTask DisposeAsync()
    {
        lock (_disposeSync) return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
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

        Exception? failure = null;
        try
        {
            await session.StopAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        try
        {
            lock (_sync)
            {
                _position = session.Position;
                _duration = session.Duration ?? _duration;
                _diagnostics = session.Diagnostics;
            }
        }
        catch (Exception exception)
        {
            PreserveFirstFailure(ref failure, exception, "reading session diagnostics");
        }

        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            PreserveFirstFailure(ref failure, exception, "disposing the media session");
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
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
        if (!ReferenceEquals(Volatile.Read(ref _session), sender))
        {
            return;
        }

        var subscribers = _frameReceivedSnapshot;
        for (var i = 0; i < subscribers.Length; i++)
        {
            try
            {
                subscribers[i](this, frame);
            }
            catch (Exception exception)
            {
                Trace.TraceWarning(
                    "A media frame subscriber failed: {0}",
                    exception);
            }
        }
    }

    private static EventHandler<MediaVideoFrame>[] BuildFrameSubscribersSnapshot(
        EventHandler<MediaVideoFrame>? handlers)
    {
        if (handlers is null)
        {
            return [];
        }

        var list = handlers.GetInvocationList();
        var result = new EventHandler<MediaVideoFrame>[list.Length];
        for (var i = 0; i < list.Length; i++)
        {
            result[i] = (EventHandler<MediaVideoFrame>)list[i];
        }

        return result;
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

        PublishEvent(
            StateChanged,
            new MediaPlaybackStateChangedEventArgs(oldState, state),
            "state");
    }

    private void PublishError(MediaPlaybackError error)
    {
        lock (_sync)
        {
            _diagnostics = _diagnostics with { LastError = error.Message };
        }
        PublishEvent(
            Error,
            new MediaPlaybackErrorEventArgs(error),
            "error");
    }

    private void PublishEvent<TEventArgs>(
        EventHandler<TEventArgs>? subscribers,
        TEventArgs args,
        string eventName)
    {
        if (subscribers is null)
        {
            return;
        }

        foreach (EventHandler<TEventArgs> subscriber in subscribers.GetInvocationList())
        {
            try
            {
                subscriber(this, args);
            }
            catch (Exception exception)
            {
                Trace.TraceWarning(
                    "A media {0} subscriber failed: {1}",
                    eventName,
                    exception);
            }
        }
    }

    private static void PreserveFirstFailure(
        ref Exception? failure,
        Exception candidate,
        string operation)
    {
        if (failure is null)
        {
            failure = candidate;
            return;
        }

        Trace.TraceWarning(
            "A secondary failure occurred while {0}: {1}",
            operation,
            candidate);
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

    public FfmpegMediaPlayerFactory(
        FfmpegMediaPlayerFactoryOptions? options = null,
        ILoggerFactory? loggerFactory = null)
    {
        _sessionFactory = new FfmpegMediaSessionFactory(loggerFactory, options);
    }

    public IMediaPlayer Create() => new FfmpegMediaPlayer(_sessionFactory);
}

public sealed record FfmpegMediaPlayerFactoryOptions
{
    /// <summary>Creates a player-owned PCM sink. Called on the decode worker, once per decoder session.</summary>
    /// <remarks>Custom outputs require dedicated sessions. A fresh output must be returned on every invocation.</remarks>
    public Func<int, int, IMediaAudioOutput>? AudioOutputFactory { get; init; }

    public int? MaximumConcurrentOpenOperations { get; init; } =
        CalculateRecommendedMaximumConcurrentOpenOperations(Environment.ProcessorCount);

    internal static int CalculateRecommendedMaximumConcurrentOpenOperations(
        int processorCount)
    {
        RuntimeCompat.AtLeast(processorCount, 1, nameof(processorCount));
        return Math.Clamp((processorCount + 3) / 4, 2, 4);
    }

    internal void Validate()
    {
        if (MaximumConcurrentOpenOperations is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumConcurrentOpenOperations),
                MaximumConcurrentOpenOperations,
                "Maximum concurrent open operations must be greater than zero.");
        }
    }
}
