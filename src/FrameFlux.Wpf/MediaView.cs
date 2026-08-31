using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using FrameFlux.Presentation;

namespace FrameFlux.Wpf;

public sealed class MediaView : System.Windows.Controls.Grid, IAsyncDisposable
{
    private static readonly DependencyPropertyKey StatePropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(State),
            typeof(MediaPlaybackState),
            typeof(MediaView),
            new FrameworkPropertyMetadata(MediaPlaybackState.Idle));

    private static readonly DependencyPropertyKey LastErrorPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(LastError),
            typeof(MediaPlaybackError),
            typeof(MediaView),
            new FrameworkPropertyMetadata(null));

    private static readonly DependencyPropertyKey IsHardwareVideoDecodingActivePropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(IsHardwareVideoDecodingActive),
            typeof(bool),
            typeof(MediaView),
            new FrameworkPropertyMetadata(false));

    private static readonly DependencyPropertyKey VideoDecoderDiagnosticsPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(VideoDecoderDiagnostics),
            typeof(string),
            typeof(MediaView),
            new FrameworkPropertyMetadata("Not started"));

    private static readonly DependencyPropertyKey EffectivePresentationModePropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(EffectivePresentationMode),
            typeof(MediaVideoPresentationMode?),
            typeof(MediaView),
            new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty SourceProperty =
        DependencyProperty.Register(
            nameof(Source),
            typeof(MediaSource),
            typeof(MediaView),
            new FrameworkPropertyMetadata(null, OnRestartPropertyChanged));

    public static readonly DependencyProperty OpenOptionsProperty =
        DependencyProperty.Register(
            nameof(OpenOptions),
            typeof(MediaOpenOptions),
            typeof(MediaView),
            new FrameworkPropertyMetadata(new MediaOpenOptions(), OnRestartPropertyChanged),
            value => value is MediaOpenOptions);

    public static readonly DependencyProperty PresentationModeProperty =
        DependencyProperty.Register(
            nameof(PresentationMode),
            typeof(MediaVideoPresentationMode),
            typeof(MediaView),
            new FrameworkPropertyMetadata(
                MediaVideoPresentationMode.Automatic,
                OnRestartPropertyChanged));

    public static readonly DependencyProperty PlayerFactoryProperty =
        DependencyProperty.Register(
            nameof(PlayerFactory),
            typeof(IMediaPlayerFactory),
            typeof(MediaView),
            new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty AutoPlayProperty =
        DependencyProperty.Register(
            nameof(AutoPlay),
            typeof(bool),
            typeof(MediaView),
            new FrameworkPropertyMetadata(true, OnAutoPlayChanged));

    public static readonly DependencyProperty KeepPlaybackAliveProperty =
        DependencyProperty.Register(
            nameof(KeepPlaybackAlive),
            typeof(bool),
            typeof(MediaView),
            new FrameworkPropertyMetadata(false));

    public static readonly DependencyProperty VolumeProperty =
        DependencyProperty.Register(
            nameof(Volume),
            typeof(double),
            typeof(MediaView),
            new FrameworkPropertyMetadata(
                1d,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnVolumeChanged),
            IsValidVolume);

    public static readonly DependencyProperty IsMutedProperty =
        DependencyProperty.Register(
            nameof(IsMuted),
            typeof(bool),
            typeof(MediaView),
            new FrameworkPropertyMetadata(
                false,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnIsMutedChanged));

    public static readonly DependencyProperty StretchProperty =
        DependencyProperty.Register(
            nameof(Stretch),
            typeof(Stretch),
            typeof(MediaView),
            new FrameworkPropertyMetadata(
                Stretch.Uniform,
                FrameworkPropertyMetadataOptions.AffectsRender,
                OnStretchChanged));

    public static readonly DependencyProperty StateProperty = StatePropertyKey.DependencyProperty;

    public static readonly DependencyProperty LastErrorProperty = LastErrorPropertyKey.DependencyProperty;

    public static readonly DependencyProperty IsHardwareVideoDecodingActiveProperty =
        IsHardwareVideoDecodingActivePropertyKey.DependencyProperty;

    public static readonly DependencyProperty VideoDecoderDiagnosticsProperty =
        VideoDecoderDiagnosticsPropertyKey.DependencyProperty;

    public static readonly DependencyProperty EffectivePresentationModeProperty =
        EffectivePresentationModePropertyKey.DependencyProperty;

    private readonly MediaPlaybackController _playback = new();
    private readonly MediaPresentationCoordinator _presentation;
    private EventHandler<MediaVideoFrame>? _frameReceived;
    private CancellationTokenSource? _restartCancellation;
    private bool _presentationReady;
    private bool _hasOverlayChildren;
    private bool _isLoaded;
    private bool _disposed;

    public MediaView()
    {
        _playback.StateChanged += OnPlayerStateChanged;
        _playback.Error += OnPlayerError;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SnapsToDevicePixels = true;
        Background = Brushes.Black;
        _presentation = new MediaPresentationCoordinator(
            this,
            mode => SetValue(EffectivePresentationModePropertyKey, mode),
            OnPresentationFailed);
        _presentation.SetStretch(Stretch);
        _hasOverlayChildren = _presentation.HasOverlayChildren();
        _presentationReady = true;
    }

    public MediaSource? Source
    {
        get => (MediaSource?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public MediaOpenOptions OpenOptions
    {
        get => (MediaOpenOptions)GetValue(OpenOptionsProperty);
        set => SetValue(OpenOptionsProperty, value ?? throw new ArgumentNullException(nameof(value)));
    }

    public MediaVideoPresentationMode PresentationMode
    {
        get => (MediaVideoPresentationMode)GetValue(PresentationModeProperty);
        set => SetValue(PresentationModeProperty, value);
    }

    public IMediaPlayerFactory? PlayerFactory
    {
        get => (IMediaPlayerFactory?)GetValue(PlayerFactoryProperty);
        set => SetValue(PlayerFactoryProperty, value);
    }

    public bool AutoPlay
    {
        get => (bool)GetValue(AutoPlayProperty);
        set => SetValue(AutoPlayProperty, value);
    }

    public bool KeepPlaybackAlive
    {
        get => (bool)GetValue(KeepPlaybackAliveProperty);
        set => SetValue(KeepPlaybackAliveProperty, value);
    }

    public double Volume
    {
        get => (double)GetValue(VolumeProperty);
        set => SetValue(VolumeProperty, value);
    }

    public bool IsMuted
    {
        get => (bool)GetValue(IsMutedProperty);
        set => SetValue(IsMutedProperty, value);
    }

    public Stretch Stretch
    {
        get => (Stretch)GetValue(StretchProperty);
        set => SetValue(StretchProperty, value);
    }

    public TimeSpan Position => _playback.Position;

    public TimeSpan? Duration => _playback.Duration;

    public MediaCapabilities Capabilities => _playback.Capabilities;

    public double PlaybackRate
    {
        get => _playback.PlaybackRate;
        set => _playback.PlaybackRate = value;
    }

    public MediaPlaybackState State => (MediaPlaybackState)GetValue(StateProperty);

    public MediaPlaybackError? LastError => (MediaPlaybackError?)GetValue(LastErrorProperty);

    public bool IsHardwareVideoDecodingActive =>
        (bool)GetValue(IsHardwareVideoDecodingActiveProperty);

    public string VideoDecoderDiagnostics => (string)GetValue(VideoDecoderDiagnosticsProperty);

    public MediaVideoPresentationMode? EffectivePresentationMode =>
        (MediaVideoPresentationMode?)GetValue(EffectivePresentationModeProperty);

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
        VerifyAccess();
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
        VerifyAccess();
        await _playback.StopAsync(cancellationToken);
        _presentation.Reset();
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

    public async ValueTask DisposeAsync()
    {
        VerifyAccess();
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Interlocked.Exchange(ref _restartCancellation, null)?.Cancel();
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
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
            try
            {
                _presentation.Reset();
            }
            finally
            {
                _presentation.Dispose();
            }
        }

        GC.SuppressFinalize(this);
    }

    protected override void OnVisualChildrenChanged(
        DependencyObject visualAdded,
        DependencyObject visualRemoved)
    {
        base.OnVisualChildrenChanged(visualAdded, visualRemoved);
        if (!_presentationReady || _disposed)
        {
            return;
        }

        var hasOverlayChildren = _presentation.HasOverlayChildren();
        if (_hasOverlayChildren == hasOverlayChildren)
        {
            return;
        }

        _hasOverlayChildren = hasOverlayChildren;
        _presentation.ClearSoftwareFallback();
        if (_isLoaded &&
            MediaPresentationPolicy.RequiresOverlayReconfiguration(
                PresentationMode,
                EffectivePresentationMode) &&
            (AutoPlay || _playback.HasPlayer))
        {
            ScheduleRestart();
        }
    }

    private static void OnRestartPropertyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var view = (MediaView)sender;
        view._presentation.ClearSoftwareFallback();
        if (!view._disposed &&
            view._isLoaded &&
            (view.AutoPlay || view._playback.HasPlayer))
        {
            view.ScheduleRestart();
        }
    }

    private static void OnAutoPlayChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var view = (MediaView)sender;
        if (!view._disposed &&
            view._isLoaded &&
            (bool)args.NewValue &&
            view.Source is not null)
        {
            _ = view.StartSafelyAsync();
        }
    }

    private static void OnVolumeChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var view = (MediaView)sender;
        view._playback.Volume = (double)args.NewValue;
    }

    private static void OnIsMutedChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var view = (MediaView)sender;
        view._playback.IsMuted = (bool)args.NewValue;
    }

    private static void OnStretchChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var view = (MediaView)sender;
        view._presentation.SetStretch((Stretch)args.NewValue);
    }

    private static bool IsValidVolume(object value) =>
        value is double volume && volume is >= 0d and <= 1d && !double.IsNaN(volume);

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        _isLoaded = true;
        if (AutoPlay && Source is not null)
        {
            _ = StartSafelyAsync();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _isLoaded = false;
        if (!KeepPlaybackAlive)
        {
            _ = StopSafelyAsync();
        }
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
            if ((_isLoaded || KeepPlaybackAlive) && Source is not null)
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

    private async Task StartSafelyAsync()
    {
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
        try
        {
            await StopAsync();
        }
        catch (Exception exception)
        {
            ReportError(new MediaPlaybackError(
                "StopFailed",
                exception.Message,
                IsRecoverable: true,
                exception));
        }
    }

    private void OnPlayerStateChanged(object? sender, MediaPlaybackStateChangedEventArgs args) =>
        Dispatcher.BeginInvoke(
            DispatcherPriority.DataBind,
            new Action(() =>
            {
                SetState(args.NewState);
                var diagnostics = _playback.Diagnostics;
                SetValue(
                    IsHardwareVideoDecodingActivePropertyKey,
                    diagnostics.IsHardwareVideoDecodingActive);
                SetValue(
                    VideoDecoderDiagnosticsPropertyKey,
                    diagnostics.VideoDecoderDiagnostics);
            }));

    private void OnPlayerError(object? sender, MediaPlaybackErrorEventArgs args) =>
        Dispatcher.BeginInvoke(
            DispatcherPriority.DataBind,
            new Action(() =>
            {
                ReportError(args.Error);
            }));

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
                    "WPF MediaView FrameReceived subscriber failed: {0}",
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

    private async Task RestartForPresentationFallbackAsync()
    {
        try
        {
            await _playback.StopAsync();
            _presentation.Reset();
            if (_isLoaded && Source is not null)
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

    private void SetState(MediaPlaybackState state)
    {
        var oldState = State;
        if (oldState == state)
        {
            return;
        }

        SetValue(StatePropertyKey, state);
        PlaybackStateChanged?.Invoke(this, new MediaPlaybackStateChangedEventArgs(oldState, state));
    }

    private void ReportError(MediaPlaybackError error)
    {
        SetValue(LastErrorPropertyKey, error);
        if (!error.IsRecoverable)
        {
            SetState(MediaPlaybackState.Faulted);
        }

        PlaybackError?.Invoke(this, new MediaPlaybackErrorEventArgs(error));
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

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(MediaView));
        }
    }

}
