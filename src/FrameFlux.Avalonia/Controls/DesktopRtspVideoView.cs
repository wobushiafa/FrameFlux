#if !ANDROID
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using System.Threading.Tasks;

namespace FrameFlux.Avalonia;

public class DesktopRtspVideoView : ContentControl
{
    private Control? _activePlayer;
    private RtspConnectionState _connectionState = RtspConnectionState.Idle;
    private RtspStreamError? _lastError;
    private bool _isHardwareAccelerationActive;
    private RtspRenderMode _effectiveRenderMode = RtspRenderMode.SoftwareBitmap;
    private string _performanceSummary = string.Empty;
    private string _runtimeDiagnosticsSummary = string.Empty;
    private System.Threading.CancellationTokenSource? _shutdownCancellation;

    public static readonly StyledProperty<string> StreamUrlProperty =
        RtspVideoView.StreamUrlProperty.AddOwner<DesktopRtspVideoView>();

    public static readonly StyledProperty<bool> IsPlaybackEnabledProperty =
        RtspVideoView.IsPlaybackEnabledProperty.AddOwner<DesktopRtspVideoView>();

    public static readonly StyledProperty<bool> KeepPlaybackAliveWhenDetachedProperty =
        RtspVideoView.KeepPlaybackAliveWhenDetachedProperty.AddOwner<DesktopRtspVideoView>();

    public static readonly StyledProperty<bool> UseHardwareAccelerationProperty =
        RtspVideoView.UseHardwareAccelerationProperty.AddOwner<DesktopRtspVideoView>();

    public static readonly StyledProperty<RtspHardwareAccelerationMode> HardwareAccelerationModeProperty =
        RtspVideoView.HardwareAccelerationModeProperty.AddOwner<DesktopRtspVideoView>();

    public static readonly StyledProperty<RtspRenderMode> RenderModeProperty =
        RtspVideoView.RenderModeProperty.AddOwner<DesktopRtspVideoView>();

    public static readonly StyledProperty<bool> FallbackToSoftwareDecodingProperty =
        RtspVideoView.FallbackToSoftwareDecodingProperty.AddOwner<DesktopRtspVideoView>();

    public static readonly StyledProperty<double> MaxFramesPerSecondProperty =
        RtspVideoView.MaxFramesPerSecondProperty.AddOwner<DesktopRtspVideoView>();

    public static readonly StyledProperty<string> TransportProperty =
        RtspVideoView.TransportProperty.AddOwner<DesktopRtspVideoView>();

    public static readonly StyledProperty<int> OpenTimeoutMillisecondsProperty =
        RtspVideoView.OpenTimeoutMillisecondsProperty.AddOwner<DesktopRtspVideoView>();

    public static readonly StyledProperty<int> EndpointProbeTimeoutMillisecondsProperty =
        RtspVideoView.EndpointProbeTimeoutMillisecondsProperty.AddOwner<DesktopRtspVideoView>();

    public static readonly StyledProperty<int> ReadTimeoutMillisecondsProperty =
        RtspVideoView.ReadTimeoutMillisecondsProperty.AddOwner<DesktopRtspVideoView>();

    public static readonly StyledProperty<int> ReconnectDelayMillisecondsProperty =
        RtspVideoView.ReconnectDelayMillisecondsProperty.AddOwner<DesktopRtspVideoView>();

    public static readonly StyledProperty<int> MaxConcurrentOpenStreamsProperty =
        AvaloniaProperty.Register<DesktopRtspVideoView, int>(
            nameof(MaxConcurrentOpenStreams),
            RtspStreamOptions.DefaultMaxConcurrentOpenStreams);

    public static readonly StyledProperty<int> MaxVideoWidthProperty =
        RtspVideoView.MaxVideoWidthProperty.AddOwner<DesktopRtspVideoView>();

    public static readonly StyledProperty<int> MaxVideoHeightProperty =
        RtspVideoView.MaxVideoHeightProperty.AddOwner<DesktopRtspVideoView>();

    public static readonly StyledProperty<bool> LowLatencyProperty =
        RtspVideoView.LowLatencyProperty.AddOwner<DesktopRtspVideoView>();

    public static readonly StyledProperty<bool> EnableAudioProperty =
        RtspVideoView.EnableAudioProperty.AddOwner<DesktopRtspVideoView>();

    public static readonly StyledProperty<double> VolumeProperty =
        RtspVideoView.VolumeProperty.AddOwner<DesktopRtspVideoView>();

    public static readonly StyledProperty<bool> IsMutedProperty =
        RtspVideoView.IsMutedProperty.AddOwner<DesktopRtspVideoView>();

    public static readonly StyledProperty<Stretch> StretchProperty =
        RtspVideoView.StretchProperty.AddOwner<DesktopRtspVideoView>();

    public static readonly StyledProperty<RtspScaleQuality> ScaleQualityProperty =
        RtspVideoView.ScaleQualityProperty.AddOwner<DesktopRtspVideoView>();

    public static readonly DirectProperty<DesktopRtspVideoView, RtspConnectionState> ConnectionStateProperty =
        RtspVideoView.ConnectionStateProperty.AddOwner<DesktopRtspVideoView>(
            view => view.ConnectionState);

    public static readonly DirectProperty<DesktopRtspVideoView, RtspStreamError?> LastErrorProperty =
        RtspVideoView.LastErrorProperty.AddOwner<DesktopRtspVideoView>(
            view => view.LastError);

    public static readonly DirectProperty<DesktopRtspVideoView, bool> IsHardwareAccelerationActiveProperty =
        RtspVideoView.IsHardwareAccelerationActiveProperty.AddOwner<DesktopRtspVideoView>(
            view => view.IsHardwareAccelerationActive);

    public static readonly DirectProperty<DesktopRtspVideoView, RtspRenderMode> EffectiveRenderModeProperty =
        RtspVideoView.EffectiveRenderModeProperty.AddOwner<DesktopRtspVideoView>(
            view => view.EffectiveRenderMode);

    public static readonly DirectProperty<DesktopRtspVideoView, string> PerformanceSummaryProperty =
        RtspVideoView.PerformanceSummaryProperty.AddOwner<DesktopRtspVideoView>(
            view => view.PerformanceSummary);

    public static readonly DirectProperty<DesktopRtspVideoView, string> RuntimeDiagnosticsSummaryProperty =
        AvaloniaProperty.RegisterDirect<DesktopRtspVideoView, string>(
            nameof(RuntimeDiagnosticsSummary),
            view => view.RuntimeDiagnosticsSummary);

    public event EventHandler<RtspConnectionStateChangedEventArgs>? ConnectionStateChanged;
    public event EventHandler<RtspStreamErrorEventArgs>? StreamError;

    public string StreamUrl
    {
        get => GetValue(StreamUrlProperty);
        set => SetValue(StreamUrlProperty, value);
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

    public bool UseHardwareAcceleration
    {
        get => GetValue(UseHardwareAccelerationProperty);
        set => SetValue(UseHardwareAccelerationProperty, value);
    }

    public RtspHardwareAccelerationMode HardwareAccelerationMode
    {
        get => GetValue(HardwareAccelerationModeProperty);
        set => SetValue(HardwareAccelerationModeProperty, value);
    }

    public RtspRenderMode RenderMode
    {
        get => GetValue(RenderModeProperty);
        set => SetValue(RenderModeProperty, value);
    }

    public bool FallbackToSoftwareDecoding
    {
        get => GetValue(FallbackToSoftwareDecodingProperty);
        set => SetValue(FallbackToSoftwareDecodingProperty, value);
    }

    public double MaxFramesPerSecond
    {
        get => GetValue(MaxFramesPerSecondProperty);
        set => SetValue(MaxFramesPerSecondProperty, value);
    }

    public string Transport
    {
        get => GetValue(TransportProperty);
        set => SetValue(TransportProperty, value);
    }

    public int OpenTimeoutMilliseconds
    {
        get => GetValue(OpenTimeoutMillisecondsProperty);
        set => SetValue(OpenTimeoutMillisecondsProperty, value);
    }

    public int EndpointProbeTimeoutMilliseconds
    {
        get => GetValue(EndpointProbeTimeoutMillisecondsProperty);
        set => SetValue(EndpointProbeTimeoutMillisecondsProperty, value);
    }

    public int ReadTimeoutMilliseconds
    {
        get => GetValue(ReadTimeoutMillisecondsProperty);
        set => SetValue(ReadTimeoutMillisecondsProperty, value);
    }

    public int ReconnectDelayMilliseconds
    {
        get => GetValue(ReconnectDelayMillisecondsProperty);
        set => SetValue(ReconnectDelayMillisecondsProperty, value);
    }

    public int MaxConcurrentOpenStreams
    {
        get => GetValue(MaxConcurrentOpenStreamsProperty);
        set => SetValue(MaxConcurrentOpenStreamsProperty, value);
    }

    public int MaxVideoWidth
    {
        get => GetValue(MaxVideoWidthProperty);
        set => SetValue(MaxVideoWidthProperty, value);
    }

    public int MaxVideoHeight
    {
        get => GetValue(MaxVideoHeightProperty);
        set => SetValue(MaxVideoHeightProperty, value);
    }

    public bool LowLatency
    {
        get => GetValue(LowLatencyProperty);
        set => SetValue(LowLatencyProperty, value);
    }

    public bool EnableAudio
    {
        get => GetValue(EnableAudioProperty);
        set => SetValue(EnableAudioProperty, value);
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

    public RtspScaleQuality ScaleQuality
    {
        get => GetValue(ScaleQualityProperty);
        set => SetValue(ScaleQualityProperty, value);
    }

    public RtspConnectionState ConnectionState
    {
        get => _connectionState;
        private set => SetConnectionState(value);
    }

    public RtspStreamError? LastError
    {
        get => _lastError;
        private set => SetAndRaise(LastErrorProperty, ref _lastError, value);
    }

    public bool IsHardwareAccelerationActive
    {
        get => _isHardwareAccelerationActive;
        private set => SetAndRaise(IsHardwareAccelerationActiveProperty, ref _isHardwareAccelerationActive, value);
    }

    public RtspRenderMode EffectiveRenderMode
    {
        get => _effectiveRenderMode;
        private set => SetAndRaise(EffectiveRenderModeProperty, ref _effectiveRenderMode, value);
    }

    public string PerformanceSummary
    {
        get => _performanceSummary;
        private set => SetAndRaise(PerformanceSummaryProperty, ref _performanceSummary, value);
    }

    public string RuntimeDiagnosticsSummary
    {
        get => _runtimeDiagnosticsSummary;
        private set => SetAndRaise(RuntimeDiagnosticsSummaryProperty, ref _runtimeDiagnosticsSummary, value);
    }

    public DesktopRtspVideoView()
    {
        RebuildPlayer();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        
        if (_shutdownCancellation != null)
        {
            _shutdownCancellation.Cancel();
            _shutdownCancellation.Dispose();
            _shutdownCancellation = null;
        }

        if (_activePlayer == null)
        {
            RebuildPlayer();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        
        _shutdownCancellation?.Cancel();
        _shutdownCancellation?.Dispose();
        _shutdownCancellation = null;

        if (KeepPlaybackAliveWhenDetached)
        {
            return;
        }

        var cancellation = new System.Threading.CancellationTokenSource();
        _shutdownCancellation = cancellation;
        var token = cancellation.Token;
        
        _ = ShutdownAfterDelayAsync();

        async Task ShutdownAfterDelayAsync()
        {
            try
            {
                await Task.Delay(1500, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (token.IsCancellationRequested ||
                    !ReferenceEquals(_shutdownCancellation, cancellation))
                {
                    return;
                }

                _shutdownCancellation = null;
                cancellation.Dispose();
                UnsubscribeFromPlayer(_activePlayer);
                ShutdownPlayer(_activePlayer);
                _activePlayer = null;
            }, DispatcherPriority.Background);
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == RenderModeProperty)
        {
            RebuildPlayer();
        }
    }

    private void RebuildPlayer()
    {
        var oldPlayer = _activePlayer;
        UnsubscribeFromPlayer(oldPlayer);
        
        ShutdownPlayer(oldPlayer);
        Content = null;

        var effectiveRenderMode = RenderMode;
        if (effectiveRenderMode == RtspRenderMode.Auto && OperatingSystem.IsWindows())
        {
            effectiveRenderMode = RtspRenderMode.SoftwareBitmap;
        }

        _activePlayer = effectiveRenderMode switch
        {
            RtspRenderMode.NativeSurface => new DesktopOpenGlRtspVideoView(),
            RtspRenderMode.SoftwareBitmap => new RtspVideoView { RenderMode = RtspRenderMode.SoftwareBitmap },
            _ => CreateDefaultPlayerForCurrentPlatform()
        };

        BindPlayerProperties(_activePlayer);
        SubscribeToPlayer(_activePlayer);
        SyncStateFromActivePlayer();
        Content = _activePlayer;
    }

    private static void ShutdownPlayer(Control? player)
    {
        switch (player)
        {
            case DesktopOpenGlRtspVideoView nativePlayer:
                nativePlayer.Shutdown();
                break;
            case RtspVideoView softwarePlayer:
                softwarePlayer.Shutdown();
                break;
        }
    }

    private static Control CreateDefaultPlayerForCurrentPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return new RtspVideoView { RenderMode = RtspRenderMode.SoftwareBitmap };
        }

        return new DesktopOpenGlRtspVideoView();
    }

    private void BindPlayerProperties(Control player)
    {
        var propertiesToBind = new AvaloniaProperty[]
        {
            StreamUrlProperty,
            IsPlaybackEnabledProperty,
            UseHardwareAccelerationProperty,
            HardwareAccelerationModeProperty,
            RenderModeProperty,
            FallbackToSoftwareDecodingProperty,
            MaxFramesPerSecondProperty,
            TransportProperty,
            OpenTimeoutMillisecondsProperty,
            EndpointProbeTimeoutMillisecondsProperty,
            ReadTimeoutMillisecondsProperty,
            ReconnectDelayMillisecondsProperty,
            MaxConcurrentOpenStreamsProperty,
            MaxVideoWidthProperty,
            MaxVideoHeightProperty,
            LowLatencyProperty,
            EnableAudioProperty,
            VolumeProperty,
            IsMutedProperty,
            ScaleQualityProperty,
            StretchProperty
        };

        foreach (var property in propertiesToBind)
        {
            player.Bind(property, this.GetObservable(property));
        }

        if (player is RtspVideoView softwarePlayer)
        {
            softwarePlayer.Bind(
                RtspVideoView.KeepPlaybackAliveWhenDetachedProperty,
                this.GetObservable(KeepPlaybackAliveWhenDetachedProperty));
            softwarePlayer.Bind(StretchProperty, this.GetObservable(StretchProperty));
            PerformanceSummary = softwarePlayer.PerformanceSummary;
        }
        else if (player is DesktopOpenGlRtspVideoView nativePlayer)
        {
            nativePlayer.Bind(
                DesktopOpenGlRtspVideoView.KeepPlaybackAliveWhenDetachedProperty,
                this.GetObservable(KeepPlaybackAliveWhenDetachedProperty));
            PerformanceSummary = nativePlayer.PerformanceSummary;
        }
        
        RefreshRuntimeDiagnosticsSummary();
    }

    private void SubscribeToPlayer(Control? player)
    {
        switch (player)
        {
            case DesktopOpenGlRtspVideoView nativePlayer:
                nativePlayer.ConnectionStateChanged += OnNativeConnectionStateChanged;
                nativePlayer.StreamError += OnNativeStreamError;
                nativePlayer.PropertyChanged += OnNativePlayerPropertyChanged;
                UpdateState(
                    nativePlayer.ConnectionState,
                    nativePlayer.LastError,
                    nativePlayer.IsHardwareAccelerationActive,
                    nativePlayer.EffectiveRenderMode,
                    nativePlayer.PerformanceSummary);
                break;
            case RtspVideoView softwarePlayer:
                softwarePlayer.ConnectionStateChanged += OnSoftwareConnectionStateChanged;
                softwarePlayer.StreamError += OnSoftwareStreamError;
                softwarePlayer.PropertyChanged += OnSoftwarePlayerPropertyChanged;
                UpdateState(
                    softwarePlayer.ConnectionState,
                    softwarePlayer.LastError,
                    softwarePlayer.IsHardwareAccelerationActive,
                    softwarePlayer.EffectiveRenderMode,
                    softwarePlayer.PerformanceSummary);
                break;
        }
    }

    private void UnsubscribeFromPlayer(Control? player)
    {
        switch (player)
        {
            case DesktopOpenGlRtspVideoView nativePlayer:
                nativePlayer.ConnectionStateChanged -= OnNativeConnectionStateChanged;
                nativePlayer.StreamError -= OnNativeStreamError;
                nativePlayer.PropertyChanged -= OnNativePlayerPropertyChanged;
                break;
            case RtspVideoView softwarePlayer:
                softwarePlayer.ConnectionStateChanged -= OnSoftwareConnectionStateChanged;
                softwarePlayer.StreamError -= OnSoftwareStreamError;
                softwarePlayer.PropertyChanged -= OnSoftwarePlayerPropertyChanged;
                break;
        }
    }

    private void OnNativePlayerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, _activePlayer))
        {
            return;
        }

        if (sender is DesktopOpenGlRtspVideoView nativePlayer)
        {
            if (e.Property == DesktopOpenGlRtspVideoView.PerformanceSummaryProperty)
            {
                SyncStateFromActivePlayer();
            }
            else if (e.Property == DesktopOpenGlRtspVideoView.IsHardwareAccelerationActiveProperty)
            {
                SyncStateFromActivePlayer();
            }
            else if (e.Property == DesktopOpenGlRtspVideoView.EffectiveRenderModeProperty)
            {
                SyncStateFromActivePlayer();
            }
        }
    }

    private void OnSoftwarePlayerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, _activePlayer))
        {
            return;
        }

        if (sender is RtspVideoView softwarePlayer)
        {
            if (e.Property == RtspVideoView.PerformanceSummaryProperty)
            {
                SyncStateFromActivePlayer();
            }
            else if (e.Property == RtspVideoView.IsHardwareAccelerationActiveProperty)
            {
                SyncStateFromActivePlayer();
            }
            else if (e.Property == RtspVideoView.EffectiveRenderModeProperty)
            {
                SyncStateFromActivePlayer();
            }
        }
    }

    private void OnNativeConnectionStateChanged(object? sender, RtspConnectionStateChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, _activePlayer))
        {
            return;
        }

        if (sender is DesktopOpenGlRtspVideoView nativePlayer)
        {
            UpdateState(e.NewState, nativePlayer.LastError, nativePlayer.IsHardwareAccelerationActive, nativePlayer.EffectiveRenderMode, nativePlayer.PerformanceSummary);
        }
    }

    private void OnSoftwareConnectionStateChanged(object? sender, RtspConnectionStateChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, _activePlayer))
        {
            return;
        }

        if (sender is RtspVideoView softwarePlayer)
        {
            UpdateState(e.NewState, softwarePlayer.LastError, softwarePlayer.IsHardwareAccelerationActive, softwarePlayer.EffectiveRenderMode, softwarePlayer.PerformanceSummary);
        }
    }

    private void OnNativeStreamError(object? sender, RtspStreamErrorEventArgs e)
    {
        if (!ReferenceEquals(sender, _activePlayer))
        {
            return;
        }

        if (sender is DesktopOpenGlRtspVideoView nativePlayer)
        {
            UpdateState(nativePlayer.ConnectionState, e.Error, nativePlayer.IsHardwareAccelerationActive, nativePlayer.EffectiveRenderMode, nativePlayer.PerformanceSummary);
        }

        StreamError?.Invoke(this, e);
    }

    private void SyncStateFromActivePlayer()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(SyncStateFromActivePlayer, DispatcherPriority.Background);
            return;
        }

        switch (_activePlayer)
        {
            case DesktopOpenGlRtspVideoView nativePlayer:
                UpdateState(
                    nativePlayer.ConnectionState,
                    nativePlayer.LastError,
                    nativePlayer.IsHardwareAccelerationActive,
                    nativePlayer.EffectiveRenderMode,
                    nativePlayer.PerformanceSummary);
                break;
            case RtspVideoView softwarePlayer:
                UpdateState(
                    softwarePlayer.ConnectionState,
                    softwarePlayer.LastError,
                    softwarePlayer.IsHardwareAccelerationActive,
                    softwarePlayer.EffectiveRenderMode,
                    softwarePlayer.PerformanceSummary);
                break;
            default:
                UpdateState(RtspConnectionState.Idle, null, false, RtspRenderMode.SoftwareBitmap, string.Empty);
                break;
        }
    }

    private void OnSoftwareStreamError(object? sender, RtspStreamErrorEventArgs e)
    {
        if (!ReferenceEquals(sender, _activePlayer))
        {
            return;
        }

        if (sender is RtspVideoView softwarePlayer)
        {
            UpdateState(softwarePlayer.ConnectionState, e.Error, softwarePlayer.IsHardwareAccelerationActive, softwarePlayer.EffectiveRenderMode, softwarePlayer.PerformanceSummary);
        }

        StreamError?.Invoke(this, e);
    }

    private void UpdateState(RtspConnectionState connectionState, RtspStreamError? lastError, bool hardwareAccelerationActive, RtspRenderMode effectiveRenderMode, string performanceSummary)
    {
        ConnectionState = connectionState;
        LastError = lastError;
        IsHardwareAccelerationActive = hardwareAccelerationActive;
        EffectiveRenderMode = effectiveRenderMode;
        PerformanceSummary = performanceSummary;
        RefreshRuntimeDiagnosticsSummary();
    }

    private void SetConnectionState(RtspConnectionState state)
    {
        var oldState = _connectionState;
        if (oldState == state)
        {
            return;
        }

        SetAndRaise(ConnectionStateProperty, ref _connectionState, state);
        if (state == RtspConnectionState.Connected)
        {
            LastError = null;
        }

        ConnectionStateChanged?.Invoke(this, new RtspConnectionStateChangedEventArgs(oldState, state));
    }

    private void RefreshRuntimeDiagnosticsSummary()
    {
        var playerType = _activePlayer?.GetType().Name ?? "None";
        var configuredMode =
            RtspPlaybackConfiguration.ResolveHardwareAccelerationMode(HardwareAccelerationMode, UseHardwareAcceleration);
        var effectiveHardwareRequest =
            RtspPlaybackConfiguration.ResolveUseHardwareAcceleration(
                configuredMode,
                UseHardwareAcceleration,
                EffectiveRenderMode);
        var inefficientCombination =
            RtspPlaybackConfiguration.IsInefficientWindowsCombination(effectiveHardwareRequest, EffectiveRenderMode);
        RuntimeDiagnosticsSummary =
            $"Mode: {EffectiveRenderMode}, HW_ACT: {IsHardwareAccelerationActive}, HW_REQ: {configuredMode}, HW_EFF: {effectiveHardwareRequest}, BadCombo: {inefficientCombination}, Player: {playerType}";
    }
}
#else
using Avalonia;

namespace FrameFlux.Avalonia;

public class DesktopRtspVideoView : DesktopOpenGlRtspVideoView
{
    public static readonly DirectProperty<DesktopRtspVideoView, string> RuntimeDiagnosticsSummaryProperty =
        AvaloniaProperty.RegisterDirect<DesktopRtspVideoView, string>(
            nameof(RuntimeDiagnosticsSummary),
            view => view.RuntimeDiagnosticsSummary);

    private string _runtimeDiagnosticsSummary = string.Empty;

    public string RuntimeDiagnosticsSummary
    {
        get => _runtimeDiagnosticsSummary;
        private set => SetAndRaise(RuntimeDiagnosticsSummaryProperty, ref _runtimeDiagnosticsSummary, value);
    }
}
#endif
