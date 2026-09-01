using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FrameFlux.FFmpeg;

internal delegate void FrameReceivedHandler(IntPtr buffer, int width, int height, int stride);
internal delegate void FrameLeaseReceivedHandler(FfmpegMediaFrameLease lease);

internal sealed partial class FfmpegPlaybackClient : IDisposable
{
    private readonly string _url;
    private readonly IMediaVideoOutput? _videoOutput;
    private readonly bool _isLive;
    private readonly ManualResetEventSlim _playbackGate = new(initialState: true);
    private readonly FfmpegPlaybackSynchronizer _playbackSynchronizer;
    private FfmpegPlaybackOptions _options;
    private Thread? _decodeThread;
    private volatile bool _isRunning;
    private CancellationTokenSource? _cancellationTokenSource;
    private TaskCompletionSource<object?> _completionSource = CreateCompletedCompletionSource();
    private PlaybackConnectionState _connectionState = PlaybackConnectionState.Idle;
    private readonly FfmpegFrameDispatcher _frameDispatcher = new();
    private int _disposeSignaled;
    private string _videoDecoderDiagnostics = "N/A";
    private double _volume;
    private bool _muted;
    private AudioPlaybackController? _audioPlayback;
    private MediaAudioDiagnostics _lastAudioDiagnostics = MediaAudioDiagnostics.Empty;
    private readonly MediaReconnectState _reconnectState;
    private MediaSeekRequest? _pendingSeek;
    private long _positionTicks;
    private long _durationTicks = -1;
    private double _playbackRate = 1d;

    public string VideoDecoderDiagnostics => _videoDecoderDiagnostics;
    public MediaAudioDiagnostics AudioDiagnostics =>
        Volatile.Read(ref _audioPlayback)?.Diagnostics ?? _lastAudioDiagnostics;
    public MediaSynchronizationDiagnostics SynchronizationDiagnostics =>
        _playbackSynchronizer.Diagnostics;
    public MediaReconnectDiagnostics ReconnectDiagnostics => _reconnectState.Diagnostics;

    internal TimeSpan Position => TimeSpan.FromTicks(Interlocked.Read(ref _positionTicks));
    internal TimeSpan? Duration => Interlocked.Read(ref _durationTicks) is var ticks && ticks >= 0 ? TimeSpan.FromTicks(ticks) : null;

    internal event FrameReceivedHandler? OnFrameReceived;
    internal event FrameLeaseReceivedHandler? OnFrameLeaseReceived;
    internal event FrameLeaseReceivedHandler? OnSnapshotFrameLeaseReceived;
    internal event EventHandler<FfmpegPlaybackErrorEventArgs>? StreamError;
    internal event EventHandler<PlaybackConnectionStateChangedEventArgs>? ConnectionStateChanged;
    internal event EventHandler<bool>? HardwareVideoDecodingChanged;
    internal event EventHandler<FfmpegPerformanceSnapshot>? PerformanceUpdated;

    internal Task Completion => Volatile.Read(ref _completionSource).Task;

    internal void SetFrameDeliveryEnabled(bool enabled) => _frameDispatcher.IsEnabled = enabled;

    internal void SetVolume(double volume)
    {
        Volatile.Write(ref _volume, volume);
        Volatile.Read(ref _audioPlayback)?.SetVolume(volume);
    }

    internal void SetMuted(bool muted)
    {
        Volatile.Write(ref _muted, muted);
        Volatile.Read(ref _audioPlayback)?.SetMuted(muted);
    }

    internal void SetPaused(bool paused)
    {
        if (_isLive)
        {
            throw new NotSupportedException("Live RTSP sources do not support pausing.");
        }

        Volatile.Read(ref _audioPlayback)?.Reset();
        _playbackSynchronizer.ResetPlaybackClock(Position.TotalSeconds);
        if (paused)
        {
            _playbackGate.Reset();
        }
        else
        {
            _playbackGate.Set();
        }
    }

    internal void SetPlaybackRate(double rate)
    {
        MediaPlaybackClock.ValidateRate(rate);
        if (_isLive && rate != 1d)
        {
            throw new NotSupportedException("Live RTSP sources do not support playback-rate changes.");
        }

        Volatile.Write(ref _playbackRate, rate);
        Volatile.Read(ref _audioPlayback)?.SetPlaybackRate(rate);
        _playbackSynchronizer.SetPlaybackRate(rate, Position.TotalSeconds);
    }


    internal ValueTask SeekAsync(TimeSpan position, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_isLive)
        {
            throw new NotSupportedException("Live RTSP sources do not support seeking.");
        }
        if (position < TimeSpan.Zero || Duration is { } duration && position > duration)
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }

        var request = new MediaSeekRequest(position);
        var previous = Interlocked.Exchange(ref _pendingSeek, request);
        previous?.Completion.TrySetException(
            new OperationCanceledException("The seek request was superseded."));
        return new ValueTask(request.Completion.Task.WaitAsync(cancellationToken));
    }
    internal FfmpegPlaybackClient(
        string url,
        FfmpegPlaybackOptions options,
        IMediaVideoOutput? videoOutput = null)
    {
        _url = url;
        _options = options;
        _videoOutput = videoOutput;
        _reconnectState = new MediaReconnectState(options);
        _volume = options.Volume;
        _muted = options.IsMuted;
        _isLive = Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            uri.Scheme is "rtsp" or "rtsps";
        _playbackSynchronizer = new FfmpegPlaybackSynchronizer(_isLive);
        FfmpegRuntimeDiagnostics.OnStreamClientCreated();
    }

    public void Start()
    {
        if (_isRunning) return;
        _isRunning = true;
        var cancellationTokenSource = new CancellationTokenSource();
        var completionSource = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _cancellationTokenSource = cancellationTokenSource;
        _completionSource = completionSource;
        _decodeThread = new Thread(() => DecodeLoop(cancellationTokenSource, completionSource))
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal
        };
        _decodeThread.Start();
    }

    public void Stop(bool waitForExit = false)
    {
        _isRunning = false;
        _playbackGate.Set();
        var cancellationTokenSource = _cancellationTokenSource;
        cancellationTokenSource?.Cancel();
        Interlocked.Exchange(ref _pendingSeek, null)?.Completion.TrySetCanceled();

        var decodeThread = _decodeThread;
        if (waitForExit && decodeThread?.IsAlive == true && Thread.CurrentThread != decodeThread)
        {
            decodeThread.Join(GetStopWaitTimeoutMilliseconds());
        }

        if (waitForExit && decodeThread?.IsAlive != true)
        {
            _decodeThread = null;
            if (ReferenceEquals(_cancellationTokenSource, cancellationTokenSource))
            {
                _cancellationTokenSource = null;
            }

            cancellationTokenSource?.Dispose();
        }

        if (!_isRunning)
        {
            _frameDispatcher.StopAcceptingReturns();
        }
    }

    private void DecodeLoop(
        CancellationTokenSource threadCancellationTokenSource,
        TaskCompletionSource<object?> completionSource)
    {
        try
        {
            while (_isRunning && !threadCancellationTokenSource.IsCancellationRequested)
            {
                FfmpegDecoder? decoder = null;
                IPlatformVideoDecoder? platformDecoder = null;
                AudioPlaybackController? audioPlayback = null;
                _playbackSynchronizer.ResetSession();
                var performanceTracker = new FfmpegPerformanceTracker(
                    snapshot => PerformanceUpdated?.Invoke(this, snapshot));
                var hasOpened = false;
                var frameInterval = _options.MaxFramesPerSecond > 0
                    ? TimeSpan.FromSeconds(1 / _options.MaxFramesPerSecond)
                    : TimeSpan.Zero;
                var openSemaphoreEntered = false;
                SemaphoreSlim? openSemaphore = null;
                _frameDispatcher.BeginSession();

                try
                {
                    RaiseConnectionStateChanged(PlaybackConnectionState.Connecting);
                    var cancellationToken = threadCancellationTokenSource.Token;
                    if (_isLive && !RtspEndpointProbe.IsReachable(
                            _url,
                            _options.EndpointProbeTimeoutMilliseconds,
                            cancellationToken,
                            out var probeFailureMessage))
                    {
                        var reconnect = RegisterReconnectFailure();
                        RaiseStreamError(new FfmpegPlaybackError(
                            FfmpegPlaybackErrorKind.OpenFailed,
                            probeFailureMessage ?? "RTSP endpoint is unavailable.",
                            WillRetry: reconnect.RetryAllowed));
                        if (!reconnect.RetryAllowed)
                        {
                            return;
                        }

                        RaiseConnectionStateChanged(PlaybackConnectionState.Reconnecting);
                        SleepBeforeReconnect(threadCancellationTokenSource, reconnect.Delay);
                        continue;
                    }

                    openSemaphore = _options.OpenOperationSemaphore;
                    if (openSemaphore != null)
                    {
                        openSemaphore.Wait(cancellationToken);
                        openSemaphoreEntered = true;
                    }

                    platformDecoder = _isLive
                        ? PlatformVideoDecoderRegistry.TryCreate(
                            _url,
                            _options,
                            _videoOutput,
                            cancellationToken)
                        : null;
                    if (platformDecoder is null)
                    {
                        decoder = new FfmpegDecoder(_url, _options, cancellationToken);
                    }
                    if (decoder is not null)
                    {
                        Interlocked.Exchange(ref _durationTicks, decoder.Duration?.Ticks ?? -1);
                        _ = ProcessPendingSeek(decoder);
                    }
                    if ((platformDecoder?.HasAudio ?? decoder!.HasAudio) && _options.EnableAudio)
                    {
                        audioPlayback = new AudioPlaybackController(
                            Volatile.Read(ref _volume),
                            Volatile.Read(ref _muted),
                            _options.AudioGainDecibels,
                            _options.AudioOutputDeviceId,
                            TimeSpan.FromMilliseconds(
                                _options.AudioBufferDurationMilliseconds));
                        audioPlayback.SetPlaybackRate(Volatile.Read(ref _playbackRate));
                        Volatile.Write(ref _audioPlayback, audioPlayback);
                    }
                    if (openSemaphoreEntered)
                    {
                        openSemaphore!.Release();
                        openSemaphoreEntered = false;
                    }

                    hasOpened = true;
                    _videoDecoderDiagnostics = platformDecoder?.VideoDecoderDiagnostics ??
                        decoder!.VideoDecoderDiagnostics;
                    HardwareVideoDecodingChanged?.Invoke(
                        this,
                        platformDecoder?.IsHardwareVideoDecodingActive ?? decoder!.IsHardwareVideoDecodingActive);
                    RaiseConnectionStateChanged(PlaybackConnectionState.Connected);
                    var loopOutcome = platformDecoder is not null
                        ? RunPlatformDecodeLoop(
                            platformDecoder,
                            audioPlayback,
                            performanceTracker,
                            frameInterval,
                            threadCancellationTokenSource)
                        : RunFfmpegDecodeLoop(
                            decoder!,
                            audioPlayback,
                            performanceTracker,
                            frameInterval,
                            threadCancellationTokenSource);
                    if (loopOutcome == DecodeLoopOutcome.Terminate)
                    {
                        return;
                    }
                }
                catch (Exception ex)
                {
                    if (!_isRunning || threadCancellationTokenSource.IsCancellationRequested)
                    {
                        break;
                    }

                    var kind = hasOpened ? FfmpegPlaybackErrorKind.DecodeFailed : FfmpegPlaybackErrorKind.OpenFailed;
                    var willFallbackToSoftware =
                        FfmpegPlaybackPolicy.ShouldFallbackToSoftware(_options, ex);
                    var reconnect = _isLive ? RegisterReconnectFailure() : default;
                    var errorMessage = FfmpegPlaybackPolicy.FormatExceptionMessage(ex);
                    RaiseStreamError(new FfmpegPlaybackError(
                        kind,
                        willFallbackToSoftware ? $"{errorMessage} Falling back to software decoding." : errorMessage,
                        ex,
                        WillRetry: reconnect.RetryAllowed));

                    if (willFallbackToSoftware)
                    {
                        _options = FfmpegPlaybackPolicy.CreateSoftwareFallbackOptions(_options);
                        HardwareVideoDecodingChanged?.Invoke(this, false);
                        continue;
                    }

                    if (!reconnect.RetryAllowed)
                    {
                        return;
                    }

                    RaiseConnectionStateChanged(PlaybackConnectionState.Reconnecting);
                    SleepBeforeReconnect(threadCancellationTokenSource, reconnect.Delay);
                }
                finally
                {
                    if (ReferenceEquals(Volatile.Read(ref _audioPlayback), audioPlayback))
                    {
                        Volatile.Write(ref _audioPlayback, null);
                    }
                    if (audioPlayback is not null)
                    {
                        _lastAudioDiagnostics = audioPlayback.Diagnostics;
                    }
                    _playbackSynchronizer.RefreshDiagnostics(audioPlayback);
                    audioPlayback?.Dispose();
                    if (openSemaphoreEntered)
                    {
                        openSemaphore?.Release();
                    }

                    decoder?.Dispose();
                    platformDecoder?.Dispose();
                    _frameDispatcher.EndSession();
                }
            }
        }
        finally
        {
            if (ReferenceEquals(_cancellationTokenSource, threadCancellationTokenSource))
            {
                _cancellationTokenSource = null;
            }

            if (ReferenceEquals(_decodeThread, Thread.CurrentThread))
            {
                _decodeThread = null;
            }

            threadCancellationTokenSource.Dispose();
            Interlocked.Exchange(ref _pendingSeek, null)?.Completion.TrySetCanceled();
            completionSource.TrySetResult(null);
        }
    }

    private static TaskCompletionSource<object?> CreateCompletedCompletionSource()
    {
        var completionSource = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        completionSource.SetResult(null);
        return completionSource;
    }

    private bool ProcessPendingSeek(FfmpegDecoder decoder)
    {
        var request = Interlocked.Exchange(ref _pendingSeek, null);
        if (request is null)
        {
            return false;
        }

        try
        {
            decoder.Seek(request.Position);
            Interlocked.Exchange(ref _positionTicks, request.Position.Ticks);
            request.Completion.TrySetResult(null);
        }
        catch (Exception exception)
        {
            request.Completion.TrySetException(exception);
        }

        return true;
    }

    private void RaiseConnectionStateChanged(PlaybackConnectionState state)
    {
        var oldState = _connectionState;
        if (oldState == state)
        {
            return;
        }

        _connectionState = state;
        ConnectionStateChanged?.Invoke(this, new PlaybackConnectionStateChangedEventArgs(oldState, state));
    }

    private void RaiseStreamError(FfmpegPlaybackError error)
    {
        StreamError?.Invoke(this, new FfmpegPlaybackErrorEventArgs(error));
    }

    private int GetStopWaitTimeoutMilliseconds()
    {
        var baseTimeout = Math.Max(_options.ReadTimeoutMilliseconds, _options.OpenTimeoutMilliseconds);
        return Math.Clamp(baseTimeout + 1000, 2000, 15000);
    }

    private void SleepBeforeReconnect(CancellationTokenSource threadCancellationTokenSource, TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        threadCancellationTokenSource.Token.WaitHandle.WaitOne(delay);
    }

    private MediaReconnectDecision RegisterReconnectFailure()
    {
        var decision = _reconnectState.RegisterFailure();
        if (decision.RetryAllowed)
        {
            FfmpegTelemetry.ReconnectAttempts.Add(1);
            FfmpegTelemetry.ReconnectDelay.Record(decision.Delay.TotalMilliseconds);
        }

        return decision;
    }

    private void RegisterReconnectSuccess()
    {
        if (_reconnectState.RegisterSuccess())
        {
            FfmpegTelemetry.ReconnectRecoveries.Add(1);
        }
    }


    internal int CalculateReconnectDelayMilliseconds(int consecutiveFailureCount) =>
        _reconnectState.CalculateDelayMilliseconds(consecutiveFailureCount);

    internal bool ShouldReconnect(int failureCount) =>
        _options.ReconnectEnabled &&
        (_options.MaximumReconnectAttempts is null ||
         failureCount <= _options.MaximumReconnectAttempts.Value);
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeSignaled, 1) == 0)
        {
            FfmpegRuntimeDiagnostics.OnStreamClientDisposed();
        }

        Stop(waitForExit: true);
    }

}
