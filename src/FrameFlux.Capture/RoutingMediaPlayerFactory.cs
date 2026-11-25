namespace FrameFlux.Capture;

/// <summary>
/// Associates a source predicate with an <see cref="IMediaPlayerFactory"/>.
/// </summary>
public sealed record MediaPlayerRoute(
    Func<MediaSource, bool> Predicate,
    IMediaPlayerFactory Factory);

/// <summary>
/// Routes <see cref="MediaSource"/> playback to matching <see cref="IMediaPlayerFactory"/> implementations
/// (for example <c>capture://</c>, WebRTC, and FFmpeg sources) behind a single <see cref="IMediaPlayerFactory"/>.
/// </summary>
public sealed class RoutingMediaPlayerFactory : IMediaPlayerFactory
{
    private readonly IMediaPlayerFactory _fallbackFactory;
    private readonly IReadOnlyList<MediaPlayerRoute> _routes;

    public RoutingMediaPlayerFactory(
        IMediaPlayerFactory fallbackFactory,
        params MediaPlayerRoute[] routes)
    {
        RuntimeCompat.NotNull(fallbackFactory, nameof(fallbackFactory));
        RuntimeCompat.NotNull(routes, nameof(routes));
        _fallbackFactory = fallbackFactory;
        _routes = [.. routes];
    }

    public IMediaPlayer Create() => new RoutingMediaPlayer(_fallbackFactory, _routes);

    private sealed class RoutingMediaPlayer : IMediaPlayer
    {
        private readonly IMediaPlayerFactory _fallbackFactory;
        private readonly IReadOnlyList<MediaPlayerRoute> _routes;
        private readonly object _sync = new();
        private readonly SemaphoreSlim _commands = new(1, 1);
        private IMediaPlayer? _innerPlayer;
        private IMediaPlayerFactory? _innerFactory;
        private IMediaVideoOutput? _videoOutput;
        private double _playbackRate = 1d;
        private double _volume = 1d;
        private bool _isMuted;
        private bool _disposed;

        internal RoutingMediaPlayer(
            IMediaPlayerFactory fallbackFactory,
            IReadOnlyList<MediaPlayerRoute> routes)
        {
            _fallbackFactory = fallbackFactory;
            _routes = routes;
        }

        public MediaSource? Source => CurrentPlayer?.Source;

        public MediaOpenOptions Options => CurrentPlayer?.Options ?? new MediaOpenOptions();

        public MediaPlaybackState State => CurrentPlayer?.State ?? MediaPlaybackState.Idle;

        public MediaCapabilities Capabilities => CurrentPlayer?.Capabilities ?? MediaCapabilities.None;

        public MediaDiagnostics Diagnostics => CurrentPlayer?.Diagnostics ?? MediaDiagnostics.Empty;

        public double PlaybackRate
        {
            get => CurrentPlayer?.PlaybackRate ?? _playbackRate;
            set
            {
                _playbackRate = value;
                if (CurrentPlayer is { } player)
                {
                    player.PlaybackRate = value;
                }
            }
        }

        public double Volume
        {
            get => CurrentPlayer?.Volume ?? _volume;
            set
            {
                _volume = value;
                if (CurrentPlayer is { } player)
                {
                    player.Volume = value;
                }
            }
        }

        public bool IsMuted
        {
            get => CurrentPlayer?.IsMuted ?? _isMuted;
            set
            {
                _isMuted = value;
                if (CurrentPlayer is { } player)
                {
                    player.IsMuted = value;
                }
            }
        }

        public IMediaVideoOutput? VideoOutput
        {
            get
            {
                lock (_sync)
                {
                    return _innerPlayer?.VideoOutput ?? _videoOutput;
                }
            }
            set
            {
                lock (_sync)
                {
                    _videoOutput = value;
                    if (_innerPlayer is not null)
                    {
                        _innerPlayer.VideoOutput = value;
                    }
                }
            }
        }

        public TimeSpan Position => CurrentPlayer?.Position ?? TimeSpan.Zero;

        public TimeSpan? Duration => CurrentPlayer?.Duration;

        public event EventHandler<MediaPlaybackStateChangedEventArgs>? StateChanged;

        public event EventHandler<MediaPlaybackErrorEventArgs>? Error;

        public event EventHandler<MediaVideoFrame>? FrameReceived;

        private IMediaPlayer? CurrentPlayer
        {
            get
            {
                lock (_sync)
                {
                    return _innerPlayer;
                }
            }
        }

        public async ValueTask OpenAsync(
            MediaSource source,
            MediaOpenOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            RuntimeCompat.NotNull(source, nameof(source));
            await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                RuntimeCompat.NotDisposed(_disposed, this);
                var targetFactory = ResolveFactory(source);
                IMediaPlayer? playerToDispose = null;
                IMediaPlayer targetPlayer;

                lock (_sync)
                {
                    if (_innerPlayer is not null && ReferenceEquals(_innerFactory, targetFactory))
                    {
                        targetPlayer = _innerPlayer;
                    }
                    else
                    {
                        playerToDispose = _innerPlayer;
                        _innerPlayer = null;
                        _innerFactory = null;

                        targetPlayer = targetFactory.Create();
                        targetPlayer.VideoOutput = _videoOutput;
                        targetPlayer.Volume = _volume;
                        targetPlayer.IsMuted = _isMuted;
                        AttachEvents(targetPlayer);
                        _innerPlayer = targetPlayer;
                        _innerFactory = targetFactory;
                    }
                }

                if (playerToDispose is not null)
                {
                    DetachEvents(playerToDispose);
                    await playerToDispose.DisposeAsync().ConfigureAwait(false);
                }

                await targetPlayer.OpenAsync(source, options, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _commands.Release();
            }
        }

        public ValueTask PlayAsync(CancellationToken cancellationToken = default) =>
            RequirePlayer().PlayAsync(cancellationToken);

        public ValueTask PauseAsync(CancellationToken cancellationToken = default) =>
            RequirePlayer().PauseAsync(cancellationToken);

        public ValueTask StopAsync(CancellationToken cancellationToken = default) =>
            CurrentPlayer?.StopAsync(cancellationToken) ?? default;

        public ValueTask SeekAsync(TimeSpan position, CancellationToken cancellationToken = default) =>
            RequirePlayer().SeekAsync(position, cancellationToken);

        public ValueTask<MediaSnapshot?> CaptureSnapshotAsync(CancellationToken cancellationToken = default) =>
            CurrentPlayer?.CaptureSnapshotAsync(cancellationToken) ??
            RuntimeCompat.Result<MediaSnapshot?>(null);

        public async ValueTask DisposeAsync()
        {
            IMediaPlayer? player;
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                player = _innerPlayer;
                _innerPlayer = null;
                _innerFactory = null;
            }

            if (player is not null)
            {
                DetachEvents(player);
                await player.DisposeAsync().ConfigureAwait(false);
            }

            _commands.Dispose();
        }

        private IMediaPlayerFactory ResolveFactory(MediaSource source)
        {
            foreach (var route in _routes)
            {
                if (route.Predicate(source))
                {
                    return route.Factory;
                }
            }

            return _fallbackFactory;
        }

        private IMediaPlayer RequirePlayer()
        {
            lock (_sync)
            {
                RuntimeCompat.NotDisposed(_disposed, this);
                return _innerPlayer ?? throw new InvalidOperationException("Open a media source first.");
            }
        }

        private void AttachEvents(IMediaPlayer player)
        {
            player.StateChanged += OnInnerStateChanged;
            player.Error += OnInnerError;
            player.FrameReceived += OnInnerFrameReceived;
        }

        private void DetachEvents(IMediaPlayer player)
        {
            player.StateChanged -= OnInnerStateChanged;
            player.Error -= OnInnerError;
            player.FrameReceived -= OnInnerFrameReceived;
        }

        private void OnInnerStateChanged(object? sender, MediaPlaybackStateChangedEventArgs e) =>
            StateChanged?.Invoke(this, e);

        private void OnInnerError(object? sender, MediaPlaybackErrorEventArgs e) =>
            Error?.Invoke(this, e);

        private void OnInnerFrameReceived(object? sender, MediaVideoFrame e) =>
            FrameReceived?.Invoke(this, e);
    }
}
