using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using DiagnosticActivity = System.Diagnostics.Activity;

namespace FrameFlux.FFmpeg;

internal interface IFfmpegMediaSession : IAsyncDisposable
{
    MediaSource Source { get; }

    MediaOpenOptions Options { get; }

    MediaPlaybackState State { get; }

    MediaDiagnostics Diagnostics { get; }

    double Volume { get; set; }

    bool IsMuted { get; set; }

    event EventHandler<MediaPlaybackStateChangedEventArgs>? StateChanged;

    event EventHandler<MediaPlaybackErrorEventArgs>? Error;

    event EventHandler<MediaVideoFrame>? FrameReceived;

    ValueTask StartAsync(CancellationToken cancellationToken = default);

    ValueTask StopAsync(CancellationToken cancellationToken = default);

    ValueTask<MediaSnapshot?> CaptureSnapshotAsync(CancellationToken cancellationToken = default);
}

internal sealed class FfmpegMediaSession : IFfmpegMediaSession
{
    private readonly object _sync = new();
    private readonly ILogger _logger;
    private readonly IMediaVideoOutput? _videoOutput;
    private readonly SemaphoreSlim? _openOperationSemaphore;
    private RtspStreamClient? _client;
    private Task? _stopTask;
    private DiagnosticActivity? _activity;
    private EventHandler<MediaVideoFrame>? _frameReceived;
    private MediaPlaybackState _state = MediaPlaybackState.Idle;
    private MediaSnapshot? _lastSnapshot;
    private RtspPerformanceSnapshot _lastPerformance;
    private string? _lastError;
    private bool _isHardwareVideoDecodingActive;
    private double _volume;
    private bool _isMuted;
    private long _frameSequence;
    private bool _disposed;

    internal FfmpegMediaSession(
        MediaSource source,
        MediaOpenOptions options,
        double volume,
        bool isMuted,
        IMediaVideoOutput? videoOutput,
        SemaphoreSlim? openOperationSemaphore = null,
        ILogger? logger = null)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Options = options ?? throw new ArgumentNullException(nameof(options));
        Options.Validate();
        _volume = volume;
        _isMuted = isMuted;
        _videoOutput = videoOutput;
        _openOperationSemaphore = openOperationSemaphore;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    public MediaSource Source { get; }

    public MediaOpenOptions Options { get; }

    public double Volume
    {
        get
        {
            lock (_sync)
            {
                return _volume;
            }
        }
        set
        {
            if (value is < 0d or > 1d || double.IsNaN(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Volume must be between 0.0 and 1.0.");
            }

            lock (_sync)
            {
                ThrowIfDisposed();
                _volume = value;
                _client?.SetVolume(value);
            }
        }
    }

    public bool IsMuted
    {
        get
        {
            lock (_sync)
            {
                return _isMuted;
            }
        }
        set
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                _isMuted = value;
                _client?.SetMuted(value);
            }
        }
    }

    public MediaPlaybackState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    public MediaDiagnostics Diagnostics
    {
        get
        {
            lock (_sync)
            {
                return new MediaDiagnostics(
                    _isHardwareVideoDecodingActive,
                    _client?.VideoDecoderDiagnostics ?? "N/A",
                    _lastPerformance.ReadMilliseconds,
                    _lastPerformance.DecodeMilliseconds,
                    _lastPerformance.SampleCount,
                    _lastError)
                {
                    Audio = _client?.AudioDiagnostics ?? MediaAudioDiagnostics.Empty,
                    Synchronization = _client?.SynchronizationDiagnostics ??
                        MediaSynchronizationDiagnostics.Empty
                };
            }
        }
    }

    public event EventHandler<MediaPlaybackStateChangedEventArgs>? StateChanged;

    public event EventHandler<MediaPlaybackErrorEventArgs>? Error;

    public event EventHandler<MediaVideoFrame>? FrameReceived
    {
        add
        {
            lock (_sync)
            {
                _frameReceived += value;
                UpdateFrameDeliveryLocked();
            }
        }
        remove
        {
            lock (_sync)
            {
                _frameReceived -= value;
                UpdateFrameDeliveryLocked();
            }
        }
    }

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_client is not null)
            {
                return ValueTask.CompletedTask;
            }

            var client = new RtspStreamClient(Source.Uri.ToString(), CreateEngineOptions());
            client.ConnectionStateChanged += OnConnectionStateChanged;
            client.StreamError += OnStreamError;
            client.HardwareVideoDecodingChanged += OnHardwareVideoDecodingChanged;
            client.PerformanceUpdated += OnPerformanceUpdated;
            client.OnFrameLeaseReceived += OnFrameLeaseReceived;
            client.OnSnapshotFrameLeaseReceived += OnSnapshotFrameLeaseReceived;
            _client = client;
            _activity = RtspTelemetry.Activities.StartActivity("media.session", ActivityKind.Client);
            _activity?.SetTag("media.source", Source.Uri.GetLeftPart(UriPartial.Authority));
            _activity?.SetTag("media.transport", Options.Network.Transport.ToString());
            UpdateFrameDeliveryLocked();
            client.Start();
        }

        RtspTelemetry.SessionsStarted.Add(1);
        _logger.LogInformation("Started media session for {Host}", Source.Uri.Host);
        return ValueTask.CompletedTask;
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        Task? stopTask;
        lock (_sync)
        {
            if (_client is null)
            {
                return;
            }

            _client.Stop(waitForExit: false);
            _stopTask ??= CompleteStopAsync(_client);
            stopTask = _stopTask;
        }

        await stopTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<MediaSnapshot?> CaptureSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            return ValueTask.FromResult(
                _lastSnapshot is null
                    ? null
                    : _lastSnapshot with { Data = _lastSnapshot.Data.ToArray() });
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private async Task CompleteStopAsync(RtspStreamClient client)
    {
        try
        {
            await client.Completion.ConfigureAwait(false);
        }
        finally
        {
            client.ConnectionStateChanged -= OnConnectionStateChanged;
            client.StreamError -= OnStreamError;
            client.HardwareVideoDecodingChanged -= OnHardwareVideoDecodingChanged;
            client.PerformanceUpdated -= OnPerformanceUpdated;
            client.OnFrameLeaseReceived -= OnFrameLeaseReceived;
            client.OnSnapshotFrameLeaseReceived -= OnSnapshotFrameLeaseReceived;
            client.Dispose();

            lock (_sync)
            {
                if (ReferenceEquals(_client, client))
                {
                    _client = null;
                    _stopTask = null;
                }
            }

            _activity?.Dispose();
            _activity = null;
            TransitionTo(MediaPlaybackState.Stopped);
            RtspTelemetry.SessionsStopped.Add(1);
            _logger.LogInformation("Stopped media session for {Host}", Source.Uri.Host);
        }
    }

    private RtspStreamOptions CreateEngineOptions() =>
        new()
        {
            Transport = Options.Network.Transport switch
            {
                MediaTransport.Udp => "udp",
                MediaTransport.HttpTunnel => "http",
                MediaTransport.HttpsTunnel => "https",
                _ => "tcp"
            },
            VideoDecodingMode = RtspPlaybackConfiguration.ResolveVideoDecodingMode(
                Options.Video.DecodingPolicy),
            FrameDeliveryMode = ResolveFrameDeliveryMode(_videoOutput),
            OpenTimeoutMilliseconds = ToMilliseconds(Options.Network.OpenTimeout),
            EndpointProbeTimeoutMilliseconds = ToMilliseconds(Options.Network.EndpointProbeTimeout),
            ReadTimeoutMilliseconds = ToMilliseconds(Options.Network.ReadTimeout),
            ReconnectEnabled = Options.Network.Reconnect.IsEnabled,
            ReconnectInitialDelayMilliseconds = ToMilliseconds(
                Options.Network.Reconnect.InitialDelay),
            ReconnectMaximumDelayMilliseconds = ToMilliseconds(
                Options.Network.Reconnect.MaximumDelay),
            MaximumReconnectAttempts = Options.Network.Reconnect.MaximumAttempts,
            OpenOperationSemaphore = _openOperationSemaphore,
            MaxFramesPerSecond = Options.Video.MaximumFrameRate ?? 0,
            MaxVideoWidth = Options.Video.MaximumWidth ?? 0,
            MaxVideoHeight = Options.Video.MaximumHeight ?? 0,
            LowLatency = Options.Network.LatencyMode == MediaLatencyMode.Low,
            EnableAudio = Options.Audio.IsEnabled,
            CreateSnapshotFrames =
                Options.Video.SnapshotPolicy == MediaSnapshotPolicy.KeepLatestFrame,
            AudioGainDecibels = Options.Audio.GainDecibels,
            AudioOutputDeviceId = Options.Audio.OutputDeviceId,
            AudioBufferDurationMilliseconds = ToMilliseconds(Options.Audio.BufferDuration),
            Volume = _volume,
            IsMuted = _isMuted
        };

    internal static RtspFrameDeliveryMode ResolveFrameDeliveryMode(IMediaVideoOutput? output) =>
        output?.PreferredFrameStorage == MediaFrameStorageKind.D3D11Texture
            ? RtspFrameDeliveryMode.D3D11Texture
            : RtspFrameDeliveryMode.CpuMemory;

    private static int ToMilliseconds(TimeSpan? value) =>
        value is null ? 0 : checked((int)Math.Min(value.Value.TotalMilliseconds, int.MaxValue));

    private void OnConnectionStateChanged(object? sender, RtspConnectionStateChangedEventArgs args)
    {
        TransitionTo(args.NewState switch
        {
            RtspConnectionState.Connecting => MediaPlaybackState.Opening,
            RtspConnectionState.Connected => MediaPlaybackState.Playing,
            RtspConnectionState.Reconnecting => MediaPlaybackState.Reconnecting,
            RtspConnectionState.Stopped => MediaPlaybackState.Stopped,
            _ => MediaPlaybackState.Idle
        });
    }

    private void OnStreamError(object? sender, RtspStreamErrorEventArgs args)
    {
        var error = new MediaPlaybackError(
            args.Error.Kind.ToString(),
            args.Error.Message,
            args.Error.WillRetry,
            args.Error.Exception);
        lock (_sync)
        {
            _lastError = error.Message;
        }

        RtspTelemetry.SessionErrors.Add(1);
        _activity?.SetStatus(ActivityStatusCode.Error, error.Message);
        _logger.LogWarning(error.Exception, "Media stream error for {Host}: {Message}", Source.Uri.Host, error.Message);
        if (!error.IsRecoverable)
        {
            TransitionTo(MediaPlaybackState.Faulted);
        }

        Error?.Invoke(this, new MediaPlaybackErrorEventArgs(error));
    }

    private void OnHardwareVideoDecodingChanged(object? sender, bool isActive)
    {
        lock (_sync)
        {
            _isHardwareVideoDecodingActive = isActive;
        }
    }

    private void OnPerformanceUpdated(object? sender, RtspPerformanceSnapshot snapshot)
    {
        lock (_sync)
        {
            _lastPerformance = snapshot;
        }

        RtspTelemetry.FrameReadDuration.Record(snapshot.ReadMilliseconds);
        RtspTelemetry.FrameDecodeDuration.Record(snapshot.DecodeMilliseconds);
    }

    private void OnFrameLeaseReceived(FfmpegMediaFrameLease lease)
    {
        var deliveryCompleted = false;
        try
        {
            EventHandler<MediaVideoFrame>? subscribers;
            lock (_sync)
            {
                subscribers = _frameReceived;
            }

            var keepLatestFrame = Options.Video.SnapshotPolicy == MediaSnapshotPolicy.KeepLatestFrame;
            var frame = TryCopyFrame(lease, keepLatestFrame || subscribers is not null);
            if (frame is not null)
            {
                lock (_sync)
                {
                    if (keepLatestFrame)
                    {
                        _lastSnapshot = new MediaSnapshot(
                            frame.Data,
                            frame.Width,
                            frame.Height,
                            frame.Stride,
                            frame.PixelFormat,
                            frame.CapturedAt);
                    }
                }

                PublishFrame(subscribers, frame);
            }

            MediaFrameDelivery.Deliver(
                _videoOutput,
                lease,
                exception => _logger.LogWarning(exception, "A media video output failed."));
            deliveryCompleted = true;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "A media video output failed.");
        }
        finally
        {
            if (!deliveryCompleted)
            {
                lease.Dispose();
            }
        }
    }

    private void OnSnapshotFrameLeaseReceived(FfmpegMediaFrameLease lease)
    {
        try
        {
            var frame = TryCopyFrame(lease, required: true);
            if (frame is null)
            {
                return;
            }

            EventHandler<MediaVideoFrame>? subscribers;
            lock (_sync)
            {
                _lastSnapshot = new MediaSnapshot(
                    frame.Data,
                    frame.Width,
                    frame.Height,
                    frame.Stride,
                    frame.PixelFormat,
                    frame.CapturedAt);
                subscribers = _frameReceived;
            }

            PublishFrame(subscribers, frame);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "A GPU snapshot frame could not be copied.");
        }
        finally
        {
            lease.Dispose();
        }
    }

    private MediaVideoFrame? TryCopyFrame(FfmpegMediaFrameLease lease, bool required)
    {
        if (!required ||
            lease.PixelFormat != RtspNativePixelFormat.Bgra32 ||
            lease.Buffer == IntPtr.Zero ||
            lease.Width <= 0 ||
            lease.Height <= 0 ||
            lease.Stride <= 0)
        {
            return null;
        }

        var byteCount = Math.Min(lease.Size, checked(lease.Stride * lease.Height));
        if (byteCount <= 0)
        {
            return null;
        }

        var data = GC.AllocateUninitializedArray<byte>(byteCount);
        Marshal.Copy(lease.Buffer, data, 0, byteCount);
        return new MediaVideoFrame(
            data,
            lease.Width,
            lease.Height,
            lease.Stride,
            MediaPixelFormat.Bgra32,
            Interlocked.Increment(ref _frameSequence),
            DateTimeOffset.UtcNow);
    }

    private void PublishFrame(EventHandler<MediaVideoFrame>? subscribers, MediaVideoFrame frame)
    {
        if (subscribers is null)
        {
            return;
        }

        foreach (EventHandler<MediaVideoFrame> subscriber in subscribers.GetInvocationList())
        {
            try
            {
                subscriber(this, frame);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "A media frame subscriber failed.");
            }
        }

        RtspTelemetry.FramesDelivered.Add(1);
    }

    private void TransitionTo(MediaPlaybackState state)
    {
        MediaPlaybackState oldState;
        lock (_sync)
        {
            oldState = _state;
            if (oldState == state)
            {
                return;
            }

            _state = state;
        }

        StateChanged?.Invoke(this, new MediaPlaybackStateChangedEventArgs(oldState, state));
    }

    private void UpdateFrameDeliveryLocked() =>
        _client?.SetFrameDeliveryEnabled(
            Options.Video.SnapshotPolicy == MediaSnapshotPolicy.KeepLatestFrame ||
            _frameReceived is not null ||
            _videoOutput is not null);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
