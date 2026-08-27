using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace FrameFlux.FFmpeg;

internal sealed class RtspStreamClient : IDisposable
{
    private static readonly object OpenSemaphoreLock = new();
    private static readonly Random ReconnectJitter = new();
    private readonly string _url;
    private RtspStreamOptions _options;
    private Thread? _decodeThread;
    private volatile bool _isRunning;
    private CancellationTokenSource? _cancellationTokenSource;
    private TaskCompletionSource<object?> _completionSource = CreateCompletedCompletionSource();
    private volatile bool _isFrameDeliveryEnabled = true;
    private RtspConnectionState _connectionState = RtspConnectionState.Idle;
    private readonly UnmanagedFrameBufferPool _frameBufferPool = new();
    private int _disposeSignaled;
    private string _videoDecoderDiagnostics = "N/A";
    private double _volume;
    private bool _muted;
    private AudioPlaybackController? _audioPlayback;
    private MediaAudioDiagnostics _lastAudioDiagnostics = MediaAudioDiagnostics.Empty;
    private MediaSynchronizationDiagnostics _synchronizationDiagnostics =
        MediaSynchronizationDiagnostics.Empty;
    public string VideoDecoderDiagnostics => _videoDecoderDiagnostics;
    public MediaAudioDiagnostics AudioDiagnostics =>
        Volatile.Read(ref _audioPlayback)?.Diagnostics ?? _lastAudioDiagnostics;
    public MediaSynchronizationDiagnostics SynchronizationDiagnostics =>
        _synchronizationDiagnostics;

    internal delegate void FrameReceivedHandler(IntPtr buffer, int width, int height, int stride);
    internal delegate void FrameLeaseReceivedHandler(FfmpegMediaFrameLease lease);
    internal event FrameReceivedHandler? OnFrameReceived;
    internal event FrameLeaseReceivedHandler? OnFrameLeaseReceived;
    internal event FrameLeaseReceivedHandler? OnSnapshotFrameLeaseReceived;
    internal event EventHandler<RtspStreamErrorEventArgs>? StreamError;
    internal event EventHandler<RtspConnectionStateChangedEventArgs>? ConnectionStateChanged;
    internal event EventHandler<bool>? HardwareVideoDecodingChanged;
    internal event EventHandler<RtspPerformanceSnapshot>? PerformanceUpdated;

    internal Task Completion => Volatile.Read(ref _completionSource).Task;

    internal void SetFrameDeliveryEnabled(bool enabled) => _isFrameDeliveryEnabled = enabled;

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

    internal RtspStreamClient(string url, RtspStreamOptions options)
    {
        _url = url;
        _options = options;
        _volume = options.Volume;
        _muted = options.IsMuted;
        RtspRuntimeDiagnostics.OnStreamClientCreated();
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
        var cancellationTokenSource = _cancellationTokenSource;
        cancellationTokenSource?.Cancel();

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
            _frameBufferPool.StopAcceptingReturns();
        }
    }

    private void DecodeLoop(
        CancellationTokenSource threadCancellationTokenSource,
        TaskCompletionSource<object?> completionSource)
    {
        var consecutiveFailureCount = 0;

        try
        {
            while (_isRunning && !threadCancellationTokenSource.IsCancellationRequested)
            {
                RtspDecoder? decoder = null;
                AudioPlaybackController? audioPlayback = null;
                var clockSynchronizer = new MediaClockSynchronizer();
                _synchronizationDiagnostics = MediaSynchronizationDiagnostics.Empty;
                var hasOpened = false;
                IntPtr bgraBuffer = IntPtr.Zero;
                var bufferSize = 0;
                var frameInterval = _options.MaxFramesPerSecond > 0
                    ? TimeSpan.FromSeconds(1 / _options.MaxFramesPerSecond)
                    : TimeSpan.Zero;
                var lastFrameAt = 0L;
                var openSemaphoreEntered = false;
                SemaphoreSlim? openSemaphore = null;
                long totalDecodeTicks = 0;
                long totalReadTicks = 0;
                long totalCodecTicks = 0;
                long totalHardwareTransferTicks = 0;
                long totalConvertTicks = 0;
                long totalDispatchTicks = 0;
                int performanceSamples = 0;

                _frameBufferPool.StartAcceptingReturns();

                try
                {
                    RaiseConnectionStateChanged(RtspConnectionState.Connecting);
                    var cancellationToken = threadCancellationTokenSource.Token;
                    if (!RtspEndpointProbe.IsReachable(
                            _url,
                            _options.EndpointProbeTimeoutMilliseconds,
                            cancellationToken,
                            out var probeFailureMessage))
                    {
                        var nextFailureCount = consecutiveFailureCount + 1;
                        var willRetry = ShouldReconnect(nextFailureCount);
                        RaiseStreamError(new RtspStreamError(
                            RtspStreamErrorKind.OpenFailed,
                            probeFailureMessage ?? "RTSP endpoint is unavailable.",
                            WillRetry: willRetry));
                        if (!willRetry)
                        {
                            return;
                        }

                        RaiseConnectionStateChanged(RtspConnectionState.Reconnecting);
                        consecutiveFailureCount = nextFailureCount;
                        SleepBeforeReconnect(threadCancellationTokenSource, consecutiveFailureCount);
                        continue;
                    }

                    openSemaphore = _options.OpenOperationSemaphore;
                    if (openSemaphore != null)
                    {
                        openSemaphore.Wait(cancellationToken);
                        openSemaphoreEntered = true;
                    }

                    decoder = new RtspDecoder(_url, _options, cancellationToken);
                    if (decoder.HasAudio && _options.EnableAudio)
                    {
                        audioPlayback = new AudioPlaybackController(
                            Volatile.Read(ref _volume),
                            Volatile.Read(ref _muted),
                            _options.AudioGainDecibels,
                            _options.AudioOutputDeviceId,
                            TimeSpan.FromMilliseconds(
                                _options.AudioBufferDurationMilliseconds));
                        Volatile.Write(ref _audioPlayback, audioPlayback);
                    }
                    if (openSemaphoreEntered)
                    {
                        openSemaphore!.Release();
                        openSemaphoreEntered = false;
                    }

                    hasOpened = true;
                    _videoDecoderDiagnostics = decoder.VideoDecoderDiagnostics;
                    HardwareVideoDecodingChanged?.Invoke(this, decoder.IsHardwareVideoDecodingActive);
                    RaiseConnectionStateChanged(RtspConnectionState.Connected);
                    while (_isRunning && !threadCancellationTokenSource.IsCancellationRequested)
                    {
                        var decodeStart = Stopwatch.GetTimestamp();
                        var hasFrame = decoder.TryDecodeNextFrame(out var frame);
                        DrainAudio(decoder, audioPlayback);
                        UpdateSynchronizationDiagnostics(clockSynchronizer, audioPlayback);
                        var decodeElapsedTicks = Stopwatch.GetTimestamp() - decodeStart;
                        if (hasFrame && frame != null)
                        {
                            consecutiveFailureCount = 0;
                            FfmpegMediaFrameLease? frameLease = null;
                            try
                            {
                                if (!SynchronizeVideo(
                                        frame,
                                        audioPlayback,
                                        clockSynchronizer,
                                        cancellationToken))
                                {
                                    continue;
                                }

                                if (!_isFrameDeliveryEnabled)
                                {
                                    continue;
                                }

                                if (!ShouldRenderFrame(frameInterval, ref lastFrameAt))
                                {
                                    continue;
                                }

                                var outputSize = CalculateOutputSize(
                                    frame.Info.Width,
                                    frame.Info.Height,
                                    _options.MaxVideoWidth,
                                    _options.MaxVideoHeight);
                                var useLeasedFrameDelivery =
                                    OnFrameLeaseReceived != null ||
                                    OnSnapshotFrameLeaseReceived != null;
                                IntPtr targetBuffer;
                                var dispatchedNativeFrame = false;

                                if (useLeasedFrameDelivery &&
                                    _options.FrameDeliveryMode == RtspFrameDeliveryMode.D3D11Texture &&
                                    decoder.TryGetNativePixelFormat(frame, out var nativePixelFormat))
                                {
                                    var nativeConvertStart = Stopwatch.GetTimestamp();
                                    if (_options.CreateSnapshotFrames &&
                                        OnSnapshotFrameLeaseReceived is { } snapshotHandler)
                                    {
                                        var snapshotStride = outputSize.Width * 4;
                                        var snapshotSize = snapshotStride * outputSize.Height;
                                        FfmpegMediaFrameLease? snapshotLease =
                                            RentFrameLease(snapshotSize);
                                        try
                                        {
                                            decoder.ConvertFrameToBgra(
                                                frame,
                                                snapshotLease.Buffer,
                                                outputSize.Width,
                                                outputSize.Height,
                                                snapshotStride);
                                            snapshotLease.ResetBgra(
                                                outputSize.Width,
                                                outputSize.Height,
                                                snapshotStride);
                                            snapshotHandler.Invoke(snapshotLease);
                                            snapshotLease = null;
                                        }
                                        finally
                                        {
                                            snapshotLease?.Dispose();
                                        }
                                    }

                                    frameLease = decoder.CreateNativeFrameLease(frame, nativePixelFormat);
                                    var nativeConvertElapsedTicks = Stopwatch.GetTimestamp() - nativeConvertStart;
                                    var nativeDispatchStart = Stopwatch.GetTimestamp();
                                    var nativeHandler = OnFrameLeaseReceived;
                                    if (nativeHandler is not null)
                                    {
                                        nativeHandler.Invoke(frameLease);
                                        frameLease = null;
                                    }
                                    var nativeDispatchElapsedTicks = Stopwatch.GetTimestamp() - nativeDispatchStart;
                                    PublishPerformanceSnapshot(
                                        ref totalReadTicks,
                                        ref totalCodecTicks,
                                        ref totalHardwareTransferTicks,
                                        ref totalDecodeTicks,
                                        ref totalConvertTicks,
                                        ref totalDispatchTicks,
                                        ref performanceSamples,
                                        decoder.LastReadTicks,
                                        decoder.LastCodecTicks,
                                        decoder.LastHardwareTransferTicks,
                                        decodeElapsedTicks,
                                        nativeConvertElapsedTicks,
                                        nativeDispatchElapsedTicks);
                                    dispatchedNativeFrame = true;
                                }

                                if (dispatchedNativeFrame)
                                {
                                    continue;
                                }

                                int dstStride = outputSize.Width * 4;
                                int requiredBufferSize = dstStride * outputSize.Height;
                                if (useLeasedFrameDelivery)
                                {
                                    frameLease = RentFrameLease(requiredBufferSize);
                                    targetBuffer = frameLease.Buffer;
                                }
                                else
                                {
                                    if (bgraBuffer == IntPtr.Zero || bufferSize != requiredBufferSize)
                                    {
                                        if (bgraBuffer != IntPtr.Zero)
                                        {
                                            Marshal.FreeHGlobal(bgraBuffer);
                                        }

                                        bgraBuffer = Marshal.AllocHGlobal(requiredBufferSize);
                                        bufferSize = requiredBufferSize;
                                    }

                                    targetBuffer = bgraBuffer;
                                }

                                var convertStart = Stopwatch.GetTimestamp();
                                decoder.ConvertFrameToBgra(frame, targetBuffer, outputSize.Width, outputSize.Height, dstStride);
                                var convertElapsedTicks = Stopwatch.GetTimestamp() - convertStart;
                                var dispatchStart = Stopwatch.GetTimestamp();
                                if (frameLease != null)
                                {
                                    frameLease.ResetBgra(outputSize.Width, outputSize.Height, dstStride);
                                    var handler = OnFrameLeaseReceived;
                                    if (handler is null)
                                    {
                                        frameLease.Dispose();
                                    }
                                    else
                                    {
                                        handler.Invoke(frameLease);
                                    }
                                    frameLease = null;
                                }
                                else
                                {
                                    OnFrameReceived?.Invoke(targetBuffer, outputSize.Width, outputSize.Height, dstStride);
                                }
                                var dispatchElapsedTicks = Stopwatch.GetTimestamp() - dispatchStart;
                                PublishPerformanceSnapshot(
                                    ref totalReadTicks,
                                    ref totalCodecTicks,
                                    ref totalHardwareTransferTicks,
                                    ref totalDecodeTicks,
                                    ref totalConvertTicks,
                                    ref totalDispatchTicks,
                                    ref performanceSamples,
                                    decoder.LastReadTicks,
                                    decoder.LastCodecTicks,
                                    decoder.LastHardwareTransferTicks,
                                    decodeElapsedTicks,
                                    convertElapsedTicks,
                                    dispatchElapsedTicks);
                            }
                            finally
                            {
                                frameLease?.Dispose();
                                frame.Dispose();
                            }
                        }
                        else
                        {
                            if (!_isRunning || threadCancellationTokenSource.IsCancellationRequested)
                            {
                                break;
                            }

                            var nextFailureCount = consecutiveFailureCount + 1;
                            var willRetry = ShouldReconnect(nextFailureCount);
                            RaiseStreamError(new RtspStreamError(
                                RtspStreamErrorKind.EndOfStream,
                                "Stream ended or no frame was received.",
                                WillRetry: willRetry));
                            if (!willRetry)
                            {
                                return;
                            }

                            RaiseConnectionStateChanged(RtspConnectionState.Reconnecting);
                            consecutiveFailureCount = nextFailureCount;
                            SleepBeforeReconnect(threadCancellationTokenSource, consecutiveFailureCount);
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (!_isRunning || threadCancellationTokenSource.IsCancellationRequested)
                    {
                        break;
                    }

                    var kind = hasOpened ? RtspStreamErrorKind.DecodeFailed : RtspStreamErrorKind.OpenFailed;
                    var willFallbackToSoftware = ShouldFallbackToSoftware(ex);
                    var nextFailureCount = consecutiveFailureCount + 1;
                    var willRetry = ShouldReconnect(nextFailureCount);
                    var errorMessage = FormatExceptionMessage(ex);
                    RaiseStreamError(new RtspStreamError(
                        kind,
                        willFallbackToSoftware ? $"{errorMessage} Falling back to software decoding." : errorMessage,
                        ex,
                        WillRetry: willRetry));

                    if (willFallbackToSoftware)
                    {
                        _options = CreateSoftwareFallbackOptions(_options);
                        HardwareVideoDecodingChanged?.Invoke(this, false);
                    }

                    if (!willRetry)
                    {
                        return;
                    }

                    RaiseConnectionStateChanged(RtspConnectionState.Reconnecting);
                    consecutiveFailureCount = nextFailureCount;
                    SleepBeforeReconnect(threadCancellationTokenSource, consecutiveFailureCount);
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
                    UpdateSynchronizationDiagnostics(clockSynchronizer, audioPlayback);
                    audioPlayback?.Dispose();
                    if (openSemaphoreEntered)
                    {
                        openSemaphore?.Release();
                    }

                    decoder?.Dispose();
                    if (bgraBuffer != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(bgraBuffer);
                    }
                    DisposeLeasePool();
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

    private FfmpegMediaFrameLease RentFrameLease(int requiredSize)
    {
        var buffer = _frameBufferPool.Rent(requiredSize);
        return new FfmpegMediaFrameLease(buffer, requiredSize, ReturnFrameLease);
    }

    private void ReturnFrameLease(FfmpegMediaFrameLease lease)
    {
        _frameBufferPool.Return(lease.Buffer, lease.Size);
    }

    private void DisposeLeasePool()
    {
        _frameBufferPool.StopAcceptingReturns();
    }

    private void RaiseConnectionStateChanged(RtspConnectionState state)
    {
        var oldState = _connectionState;
        if (oldState == state)
        {
            return;
        }

        _connectionState = state;
        ConnectionStateChanged?.Invoke(this, new RtspConnectionStateChangedEventArgs(oldState, state));
    }

    private void RaiseStreamError(RtspStreamError error)
    {
        StreamError?.Invoke(this, new RtspStreamErrorEventArgs(error));
    }

    private bool ShouldFallbackToSoftware(Exception exception)
    {
        return RtspPlaybackConfiguration.UsesHardwareDecoding(_options.VideoDecodingMode) &&
               RtspPlaybackConfiguration.AllowsSoftwareFallback(_options.VideoDecodingMode) &&
               exception is RtspDecoderRuntimeException { IsHardwareVideoDecodingActive: true };
    }

    private static string FormatExceptionMessage(Exception exception)
    {
        var message = exception.Message;
        var inner = exception.InnerException;
        while (inner != null)
        {
            message = $"{message} Inner: {inner.Message}";
            inner = inner.InnerException;
        }

        return message;
    }

    private static RtspStreamOptions CreateSoftwareFallbackOptions(RtspStreamOptions options)
    {
        return new RtspStreamOptions
        {
            VideoDecodingMode = RtspVideoDecodingMode.SoftwareOnly,
            FrameDeliveryMode = options.FrameDeliveryMode,
            Transport = options.Transport,
            OpenTimeoutMilliseconds = options.OpenTimeoutMilliseconds,
            EndpointProbeTimeoutMilliseconds = options.EndpointProbeTimeoutMilliseconds,
            ReadTimeoutMilliseconds = options.ReadTimeoutMilliseconds,
            ReconnectEnabled = options.ReconnectEnabled,
            ReconnectInitialDelayMilliseconds = options.ReconnectInitialDelayMilliseconds,
            ReconnectMaximumDelayMilliseconds = options.ReconnectMaximumDelayMilliseconds,
            MaximumReconnectAttempts = options.MaximumReconnectAttempts,
            OpenOperationSemaphore = options.OpenOperationSemaphore,
            MaxFramesPerSecond = options.MaxFramesPerSecond,
            MaxVideoWidth = options.MaxVideoWidth,
            MaxVideoHeight = options.MaxVideoHeight,
            LowLatency = options.LowLatency,
            EnableAudio = options.EnableAudio,
            CreateSnapshotFrames = options.CreateSnapshotFrames,
            AudioGainDecibels = options.AudioGainDecibels,
            AudioOutputDeviceId = options.AudioOutputDeviceId,
            AudioBufferDurationMilliseconds = options.AudioBufferDurationMilliseconds,
            Volume = options.Volume,
            IsMuted = options.IsMuted,
            ForceOpaqueAlpha = options.ForceOpaqueAlpha,
            ScaleQuality = options.ScaleQuality
        };
    }

    private static bool ShouldRenderFrame(TimeSpan frameInterval, ref long lastFrameAt)
    {
        if (frameInterval <= TimeSpan.Zero)
        {
            return true;
        }

        var now = Stopwatch.GetTimestamp();
        if (lastFrameAt == 0)
        {
            lastFrameAt = now;
            return true;
        }

        var elapsed = Stopwatch.GetElapsedTime(lastFrameAt, now);
        if (elapsed < frameInterval)
        {
            return false;
        }

        lastFrameAt = now;
        return true;
    }

    private static void DrainAudio(
        RtspDecoder decoder,
        AudioPlaybackController? audioPlayback)
    {
        if (audioPlayback is null)
        {
            while (decoder.TryDequeueAudioFrame(out _)) { }
            return;
        }

        while (decoder.TryDequeueAudioFrame(out var audioFrame) && audioFrame is not null)
        {
            audioPlayback.Write(audioFrame);
        }
    }

    private bool SynchronizeVideo(
        NativeDecodedFrame frame,
        AudioPlaybackController? audioPlayback,
        MediaClockSynchronizer clockSynchronizer,
        CancellationToken cancellationToken)
    {
        if (frame.Info.PresentationTimestamp == long.MinValue ||
            frame.Info.TimeBaseDenominator <= 0)
        {
            return true;
        }

        var videoPosition = frame.Info.PresentationTimestamp *
            (double)frame.Info.TimeBaseNumerator / frame.Info.TimeBaseDenominator;
        var decision = clockSynchronizer.EvaluateVideo(
            videoPosition,
            audioPlayback?.PositionSeconds);
        UpdateSynchronizationDiagnostics(clockSynchronizer, audioPlayback);
        if (decision.Action == MediaVideoSynchronizationAction.Drop)
        {
            return false;
        }

        if (decision.Action == MediaVideoSynchronizationAction.Delay)
        {
            if (cancellationToken.WaitHandle.WaitOne(decision.Delay))
            {
                return false;
            }
        }

        return true;
    }

    private void UpdateSynchronizationDiagnostics(
        MediaClockSynchronizer clockSynchronizer,
        AudioPlaybackController? audioPlayback)
    {
        _synchronizationDiagnostics = clockSynchronizer.GetDiagnostics(
            audioPlayback?.ClockResetCount ?? 0);
    }

    private static (int Width, int Height) CalculateOutputSize(int sourceWidth, int sourceHeight, int maxWidth, int maxHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            return (Math.Max(1, sourceWidth), Math.Max(1, sourceHeight));
        }

        if (maxWidth <= 0 && maxHeight <= 0)
        {
            return (sourceWidth, sourceHeight);
        }

        var widthScale = maxWidth > 0 ? (double)maxWidth / sourceWidth : double.PositiveInfinity;
        var heightScale = maxHeight > 0 ? (double)maxHeight / sourceHeight : double.PositiveInfinity;
        var scale = Math.Min(1d, Math.Min(widthScale, heightScale));

        var width = Math.Max(1, (int)Math.Round(sourceWidth * scale));
        var height = Math.Max(1, (int)Math.Round(sourceHeight * scale));
        return (width, height);
    }

    private int GetStopWaitTimeoutMilliseconds()
    {
        var baseTimeout = Math.Max(_options.ReadTimeoutMilliseconds, _options.OpenTimeoutMilliseconds);
        return Math.Clamp(baseTimeout + 1000, 2000, 15000);
    }

    private void SleepBeforeReconnect(CancellationTokenSource threadCancellationTokenSource, int consecutiveFailureCount)
    {
        var delay = CalculateReconnectDelayMilliseconds(consecutiveFailureCount);
        if (delay == 0)
        {
            return;
        }

        threadCancellationTokenSource.Token.WaitHandle.WaitOne(delay);
    }

    internal int CalculateReconnectDelayMilliseconds(int consecutiveFailureCount)
    {
        var baseDelay = Math.Max(0, _options.ReconnectInitialDelayMilliseconds);
        if (baseDelay == 0)
        {
            return 0;
        }

        var exponent = Math.Clamp(Math.Max(consecutiveFailureCount, 1) - 1, 0, 5);
        var multiplier = 1 << exponent;
        var cappedDelay = (int)Math.Min(
            (long)baseDelay * multiplier,
            _options.ReconnectMaximumDelayMilliseconds);
        if (cappedDelay >= _options.ReconnectMaximumDelayMilliseconds)
        {
            return Math.Max(0, _options.ReconnectMaximumDelayMilliseconds);
        }

        var jitterRange = Math.Max(250, cappedDelay / 5);
        lock (OpenSemaphoreLock)
        {
            var remainingBeforeMaximum =
                _options.ReconnectMaximumDelayMilliseconds - cappedDelay;
            return cappedDelay + ReconnectJitter.Next(
                Math.Min(jitterRange, remainingBeforeMaximum + 1));
        }
    }

    internal bool ShouldReconnect(int failureCount) =>
        _options.ReconnectEnabled &&
        (_options.MaximumReconnectAttempts is null ||
         failureCount <= _options.MaximumReconnectAttempts.Value);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeSignaled, 1) == 0)
        {
            RtspRuntimeDiagnostics.OnStreamClientDisposed();
        }

        Stop(waitForExit: true);
    }

    private void PublishPerformanceSnapshot(
        ref long totalReadTicks,
        ref long totalCodecTicks,
        ref long totalHardwareTransferTicks,
        ref long totalDecodeTicks,
        ref long totalConvertTicks,
        ref long totalDispatchTicks,
        ref int performanceSamples,
        long readTicks,
        long codecTicks,
        long hardwareTransferTicks,
        long decodeTicks,
        long convertTicks,
        long dispatchTicks)
    {
        totalReadTicks += readTicks;
        totalCodecTicks += codecTicks;
        totalHardwareTransferTicks += hardwareTransferTicks;
        totalDecodeTicks += decodeTicks;
        totalConvertTicks += convertTicks;
        totalDispatchTicks += dispatchTicks;
        performanceSamples++;

        if (performanceSamples < 30)
        {
            return;
        }

        var snapshot = new RtspPerformanceSnapshot(
            ReadMilliseconds: totalReadTicks * 1000d / Stopwatch.Frequency / performanceSamples,
            CodecMilliseconds: totalCodecTicks * 1000d / Stopwatch.Frequency / performanceSamples,
            HardwareTransferMilliseconds: totalHardwareTransferTicks * 1000d / Stopwatch.Frequency / performanceSamples,
            DecodeMilliseconds: totalDecodeTicks * 1000d / Stopwatch.Frequency / performanceSamples,
            ConvertMilliseconds: totalConvertTicks * 1000d / Stopwatch.Frequency / performanceSamples,
            DispatchMilliseconds: totalDispatchTicks * 1000d / Stopwatch.Frequency / performanceSamples,
            SampleCount: performanceSamples);

        totalReadTicks = 0;
        totalCodecTicks = 0;
        totalHardwareTransferTicks = 0;
        totalDecodeTicks = 0;
        totalConvertTicks = 0;
        totalDispatchTicks = 0;
        performanceSamples = 0;
        PerformanceUpdated?.Invoke(this, snapshot);
    }
}
