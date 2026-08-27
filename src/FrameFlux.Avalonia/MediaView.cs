using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FrameFlux.Presentation;

namespace FrameFlux.Avalonia;

public sealed class MediaView : ContentControl, IAsyncDisposable, IMediaVideoOutput
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
    private readonly object _frameSync = new();
    private readonly Grid _surface = new();
    private readonly Image _image = new();
#if !ANDROID
    private readonly WindowsD3D11MediaOutput _nativeOutput = new();
#endif
    private IMediaFrameLease? _pendingFrame;
    private EventHandler<MediaVideoFrame>? _frameReceived;
    private WriteableBitmap? _bitmap;
    private MediaPlaybackState _state = MediaPlaybackState.Idle;
    private MediaPlaybackError? _lastError;
    private bool _isHardwareAccelerationActive;
    private string _hardwareDiagnostics = "Not started";
    private string? _activeRendererId;
    private bool _renderScheduled;
    private bool _attached;
    private bool _disposed;

    public MediaView()
    {
        _playback.StateChanged += OnPlayerStateChanged;
        _playback.Error += OnPlayerError;
        _image.Stretch = Stretch;
        _surface.Children.Add(_image);
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

    MediaRenderPreference IMediaVideoOutput.Preference => MediaRenderPreference.Software;

    bool IMediaVideoOutput.Supports(MediaFramePixelFormat pixelFormat) =>
        pixelFormat == MediaFramePixelFormat.Bgra32;

    bool IMediaVideoOutput.TryPresent(IMediaFrameLease frame)
    {
        if (_disposed ||
            frame.PixelFormat != MediaFramePixelFormat.Bgra32 ||
            !frame.TryGetCpuBuffer(out _))
        {
            return false;
        }

        IMediaFrameLease? droppedFrame;
        var schedule = false;
        lock (_frameSync)
        {
            if (_disposed)
            {
                return false;
            }

            droppedFrame = _pendingFrame;
            _pendingFrame = frame;
            if (!_renderScheduled)
            {
                _renderScheduled = true;
                schedule = true;
            }
        }

        droppedFrame?.Dispose();
        if (schedule)
        {
            try
            {
                Dispatcher.UIThread.Post(RenderLatestFrame, DispatcherPriority.Render);
            }
            catch
            {
                ClearPendingFrame();
            }
        }

        return true;
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
            _image.Stretch = Stretch;
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
        if (_frameReceived is not null)
        {
            _playback.FrameReceived -= OnFrameReceived;
        }
        await _playback.DisposeAsync();
        _bitmap?.Dispose();
        _bitmap = null;
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
        var output = useNativeOutput ? (IMediaVideoOutput)_nativeOutput : this;
#else
        useNativeOutput = false;
        var output = (IMediaVideoOutput)this;
#endif
        _image.IsVisible = !useNativeOutput;
        ActiveRendererId = useNativeOutput ? "windows-d3d11" : "software-bitmap";
        return output;
    }

    private void ResetPresentation()
    {
        ClearPendingFrame();
#if !ANDROID
        _nativeOutput.ClearPendingFrame();
        _nativeOutput.IsVisible = false;
#endif
        _image.IsVisible = true;
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

    private unsafe void RenderLatestFrame()
    {
        IMediaFrameLease? frame;
        lock (_frameSync)
        {
            frame = _pendingFrame;
            _pendingFrame = null;
            _renderScheduled = false;
        }

        if (frame is null)
        {
            return;
        }

        try
        {
            if (_disposed ||
                !frame.TryGetCpuBuffer(out var source) ||
                source.Plane0 == IntPtr.Zero ||
                source.Plane0Stride <= 0)
            {
                return;
            }

#if !ANDROID
            _nativeOutput.IsVisible = false;
#endif
            _image.IsVisible = true;
            ActiveRendererId = "software-bitmap";
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
            var rowBytes = Math.Min(
                checked(frame.Width * 4),
                Math.Min(source.Plane0Stride, framebuffer.RowBytes));
            var requiredSourceBytes =
                checked((long)source.Plane0Stride * (frame.Height - 1) + rowBytes);
            if (source.Size < requiredSourceBytes)
            {
                return;
            }

            for (var row = 0; row < frame.Height; row++)
            {
                var sourceRow = new ReadOnlySpan<byte>(
                    (byte*)source.Plane0 + row * source.Plane0Stride,
                    rowBytes);
                var destinationRow = new Span<byte>(
                    (byte*)framebuffer.Address + row * framebuffer.RowBytes,
                    rowBytes);
                sourceRow.CopyTo(destinationRow);
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError(
                "Avalonia software presentation failed: {0}",
                exception);
        }
        finally
        {
            frame.Dispose();
        }
    }

    private void ClearPendingFrame()
    {
        IMediaFrameLease? frame;
        lock (_frameSync)
        {
            frame = _pendingFrame;
            _pendingFrame = null;
            _renderScheduled = false;
        }

        frame?.Dispose();
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
