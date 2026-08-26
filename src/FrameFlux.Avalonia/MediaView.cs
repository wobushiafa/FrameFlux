using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using FrameFlux.FFmpeg;

namespace FrameFlux.Avalonia;

public sealed class MediaView : ContentControl, IAsyncDisposable
{
    public static readonly StyledProperty<MediaSource?> SourceProperty =
        AvaloniaProperty.Register<MediaView, MediaSource?>(nameof(Source));

    public static readonly StyledProperty<MediaOpenOptions> OpenOptionsProperty =
        AvaloniaProperty.Register<MediaView, MediaOpenOptions>(nameof(OpenOptions), new MediaOpenOptions());

    public static readonly StyledProperty<bool> IsPlaybackEnabledProperty =
        AvaloniaProperty.Register<MediaView, bool>(nameof(IsPlaybackEnabled), true);

    public static readonly StyledProperty<bool> KeepPlaybackAliveWhenDetachedProperty =
        AvaloniaProperty.Register<MediaView, bool>(nameof(KeepPlaybackAliveWhenDetached));

    public static readonly StyledProperty<double> VolumeProperty =
        AvaloniaProperty.Register<MediaView, double>(nameof(Volume), 1d);

    public static readonly StyledProperty<bool> IsMutedProperty =
        AvaloniaProperty.Register<MediaView, bool>(nameof(IsMuted));

    public static readonly StyledProperty<Stretch> StretchProperty =
        AvaloniaProperty.Register<MediaView, Stretch>(nameof(Stretch), Stretch.Uniform);

    public static readonly DirectProperty<MediaView, MediaPlaybackState> StateProperty =
        AvaloniaProperty.RegisterDirect<MediaView, MediaPlaybackState>(
            nameof(State),
            view => view.State);

    public static readonly DirectProperty<MediaView, MediaPlaybackError?> LastErrorProperty =
        AvaloniaProperty.RegisterDirect<MediaView, MediaPlaybackError?>(
            nameof(LastError),
            view => view.LastError);

    public static readonly DirectProperty<MediaView, bool> IsHardwareAccelerationActiveProperty =
        AvaloniaProperty.RegisterDirect<MediaView, bool>(
            nameof(IsHardwareAccelerationActive),
            view => view.IsHardwareAccelerationActive);

    public static readonly DirectProperty<MediaView, string> HardwareDiagnosticsProperty =
        AvaloniaProperty.RegisterDirect<MediaView, string>(
            nameof(HardwareDiagnostics),
            view => view.HardwareDiagnostics);

    public static readonly DirectProperty<MediaView, string?> ActiveRendererIdProperty =
        AvaloniaProperty.RegisterDirect<MediaView, string?>(
            nameof(ActiveRendererId),
            view => view.ActiveRendererId);

    private readonly RtspPlayerView _player;
    private MediaPlaybackState _state = MediaPlaybackState.Idle;
    private MediaPlaybackError? _lastError;
    private bool _isHardwareAccelerationActive;
    private string _hardwareDiagnostics = "Not started";
    private string? _activeRendererId;
    private bool _disposed;

    public MediaView()
    {
        _player = new RtspPlayerView();
        _player.StateChanged += OnPlayerStateChanged;
        _player.Error += OnPlayerError;
        _player.FrameReceived += OnFrameReceived;
        _player.PropertyChanged += OnPlayerPropertyChanged;
        Content = _player;
        ApplyConfiguration();
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

    public bool IsPlaybackEnabled
    {
        get => GetValue(IsPlaybackEnabledProperty);
        set => SetValue(IsPlaybackEnabledProperty, value);
    }

    public bool KeepPlaybackAliveWhenDetached
    {
        get => GetValue(KeepPlaybackAliveWhenDetachedProperty);
        set => SetValue(KeepPlaybackAliveWhenDetachedProperty, value);
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

    public bool IsHardwareAccelerationActive
    {
        get => _isHardwareAccelerationActive;
        private set => SetAndRaise(
            IsHardwareAccelerationActiveProperty,
            ref _isHardwareAccelerationActive,
            value);
    }

    public string HardwareDiagnostics
    {
        get => _hardwareDiagnostics;
        private set => SetAndRaise(HardwareDiagnosticsProperty, ref _hardwareDiagnostics, value);
    }

    public string? ActiveRendererId
    {
        get => _activeRendererId;
        private set => SetAndRaise(ActiveRendererIdProperty, ref _activeRendererId, value);
    }

    public event EventHandler<MediaPlaybackStateChangedEventArgs>? PlaybackStateChanged;

    public event EventHandler<MediaPlaybackErrorEventArgs>? PlaybackError;

    public event EventHandler<MediaVideoFrame>? FrameReceived;

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ApplyConfiguration();
        await _player.StartAsync(cancellationToken);
    }

    public ValueTask StopAsync(CancellationToken cancellationToken = default) =>
        _player.StopAsync(cancellationToken);

    public async ValueTask<MediaSnapshot?> CaptureSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        var snapshot = await _player.CaptureSnapshotAsync(cancellationToken);
        return snapshot is null
            ? null
            : new MediaSnapshot(
                snapshot.Data,
                snapshot.Width,
                snapshot.Height,
                snapshot.Stride,
                MediaFramePixelFormat.Bgra32,
                snapshot.CapturedAt);
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
            _player.Volume = Volume;
        }
        else if (change.Property == IsMutedProperty)
        {
            _player.IsMuted = IsMuted;
        }
        else if (change.Property == SourceProperty || change.Property == OpenOptionsProperty)
        {
            ApplyConfiguration();
        }
        else if (change.Property == IsPlaybackEnabledProperty)
        {
            _player.IsPlaybackEnabled = IsPlaybackEnabled;
        }
        else if (change.Property == KeepPlaybackAliveWhenDetachedProperty)
        {
            _player.KeepPlaybackAliveWhenDetached = KeepPlaybackAliveWhenDetached;
        }
        else if (change.Property == StretchProperty)
        {
            _player.Stretch = Stretch;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _player.StateChanged -= OnPlayerStateChanged;
        _player.Error -= OnPlayerError;
        _player.FrameReceived -= OnFrameReceived;
        _player.PropertyChanged -= OnPlayerPropertyChanged;
        await _player.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private void ApplyConfiguration()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            var options = OpenOptions;
            options.Validate();
            _player.Options = FfmpegMediaAdapter.ToRtspOptions(
                options,
                Volume,
                IsMuted,
                supportsNativeSurface: true);
            _player.Volume = Volume;
            _player.IsMuted = IsMuted;
            _player.KeepPlaybackAliveWhenDetached = KeepPlaybackAliveWhenDetached;
            _player.Stretch = Stretch;
            _player.Source = Source is null ? null : FfmpegMediaAdapter.ToRtspSource(Source);
            _player.IsPlaybackEnabled = IsPlaybackEnabled;
        }
        catch (Exception exception)
        {
            ReportError(new MediaPlaybackError(
                "ConfigurationFailed",
                exception.Message,
                IsRecoverable: false,
                exception));
        }
    }

    private void OnPlayerStateChanged(object? sender, RtspSessionStateChangedEventArgs args)
    {
        var newState = args.NewState switch
        {
            RtspSessionState.Connecting => MediaPlaybackState.Opening,
            RtspSessionState.Connected => MediaPlaybackState.Playing,
            RtspSessionState.Reconnecting => MediaPlaybackState.Reconnecting,
            RtspSessionState.Stopped => MediaPlaybackState.Stopped,
            RtspSessionState.Faulted => MediaPlaybackState.Faulted,
            _ => MediaPlaybackState.Idle
        };
        var oldState = State;
        State = newState;
        if (oldState != newState)
        {
            PlaybackStateChanged?.Invoke(
                this,
                new MediaPlaybackStateChangedEventArgs(oldState, newState));
        }
    }

    private void OnPlayerError(object? sender, RtspSessionErrorEventArgs args) =>
        ReportError(new MediaPlaybackError(
            args.Error.Code,
            args.Error.Message,
            args.Error.WillRetry,
            args.Error.Exception));

    private void OnFrameReceived(object? sender, RtspVideoFrame frame) =>
        FrameReceived?.Invoke(this, new MediaVideoFrame(
            frame.Data,
            frame.Width,
            frame.Height,
            frame.Stride,
            MediaFramePixelFormat.Bgra32,
            frame.Sequence,
            frame.CapturedAt));

    private void OnPlayerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == RtspPlayerView.IsHardwareAccelerationActiveProperty)
        {
            IsHardwareAccelerationActive = _player.IsHardwareAccelerationActive;
        }
        else if (e.Property == RtspPlayerView.HardwareDiagnosticsProperty)
        {
            HardwareDiagnostics = _player.HardwareDiagnostics;
        }
        else if (e.Property == RtspPlayerView.ActiveRendererIdProperty)
        {
            ActiveRendererId = _player.ActiveRendererId;
        }
    }

    private void ReportError(MediaPlaybackError error)
    {
        LastError = error;
        if (!error.IsRecoverable)
        {
            State = MediaPlaybackState.Faulted;
        }
        PlaybackError?.Invoke(this, new MediaPlaybackErrorEventArgs(error));
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(MediaView));
        }
    }
}
