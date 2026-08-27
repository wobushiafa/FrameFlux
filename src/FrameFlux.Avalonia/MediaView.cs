using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FrameFlux.Presentation;

namespace FrameFlux.Avalonia;

public sealed class MediaView : ContentControl, IAsyncDisposable
{
    public static readonly StyledProperty<MediaSource?> SourceProperty =
        AvaloniaProperty.Register<MediaView, MediaSource?>(nameof(Source));

    public static readonly StyledProperty<MediaOpenOptions> OpenOptionsProperty =
        AvaloniaProperty.Register<MediaView, MediaOpenOptions>(nameof(OpenOptions), new MediaOpenOptions());

    public static readonly StyledProperty<IMediaPlayerFactory?> PlayerFactoryProperty =
        AvaloniaProperty.Register<MediaView, IMediaPlayerFactory?>(nameof(PlayerFactory));

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
        AvaloniaProperty.RegisterDirect<MediaView, MediaPlaybackState>(nameof(State), view => view.State);

    public static readonly DirectProperty<MediaView, MediaPlaybackError?> LastErrorProperty =
        AvaloniaProperty.RegisterDirect<MediaView, MediaPlaybackError?>(nameof(LastError), view => view.LastError);

    public static readonly DirectProperty<MediaView, bool> IsHardwareAccelerationActiveProperty =
        AvaloniaProperty.RegisterDirect<MediaView, bool>(
            nameof(IsHardwareAccelerationActive),
            view => view.IsHardwareAccelerationActive);

    public static readonly DirectProperty<MediaView, string> HardwareDiagnosticsProperty =
        AvaloniaProperty.RegisterDirect<MediaView, string>(
            nameof(HardwareDiagnostics),
            view => view.HardwareDiagnostics);

    public static readonly DirectProperty<MediaView, string?> ActiveRendererIdProperty =
        AvaloniaProperty.RegisterDirect<MediaView, string?>(nameof(ActiveRendererId), view => view.ActiveRendererId);

    private readonly MediaPlaybackController _playback = new();
    private readonly Grid _surface = new();
    private readonly SoftwareBitmapMediaOutput _softwareOutput = new();
#if !ANDROID
    private readonly WindowsD3D11MediaOutput _nativeOutput = new();
#endif
    private EventHandler<MediaVideoFrame>? _frameReceived;
    private MediaPlaybackState _state = MediaPlaybackState.Idle;
    private MediaPlaybackError? _lastError;
    private bool _isHardwareAccelerationActive;
    private string _hardwareDiagnostics = "Not started";
    private string? _activeRendererId;
    private bool _attached;
    private bool _disposed;

    public MediaView()
    {
        _playback.StateChanged += OnPlayerStateChanged;
        _playback.Error += OnPlayerError;
        _softwareOutput.Stretch = Stretch;
        _softwareOutput.FramePresented += OnSoftwareFramePresented;
        _surface.Children.Add(_softwareOutput);
#if !ANDROID
        _nativeOutput.IsVisible = false;
        _surface.Children.Add(_nativeOutput);
#endif
        Content = _surface;
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

    public IMediaPlayerFactory? PlayerFactory
    {
        get => GetValue(PlayerFactoryProperty);
        set => SetValue(PlayerFactoryProperty, value);
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
        private set => SetAndRaise(IsHardwareAccelerationActiveProperty, ref _isHardwareAccelerationActive, value);
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
        ResetPresentation();
        var output = ConfigureVideoOutput(OpenOptions);
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
        ResetPresentation();
    }

    public ValueTask<MediaSnapshot?> CaptureSnapshotAsync(CancellationToken cancellationToken = default) =>
        _playback.CaptureSnapshotAsync(cancellationToken);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        if (IsPlaybackEnabled && Source is not null)
        {
            _ = StartSafelyAsync();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        if (!KeepPlaybackAliveWhenDetached)
        {
            _ = StopSafelyAsync();
        }

        base.OnDetachedFromVisualTree(e);
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
            _softwareOutput.Stretch = Stretch;
#if !ANDROID
            _nativeOutput.Stretch = Stretch;
#endif
        }
        else if (change.Property == IsPlaybackEnabledProperty)
        {
            _ = IsPlaybackEnabled ? StartSafelyAsync() : StopSafelyAsync();
        }
        else if ((change.Property == SourceProperty || change.Property == OpenOptionsProperty) &&
                 _attached && IsPlaybackEnabled)
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
        _softwareOutput.FramePresented -= OnSoftwareFramePresented;
        if (_frameReceived is not null)
        {
            _playback.FrameReceived -= OnFrameReceived;
        }
        await _playback.DisposeAsync();
        _softwareOutput.Dispose();
#if !ANDROID
        _nativeOutput.Dispose();
#endif
        GC.SuppressFinalize(this);
    }

    private IMediaVideoOutput ConfigureVideoOutput(MediaOpenOptions options)
    {
        var useNativeOutput =
            options.StreamSharing == MediaStreamSharingMode.Dedicated &&
            options.RenderPreference == MediaRenderPreference.NativeSurface &&
            options.HardwareAcceleration != MediaHardwareAcceleration.Disabled;
#if !ANDROID
        useNativeOutput &= OperatingSystem.IsWindows();
        _nativeOutput.Stretch = Stretch;
        _nativeOutput.IsVisible = useNativeOutput;
        var output = useNativeOutput ? (IMediaVideoOutput)_nativeOutput : _softwareOutput;
#else
        useNativeOutput = false;
        var output = (IMediaVideoOutput)_softwareOutput;
#endif
        _softwareOutput.IsVisible = !useNativeOutput;
        ActiveRendererId = useNativeOutput ? "windows-d3d11" : "software-bitmap";
        return output;
    }

    private void ResetPresentation()
    {
        _softwareOutput.Clear();
#if !ANDROID
        _nativeOutput.ClearPendingFrame();
        _nativeOutput.IsVisible = false;
#endif
        _softwareOutput.IsVisible = true;
    }

    private void OnPlayerStateChanged(object? sender, MediaPlaybackStateChangedEventArgs args) =>
        Dispatcher.UIThread.Post(
            () =>
            {
                SetState(args.NewState);
                var diagnostics = _playback.Diagnostics;
                IsHardwareAccelerationActive = diagnostics.IsHardwareAccelerationActive;
                HardwareDiagnostics = diagnostics.HardwareDiagnostics;
            },
            DispatcherPriority.Normal);

    private void OnPlayerError(object? sender, MediaPlaybackErrorEventArgs args) =>
        Dispatcher.UIThread.Post(() => ReportError(args.Error), DispatcherPriority.Background);

    private void OnFrameReceived(object? sender, MediaVideoFrame frame)
    {
        _frameReceived?.Invoke(this, frame);
    }

    private void OnSoftwareFramePresented(object? sender, EventArgs args)
    {
#if !ANDROID
        _nativeOutput.IsVisible = false;
#endif
        _softwareOutput.IsVisible = true;
        ActiveRendererId = "software-bitmap";
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

    private async Task RestartSafelyAsync()
    {
        await StopSafelyAsync();
        if (_attached && IsPlaybackEnabled && Source is not null)
        {
            await StartSafelyAsync();
        }
    }

    private async Task StartSafelyAsync()
    {
        if (_disposed || Source is null || !IsPlaybackEnabled)
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
