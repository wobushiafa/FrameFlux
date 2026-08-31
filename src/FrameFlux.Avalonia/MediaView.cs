using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FrameFlux.Presentation;

namespace FrameFlux.Avalonia;

public sealed class MediaView : Control, IAsyncDisposable
{
    public static readonly StyledProperty<MediaSource?> SourceProperty =
        AvaloniaProperty.Register<MediaView, MediaSource?>(nameof(Source));

    public static readonly StyledProperty<MediaOpenOptions> OpenOptionsProperty =
        AvaloniaProperty.Register<MediaView, MediaOpenOptions>(nameof(OpenOptions), new MediaOpenOptions());

    public static readonly StyledProperty<MediaVideoPresentationMode> PresentationModeProperty =
        AvaloniaProperty.Register<MediaView, MediaVideoPresentationMode>(
            nameof(PresentationMode),
            MediaVideoPresentationMode.Automatic);

    public static readonly StyledProperty<IMediaPlayerFactory?> PlayerFactoryProperty =
        AvaloniaProperty.Register<MediaView, IMediaPlayerFactory?>(nameof(PlayerFactory));

    public static readonly StyledProperty<bool> AutoPlayProperty =
        AvaloniaProperty.Register<MediaView, bool>(nameof(AutoPlay), true);

    public static readonly StyledProperty<bool> KeepPlaybackAliveProperty =
        AvaloniaProperty.Register<MediaView, bool>(nameof(KeepPlaybackAlive));

    public static readonly StyledProperty<double> VolumeProperty =
        AvaloniaProperty.Register<MediaView, double>(nameof(Volume), 1d);

    public static readonly StyledProperty<bool> IsMutedProperty =
        AvaloniaProperty.Register<MediaView, bool>(nameof(IsMuted));

    public static readonly StyledProperty<Stretch> StretchProperty =
        AvaloniaProperty.Register<MediaView, Stretch>(nameof(Stretch), Stretch.Uniform);

    public static readonly StyledProperty<Control?> OverlayProperty =
        AvaloniaProperty.Register<MediaView, Control?>(nameof(Overlay));

    public static readonly DirectProperty<MediaView, MediaPlaybackState> StateProperty =
        AvaloniaProperty.RegisterDirect<MediaView, MediaPlaybackState>(nameof(State), view => view.State);

    public static readonly DirectProperty<MediaView, MediaPlaybackError?> LastErrorProperty =
        AvaloniaProperty.RegisterDirect<MediaView, MediaPlaybackError?>(nameof(LastError), view => view.LastError);

    public static readonly DirectProperty<MediaView, bool> IsHardwareVideoDecodingActiveProperty =
        AvaloniaProperty.RegisterDirect<MediaView, bool>(
            nameof(IsHardwareVideoDecodingActive),
            view => view.IsHardwareVideoDecodingActive);

    public static readonly DirectProperty<MediaView, string> VideoDecoderDiagnosticsProperty =
        AvaloniaProperty.RegisterDirect<MediaView, string>(
            nameof(VideoDecoderDiagnostics),
            view => view.VideoDecoderDiagnostics);

    public static readonly DirectProperty<MediaView, MediaVideoPresentationMode?> EffectivePresentationModeProperty =
        AvaloniaProperty.RegisterDirect<MediaView, MediaVideoPresentationMode?>(
            nameof(EffectivePresentationMode),
            view => view.EffectivePresentationMode);

    private readonly MediaPlaybackController _playback = new();
    private readonly MediaPresentationCoordinator _presentation;
    private EventHandler<MediaVideoFrame>? _frameReceived;
    private CancellationTokenSource? _restartCancellation;
    private MediaPlaybackState _state = MediaPlaybackState.Idle;
    private MediaPlaybackError? _lastError;
    private bool _isHardwareVideoDecodingActive;
    private string _videoDecoderDiagnostics = "Not started";
    private MediaVideoPresentationMode? _effectivePresentationMode;
    private bool _hasOverlay;
    private bool _attached;
    private bool _disposed;

    public MediaView()
    {
        _presentation = new MediaPresentationCoordinator(
            mode => EffectivePresentationMode = mode,
            OnPresentationFailed);
        _playback.StateChanged += OnPlayerStateChanged;
        _playback.Error += OnPlayerError;
        _presentation.SetStretch(Stretch);
        LogicalChildren.Add(_presentation.Surface);
        VisualChildren.Add(_presentation.Surface);
    }

    public MediaSource? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public MediaOpenOptions OpenOptions
    {
        get => GetValue(OpenOptionsProperty);
        set => SetValue(OpenOptionsProperty, value ?? throw new ArgumentNullException(nameof(value)));
    }

    public MediaVideoPresentationMode PresentationMode
    {
        get => GetValue(PresentationModeProperty);
        set => SetValue(PresentationModeProperty, value);
    }

    public IMediaPlayerFactory? PlayerFactory
    {
        get => GetValue(PlayerFactoryProperty);
        set => SetValue(PlayerFactoryProperty, value);
    }

    public bool AutoPlay
    {
        get => GetValue(AutoPlayProperty);
        set => SetValue(AutoPlayProperty, value);
    }

    public bool KeepPlaybackAlive
    {
        get => GetValue(KeepPlaybackAliveProperty);
        set => SetValue(KeepPlaybackAliveProperty, value);
    }

    public double Volume
    {
        get => GetValue(VolumeProperty);
        set => SetValue(VolumeProperty, Math.Clamp(value, 0d, 1d));
    }

    public bool IsMuted
    {
        get => GetValue(IsMutedProperty);
        set => SetValue(IsMutedProperty, value);
    }

    public Stretch Stretch
    {
        get => GetValue(StretchProperty);
        set => SetValue(StretchProperty, value);
    }

    public Control? Overlay
    {
        get => GetValue(OverlayProperty);
        set => SetValue(OverlayProperty, value);
    }

    public TimeSpan Position => _playback.Position;

    public TimeSpan? Duration => _playback.Duration;

    public MediaCapabilities Capabilities => _playback.Capabilities;

    public double PlaybackRate
    {
        get => _playback.PlaybackRate;
        set => _playback.PlaybackRate = value;
    }

    public MediaPlaybackState State
    {
        get => _state;
        private set => SetAndRaise(StateProperty, ref _state, value);
    }

    public MediaPlaybackError? LastError
    {
        get => _lastError;
        private set => SetAndRaise(LastErrorProperty, ref _lastError, value);
    }

    public bool IsHardwareVideoDecodingActive
    {
        get => _isHardwareVideoDecodingActive;
        private set => SetAndRaise(
            IsHardwareVideoDecodingActiveProperty,
            ref _isHardwareVideoDecodingActive,
            value);
    }

    public string VideoDecoderDiagnostics
    {
        get => _videoDecoderDiagnostics;
        private set => SetAndRaise(
            VideoDecoderDiagnosticsProperty,
            ref _videoDecoderDiagnostics,
            value);
    }

    public MediaVideoPresentationMode? EffectivePresentationMode
    {
        get => _effectivePresentationMode;
        private set => SetAndRaise(
            EffectivePresentationModeProperty,
            ref _effectivePresentationMode,
            value);
    }

    public event EventHandler<MediaPlaybackStateChangedEventArgs>? PlaybackStateChanged;

    public event EventHandler<MediaPlaybackErrorEventArgs>? PlaybackError;

    public event EventHandler<MediaVideoFrame>? FrameReceived
    {
        add
        {
            if (_frameReceived is null)
            {
                _playback.FrameReceived += OnFrameReceived;
            }

            _frameReceived += value;
        }
        remove
        {
            _frameReceived -= value;
            if (_frameReceived is null)
            {
                _playback.FrameReceived -= OnFrameReceived;
            }
        }
    }

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        Dispatcher.UIThread.VerifyAccess();
        ThrowIfDisposed();
        if (State == MediaPlaybackState.Paused)
        {
            await _playback.ResumeAsync(cancellationToken);
            return;
        }
        _presentation.Reset();
        var options = OpenOptions;
        var output = _presentation.Configure(
            options,
            PresentationMode,
            Stretch);
        _playback.Volume = Volume;
        _playback.IsMuted = IsMuted;
        await _playback.StartAsync(
            PlayerFactory,
            Source,
            options,
            output,
            cancellationToken);
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        Dispatcher.UIThread.VerifyAccess();
        await _playback.StopAsync(cancellationToken);
        await _presentation.ReleaseResourcesAsync();
        _presentation.ClearSoftwareFallback();
    }

    public ValueTask PauseAsync(CancellationToken cancellationToken = default) =>
        _playback.PauseAsync(cancellationToken);

    public ValueTask ResumeAsync(CancellationToken cancellationToken = default) =>
        _playback.ResumeAsync(cancellationToken);

    public ValueTask SeekAsync(TimeSpan position, CancellationToken cancellationToken = default) =>
        _playback.SeekAsync(position, cancellationToken);

    public ValueTask<MediaSnapshot?> CaptureSnapshotAsync(CancellationToken cancellationToken = default) =>
        _playback.CaptureSnapshotAsync(cancellationToken);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        if (AutoPlay && Source is not null)
        {
            _ = StartSafelyAsync();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        if (!KeepPlaybackAlive)
        {
            _ = StopSafelyAsync();
        }

        base.OnDetachedFromVisualTree(e);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _presentation.Surface.Measure(availableSize);
        return _presentation.Surface.DesiredSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _presentation.Surface.Arrange(new Rect(finalSize));
        return finalSize;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (_disposed)
        {
            return;
        }

        if (change.Property == VolumeProperty)
        {
            _playback.Volume = Volume;
        }
        else if (change.Property == IsMutedProperty)
        {
            _playback.IsMuted = IsMuted;
        }
        else if (change.Property == StretchProperty)
        {
            _presentation.SetStretch(Stretch);
        }
        else if (change.Property == OverlayProperty)
        {
            var hasOverlay = Overlay is not null;
            var overlayPresenceChanged = _hasOverlay != hasOverlay;
            _hasOverlay = hasOverlay;
            _presentation.SetOverlay(Overlay);
            if (overlayPresenceChanged &&
                MediaPresentationPolicy.RequiresOverlayReconfiguration(
                    PresentationMode,
                    EffectivePresentationMode) &&
                _attached &&
                (AutoPlay || _playback.HasPlayer))
            {
                ScheduleRestart();
            }
        }
        else if (change.Property == AutoPlayProperty)
        {
            if (_attached && AutoPlay && Source is not null)
            {
                _ = StartSafelyAsync();
            }
        }
        else if (change.Property == SourceProperty ||
                 change.Property == OpenOptionsProperty ||
                 change.Property == PresentationModeProperty)
        {
            _presentation.ClearSoftwareFallback();
            if (_attached && (AutoPlay || _playback.HasPlayer))
            {
                ScheduleRestart();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Interlocked.Exchange(ref _restartCancellation, null)?.Cancel();
        _playback.StateChanged -= OnPlayerStateChanged;
        _playback.Error -= OnPlayerError;
        if (_frameReceived is not null)
        {
            _playback.FrameReceived -= OnFrameReceived;
        }
        try
        {
            await _playback.DisposeAsync();
        }
        finally
        {
            await _presentation.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    private void OnPlayerStateChanged(object? sender, MediaPlaybackStateChangedEventArgs args) =>
        Dispatcher.UIThread.Post(
            () =>
            {
                SetState(args.NewState);
                var diagnostics = _playback.Diagnostics;
                IsHardwareVideoDecodingActive = diagnostics.IsHardwareVideoDecodingActive;
                VideoDecoderDiagnostics = diagnostics.VideoDecoderDiagnostics;
            },
            DispatcherPriority.Normal);

    private void OnPlayerError(object? sender, MediaPlaybackErrorEventArgs args) =>
        Dispatcher.UIThread.Post(() => ReportError(args.Error), DispatcherPriority.Background);

    private void OnFrameReceived(object? sender, MediaVideoFrame frame)
    {
        var handlers = _frameReceived;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<MediaVideoFrame> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, frame);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Trace.TraceError(
                    "Avalonia MediaView FrameReceived subscriber failed: {0}",
                    exception);
            }
        }
    }

    private void OnPresentationFailed(MediaPresentationFailure failure)
    {
        ReportError(new MediaPlaybackError(
            "GpuCompositionFailed",
            failure.Exception.Message,
            IsRecoverable: true,
            failure.Exception));
        if (failure.RequiresSoftwareFallback)
        {
            _ = RestartForPresentationFallbackAsync();
        }
    }

    private void SetState(MediaPlaybackState state)
    {
        var oldState = State;
        if (oldState == state)
        {
            return;
        }

        State = state;
        PlaybackStateChanged?.Invoke(this, new MediaPlaybackStateChangedEventArgs(oldState, state));
    }

    private void ReportError(MediaPlaybackError error)
    {
        LastError = error;
        if (!error.IsRecoverable)
        {
            SetState(MediaPlaybackState.Faulted);
        }

        PlaybackError?.Invoke(this, new MediaPlaybackErrorEventArgs(error));
    }

    private void ScheduleRestart()
    {
        var request = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _restartCancellation, request);
        previous?.Cancel();
        _ = RestartSafelyAsync(request, request.Token);
    }

    private async Task RestartSafelyAsync(
        CancellationTokenSource request,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            await StopAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if ((_attached || KeepPlaybackAlive) && Source is not null)
            {
                await StartAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ReportLifecycleFailure("RestartFailed", exception);
        }
        finally
        {
            _ = Interlocked.CompareExchange(ref _restartCancellation, null, request);
            request.Dispose();
        }
    }

    private async Task RestartForPresentationFallbackAsync()
    {
        try
        {
            await _playback.StopAsync();
            await _presentation.ReleaseResourcesAsync();
            if (_attached && Source is not null)
            {
                await StartAsync();
            }
        }
        catch (Exception exception)
        {
            ReportError(new MediaPlaybackError(
                "PresentationFallbackFailed",
                exception.Message,
                IsRecoverable: false,
                exception));
        }
    }

    private async Task StartSafelyAsync()
    {
        if (_disposed || !_attached || Source is null)
        {
            return;
        }

        try
        {
            await StartAsync();
        }
        catch (Exception exception)
        {
            ReportLifecycleFailure("StartFailed", exception);
        }
    }

    private async Task StopSafelyAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            await StopAsync();
        }
        catch (Exception exception)
        {
            ReportError(new MediaPlaybackError("StopFailed", exception.Message, false, exception));
        }
    }

    private void ReportLifecycleFailure(string code, Exception exception)
    {
        if (_disposed || ReferenceEquals(_playback.LastError?.Exception, exception))
        {
            return;
        }

        ReportError(new MediaPlaybackError(
            code,
            exception.Message,
            IsRecoverable: false,
            exception));
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

}
