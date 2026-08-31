namespace FrameFlux.Presentation;

internal sealed class MediaPlaybackController : IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _eventSync = new();
    private readonly object _disposeSync = new();
    private IMediaPlayer? _player;
    private Task? _disposeTask;
    private EventHandler<MediaVideoFrame>? _frameReceived;
    private MediaPlaybackState _state = MediaPlaybackState.Idle;
    private MediaPlaybackError? _lastError;
    private MediaDiagnostics _diagnostics = MediaDiagnostics.Empty;
    private double _playbackRate = 1d;
    private double _volume = 1d;
    private bool _isMuted;
    private bool _disposed;

    public MediaPlaybackState State => _state;

    public MediaPlaybackError? LastError => _lastError;

    public MediaDiagnostics Diagnostics => _diagnostics;

    public bool HasPlayer => _player is not null;

    public TimeSpan Position => _player?.Position ?? TimeSpan.Zero;

    public TimeSpan? Duration => _player?.Duration;

    public MediaCapabilities Capabilities => _player?.Capabilities ?? MediaCapabilities.None;

    public double PlaybackRate
    {
        get => _player?.PlaybackRate ?? _playbackRate;
        set
        {
            if (!double.IsFinite(value) || value is < 0.5d or > 2d)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    value,
                    "Playback rate must be between 0.5 and 2.0.");
            }

            _playbackRate = value;
            if (_player is not null)
            {
                _player.PlaybackRate = value;
            }
        }
    }

    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0d, 1d);
            var player = _player;
            if (player is not null)
            {
                player.Volume = _volume;
            }
        }
    }

    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            _isMuted = value;
            var player = _player;
            if (player is not null)
            {
                player.IsMuted = value;
            }
        }
    }

    public event EventHandler<MediaPlaybackStateChangedEventArgs>? StateChanged;

    public event EventHandler<MediaPlaybackErrorEventArgs>? Error;

    public event EventHandler<MediaVideoFrame>? FrameReceived
    {
        add
        {
            lock (_eventSync)
            {
                var subscribe = _frameReceived is null;
                _frameReceived += value;
                if (subscribe && _player is not null)
                {
                    _player.FrameReceived += OnPlayerFrameReceived;
                }
            }
        }
        remove
        {
            lock (_eventSync)
            {
                _frameReceived -= value;
                if (_frameReceived is null && _player is not null)
                {
                    _player.FrameReceived -= OnPlayerFrameReceived;
                }
            }
        }
    }

    public async ValueTask StartAsync(
        IMediaPlayerFactory? playerFactory,
        MediaSource? source,
        MediaOpenOptions options,
        IMediaVideoOutput videoOutput,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(videoOutput);

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await StopPlayerCoreAsync(
                setStoppedState: false,
                cancellationToken).ConfigureAwait(false);
            if (playerFactory is null)
            {
                throw new InvalidOperationException(
                    "PlayerFactory must be set before playback can start.");
            }

            if (source is null)
            {
                throw new InvalidOperationException(
                    "A media source is required before playback can start.");
            }
            options.Validate();

            _lastError = null;
            SetState(MediaPlaybackState.Opening);
            var player = playerFactory.Create() ??
                throw new InvalidOperationException("PlayerFactory returned null.");
            lock (_eventSync)
            {
                _player = player;
            }

            try
            {
                player.Volume = _volume;
                player.IsMuted = _isMuted;
                player.VideoOutput = videoOutput;
                player.StateChanged += OnPlayerStateChanged;
                player.Error += OnPlayerError;
                lock (_eventSync)
                {
                    if (_frameReceived is not null)
                    {
                        player.FrameReceived += OnPlayerFrameReceived;
                    }
                }

                await player.OpenAsync(source, options, cancellationToken).ConfigureAwait(false);
                player.PlaybackRate = _playbackRate;
                await player.PlayAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await StopPlayerCoreAsync(setStoppedState: false).ConfigureAwait(false);
                throw;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetState(MediaPlaybackState.Stopped);
            throw;
        }
        catch (Exception exception) when (!_disposed || exception is not ObjectDisposedException)
        {
            ReportError(new MediaPlaybackError(
                "OpenFailed",
                exception.Message,
                IsRecoverable: false,
                exception));
            throw;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            await StopPlayerCoreAsync(
                setStoppedState: true,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public ValueTask PauseAsync(CancellationToken cancellationToken = default) =>
        _player?.PauseAsync(cancellationToken) ?? ValueTask.CompletedTask;

    public ValueTask ResumeAsync(CancellationToken cancellationToken = default) =>
        _player?.PlayAsync(cancellationToken) ?? ValueTask.CompletedTask;

    public ValueTask SeekAsync(
        TimeSpan position,
        CancellationToken cancellationToken = default)
    {
        var player = _player;
        if (player is null)
        {
            throw new InvalidOperationException("Start playback before seeking.");
        }

        return player.SeekAsync(position, cancellationToken);
    }

    public ValueTask<MediaSnapshot?> CaptureSnapshotAsync(CancellationToken cancellationToken = default) =>
        _player?.CaptureSnapshotAsync(cancellationToken) ??
        ValueTask.FromResult<MediaSnapshot?>(null);

    public ValueTask DisposeAsync()
    {
        lock (_disposeSync)
        {
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await StopPlayerCoreAsync(setStoppedState: true).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async ValueTask StopPlayerCoreAsync(
        bool setStoppedState,
        CancellationToken cancellationToken = default)
    {
        IMediaPlayer? player;
        lock (_eventSync)
        {
            player = _player;
            _player = null;
            if (player is not null && _frameReceived is not null)
            {
                player.FrameReceived -= OnPlayerFrameReceived;
            }
        }

        try
        {
            if (player is not null)
            {
                player.StateChanged -= OnPlayerStateChanged;
                player.Error -= OnPlayerError;
                try
                {
                    await player.StopAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    await player.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _diagnostics = MediaDiagnostics.Empty;
            if (setStoppedState)
            {
                SetState(MediaPlaybackState.Stopped);
            }
        }
    }

    private void OnPlayerStateChanged(object? sender, MediaPlaybackStateChangedEventArgs args)
    {
        if (sender is not IMediaPlayer player || !ReferenceEquals(_player, player))
        {
            return;
        }

        _diagnostics = player.Diagnostics;
        SetState(args.NewState);
    }

    private void OnPlayerError(object? sender, MediaPlaybackErrorEventArgs args)
    {
        if (ReferenceEquals(_player, sender))
        {
            ReportError(args.Error);
        }
    }

    private void OnPlayerFrameReceived(object? sender, MediaVideoFrame frame)
    {
        EventHandler<MediaVideoFrame>? handler;
        lock (_eventSync)
        {
            if (!ReferenceEquals(_player, sender))
            {
                return;
            }

            handler = _frameReceived;
        }

        handler?.Invoke(this, frame);
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

    private void ReportError(MediaPlaybackError error)
    {
        _lastError = error;
        if (!error.IsRecoverable)
        {
            SetState(MediaPlaybackState.Faulted);
        }

        Error?.Invoke(this, new MediaPlaybackErrorEventArgs(error));
    }
}
