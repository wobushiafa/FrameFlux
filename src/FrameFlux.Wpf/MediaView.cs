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

    private static readonly DependencyPropertyKey IsHardwareAccelerationActivePropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(IsHardwareAccelerationActive),
            typeof(bool),
            typeof(MediaView),
            new FrameworkPropertyMetadata(false));

    private static readonly DependencyPropertyKey HardwareDiagnosticsPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(HardwareDiagnostics),
            typeof(string),
            typeof(MediaView),
            new FrameworkPropertyMetadata("Not started"));

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

    public static readonly DependencyProperty KeepPlaybackAliveWhenUnloadedProperty =
        DependencyProperty.Register(
            nameof(KeepPlaybackAliveWhenUnloaded),
            typeof(bool),
            typeof(MediaView),
            new FrameworkPropertyMetadata(false));

    public static readonly DependencyProperty EnableAudioProperty =
        DependencyProperty.Register(
            nameof(EnableAudio),
            typeof(bool),
            typeof(MediaView),
            new FrameworkPropertyMetadata(true, OnRestartPropertyChanged));

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

    public static readonly DependencyProperty IsHardwareAccelerationActiveProperty =
        IsHardwareAccelerationActivePropertyKey.DependencyProperty;

    public static readonly DependencyProperty HardwareDiagnosticsProperty =
        HardwareDiagnosticsPropertyKey.DependencyProperty;

    private readonly MediaPlaybackController _playback = new();
    private readonly SoftwareBitmapMediaOutput _softwareOutput = new();
    private readonly D3D11SwapChainPresenter _nativePresenter = new();
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
        _softwareOutput.Stretch = Stretch;
        _softwareOutput.FramePresented += OnSoftwareFramePresented;
        Children.Add(_softwareOutput);
        _nativePresenter.Visibility = Visibility.Collapsed;
        Children.Add(_nativePresenter);
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

    public bool KeepPlaybackAliveWhenUnloaded
    {
        get => (bool)GetValue(KeepPlaybackAliveWhenUnloadedProperty);
        set => SetValue(KeepPlaybackAliveWhenUnloadedProperty, value);
    }

    public bool EnableAudio
    {
        get => (bool)GetValue(EnableAudioProperty);
        set => SetValue(EnableAudioProperty, value);
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

    public MediaPlaybackState State => (MediaPlaybackState)GetValue(StateProperty);

    public MediaPlaybackError? LastError => (MediaPlaybackError?)GetValue(LastErrorProperty);

    public bool IsHardwareAccelerationActive =>
        (bool)GetValue(IsHardwareAccelerationActiveProperty);

    public string HardwareDiagnostics => (string)GetValue(HardwareDiagnosticsProperty);

    public event EventHandler<MediaPlaybackStateChangedEventArgs>? PlaybackStateChanged;

    public event EventHandler<MediaPlaybackErrorEventArgs>? PlaybackError;

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        VerifyAccess();
        ThrowIfDisposed();
        ResetPresentation();
        var options = OpenOptions with
        {
            EnableAudio = EnableAudio
        };
        var useNativeOutput =
            options.StreamSharing == MediaStreamSharingMode.Dedicated &&
            options.RenderPreference == MediaRenderPreference.NativeSurface &&
            options.HardwareAcceleration != MediaHardwareAcceleration.Disabled &&
            OperatingSystem.IsWindows();
        _nativePresenter.SetStretch(Stretch);
        _nativePresenter.Visibility = useNativeOutput ? Visibility.Visible : Visibility.Collapsed;
        _softwareOutput.Visibility = useNativeOutput ? Visibility.Collapsed : Visibility.Visible;
        _playback.Volume = Volume;
        _playback.IsMuted = IsMuted;
        await _playback.StartAsync(
            PlayerFactory,
            Source,
            options,
            useNativeOutput ? _nativePresenter : _softwareOutput,
            cancellationToken);
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        VerifyAccess();
        await _playback.StopAsync(cancellationToken);
        ResetPresentation();
    }

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
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        _playback.StateChanged -= OnPlayerStateChanged;
        _playback.Error -= OnPlayerError;
        _softwareOutput.FramePresented -= OnSoftwareFramePresented;
        await _playback.DisposeAsync();
        ResetPresentation();
        _softwareOutput.Dispose();
        _nativePresenter.Dispose();
        GC.SuppressFinalize(this);
    }

    private static void OnRestartPropertyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var view = (MediaView)sender;
        if (view._isLoaded && (view.AutoPlay || view._playback.HasPlayer))
        {
            _ = view.RestartSafelyAsync();
        }
    }

    private static void OnAutoPlayChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var view = (MediaView)sender;
        if (view._isLoaded && (bool)args.NewValue && view.Source is not null)
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
        view._softwareOutput.Stretch = (Stretch)args.NewValue;
        view._nativePresenter.SetStretch((Stretch)args.NewValue);
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
        if (!KeepPlaybackAliveWhenUnloaded)
        {
            _ = StopSafelyAsync();
        }
    }

    private async Task RestartSafelyAsync()
    {
        await StopSafelyAsync();
        if (_isLoaded && Source is not null)
        {
            await StartSafelyAsync();
        }
    }

    private async Task StartSafelyAsync()
    {
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

    private void ResetPresentation()
    {
        _softwareOutput.Clear();
        _nativePresenter.ClearPendingFrame();
        _nativePresenter.Visibility = Visibility.Collapsed;
        _softwareOutput.Visibility = Visibility.Visible;
    }

    private void OnPlayerStateChanged(object? sender, MediaPlaybackStateChangedEventArgs args) =>
        Dispatcher.BeginInvoke(
            DispatcherPriority.DataBind,
            new Action(() =>
            {
                SetState(args.NewState);
                var diagnostics = _playback.Diagnostics;
                SetValue(
                    IsHardwareAccelerationActivePropertyKey,
                    diagnostics.IsHardwareAccelerationActive);
                SetValue(HardwareDiagnosticsPropertyKey, diagnostics.HardwareDiagnostics);
            }));

    private void OnPlayerError(object? sender, MediaPlaybackErrorEventArgs args) =>
        Dispatcher.BeginInvoke(
            DispatcherPriority.DataBind,
            new Action(() =>
            {
                ReportError(args.Error);
            }));

    private void OnSoftwareFramePresented(object? sender, EventArgs args)
    {
        _nativePresenter.Visibility = Visibility.Collapsed;
        _softwareOutput.Visibility = Visibility.Visible;
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
