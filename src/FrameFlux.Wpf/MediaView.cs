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

    public MediaPlaybackState State => (MediaPlaybackState)GetValue(StateProperty);

    public MediaPlaybackError? LastError => (MediaPlaybackError?)GetValue(LastErrorProperty);

    public bool IsHardwareVideoDecodingActive =>
        (bool)GetValue(IsHardwareVideoDecodingActiveProperty);

    public string VideoDecoderDiagnostics => (string)GetValue(VideoDecoderDiagnosticsProperty);

    public MediaVideoPresentationMode? EffectivePresentationMode =>
        (MediaVideoPresentationMode?)GetValue(EffectivePresentationModeProperty);

    public event EventHandler<MediaPlaybackStateChangedEventArgs>? PlaybackStateChanged;

    public event EventHandler<MediaPlaybackErrorEventArgs>? PlaybackError;

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        VerifyAccess();
        ThrowIfDisposed();
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
        await _playback.DisposeAsync();
        _presentation.Reset();
        _presentation.Dispose();
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
