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
    TimeSpan Position => TimeSpan.Zero;

    TimeSpan? Duration => null;

    double PlaybackRate
    {
        get => 1d;
        set
        {
            if (value != 1d)
                throw new NotSupportedException("This media session does not support playback-rate changes.");
        }
    }


    double Volume { get; set; }

    bool IsMuted { get; set; }

    event EventHandler<MediaPlaybackStateChangedEventArgs>? StateChanged;

    event EventHandler<MediaPlaybackErrorEventArgs>? Error;

    event EventHandler<MediaVideoFrame>? FrameReceived;

    ValueTask StartAsync(CancellationToken cancellationToken = default);

    ValueTask StopAsync(CancellationToken cancellationToken = default);

    ValueTask SetPausedAsync(bool paused, CancellationToken cancellationToken = default) =>
        RuntimeCompat.Failure(new NotSupportedException("This media session does not support pausing."));

    ValueTask SeekAsync(TimeSpan position, CancellationToken cancellationToken = default) =>
        RuntimeCompat.Failure(
            new NotSupportedException("This media session does not support seeking."));


    ValueTask<MediaSnapshot?> CaptureSnapshotAsync(CancellationToken cancellationToken = default);
}

internal sealed class FfmpegMediaSession : IFfmpegMediaSession, IMediaFrameLeaseSource
{
    private readonly object _sync = new();
    private readonly ILogger _logger;
    private readonly IMediaVideoOutput? _videoOutput;
    private readonly bool _isSharedPhysicalSession;
    private readonly SemaphoreSlim? _openOperationSemaphore;
    private readonly HashSet<IMediaVideoOutput> _sharedVideoOutputs = [];
    private FfmpegPlaybackClient? _client;
    private ISharedPlatformVideoOutput? _sharedPlatformOutput;
    private readonly Func<int, int, IMediaAudioOutput>? _audioOutputFactory;
    private Task? _stopTask;
    private DiagnosticActivity? _activity;
    private EventHandler<MediaVideoFrame>? _frameReceived;
    private volatile EventHandler<MediaVideoFrame>[] _frameReceivedSnapshot = [];
    private Action<IMediaFrameLease>? _frameLeaseConsumer;
    private Action<IMediaFrameLease>? _supplementaryCpuFrameLeaseConsumer;
    private bool _requiresNativeFrames = true;
    private bool _requiresCpuFrames;
    private MediaPlaybackState _state = MediaPlaybackState.Idle;
    private MediaSnapshot? _lastSnapshot;
    private FfmpegPerformanceSnapshot _lastPerformance;
    private string? _lastError;
    private bool _isHardwareVideoDecodingActive;
    private double _playbackRate = 1d;
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
        ILogger? logger = null,
        Func<int, int, IMediaAudioOutput>? audioOutputFactory = null,
        bool isSharedPhysicalSession = false)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Options = options ?? throw new ArgumentNullException(nameof(options));
        Options.Validate();
        _volume = volume;
        _isMuted = isMuted;
        _videoOutput = videoOutput;
        _isSharedPhysicalSession = isSharedPhysicalSession;
        _openOperationSemaphore = openOperationSemaphore;
        _audioOutputFactory = audioOutputFactory;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    public MediaSource Source { get; }

    public MediaOpenOptions Options { get; }

    public double PlaybackRate
    {
        get
        {
            lock (_sync)
            {
                return _playbackRate;
            }
        }
        set
        {
            MediaPlaybackClock.ValidateRate(value);
            lock (_sync)
            {
                ThrowIfDisposed();
                if (!FfmpegSource.IsSeekable(Source.Uri) && value != 1d)
                {
                    throw new NotSupportedException(
                        "Live sources do not support playback-rate changes.");
                }

                _playbackRate = value;
                _client?.SetPlaybackRate(value);
            }
        }
    }

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
                        MediaSynchronizationDiagnostics.Empty,
                    Reconnect = _client?.ReconnectDiagnostics ??
                        MediaReconnectDiagnostics.Empty
                };
            }
        }
    }

    public event EventHandler<MediaPlaybackStateChangedEventArgs>? StateChanged;

    public TimeSpan Position
    {
        get
        {
            lock (_sync)
            {
                return _client?.Position ?? TimeSpan.Zero;
            }
        }
    }

    public TimeSpan? Duration
    {
        get
        {
            lock (_sync)
            {
                return _client?.Duration;
            }
        }
    }

    public event EventHandler<MediaPlaybackErrorEventArgs>? Error;

    public event EventHandler<MediaVideoFrame>? FrameReceived
    {
        add
        {
            lock (_sync)
            {
                _frameReceived += value;
                _frameReceivedSnapshot = BuildFrameSubscribersSnapshot(_frameReceived);
                UpdateFrameDeliveryLocked();
            }
        }
        remove
        {
            lock (_sync)
            {
                _frameReceived -= value;
                _frameReceivedSnapshot = BuildFrameSubscribersSnapshot(_frameReceived);
                UpdateFrameDeliveryLocked();
            }
        }
    }

    MediaFrameStorageKind IMediaFrameLeaseSource.NativeFrameStorageKind =>
        ResolveNativeFrameStorageKind();

    bool IMediaFrameLeaseSource.HandlesOutputNatively(IMediaVideoOutput output)
    {
        RuntimeCompat.NotNull(output, nameof(output));
        ISharedPlatformVideoOutput? sharedPlatformOutput;
        lock (_sync)
        {
            sharedPlatformOutput = _sharedPlatformOutput;
        }

        if (sharedPlatformOutput is not null)
        {
            return sharedPlatformOutput.HandlesOutputNatively(output);
        }

        if (_isSharedPhysicalSession &&
            PlatformVideoDecoderRegistry.HandlesSharedOutputNatively(output, CreateEngineOptions()))
        {
            return true;
        }

        var nativeStorageKind = ResolveNativeFrameStorageKind();
        return nativeStorageKind is MediaFrameStorageKind.D3D11Texture or MediaFrameStorageKind.DmaBuf &&
            (output.PreferredFrameStorage == nativeStorageKind ||
             output.Supports(nativeStorageKind, MediaPixelFormat.Unknown));
    }

    void IMediaFrameLeaseSource.SetFrameLeaseConsumer(
        Action<IMediaFrameLease>? consumer)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (consumer is not null &&
                _videoOutput is not null)
            {
                throw new InvalidOperationException(
                    "A media session cannot use a video output and an internal frame consumer together.");
            }
            if (consumer is not null &&
                _frameLeaseConsumer is not null &&
                _frameLeaseConsumer != consumer)
            {
                throw new InvalidOperationException(
                    "The media session already has an internal frame consumer.");
            }

            _frameLeaseConsumer = consumer;
            UpdateFrameDeliveryLocked();
        }
    }

    void IMediaFrameLeaseSource.SetSupplementaryCpuFrameLeaseConsumer(
        Action<IMediaFrameLease>? consumer)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (consumer is not null &&
                _videoOutput is not null)
            {
                throw new InvalidOperationException(
                    "A media session cannot use a video output and an internal frame consumer together.");
            }
            if (consumer is not null &&
                _supplementaryCpuFrameLeaseConsumer is not null &&
                _supplementaryCpuFrameLeaseConsumer != consumer)
            {
                throw new InvalidOperationException(
                    "The media session already has an internal CPU frame consumer.");
            }

            _supplementaryCpuFrameLeaseConsumer = consumer;
            UpdateFrameDeliveryLocked();
        }
    }

    void IMediaFrameLeaseSource.UpdateSharedFrameRequirements(
        bool requiresNativeFrames,
        bool requiresCpuFrames)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _requiresNativeFrames = requiresNativeFrames;
            _requiresCpuFrames = requiresCpuFrames;
            UpdateFrameDeliveryLocked();
        }
    }

    void IMediaFrameLeaseSource.AttachSharedVideoOutput(IMediaVideoOutput output)
    {
        RuntimeCompat.NotNull(output, nameof(output));
        ISharedPlatformVideoOutput? sharedPlatformOutput;
        lock (_sync)
        {
            ThrowIfDisposed();
            if (!_sharedVideoOutputs.Add(output))
            {
                return;
            }

            sharedPlatformOutput = _sharedPlatformOutput;
        }

        sharedPlatformOutput?.AttachOutput(output);
    }

    void IMediaFrameLeaseSource.DetachSharedVideoOutput(IMediaVideoOutput output)
    {
        RuntimeCompat.NotNull(output, nameof(output));
        ISharedPlatformVideoOutput? sharedPlatformOutput;
        lock (_sync)
        {
            if (!_sharedVideoOutputs.Remove(output))
            {
                return;
            }

            sharedPlatformOutput = _sharedPlatformOutput;
        }

        sharedPlatformOutput?.DetachOutput(output);
    }

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FfmpegPlaybackClient client;
        ISharedPlatformVideoOutput? sharedPlatformOutput = null;
        IMediaVideoOutput[] attachedOutputs;
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_client is not null)
            {
                return default(ValueTask);
            }

            var engineOptions = CreateEngineOptions();
            if (_isSharedPhysicalSession && _videoOutput is null)
            {
                sharedPlatformOutput = PlatformVideoDecoderRegistry.TryCreateSharedOutput(engineOptions);
                _sharedPlatformOutput = sharedPlatformOutput;
            }

            attachedOutputs = sharedPlatformOutput is not null
                ? [.. _sharedVideoOutputs]
                : [];

            try
            {
                client = new FfmpegPlaybackClient(
                    Source.Uri.IsFile ? Source.Uri.LocalPath : Source.Uri.ToString(),
                    engineOptions,
                    (IMediaVideoOutput?)sharedPlatformOutput ?? _videoOutput);
                client.SetPlaybackRate(_playbackRate);
                client.ConnectionStateChanged += OnConnectionStateChanged;
                client.StreamError += OnStreamError;
                client.HardwareVideoDecodingChanged += OnHardwareVideoDecodingChanged;
                client.PerformanceUpdated += OnPerformanceUpdated;
                client.OnFrameLeaseReceived += OnFrameLeaseReceived;
                client.OnSnapshotFrameLeaseReceived += OnSnapshotFrameLeaseReceived;
                _client = client;
                _activity = FfmpegTelemetry.Activities.StartActivity("media.session", ActivityKind.Client);
                _activity?.SetTag("media.source", Source.Uri.GetLeftPart(UriPartial.Authority));
                _activity?.SetTag("media.transport", Options.Network.Transport.ToString());
                UpdateFrameDeliveryLocked();
            }
            catch
            {
                _sharedPlatformOutput = null;
                sharedPlatformOutput?.Dispose();
                throw;
            }
        }

        foreach (var attachedOutput in attachedOutputs)
        {
            sharedPlatformOutput!.AttachOutput(attachedOutput);
        }

        client.Start();
        FfmpegTelemetry.SessionsStarted.Add(1);
        _logger.LogInformation("Started media session for {Host}", Source.Uri.Host);
        return default(ValueTask);
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

    public ValueTask SetPausedAsync(bool paused, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ThrowIfDisposed();
            if (!FfmpegSource.IsSeekable(Source.Uri))
            {
                throw new NotSupportedException("Live sources do not support pausing.");
            }

            var client = _client ?? throw new InvalidOperationException(
                "Start the media session before changing pause state.");
            client.SetPaused(paused);
        }

        TransitionTo(paused
            ? MediaPlaybackState.Paused
            : _client?.IsBuffering == true
                ? MediaPlaybackState.Buffering
                : MediaPlaybackState.Playing);
        return default(ValueTask);
    }

    public ValueTask SeekAsync(
        TimeSpan position,
        CancellationToken cancellationToken = default)
    {
        FfmpegPlaybackClient client;
        lock (_sync)
        {
            ThrowIfDisposed();
            client = _client ?? throw new InvalidOperationException(
                "Start the media session before seeking.");
        }

        return client.SeekAsync(position, cancellationToken);
    }

    public ValueTask<MediaSnapshot?> CaptureSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            return RuntimeCompat.Result(
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

    private async Task CompleteStopAsync(FfmpegPlaybackClient client)
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

            ISharedPlatformVideoOutput? sharedPlatformOutput = null;
            lock (_sync)
            {
                if (ReferenceEquals(_client, client))
                {
                    _client = null;
                    _stopTask = null;
                    _lastSnapshot = null;
                    sharedPlatformOutput = _sharedPlatformOutput;
                    _sharedPlatformOutput = null;
                }
            }

            sharedPlatformOutput?.ConfigureCpuFrameDelivery(null, null, false);
            sharedPlatformOutput?.Dispose();

            _activity?.Dispose();
            _activity = null;
            TransitionTo(MediaPlaybackState.Stopped);
            FfmpegTelemetry.SessionsStopped.Add(1);
            _logger.LogInformation("Stopped media session for {Host}", Source.Uri.Host);
        }
    }

    private FfmpegPlaybackOptions CreateEngineOptions() =>
        new()
        {
            AudioOutputFactory = _audioOutputFactory,
            Transport = Options.Network.Transport switch
            {
                MediaTransport.Udp => "udp",
                MediaTransport.HttpTunnel => "http",
                MediaTransport.HttpsTunnel => "https",
                _ => "tcp"
            },
            VideoDecodingMode = FfmpegPlaybackConfiguration.ResolveVideoDecodingMode(
                Options.Video.DecodingPolicy),
            FrameDeliveryMode = ResolveSessionFrameDeliveryMode(),
            OpenTimeoutMilliseconds = ToMilliseconds(Options.Network.OpenTimeout),
            EndpointProbeTimeoutMilliseconds = ToMilliseconds(Options.Network.EndpointProbeTimeout),
            ReadTimeoutMilliseconds = ToMilliseconds(Options.Network.ReadTimeout),
            InitialBufferDurationMilliseconds = ToMilliseconds(Options.Network.InitialBufferDuration),
            RebufferDurationMilliseconds = ToMilliseconds(Options.Network.RebufferDuration),
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

    internal static FfmpegFrameDeliveryMode ResolveFrameDeliveryMode(IMediaVideoOutput? output) =>
        output?.PreferredFrameStorage switch
        {
            MediaFrameStorageKind.D3D11Texture => FfmpegFrameDeliveryMode.D3D11Texture,
            MediaFrameStorageKind.DmaBuf => FfmpegFrameDeliveryMode.DmaBuf,
            _ => FfmpegFrameDeliveryMode.CpuMemory
        };

    private FfmpegFrameDeliveryMode ResolveSessionFrameDeliveryMode()
    {
        if (!_isSharedPhysicalSession || _videoOutput is not null)
        {
            return ResolveFrameDeliveryMode(_videoOutput);
        }

        if (Options.Video.DecodingPolicy == MediaVideoDecodingPolicy.SoftwareOnly)
        {
            return FfmpegFrameDeliveryMode.CpuMemory;
        }

        if (OperatingSystem.IsWindows())
        {
            return FfmpegFrameDeliveryMode.D3D11Texture;
        }

        if (OperatingSystem.IsLinux())
        {
            return FfmpegFrameDeliveryMode.DmaBuf;
        }

        return FfmpegFrameDeliveryMode.CpuMemory;
    }

    private MediaFrameStorageKind ResolveNativeFrameStorageKind() =>
        ResolveSessionFrameDeliveryMode() switch
        {
            FfmpegFrameDeliveryMode.D3D11Texture => MediaFrameStorageKind.D3D11Texture,
            FfmpegFrameDeliveryMode.DmaBuf => MediaFrameStorageKind.DmaBuf,
            _ => MediaFrameStorageKind.CpuMemory
        };

    private static int ToMilliseconds(TimeSpan? value) =>
        value is null ? 0 : checked((int)Math.Min(value.Value.TotalMilliseconds, int.MaxValue));

    private void OnConnectionStateChanged(object? sender, PlaybackConnectionStateChangedEventArgs args)
    {
        TransitionTo(args.NewState switch
        {
            PlaybackConnectionState.Connecting => MediaPlaybackState.Opening,
            PlaybackConnectionState.Connected => MediaPlaybackState.Playing,
            PlaybackConnectionState.Buffering => MediaPlaybackState.Buffering,
            PlaybackConnectionState.Reconnecting => MediaPlaybackState.Reconnecting,
            PlaybackConnectionState.Stopped => MediaPlaybackState.Stopped,
            _ => MediaPlaybackState.Idle
        });
    }

    private void OnStreamError(object? sender, FfmpegPlaybackErrorEventArgs args)
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

        FfmpegTelemetry.SessionErrors.Add(1);
        _activity?.SetStatus(ActivityStatusCode.Error, error.Message);
        _logger.LogWarning(error.Exception, "Media stream error for {Host}: {Message}", Source.Uri.Host, error.Message);
        if (!error.IsRecoverable)
        {
            TransitionTo(MediaPlaybackState.Faulted);
        }

        PublishEvent(
            Error,
            new MediaPlaybackErrorEventArgs(error),
            "error");
    }

    private void OnHardwareVideoDecodingChanged(object? sender, bool isActive)
    {
        lock (_sync)
        {
            _isHardwareVideoDecodingActive = isActive;
        }
    }

    private void OnPerformanceUpdated(object? sender, FfmpegPerformanceSnapshot snapshot)
    {
        lock (_sync)
        {
            _lastPerformance = snapshot;
        }

        FfmpegTelemetry.FrameReadDuration.Record(snapshot.ReadMilliseconds);
        FfmpegTelemetry.FrameDecodeDuration.Record(snapshot.DecodeMilliseconds);
    }

    private void OnFrameLeaseReceived(FfmpegMediaFrameLease lease)
    {
        var deliveryCompleted = false;
        try
        {
            EventHandler<MediaVideoFrame>[] subscribers;
            Action<IMediaFrameLease>? frameLeaseConsumer;
            lock (_sync)
            {
                subscribers = _frameReceivedSnapshot;
                frameLeaseConsumer = _frameLeaseConsumer;
            }

            var keepLatestFrame = Options.Video.SnapshotPolicy == MediaSnapshotPolicy.KeepLatestFrame;
            var frame = TryCopyFrame(lease, keepLatestFrame || subscribers.Length > 0);
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

            if (frameLeaseConsumer is null)
            {
                MediaFrameDelivery.Deliver(_videoOutput, lease, out var deliveryError);
                if (deliveryError is not null)
                {
                    _logger.LogWarning(deliveryError, "A media video output failed.");
                }
            }
            else
            {
                frameLeaseConsumer(lease);
            }
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
        var deliveredToConsumer = false;
        try
        {
            Action<IMediaFrameLease>? supplementaryConsumer;
            EventHandler<MediaVideoFrame>[] subscribers;
            var keepLatestFrame = Options.Video.SnapshotPolicy == MediaSnapshotPolicy.KeepLatestFrame;
            lock (_sync)
            {
                subscribers = _frameReceivedSnapshot;
                supplementaryConsumer = _supplementaryCpuFrameLeaseConsumer;
            }

            var frame = TryCopyFrame(lease, keepLatestFrame || subscribers.Length > 0);
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

            if (supplementaryConsumer is not null)
            {
                supplementaryConsumer(lease);
                deliveredToConsumer = true;
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "A GPU snapshot frame could not be copied.");
        }
        finally
        {
            if (!deliveredToConsumer)
            {
                lease.Dispose();
            }
        }
    }

    private MediaVideoFrame? TryCopyFrame(FfmpegMediaFrameLease lease, bool required)
    {
        if (!required ||
            lease.PixelFormat != FfmpegNativePixelFormat.Bgra32 ||
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

        var data = RuntimeCompat.AllocateBytes(byteCount);
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

    private void PublishFrame(EventHandler<MediaVideoFrame>[] subscribers, MediaVideoFrame frame)
    {
        if (subscribers.Length == 0)
        {
            return;
        }

        for (var i = 0; i < subscribers.Length; i++)
        {
            try
            {
                subscribers[i](this, frame);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "A media frame subscriber failed.");
            }
        }

        FfmpegTelemetry.FramesDelivered.Add(1);
    }

    private static EventHandler<MediaVideoFrame>[] BuildFrameSubscribersSnapshot(
        EventHandler<MediaVideoFrame>? handlers)
    {
        if (handlers is null)
        {
            return [];
        }

        var list = handlers.GetInvocationList();
        var result = new EventHandler<MediaVideoFrame>[list.Length];
        for (var i = 0; i < list.Length; i++)
        {
            result[i] = (EventHandler<MediaVideoFrame>)list[i];
        }

        return result;
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

        PublishEvent(
            StateChanged,
            new MediaPlaybackStateChangedEventArgs(oldState, state),
            "state");
    }

    private void PublishEvent<TEventArgs>(
        EventHandler<TEventArgs>? subscribers,
        TEventArgs args,
        string eventName)
    {
        if (subscribers is null)
        {
            return;
        }

        foreach (EventHandler<TEventArgs> subscriber in subscribers.GetInvocationList())
        {
            try
            {
                subscriber(this, args);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "A media {EventName} subscriber failed.",
                    eventName);
            }
        }
    }

    private void UpdateFrameDeliveryLocked()
    {
        if (_client is null)
        {
            return;
        }

        if (_isSharedPhysicalSession)
        {
            _client.SetSharedFrameDeliveryRequirements(
                _requiresNativeFrames,
                _requiresCpuFrames || _frameReceived is not null);
        }

        _client.SetFrameDeliveryEnabled(
            Options.Video.SnapshotPolicy == MediaSnapshotPolicy.KeepLatestFrame ||
            _frameReceived is not null ||
            _frameLeaseConsumer is not null ||
            _supplementaryCpuFrameLeaseConsumer is not null ||
            _videoOutput is not null);
    }

    private void ThrowIfDisposed() => RuntimeCompat.NotDisposed(_disposed, this);
}
