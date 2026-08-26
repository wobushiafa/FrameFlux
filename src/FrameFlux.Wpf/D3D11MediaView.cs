using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FrameFlux.FFmpeg;

namespace FrameFlux.Wpf;

public sealed class D3D11MediaView : Grid, IAsyncDisposable
{
    private static readonly DependencyPropertyKey StatePropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(State),
            typeof(MediaPlaybackState),
            typeof(D3D11MediaView),
            new FrameworkPropertyMetadata(MediaPlaybackState.Idle));

    private static readonly DependencyPropertyKey LastErrorPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(LastError),
            typeof(MediaPlaybackError),
            typeof(D3D11MediaView),
            new FrameworkPropertyMetadata(null));

    private static readonly DependencyPropertyKey IsHardwareAccelerationActivePropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(IsHardwareAccelerationActive),
            typeof(bool),
            typeof(D3D11MediaView),
            new FrameworkPropertyMetadata(false));

    private static readonly DependencyPropertyKey HardwareDiagnosticsPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(HardwareDiagnostics),
            typeof(string),
            typeof(D3D11MediaView),
            new FrameworkPropertyMetadata("Not started"));

    public static readonly DependencyProperty SourceProperty =
        DependencyProperty.Register(
            nameof(Source),
            typeof(MediaSource),
            typeof(D3D11MediaView),
            new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty OpenOptionsProperty =
        DependencyProperty.Register(
            nameof(OpenOptions),
            typeof(MediaOpenOptions),
            typeof(D3D11MediaView),
            new FrameworkPropertyMetadata(new MediaOpenOptions()),
            value => value is MediaOpenOptions);

    public static readonly DependencyProperty AutoPlayProperty =
        DependencyProperty.Register(
            nameof(AutoPlay),
            typeof(bool),
            typeof(D3D11MediaView),
            new FrameworkPropertyMetadata(true));

    public static readonly DependencyProperty EnableAudioProperty =
        DependencyProperty.Register(
            nameof(EnableAudio),
            typeof(bool),
            typeof(D3D11MediaView),
            new FrameworkPropertyMetadata(true));

    public static readonly DependencyProperty VolumeProperty =
        DependencyProperty.Register(
            nameof(Volume),
            typeof(double),
            typeof(D3D11MediaView),
            new FrameworkPropertyMetadata(1d, OnVolumeChanged),
            value => value is double volume && volume is >= 0d and <= 1d);

    public static readonly DependencyProperty IsMutedProperty =
        DependencyProperty.Register(
            nameof(IsMuted),
            typeof(bool),
            typeof(D3D11MediaView),
            new FrameworkPropertyMetadata(false, OnMutedChanged));

    public static readonly DependencyProperty StretchProperty =
        DependencyProperty.Register(
            nameof(Stretch),
            typeof(System.Windows.Media.Stretch),
            typeof(D3D11MediaView),
            new FrameworkPropertyMetadata(
                System.Windows.Media.Stretch.Uniform,
                OnStretchChanged));

    public static readonly DependencyProperty StateProperty = StatePropertyKey.DependencyProperty;
    public static readonly DependencyProperty LastErrorProperty = LastErrorPropertyKey.DependencyProperty;
    public static readonly DependencyProperty IsHardwareAccelerationActiveProperty =
        IsHardwareAccelerationActivePropertyKey.DependencyProperty;
    public static readonly DependencyProperty HardwareDiagnosticsProperty =
        HardwareDiagnosticsPropertyKey.DependencyProperty;

    private readonly D3D11SwapChainPresenter _presenter = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private RtspStreamClient? _client;
    private bool _disposed;

    public D3D11MediaView()
    {
        Background = System.Windows.Media.Brushes.Black;
        Children.Add(_presenter);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
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

    public bool AutoPlay
    {
        get => (bool)GetValue(AutoPlayProperty);
        set => SetValue(AutoPlayProperty, value);
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

    public System.Windows.Media.Stretch Stretch
    {
        get => (System.Windows.Media.Stretch)GetValue(StretchProperty);
        set => SetValue(StretchProperty, value);
    }

    public MediaPlaybackState State => (MediaPlaybackState)GetValue(StateProperty);
    public MediaPlaybackError? LastError => (MediaPlaybackError?)GetValue(LastErrorProperty);
    public bool IsHardwareAccelerationActive => (bool)GetValue(IsHardwareAccelerationActiveProperty);
    public string HardwareDiagnostics => (string)GetValue(HardwareDiagnosticsProperty);

    public event EventHandler<MediaPlaybackStateChangedEventArgs>? PlaybackStateChanged;
    public event EventHandler<MediaPlaybackErrorEventArgs>? PlaybackError;

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            StopClient();
            var source = Source ?? throw new InvalidOperationException("A media source is required before playback can start.");
            if (source.Uri.Scheme is not ("rtsp" or "rtsps"))
            {
                throw new NotSupportedException("The D3D11 view currently supports RTSP sources.");
            }

            SetValue(LastErrorPropertyKey, null);
            SetState(MediaPlaybackState.Opening);
            var options = OpenOptions;
            var client = new RtspStreamClient(
                source.Uri.ToString(),
                new RtspStreamOptions
                {
                    UseHardwareAcceleration = true,
                    HardwareAccelerationMode = RtspHardwareAccelerationMode.Enabled,
                    RenderMode = RtspRenderMode.NativeSurface,
                    FallbackToSoftwareDecoding = false,
                    Transport = options.Transport == MediaTransport.Udp ? "udp" : "tcp",
                    OpenTimeoutMilliseconds = ToMilliseconds(options.OpenTimeout),
                    EndpointProbeTimeoutMilliseconds = ToMilliseconds(options.EndpointProbeTimeout),
                    ReadTimeoutMilliseconds = ToMilliseconds(options.ReadTimeout),
                    ReconnectDelayMilliseconds = ToMilliseconds(options.ReconnectDelay),
                    MaxConcurrentOpenStreams = options.MaxConcurrentOpenStreams,
                    MaxFramesPerSecond = options.MaxFramesPerSecond,
                    MaxVideoWidth = options.MaxVideoWidth,
                    MaxVideoHeight = options.MaxVideoHeight,
                    LowLatency = options.LowLatency,
                    EnableAudio = EnableAudio,
                    Volume = Volume,
                    IsMuted = IsMuted
                });
            client.OnFrameLeaseReceived += OnFrameReceived;
            client.ConnectionStateChanged += OnConnectionStateChanged;
            client.StreamError += OnStreamError;
            client.HardwareAccelerationChanged += OnHardwareAccelerationChanged;
            client.SetFrameDeliveryEnabled(true);
            _client = client;
            client.Start();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        VerifyAccess();
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            StopClient();
            SetState(MediaPlaybackState.Stopped);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        VerifyAccess();
        if (_disposed) return;
        await StopAsync();
        _disposed = true;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        _lifecycleGate.Dispose();
        GC.SuppressFinalize(this);
    }

    private void OnFrameReceived(RtspFrameLease lease)
    {
        try
        {
            _presenter.Present(lease);
        }
        catch (Exception exception)
        {
            DispatchError("D3D11PresentFailed", exception.Message, exception);
        }
        finally
        {
            lease.Dispose();
        }
    }

    private void OnConnectionStateChanged(object? sender, RtspConnectionStateChangedEventArgs args) =>
        Dispatcher.BeginInvoke(
            DispatcherPriority.DataBind,
            new Action(() =>
            {
                if (!ReferenceEquals(_client, sender)) return;
                SetState(args.NewState switch
                {
                    RtspConnectionState.Connecting => MediaPlaybackState.Opening,
                    RtspConnectionState.Connected => MediaPlaybackState.Playing,
                    RtspConnectionState.Reconnecting => MediaPlaybackState.Reconnecting,
                    RtspConnectionState.Stopped => MediaPlaybackState.Stopped,
                    _ => MediaPlaybackState.Idle
                });
            }));

    private void OnStreamError(object? sender, RtspStreamErrorEventArgs args) =>
        DispatchError(args.Error.Kind.ToString(), args.Error.Message, args.Error.Exception);

    private void OnHardwareAccelerationChanged(object? sender, bool active) =>
        Dispatcher.BeginInvoke(
            DispatcherPriority.DataBind,
            new Action(() =>
            {
                if (!ReferenceEquals(_client, sender)) return;
                SetValue(IsHardwareAccelerationActivePropertyKey, active);
                SetValue(HardwareDiagnosticsPropertyKey, _client!.HardwareDiagnostics);
            }));

    private void DispatchError(string code, string message, Exception? exception) =>
        Dispatcher.BeginInvoke(
            DispatcherPriority.DataBind,
            new Action(() =>
            {
                var error = new MediaPlaybackError(code, message, false, exception);
                SetValue(LastErrorPropertyKey, error);
                PlaybackError?.Invoke(this, new MediaPlaybackErrorEventArgs(error));
            }));

    private void StopClient()
    {
        var client = _client;
        _client = null;
        if (client is null) return;
        client.OnFrameLeaseReceived -= OnFrameReceived;
        client.ConnectionStateChanged -= OnConnectionStateChanged;
        client.StreamError -= OnStreamError;
        client.HardwareAccelerationChanged -= OnHardwareAccelerationChanged;
        client.Stop(waitForExit: true);
        client.Dispose();
        SetValue(IsHardwareAccelerationActivePropertyKey, false);
    }

    private void SetState(MediaPlaybackState state)
    {
        var oldState = State;
        if (oldState == state) return;
        SetValue(StatePropertyKey, state);
        PlaybackStateChanged?.Invoke(this, new MediaPlaybackStateChangedEventArgs(oldState, state));
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (AutoPlay && Source is not null)
        {
            _ = StartSafelyAsync();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs args) => _ = StopSafelyAsync();

    private async Task StartSafelyAsync()
    {
        try
        {
            await StartAsync();
        }
        catch (Exception exception)
        {
            DispatchError("OpenFailed", exception.Message, exception);
            SetState(MediaPlaybackState.Faulted);
        }
    }

    private async Task StopSafelyAsync()
    {
        try
        {
            await StopAsync();
        }
        catch
        {
        }
    }

    private static void OnVolumeChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((D3D11MediaView)sender)._client?.SetVolume((double)args.NewValue);

    private static void OnMutedChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((D3D11MediaView)sender)._client?.SetMuted((bool)args.NewValue);

    private static void OnStretchChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((D3D11MediaView)sender)._presenter.SetStretch((System.Windows.Media.Stretch)args.NewValue);

    private static int ToMilliseconds(TimeSpan value) =>
        checked((int)Math.Min(value.TotalMilliseconds, int.MaxValue));
}
