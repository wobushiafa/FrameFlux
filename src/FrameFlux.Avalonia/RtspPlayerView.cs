using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using FrameFlux;
using FrameFlux.FFmpeg;

namespace FrameFlux.Avalonia;

/// <summary>
/// A bindable cross-platform RTSP playback control. Software rendering exposes frame and snapshot APIs;
/// the native-surface backend preserves the existing platform player where it is available.
/// </summary>
public sealed class RtspPlayerView : ContentControl, IAsyncDisposable
{
    public static readonly StyledProperty<RtspSource?> SourceProperty =
        AvaloniaProperty.Register<RtspPlayerView, RtspSource?>(nameof(Source));

    public static readonly StyledProperty<RtspSessionOptions> OptionsProperty =
        AvaloniaProperty.Register<RtspPlayerView, RtspSessionOptions>(nameof(Options), new RtspSessionOptions());

    public static readonly StyledProperty<bool> IsPlaybackEnabledProperty =
        AvaloniaProperty.Register<RtspPlayerView, bool>(nameof(IsPlaybackEnabled), true);

    public static readonly StyledProperty<bool> KeepPlaybackAliveWhenDetachedProperty =
        AvaloniaProperty.Register<RtspPlayerView, bool>(nameof(KeepPlaybackAliveWhenDetached));

    public static readonly StyledProperty<double> VolumeProperty =
        AvaloniaProperty.Register<RtspPlayerView, double>(nameof(Volume), 1d);

    public static readonly StyledProperty<bool> IsMutedProperty =
        AvaloniaProperty.Register<RtspPlayerView, bool>(nameof(IsMuted));

    public static readonly StyledProperty<Stretch> StretchProperty =
        AvaloniaProperty.Register<RtspPlayerView, Stretch>(nameof(Stretch), Stretch.Uniform);

    public static readonly StyledProperty<RtspVideoTransform> VideoTransformProperty =
        AvaloniaProperty.Register<RtspPlayerView, RtspVideoTransform>(nameof(VideoTransform), new RtspVideoTransform());

    public static readonly DirectProperty<RtspPlayerView, RtspSessionState> StateProperty =
        AvaloniaProperty.RegisterDirect<RtspPlayerView, RtspSessionState>(nameof(State), view => view.State);

    public static readonly DirectProperty<RtspPlayerView, RtspSessionError?> LastErrorProperty =
        AvaloniaProperty.RegisterDirect<RtspPlayerView, RtspSessionError?>(nameof(LastError), view => view.LastError);

    public static readonly DirectProperty<RtspPlayerView, string?> ActiveRendererIdProperty =
        AvaloniaProperty.RegisterDirect<RtspPlayerView, string?>(nameof(ActiveRendererId), view => view.ActiveRendererId);

    public static readonly DirectProperty<RtspPlayerView, bool> IsHardwareAccelerationActiveProperty =
        AvaloniaProperty.RegisterDirect<RtspPlayerView, bool>(
            nameof(IsHardwareAccelerationActive),
            view => view.IsHardwareAccelerationActive);

    public static readonly DirectProperty<RtspPlayerView, string> HardwareDiagnosticsProperty =
        AvaloniaProperty.RegisterDirect<RtspPlayerView, string>(
            nameof(HardwareDiagnostics),
            view => view.HardwareDiagnostics);

    private readonly Image _image;
    private readonly RtspRendererBackendRegistry _rendererBackends;
    private readonly object _frameSync = new();
    private readonly SemaphoreSlim _backendGate = new(1, 1);
    private IRtspSessionFactory _sessionFactory;
    private IRtspSession? _session;
    private DesktopRtspVideoView? _nativePlayer;
#if !ANDROID
    private WindowsD3D11RtspVideoView? _windowsD3D11Player;
#endif
    private RtspVideoFrame? _pendingFrame;
    private WriteableBitmap? _bitmap;
    private RtspSessionState _state = RtspSessionState.Idle;
    private RtspSessionError? _lastError;
    private string? _activeRendererId;
    private bool _isHardwareAccelerationActive;
    private string _hardwareDiagnostics = "Not started";
    private bool _isAttached;
    private bool _renderScheduled;
    private bool _disposed;

    public RtspPlayerView(
        IRtspSessionFactory? sessionFactory = null,
        RtspRendererBackendRegistry? rendererBackends = null)
    {
        _sessionFactory = sessionFactory ?? new RtspSessionFactory();
        _rendererBackends = rendererBackends ?? RtspAvaloniaRendererBackends.CreateDefaultRegistry();
        _image = new Image { Stretch = Stretch };
        Content = _image;
        ApplyVideoTransform();
    }

    public RtspSource? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public RtspSessionOptions Options
    {
        get => GetValue(OptionsProperty);
        set => SetValue(OptionsProperty, value);
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

    public RtspVideoTransform VideoTransform
    {
        get => GetValue(VideoTransformProperty);
        set => SetValue(VideoTransformProperty, value);
    }

    public RtspSessionState State
    {
        get => _state;
        private set => SetAndRaise(StateProperty, ref _state, value);
    }

    public RtspSessionError? LastError
    {
        get => _lastError;
        private set => SetAndRaise(LastErrorProperty, ref _lastError, value);
    }

    public string? ActiveRendererId
    {
        get => _activeRendererId;
        private set => SetAndRaise(ActiveRendererIdProperty, ref _activeRendererId, value);
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

    public bool SupportsFrameSubscription => _session is not null;

    public event EventHandler<RtspVideoFrame>? FrameReceived;

    public event EventHandler<RtspSessionErrorEventArgs>? Error;

    public event EventHandler<RtspSessionStateChangedEventArgs>? StateChanged;

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        IsPlaybackEnabled = true;
        if (_isAttached)
        {
            await RunBackendOperationAsync(StartBackendAsync, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        IsPlaybackEnabled = false;
        await RunBackendOperationAsync(StopBackendAsync, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<RtspSnapshot?> CaptureSnapshotAsync(CancellationToken cancellationToken = default) =>
        _session?.CaptureSnapshotAsync(cancellationToken) ?? ValueTask.FromResult<RtspSnapshot?>(null);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        if (IsPlaybackEnabled)
        {
            _ = StartBackendSafelyAsync();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _isAttached = false;
        if (!KeepPlaybackAliveWhenDetached)
        {
            _ = StopBackendSafelyAsync();
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == StretchProperty)
        {
            _image.Stretch = Stretch;
#if !ANDROID
            if (_windowsD3D11Player is not null)
            {
                _windowsD3D11Player.Stretch = Stretch;
            }
#endif
        }
        else if (change.Property == VideoTransformProperty)
        {
            ApplyVideoTransform();
        }
        else if (change.Property == IsPlaybackEnabledProperty)
        {
            _ = IsPlaybackEnabled ? StartBackendSafelyAsync() : StopBackendSafelyAsync();
        }
        else if (change.Property == VolumeProperty)
        {
            if (_session is not null)
            {
                _session.Volume = Volume;
            }
            if (_nativePlayer is not null)
            {
                _nativePlayer.Volume = Volume;
            }
#if !ANDROID
            _windowsD3D11Player?.SetVolume(Volume);
#endif
        }
        else if (change.Property == IsMutedProperty)
        {
            if (_session is not null)
            {
                _session.IsMuted = IsMuted;
            }
            if (_nativePlayer is not null)
            {
                _nativePlayer.IsMuted = IsMuted;
            }
#if !ANDROID
            _windowsD3D11Player?.SetMuted(IsMuted);
#endif
        }
        else if (change.Property == SourceProperty || change.Property == OptionsProperty)
        {
            if (change.Property == OptionsProperty)
            {
                SetCurrentValue(VolumeProperty, Options.Volume);
                SetCurrentValue(IsMutedProperty, Options.IsMuted);
            }
            _ = RestartBackendSafelyAsync();
        }
        else if (change.Property == KeepPlaybackAliveWhenDetachedProperty && _nativePlayer is not null)
        {
            _nativePlayer.KeepPlaybackAliveWhenDetached = KeepPlaybackAliveWhenDetached;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await RunBackendOperationAsync(StopBackendAsync, CancellationToken.None).ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private async Task StartBackendSafelyAsync()
    {
        try
        {
            await RunBackendOperationAsync(StartBackendAsync, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ReportError("start_failed", exception.Message, false, exception);
        }
    }

    private async Task StopBackendSafelyAsync()
    {
        try
        {
            await RunBackendOperationAsync(StopBackendAsync, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ReportError("stop_failed", exception.Message, false, exception);
        }
    }

    private async Task RestartBackendSafelyAsync()
    {
        try
        {
            await RunBackendOperationAsync(
                async cancellationToken =>
                {
                    await StopBackendAsync(CancellationToken.None).ConfigureAwait(false);
                    if (_isAttached && IsPlaybackEnabled)
                    {
                        await StartBackendAsync(cancellationToken).ConfigureAwait(false);
                    }
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ReportError("restart_failed", exception.Message, false, exception);
        }
    }

    private async Task RunBackendOperationAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        await _backendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                await operation(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await Dispatcher.UIThread.InvokeAsync(
                    () => operation(cancellationToken));
            }
        }
        finally
        {
            _backendGate.Release();
        }
    }

    private async Task StartBackendAsync(CancellationToken cancellationToken)
    {
        if (_disposed || !_isAttached || !IsPlaybackEnabled || Source is null)
        {
            return;
        }

        var options = Options with { Volume = Volume, IsMuted = IsMuted };
        options.Validate();
        if (options.StreamSharing == RtspStreamSharingMode.Shared &&
            options.RenderPreference == RtspRenderPreference.NativeSurface)
        {
            throw new NotSupportedException(
                "Shared stream playback requires software rendering because a native surface cannot be shared by multiple views.");
        }

        var renderPreference = options.StreamSharing == RtspStreamSharingMode.Shared
            ? RtspRenderPreference.Software
            : options.RenderPreference;
        var backend = _rendererBackends.Select(renderPreference);
        if (backend is null)
        {
            throw new InvalidOperationException("No RTSP renderer is available for the requested preference.");
        }

        ActiveRendererId = backend.Id;
        if (backend.Preference == RtspRenderPreference.NativeSurface)
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                CreateNativePlayer();
            }
            else
            {
                await Dispatcher.UIThread.InvokeAsync(CreateNativePlayer);
            }
            return;
        }

        SetHardwareDiagnostics(false, "Software bitmap renderer");
        if (_session is not null)
        {
            return;
        }

        var session = _sessionFactory.Create(Source, options);
        session.StateChanged += OnSessionStateChanged;
        session.Error += OnSessionError;
        session.FrameReceived += OnFrameReceived;
        _session = session;
        await session.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task StopBackendAsync(CancellationToken cancellationToken)
    {
        var session = _session;
        _session = null;
        if (session is not null)
        {
            session.StateChanged -= OnSessionStateChanged;
            session.Error -= OnSessionError;
            session.FrameReceived -= OnFrameReceived;
            await session.StopAsync(cancellationToken).ConfigureAwait(false);
            await session.DisposeAsync().ConfigureAwait(false);
        }

        var nativePlayer = _nativePlayer;
        _nativePlayer = null;
        if (nativePlayer is not null)
        {
            nativePlayer.ConnectionStateChanged -= OnNativeConnectionStateChanged;
            nativePlayer.StreamError -= OnNativeStreamError;
            nativePlayer.PropertyChanged -= OnNativePlayerPropertyChanged;
            nativePlayer.IsPlaybackEnabled = false;
        }

#if !ANDROID
        var windowsD3D11Player = _windowsD3D11Player;
        _windowsD3D11Player = null;
        if (windowsD3D11Player is not null)
        {
            windowsD3D11Player.ConnectionStateChanged -= OnNativeConnectionStateChanged;
            windowsD3D11Player.StreamError -= OnNativeStreamError;
            windowsD3D11Player.HardwareAccelerationChanged -= OnWindowsHardwareAccelerationChanged;
            windowsD3D11Player.Stop();
        }
#endif

        lock (_frameSync)
        {
            _pendingFrame = null;
            _renderScheduled = false;
        }

        if (_isAttached)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!ReferenceEquals(Content, _image))
                {
                    Content = _image;
                }
                _image.Source = null;
#if !ANDROID
                windowsD3D11Player?.Dispose();
#endif
            });
        }
#if !ANDROID
        else
        {
            windowsD3D11Player?.Dispose();
        }
#endif

        if (State != RtspSessionState.Faulted)
        {
            SetState(RtspSessionState.Stopped);
        }
        SetHardwareDiagnostics(false, "Not started");
    }

    private void CreateNativePlayer()
    {
        if (_nativePlayer is not null ||
#if !ANDROID
            _windowsD3D11Player is not null ||
#endif
            Source is null)
        {
            return;
        }

        var options = Options with { Volume = Volume, IsMuted = IsMuted };
#if !ANDROID
        if (OperatingSystem.IsWindows())
        {
            var player = new WindowsD3D11RtspVideoView(Source, options, Stretch);
            player.ConnectionStateChanged += OnNativeConnectionStateChanged;
            player.StreamError += OnNativeStreamError;
            player.HardwareAccelerationChanged += OnWindowsHardwareAccelerationChanged;
            _windowsD3D11Player = player;
            Content = player;
            player.Start();
            return;
        }
#endif

        _nativePlayer = new DesktopRtspVideoView
        {
            StreamUrl = Source.Uri.ToString(),
            IsPlaybackEnabled = true,
            KeepPlaybackAliveWhenDetached = KeepPlaybackAliveWhenDetached,
            UseHardwareAcceleration = options.HardwareAcceleration != RtspHardwareAcceleration.Disabled,
            HardwareAccelerationMode = options.HardwareAcceleration switch
            {
                RtspHardwareAcceleration.Disabled => RtspHardwareAccelerationMode.Disabled,
                RtspHardwareAcceleration.Enabled => RtspHardwareAccelerationMode.Enabled,
                _ => RtspHardwareAccelerationMode.Auto
            },
            FallbackToSoftwareDecoding = options.FallbackToSoftwareDecoding,
            MaxFramesPerSecond = options.MaxFramesPerSecond,
            Transport = options.Transport.ToString().ToLowerInvariant(),
            OpenTimeoutMilliseconds = ToMilliseconds(options.OpenTimeout),
            EndpointProbeTimeoutMilliseconds = ToMilliseconds(options.EndpointProbeTimeout),
            ReadTimeoutMilliseconds = ToMilliseconds(options.ReadTimeout),
            ReconnectDelayMilliseconds = ToMilliseconds(options.ReconnectDelay),
            MaxConcurrentOpenStreams = options.MaxConcurrentOpenStreams,
            MaxVideoWidth = options.MaxVideoWidth,
            MaxVideoHeight = options.MaxVideoHeight,
            LowLatency = options.LowLatency,
            EnableAudio = options.EnableAudio,
            Volume = options.Volume,
            IsMuted = options.IsMuted,
            Stretch = Stretch
        };
#if !ANDROID
        _nativePlayer.RenderMode = RtspRenderMode.NativeSurface;
#endif
        _nativePlayer.ConnectionStateChanged += OnNativeConnectionStateChanged;
        _nativePlayer.StreamError += OnNativeStreamError;
        _nativePlayer.PropertyChanged += OnNativePlayerPropertyChanged;
        Content = _nativePlayer;
    }

    private void OnSessionStateChanged(object? sender, RtspSessionStateChangedEventArgs e) =>
        SetState(e.NewState);

    private void OnSessionError(object? sender, RtspSessionErrorEventArgs e) =>
        Dispatcher.UIThread.Post(() => ReportError(e.Error.Code, e.Error.Message, e.Error.WillRetry, e.Error.Exception), DispatcherPriority.Background);

    private void OnNativeConnectionStateChanged(object? sender, RtspConnectionStateChangedEventArgs e) =>
        SetState(e.NewState switch
        {
            RtspConnectionState.Connecting => RtspSessionState.Connecting,
            RtspConnectionState.Connected => RtspSessionState.Connected,
            RtspConnectionState.Reconnecting => RtspSessionState.Reconnecting,
            RtspConnectionState.Stopped => RtspSessionState.Stopped,
            _ => RtspSessionState.Idle
        });

    private void OnNativeStreamError(object? sender, RtspStreamErrorEventArgs e) =>
        ReportError(e.Error.Kind.ToString(), e.Error.Message, e.Error.WillRetry, e.Error.Exception);

    private void OnWindowsHardwareAccelerationChanged(object? sender, bool active)
    {
#if !ANDROID
        if (_windowsD3D11Player is { } player && ReferenceEquals(sender, player))
        {
            SetHardwareDiagnostics(active, player.HardwareDiagnostics);
        }
#endif
    }

    private void OnNativePlayerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (sender is DesktopRtspVideoView player &&
            (e.Property == DesktopRtspVideoView.IsHardwareAccelerationActiveProperty ||
             e.Property == DesktopRtspVideoView.RuntimeDiagnosticsSummaryProperty))
        {
            SetHardwareDiagnostics(
                player.IsHardwareAccelerationActive,
                player.RuntimeDiagnosticsSummary);
        }
    }

    private void SetHardwareDiagnostics(bool active, string diagnostics)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(
                () => SetHardwareDiagnostics(active, diagnostics),
                DispatcherPriority.Background);
            return;
        }

        IsHardwareAccelerationActive = active;
        HardwareDiagnostics = diagnostics;
    }

    private void OnFrameReceived(object? sender, RtspVideoFrame frame)
    {
        FrameReceived?.Invoke(this, frame);
        lock (_frameSync)
        {
            _pendingFrame = frame;
            if (_renderScheduled)
            {
                return;
            }

            _renderScheduled = true;
        }

        Dispatcher.UIThread.Post(RenderLatestFrame, DispatcherPriority.Render);
    }

    private unsafe void RenderLatestFrame()
    {
        RtspVideoFrame? frame;
        lock (_frameSync)
        {
            frame = _pendingFrame;
            _pendingFrame = null;
            _renderScheduled = false;
        }

        if (frame is null || _disposed)
        {
            return;
        }

        if (_bitmap is null ||
            _bitmap.PixelSize.Width != frame.Width ||
            _bitmap.PixelSize.Height != frame.Height)
        {
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(
                new PixelSize(frame.Width, frame.Height),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Unpremul);
            _image.Source = _bitmap;
        }

        using var framebuffer = _bitmap.Lock();
        var destinationLength = checked(framebuffer.RowBytes * frame.Height);
        var sourceLength = Math.Min(frame.Data.Length, checked(frame.Stride * frame.Height));
        var destination = new Span<byte>((void*)framebuffer.Address, destinationLength);
        frame.Data.Span[..Math.Min(sourceLength, destinationLength)].CopyTo(destination);
    }

    private void ApplyVideoTransform()
    {
        var transform = VideoTransform;
        _image.RenderTransform = new TransformGroup
        {
            Children =
            {
                new ScaleTransform(
                    transform.MirrorHorizontally ? -1 : 1,
                    transform.MirrorVertically ? -1 : 1),
                new RotateTransform(transform.NormalizedRotationDegrees)
            }
        };
    }

    private void ReportError(string code, string message, bool willRetry, Exception? exception)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(
                () => ReportError(code, message, willRetry, exception),
                DispatcherPriority.Background);
            return;
        }

        var error = new RtspSessionError(code, message, willRetry, exception);
        LastError = error;
        if (!willRetry)
        {
            SetState(RtspSessionState.Faulted);
        }
        Error?.Invoke(this, new RtspSessionErrorEventArgs(error));
    }

    private static int ToMilliseconds(TimeSpan value) =>
        checked((int)Math.Min(value.TotalMilliseconds, int.MaxValue));

    private void SetState(RtspSessionState state)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => SetState(state), DispatcherPriority.Background);
            return;
        }

        var oldState = State;
        if (oldState == state)
        {
            return;
        }
        State = state;
        StateChanged?.Invoke(this, new RtspSessionStateChangedEventArgs(oldState, state));
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(RtspPlayerView));
        }
    }
}
