using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using FrameFlux;
using DiagnosticActivity = System.Diagnostics.Activity;

namespace FrameFlux.FFmpeg;

public sealed class FfmpegRtspSession : IRtspSession
{
    private readonly object _sync = new();
    private readonly ILogger _logger;
    private RtspStreamClient? _client;
    private Task? _stopTask;
    private DiagnosticActivity? _activity;
    private EventHandler<RtspVideoFrame>? _frameReceived;
    private RtspSessionState _state = RtspSessionState.Idle;
    private RtspSnapshot? _lastSnapshot;
    private RtspPerformanceSnapshot _lastPerformance;
    private string? _lastError;
    private bool _isHardwareAccelerationActive;
    private double _volume;
    private bool _isMuted;
    private long _frameSequence;
    private bool _disposed;

    public FfmpegRtspSession(
        RtspSource source,
        RtspSessionOptions options,
        ILogger? logger = null)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Options = options ?? throw new ArgumentNullException(nameof(options));
        Options.Validate();
        _volume = Options.Volume;
        _isMuted = Options.IsMuted;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    public RtspSource Source { get; }

    public RtspSessionOptions Options { get; }

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

    public RtspSessionState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    public RtspSessionDiagnostics Diagnostics
    {
        get
        {
            lock (_sync)
            {
                return new RtspSessionDiagnostics(
                    _isHardwareAccelerationActive,
                    _client?.HardwareDiagnostics ?? "N/A",
                    _lastPerformance.ReadMilliseconds,
                    _lastPerformance.DecodeMilliseconds,
                    _lastPerformance.SampleCount,
                    _lastError);
            }
        }
    }

    public event EventHandler<RtspSessionStateChangedEventArgs>? StateChanged;

    public event EventHandler<RtspSessionErrorEventArgs>? Error;

    public event EventHandler<RtspVideoFrame>? FrameReceived
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
            if (_client != null)
            {
                return ValueTask.CompletedTask;
            }

            var client = new RtspStreamClient(Source.Uri.ToString(), CreateEngineOptions());
            client.ConnectionStateChanged += OnConnectionStateChanged;
            client.StreamError += OnStreamError;
            client.HardwareAccelerationChanged += OnHardwareAccelerationChanged;
            client.PerformanceUpdated += OnPerformanceUpdated;
            client.OnFrameLeaseReceived += OnFrameLeaseReceived;
            _client = client;
            _activity = RtspTelemetry.Activities.StartActivity(
                "rtsp.session",
                ActivityKind.Client);
            _activity?.SetTag("rtsp.source", Source.Uri.GetLeftPart(UriPartial.Authority));
            _activity?.SetTag("rtsp.transport", Options.Transport.ToString());
            UpdateFrameDeliveryLocked();
            client.Start();
        }

        RtspTelemetry.SessionsStarted.Add(1);
        _logger.LogInformation("Started RTSP session for {Host}", Source.Uri.Host);
        return ValueTask.CompletedTask;
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        Task? stopTask;
        lock (_sync)
        {
            if (_client == null)
            {
                return;
            }

            _client.Stop(waitForExit: false);
            _stopTask ??= CompleteStopAsync(_client);
            stopTask = _stopTask;
        }

        await stopTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<RtspSnapshot?> CaptureSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_lastSnapshot is null)
            {
                return ValueTask.FromResult<RtspSnapshot?>(null);
            }

            return ValueTask.FromResult<RtspSnapshot?>(
                _lastSnapshot with { Data = _lastSnapshot.Data.ToArray() });
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
            client.HardwareAccelerationChanged -= OnHardwareAccelerationChanged;
            client.PerformanceUpdated -= OnPerformanceUpdated;
            client.OnFrameLeaseReceived -= OnFrameLeaseReceived;
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
            TransitionTo(RtspSessionState.Stopped);
            RtspTelemetry.SessionsStopped.Add(1);
            _logger.LogInformation("Stopped RTSP session for {Host}", Source.Uri.Host);
        }
    }

    private RtspStreamOptions CreateEngineOptions() =>
        new()
        {
            Transport = Options.Transport.ToString().ToLowerInvariant(),
            UseHardwareAcceleration = Options.HardwareAcceleration != RtspHardwareAcceleration.Disabled,
            HardwareAccelerationMode = Options.HardwareAcceleration switch
            {
                RtspHardwareAcceleration.Disabled => RtspHardwareAccelerationMode.Disabled,
                RtspHardwareAcceleration.Enabled => RtspHardwareAccelerationMode.Enabled,
                _ => RtspHardwareAccelerationMode.Auto
            },
            // IRtspSession publishes owned BGRA frames. Native surfaces are consumed
            // only by controls that use RtspStreamClient directly.
            RenderMode = RtspRenderMode.SoftwareBitmap,
            OpenTimeoutMilliseconds = ToMilliseconds(Options.OpenTimeout),
            EndpointProbeTimeoutMilliseconds = ToMilliseconds(Options.EndpointProbeTimeout),
            ReadTimeoutMilliseconds = ToMilliseconds(Options.ReadTimeout),
            ReconnectDelayMilliseconds = ToMilliseconds(Options.ReconnectDelay),
            MaxConcurrentOpenStreams = Options.MaxConcurrentOpenStreams,
            MaxFramesPerSecond = Options.MaxFramesPerSecond,
            MaxVideoWidth = Options.MaxVideoWidth,
            MaxVideoHeight = Options.MaxVideoHeight,
            LowLatency = Options.LowLatency,
            FallbackToSoftwareDecoding = Options.FallbackToSoftwareDecoding,
            EnableAudio = Options.EnableAudio,
            Volume = _volume,
            IsMuted = _isMuted
        };

    private static int ToMilliseconds(TimeSpan value) =>
        checked((int)Math.Min(value.TotalMilliseconds, int.MaxValue));

    private void OnConnectionStateChanged(object? sender, RtspConnectionStateChangedEventArgs e)
    {
        TransitionTo(e.NewState switch
        {
            RtspConnectionState.Connecting => RtspSessionState.Connecting,
            RtspConnectionState.Connected => RtspSessionState.Connected,
            RtspConnectionState.Reconnecting => RtspSessionState.Reconnecting,
            RtspConnectionState.Stopped => RtspSessionState.Stopped,
            _ => RtspSessionState.Idle
        });
    }

    private void OnStreamError(object? sender, RtspStreamErrorEventArgs e)
    {
        var error = new RtspSessionError(
            e.Error.Kind.ToString(),
            e.Error.Message,
            e.Error.WillRetry,
            e.Error.Exception);
        lock (_sync)
        {
            _lastError = error.Message;
        }

        RtspTelemetry.SessionErrors.Add(1);
        _activity?.SetStatus(ActivityStatusCode.Error, error.Message);
        _logger.LogWarning(error.Exception, "RTSP stream error for {Host}: {Message}", Source.Uri.Host, error.Message);
        if (!error.WillRetry)
        {
            TransitionTo(RtspSessionState.Faulted);
        }
        Error?.Invoke(this, new RtspSessionErrorEventArgs(error));
    }

    private void OnHardwareAccelerationChanged(object? sender, bool isActive)
    {
        lock (_sync)
        {
            _isHardwareAccelerationActive = isActive;
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

    private void OnFrameLeaseReceived(RtspFrameLease lease)
    {
        try
        {
            EventHandler<RtspVideoFrame>? subscribers;
            lock (_sync)
            {
                subscribers = _frameReceived;
                if (!Options.CaptureSnapshots && subscribers is null)
                {
                    return;
                }
            }

            if (lease.Buffer == IntPtr.Zero ||
                lease.Width <= 0 ||
                lease.Height <= 0 ||
                lease.Stride <= 0)
            {
                return;
            }

            var byteCount = Math.Min(lease.Size, checked(lease.Stride * lease.Height));
            if (byteCount <= 0)
            {
                return;
            }

            var data = GC.AllocateUninitializedArray<byte>(byteCount);
            Marshal.Copy(lease.Buffer, data, 0, byteCount);
            var capturedAt = DateTimeOffset.UtcNow;
            var sequence = Interlocked.Increment(ref _frameSequence);
            var frame = new RtspVideoFrame(
                data,
                lease.Width,
                lease.Height,
                lease.Stride,
                RtspFramePixelFormat.Bgra32,
                sequence,
                capturedAt);

            lock (_sync)
            {
                if (Options.CaptureSnapshots)
                {
                    _lastSnapshot = new RtspSnapshot(
                        data,
                        frame.Width,
                        frame.Height,
                        frame.Stride,
                        frame.PixelFormat,
                        capturedAt);
                }
            }

            if (subscribers != null)
            {
                foreach (EventHandler<RtspVideoFrame> subscriber in subscribers.GetInvocationList())
                {
                    try
                    {
                        subscriber(this, frame);
                    }
                    catch (Exception exception)
                    {
                        _logger.LogWarning(exception, "An RTSP frame subscriber failed.");
                    }
                }

                RtspTelemetry.FramesDelivered.Add(1);
            }
        }
        finally
        {
            lease.Dispose();
        }
    }

    private void TransitionTo(RtspSessionState state)
    {
        RtspSessionState oldState;
        lock (_sync)
        {
            oldState = _state;
            if (oldState == state)
            {
                return;
            }

            _state = state;
        }

        StateChanged?.Invoke(this, new RtspSessionStateChangedEventArgs(oldState, state));
    }

    private void UpdateFrameDeliveryLocked()
    {
        _client?.SetFrameDeliveryEnabled(Options.CaptureSnapshots || _frameReceived != null);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
