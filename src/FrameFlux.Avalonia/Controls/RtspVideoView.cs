using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace FrameFlux.Avalonia;

public class RtspVideoView : Control
{
    private IRtspFrameRenderer? _frameRenderer;
    private RtspStreamClient? _streamClient;
    private RtspConnectionState _connectionState = RtspConnectionState.Idle;
    private RtspStreamError? _lastError;
    private bool _isHardwareAccelerationActive;
    private RtspRenderMode _effectiveRenderMode = RtspRenderMode.SoftwareBitmap;
    private bool _isAttached;
    private volatile bool _isVisuallyAttached;
    private bool _restartPending;
    private int _streamVersion;
    private string _performanceSummary = string.Empty;
    private string _streamPerformanceSummary = string.Empty;
    private string _rendererPerformanceSummary = string.Empty;
    private long _rendererTotalTicks;
    private int _rendererSamples;
    private CancellationTokenSource? _shutdownCancellation;

    public static readonly StyledProperty<string> StreamUrlProperty =
        AvaloniaProperty.Register<RtspVideoView, string>(nameof(StreamUrl));

    public static readonly StyledProperty<bool> IsPlaybackEnabledProperty =
        AvaloniaProperty.Register<RtspVideoView, bool>(nameof(IsPlaybackEnabled), true);

    public static readonly StyledProperty<bool> KeepPlaybackAliveWhenDetachedProperty =
        AvaloniaProperty.Register<RtspVideoView, bool>(nameof(KeepPlaybackAliveWhenDetached));

    public static readonly StyledProperty<bool> UseHardwareAccelerationProperty =
        AvaloniaProperty.Register<RtspVideoView, bool>(nameof(UseHardwareAcceleration), true);

    public static readonly StyledProperty<RtspHardwareAccelerationMode> HardwareAccelerationModeProperty =
        AvaloniaProperty.Register<RtspVideoView, RtspHardwareAccelerationMode>(
            nameof(HardwareAccelerationMode),
            RtspHardwareAccelerationMode.Auto);

    public static readonly StyledProperty<RtspRenderMode> RenderModeProperty =
        AvaloniaProperty.Register<RtspVideoView, RtspRenderMode>(nameof(RenderMode), RtspRenderMode.Auto);

    public static readonly StyledProperty<bool> FallbackToSoftwareDecodingProperty =
        AvaloniaProperty.Register<RtspVideoView, bool>(nameof(FallbackToSoftwareDecoding), true);

    public static readonly StyledProperty<double> MaxFramesPerSecondProperty =
        AvaloniaProperty.Register<RtspVideoView, double>(nameof(MaxFramesPerSecond), 0);

    public static readonly StyledProperty<string> TransportProperty =
        AvaloniaProperty.Register<RtspVideoView, string>(nameof(Transport), "tcp");

    public static readonly StyledProperty<int> OpenTimeoutMillisecondsProperty =
        AvaloniaProperty.Register<RtspVideoView, int>(nameof(OpenTimeoutMilliseconds), 5000);

    public static readonly StyledProperty<int> EndpointProbeTimeoutMillisecondsProperty =
        AvaloniaProperty.Register<RtspVideoView, int>(nameof(EndpointProbeTimeoutMilliseconds), 0);

    public static readonly StyledProperty<int> ReadTimeoutMillisecondsProperty =
        AvaloniaProperty.Register<RtspVideoView, int>(nameof(ReadTimeoutMilliseconds), 5000);

    public static readonly StyledProperty<int> ReconnectDelayMillisecondsProperty =
        AvaloniaProperty.Register<RtspVideoView, int>(nameof(ReconnectDelayMilliseconds), 3000);

    public static readonly StyledProperty<int> MaxConcurrentOpenStreamsProperty =
        AvaloniaProperty.Register<RtspVideoView, int>(
            nameof(MaxConcurrentOpenStreams),
            RtspStreamOptions.DefaultMaxConcurrentOpenStreams);

    public static readonly StyledProperty<int> MaxVideoWidthProperty =
        AvaloniaProperty.Register<RtspVideoView, int>(nameof(MaxVideoWidth), 0);

    public static readonly StyledProperty<int> MaxVideoHeightProperty =
        AvaloniaProperty.Register<RtspVideoView, int>(nameof(MaxVideoHeight), 0);

    public static readonly StyledProperty<bool> LowLatencyProperty =
        AvaloniaProperty.Register<RtspVideoView, bool>(nameof(LowLatency), false);

    public static readonly StyledProperty<bool> EnableAudioProperty =
        AvaloniaProperty.Register<RtspVideoView, bool>(nameof(EnableAudio), true);

    public static readonly StyledProperty<double> VolumeProperty =
        AvaloniaProperty.Register<RtspVideoView, double>(nameof(Volume), 1d);

    public static readonly StyledProperty<bool> IsMutedProperty =
        AvaloniaProperty.Register<RtspVideoView, bool>(nameof(IsMuted));

    public static readonly StyledProperty<Stretch> StretchProperty =
        AvaloniaProperty.Register<RtspVideoView, Stretch>(nameof(Stretch), Stretch.Uniform);

    public static readonly StyledProperty<RtspScaleQuality> ScaleQualityProperty =
        AvaloniaProperty.Register<RtspVideoView, RtspScaleQuality>(nameof(ScaleQuality), RtspScaleQuality.Bilinear);

    public static readonly DirectProperty<RtspVideoView, RtspConnectionState> ConnectionStateProperty =
        AvaloniaProperty.RegisterDirect<RtspVideoView, RtspConnectionState>(
            nameof(ConnectionState),
            view => view.ConnectionState);

    public static readonly DirectProperty<RtspVideoView, RtspStreamError?> LastErrorProperty =
        AvaloniaProperty.RegisterDirect<RtspVideoView, RtspStreamError?>(
            nameof(LastError),
            view => view.LastError);

    public static readonly DirectProperty<RtspVideoView, bool> IsHardwareAccelerationActiveProperty =
        AvaloniaProperty.RegisterDirect<RtspVideoView, bool>(
            nameof(IsHardwareAccelerationActive),
            view => view.IsHardwareAccelerationActive);

    public static readonly DirectProperty<RtspVideoView, RtspRenderMode> EffectiveRenderModeProperty =
        AvaloniaProperty.RegisterDirect<RtspVideoView, RtspRenderMode>(
            nameof(EffectiveRenderMode),
            view => view.EffectiveRenderMode);

    public static readonly DirectProperty<RtspVideoView, string> PerformanceSummaryProperty =
        AvaloniaProperty.RegisterDirect<RtspVideoView, string>(
            nameof(PerformanceSummary),
            view => view.PerformanceSummary);

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

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (ShouldRestartForProperty(change.Property))
        {
            QueueRestartStream();
        }
        else if (change.Property == VolumeProperty)
        {
            _streamClient?.SetVolume(Volume);
        }
        else if (change.Property == IsMutedProperty)
        {
            _streamClient?.SetMuted(IsMuted);
        }
        else if (change.Property == StretchProperty)
        {
            InvalidateVisual();
        }
    }

    private void RestartStream()
    {
        StopStream();
        LastError = null;

        if (!_isAttached)
        {
            ConnectionState = RtspConnectionState.Idle;
            return;
        }

        if (!IsPlaybackEnabled || string.IsNullOrWhiteSpace(StreamUrl))
        {
            ConnectionState = string.IsNullOrWhiteSpace(StreamUrl) ? RtspConnectionState.Idle : RtspConnectionState.Stopped;
            return;
        }

        ConnectionState = RtspConnectionState.Connecting;
        var renderer = CreateFrameRenderer();

        var client = new RtspStreamClient(StreamUrl, CreateStreamOptions(renderer.Mode));
        _streamClient = client;
        AttachStreamClient(client, _streamVersion);
        client.Start();
    }

    private void StopStream()
    {
        _streamVersion++;
        var client = _streamClient;
        var renderer = _frameRenderer;
        _streamClient = null;
        _frameRenderer = null;
        if (client != null)
        {
            client.Stop();
            _ = DisposeStreamResourcesAsync(client, renderer);
        }
        else
        {
            DisposeFrameRenderer(renderer);
        }

        IsHardwareAccelerationActive = false;
        EffectiveRenderMode = RtspRenderMode.SoftwareBitmap;
        _streamPerformanceSummary = string.Empty;
        _rendererPerformanceSummary = string.Empty;
        PerformanceSummary = string.Empty;
        ConnectionState = RtspConnectionState.Stopped;
    }

    private static async Task DisposeStreamResourcesAsync(
        RtspStreamClient client,
        IRtspFrameRenderer? renderer)
    {
        try
        {
            await client.Completion.ConfigureAwait(false);
            client.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to dispose RTSP stream resources: {ex}");
        }
        finally
        {
            if (renderer != null)
            {
                Dispatcher.UIThread.Post(
                    () => DisposeFrameRenderer(renderer),
                    DispatcherPriority.Background);
            }
        }
    }

    private static void DisposeFrameRenderer(IRtspFrameRenderer? renderer)
    {
        renderer?.Detach();
        renderer?.Dispose();
    }

    internal void Shutdown()
    {
        _isAttached = false;
        _isVisuallyAttached = false;
        StopStream();
    }

    internal void Suspend()
    {
        StopStream();
        InvalidateVisual();
    }

    private static bool ShouldRestartForProperty(AvaloniaProperty property)
    {
        return property == StreamUrlProperty ||
               property == IsPlaybackEnabledProperty ||
               property == UseHardwareAccelerationProperty ||
               property == HardwareAccelerationModeProperty ||
               property == RenderModeProperty ||
               property == FallbackToSoftwareDecodingProperty ||
               property == MaxFramesPerSecondProperty ||
               property == TransportProperty ||
               property == OpenTimeoutMillisecondsProperty ||
               property == EndpointProbeTimeoutMillisecondsProperty ||
               property == ReadTimeoutMillisecondsProperty ||
               property == ReconnectDelayMillisecondsProperty ||
               property == MaxConcurrentOpenStreamsProperty ||
               property == MaxVideoWidthProperty ||
               property == MaxVideoHeightProperty ||
               property == LowLatencyProperty ||
               property == EnableAudioProperty ||
               property == ScaleQualityProperty;
    }


    private RtspStreamOptions CreateStreamOptions(RtspRenderMode effectiveRenderMode)
    {
        var resolvedHardwareAccelerationMode =
            RtspPlaybackConfiguration.ResolveHardwareAccelerationMode(HardwareAccelerationMode, UseHardwareAcceleration);
        var useHardwareAcceleration =
            RtspPlaybackConfiguration.ResolveUseHardwareAcceleration(
                resolvedHardwareAccelerationMode,
                UseHardwareAcceleration,
                effectiveRenderMode);

        return new RtspStreamOptions
        {
            UseHardwareAcceleration = useHardwareAcceleration,
            HardwareAccelerationMode = resolvedHardwareAccelerationMode,
            RenderMode = effectiveRenderMode,
            FallbackToSoftwareDecoding = FallbackToSoftwareDecoding,
            MaxFramesPerSecond = MaxFramesPerSecond,
            Transport = Transport,
            OpenTimeoutMilliseconds = OpenTimeoutMilliseconds,
            EndpointProbeTimeoutMilliseconds = EndpointProbeTimeoutMilliseconds,
            ReadTimeoutMilliseconds = ReadTimeoutMilliseconds,
            ReconnectDelayMilliseconds = ReconnectDelayMilliseconds,
            MaxConcurrentOpenStreams = MaxConcurrentOpenStreams,
            MaxVideoWidth = MaxVideoWidth,
            MaxVideoHeight = MaxVideoHeight,
            LowLatency = LowLatency,
            EnableAudio = EnableAudio,
            Volume = Volume,
            IsMuted = IsMuted,
            ForceOpaqueAlpha = true,
            ScaleQuality = ScaleQuality
        };
    }

    private RtspRenderMode ResolveEffectiveRenderMode()
    {
        return RenderMode switch
        {
            RtspRenderMode.SoftwareBitmap => RtspRenderMode.SoftwareBitmap,
            RtspRenderMode.NativeSurface => RtspRenderMode.NativeSurface,
            _ => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsAndroid()
                ? RtspRenderMode.NativeSurface
                : RtspRenderMode.SoftwareBitmap
        };
    }

    private void AttachStreamClient(RtspStreamClient client, int version)
    {
        client.SetFrameDeliveryEnabled(_isVisuallyAttached);

        if (_frameRenderer is IRtspLeaseFrameRenderer leaseRenderer)
        {
            client.OnFrameLeaseReceived += lease =>
            {
                if (version == _streamVersion && _isVisuallyAttached)
                {
                    UpdateFrameLease(leaseRenderer, lease);
                }
                else
                {
                    lease.Dispose();
                }
            };
        }

        client.OnFrameReceived += (buffer, width, height, stride) =>
        {
            if (version == _streamVersion && _isVisuallyAttached)
            {
                UpdateFrame(buffer, width, height, stride);
            }
        };
        client.StreamError += (sender, e) =>
        {
            if (version == _streamVersion)
            {
                UpdateError(version, e);
            }
        };
        client.ConnectionStateChanged += (sender, e) =>
        {
            if (version == _streamVersion)
            {
                UpdateConnectionState(version, e.NewState);
            }
        };
        client.HardwareAccelerationChanged += (_, active) =>
        {
            if (version == _streamVersion)
            {
                PostToUiThread(() =>
                {
                    if (version == _streamVersion)
                    {
                        IsHardwareAccelerationActive = active;
                    }
                }, DispatcherPriority.Background);
            }
        };
        client.PerformanceUpdated += (_, snapshot) =>
        {
            if (version == _streamVersion)
            {
                PostToUiThread(() =>
                {
                    if (version != _streamVersion)
                    {
                        return;
                    }

                    _streamPerformanceSummary =
                        $"wait {snapshot.ReadMilliseconds:F1}ms | cpu {snapshot.PipelineCpuMilliseconds:F1}ms | codec {snapshot.CodecMilliseconds:F1}ms | transfer {snapshot.HardwareTransferMilliseconds:F1}ms | convert {snapshot.ConvertMilliseconds:F1}ms | dispatch {snapshot.DispatchMilliseconds:F1}ms";
                    RefreshPerformanceSummary();
                }, DispatcherPriority.Background);
            }
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        _isVisuallyAttached = true;
        _streamClient?.SetFrameDeliveryEnabled(true);
        
        if (_shutdownCancellation != null)
        {
            _shutdownCancellation.Cancel();
            _shutdownCancellation.Dispose();
            _shutdownCancellation = null;
        }

        if (_streamClient == null)
        {
            QueueRestartStream();
        }
        else
        {
            InvalidateVisual();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _isVisuallyAttached = false;
        _streamClient?.SetFrameDeliveryEnabled(false);

        _shutdownCancellation?.Cancel();
        _shutdownCancellation?.Dispose();
        _shutdownCancellation = null;

        if (KeepPlaybackAliveWhenDetached)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
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
                _isAttached = false;
                Shutdown();
            }, DispatcherPriority.Background);
        }
    }

    private void QueueRestartStream()
    {
        if (!_isAttached || _restartPending)
        {
            return;
        }

        _restartPending = true;
        PostToUiThread(() =>
        {
            _restartPending = false;
            RestartStream();
        }, DispatcherPriority.Background);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 320 : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) ? 180 : availableSize.Height;

        if (width <= 0)
        {
            width = 320;
        }

        if (height <= 0)
        {
            height = 180;
        }

        return new Size(width, height);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        (_frameRenderer ??= CreateFrameRenderer()).Render(context, new Rect(Bounds.Size), Stretch);
    }

    internal static Rect CalculateDestinationRect(Size sourceSize, Size boundsSize, Stretch stretch)
    {
        if (sourceSize.Width <= 0 || sourceSize.Height <= 0 || boundsSize.Width <= 0 || boundsSize.Height <= 0)
        {
            return new Rect(boundsSize);
        }

        if (stretch == Stretch.Fill)
        {
            return new Rect(boundsSize);
        }

        var scaleX = boundsSize.Width / sourceSize.Width;
        var scaleY = boundsSize.Height / sourceSize.Height;
        var scale = stretch switch
        {
            Stretch.None => 1,
            Stretch.UniformToFill => Math.Max(scaleX, scaleY),
            _ => Math.Min(scaleX, scaleY)
        };

        var finalWidth = sourceSize.Width * scale;
        var finalHeight = sourceSize.Height * scale;

        return new Rect(
            (boundsSize.Width - finalWidth) / 2,
            (boundsSize.Height - finalHeight) / 2,
            finalWidth,
            finalHeight);
    }

    public void UpdateFrame(IntPtr buffer, int width, int height, int stride)
    {
        try
        {
            var renderer = _frameRenderer ??= CreateFrameRenderer();
            renderer.UpdateFrame(buffer, width, height, stride);
        }
        catch (Exception ex)
        {
            var error = new RtspStreamError(
                RtspStreamErrorKind.Unknown,
                $"Failed to render video frame. {ex.Message}",
                ex,
                WillRetry: true);
            LastError = error;
            StreamError?.Invoke(this, new RtspStreamErrorEventArgs(error));
        }
    }

    private void UpdateFrameLease(IRtspLeaseFrameRenderer renderer, RtspFrameLease lease)
    {
        try
        {
            renderer.UpdateFrameLease(lease);
        }
        catch (Exception ex)
        {
            lease.Dispose();
            var error = new RtspStreamError(
                RtspStreamErrorKind.Unknown,
                $"Failed to render video frame. {ex.Message}",
                ex,
                WillRetry: true);
            LastError = error;
            StreamError?.Invoke(this, new RtspStreamErrorEventArgs(error));
        }
    }

    internal void PostRendererUpdate(Action action)
    {
        Dispatcher.UIThread.Post(action, DispatcherPriority.Normal);
    }

    internal void NotifyRendererFrameReady() => InvalidateVisual();

    internal void RecordRendererPresentation(long elapsedTicks)
    {
        _rendererTotalTicks += elapsedTicks;
        _rendererSamples++;
        if (_rendererSamples < 30)
        {
            return;
        }

        var averageMs = _rendererTotalTicks * 1000d / Stopwatch.Frequency / _rendererSamples;
        _rendererTotalTicks = 0;
        _rendererSamples = 0;
        PostToUiThread(() =>
        {
            _rendererPerformanceSummary = $"present {averageMs:F1}ms";
            RefreshPerformanceSummary();
        }, DispatcherPriority.Background);
    }

    private void UpdateConnectionState(int version, RtspConnectionState newState)
    {
        PostToUiThread(() =>
        {
            if (version == _streamVersion)
            {
                ConnectionState = newState;
            }
        }, DispatcherPriority.Background);
    }

    private void UpdateError(int version, RtspStreamErrorEventArgs e)
    {
        PostToUiThread(() =>
        {
            if (version != _streamVersion)
            {
                return;
            }

            LastError = e.Error;
            StreamError?.Invoke(this, e);
            if (e.Error.WillRetry)
            {
                ConnectionState = RtspConnectionState.Reconnecting;
            }
            else
            {
                ConnectionState = RtspConnectionState.Failed;
            }
        }, DispatcherPriority.Background);
    }

    private static void PostToUiThread(Action action, DispatcherPriority priority)
    {
        Dispatcher.UIThread.Post(action, priority);
    }

    private IRtspFrameRenderer CreateFrameRenderer()
    {
        _frameRenderer?.Detach();
        _frameRenderer?.Dispose();
        var requestedMode = ResolveEffectiveRenderMode();
        var resolvedHardwareAccelerationMode =
            RtspPlaybackConfiguration.ResolveHardwareAccelerationMode(HardwareAccelerationMode, UseHardwareAcceleration);
        var useHardwareAcceleration =
            RtspPlaybackConfiguration.ResolveUseHardwareAcceleration(
                resolvedHardwareAccelerationMode,
                UseHardwareAcceleration,
                requestedMode);
        _frameRenderer = RtspFrameRendererFactory.Create(requestedMode, useHardwareAcceleration, out var effectiveMode);
        _frameRenderer.Attach(this);
        EffectiveRenderMode = effectiveMode;
        return _frameRenderer;
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

    private void RefreshPerformanceSummary()
    {
        if (string.IsNullOrEmpty(_streamPerformanceSummary))
        {
            PerformanceSummary = _rendererPerformanceSummary;
            return;
        }

        if (string.IsNullOrEmpty(_rendererPerformanceSummary))
        {
            PerformanceSummary = _streamPerformanceSummary;
            return;
        }

        PerformanceSummary = $"{_streamPerformanceSummary} | {_rendererPerformanceSummary}";
    }
}
