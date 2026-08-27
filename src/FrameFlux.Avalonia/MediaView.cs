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
    private MediaPlaybackState _state = MediaPlaybackState.Idle;
    private MediaPlaybackError? _lastError;
    private bool _isHardwareVideoDecodingActive;
    private string _videoDecoderDiagnostics = "Not started";
    private MediaVideoPresentationMode? _effectivePresentationMode;
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
        ThrowIfDisposed();
        _presentation.Reset();
        var output = _presentation.Configure(
            OpenOptions,
            PresentationMode,
            Stretch);
        _playback.Volume = Volume;
        _playback.IsMuted = IsMuted;
        await _playback.StartAsync(
            PlayerFactory,
            Source,
            OpenOptions,
            output,
            cancellationToken);
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        await _playback.StopAsync(cancellationToken);
        _presentation.Reset();
    }

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
            _presentation.SetOverlay(Overlay);
        }
        else if (change.Property == AutoPlayProperty)
        {
            _ = AutoPlay ? StartSafelyAsync() : StopSafelyAsync();
        }
        else if ((change.Property == SourceProperty ||
                  change.Property == OpenOptionsProperty ||
                  change.Property == PresentationModeProperty) &&
                 _attached && AutoPlay)
        {
            _ = RestartSafelyAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAsync();
        _disposed = true;
        _playback.StateChanged -= OnPlayerStateChanged;
        _playback.Error -= OnPlayerError;
        if (_frameReceived is not null)
        {
            _playback.FrameReceived -= OnFrameReceived;
        }
        await _playback.DisposeAsync();
        await _presentation.DisposeAsync();
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
        _frameReceived?.Invoke(this, frame);
    }

    private void OnPresentationFailed(Exception exception) =>
        ReportError(new MediaPlaybackError(
            "GpuCompositionFailed",
            exception.Message,
            IsRecoverable: false,
            exception));

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

    private async Task RestartSafelyAsync()
    {
        await StopSafelyAsync();
        if (_attached && AutoPlay && Source is not null)
        {
            await StartSafelyAsync();
        }
    }

    private async Task StartSafelyAsync()
    {
        if (_disposed || Source is null || !AutoPlay)
        {
            return;
        }

        try
        {
            await StartAsync();
        }
        catch
        {
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

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

}
