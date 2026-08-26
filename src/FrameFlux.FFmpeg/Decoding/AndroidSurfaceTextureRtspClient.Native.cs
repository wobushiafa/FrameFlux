#if ANDROID
using Android.Graphics;
using Android.Media;
using Android.Views;
using Java.Nio;
using System.Buffers;
using System.Diagnostics;

namespace FrameFlux.FFmpeg;

internal sealed class AndroidSurfaceTextureRtspClient : IDisposable
{
    private const int DequeueTimeoutMicroseconds = 10_000;
    private const int InfoTryAgainLater = -1;
    private const int InfoOutputFormatChanged = -2;
    private static readonly byte[] StartCode = [0, 0, 0, 1];
    private readonly string _url;
    private readonly SurfaceTexture _surfaceTexture;
    private readonly RtspStreamOptions _options;
    private Thread? _thread;
    private volatile bool _isRunning;
    private CancellationTokenSource? _cancellationTokenSource;
    private TaskCompletionSource<object?> _completionSource = CreateCompletedCompletionSource();
    private RtspConnectionState _connectionState = RtspConnectionState.Idle;
    private AudioPlaybackController? _audioPlayback;
    private double _volume;
    private bool _isMuted;

    internal string HardwareDiagnostics { get; private set; } = "MediaCodec not started";

    public AndroidSurfaceTextureRtspClient(
        string url,
        SurfaceTexture surfaceTexture,
        RtspStreamOptions options)
    {
        _url = url;
        _surfaceTexture = surfaceTexture;
        _options = options;
        _volume = options.Volume;
        _isMuted = options.IsMuted;
    }

    public event EventHandler<RtspConnectionStateChangedEventArgs>? ConnectionStateChanged;
    public event EventHandler<RtspStreamErrorEventArgs>? StreamError;
    public event EventHandler<bool>? HardwareAccelerationChanged;
    public event EventHandler<RtspPerformanceSnapshot>? PerformanceUpdated;
    public event EventHandler<AndroidVideoSizeChangedEventArgs>? VideoSizeChanged;

    internal Task Completion => Volatile.Read(ref _completionSource).Task;

    internal void SetVolume(double volume)
    {
        var normalized = double.IsNaN(volume) ? 1d : Math.Clamp(volume, 0d, 1d);
        Volatile.Write(ref _volume, normalized);
        Volatile.Read(ref _audioPlayback)?.SetVolume(normalized);
    }

    internal void SetMuted(bool muted)
    {
        Volatile.Write(ref _isMuted, muted);
        Volatile.Read(ref _audioPlayback)?.SetMuted(muted);
    }

    public void Start()
    {
        if (_isRunning)
        {
            return;
        }

        _isRunning = true;
        var cancellation = new CancellationTokenSource();
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _cancellationTokenSource = cancellation;
        _completionSource = completion;
        _thread = new Thread(() => DecodeLoop(cancellation, completion))
        {
            IsBackground = true,
            Name = "AndroidSurfaceTextureRtspClient"
        };
        _thread.Start();
    }

    public void Stop(bool waitForExit = false)
    {
        _isRunning = false;
        var cancellation = _cancellationTokenSource;
        cancellation?.Cancel();
        if (waitForExit && _thread?.IsAlive == true && Thread.CurrentThread != _thread)
        {
            _thread.Join(Math.Max(1000, _options.ReadTimeoutMilliseconds + 1000));
        }
    }

    public void Dispose() => Stop(waitForExit: true);

    private void DecodeLoop(
        CancellationTokenSource threadCancellation,
        TaskCompletionSource<object?> completion)
    {
        var cancellationToken = threadCancellation.Token;
        NativeRtspPacketReader? reader = null;
        AudioPlaybackController? audioPlayback = null;
        MediaCodec? codec = null;
        Surface? surface = null;
        SemaphoreSlim? openSemaphore = null;
        var openSemaphoreEntered = false;
        var terminalState = RtspConnectionState.Stopped;
        long totalReadTicks = 0;
        long totalCodecTicks = 0;
        var performanceSamples = 0;

        try
        {
            RaiseConnectionStateChanged(RtspConnectionState.Connecting);
            openSemaphore = RtspOpenStreamLimiter.GetSemaphore(_options.MaxConcurrentOpenStreams);
            if (openSemaphore != null)
            {
                openSemaphore.Wait(cancellationToken);
                openSemaphoreEntered = true;
            }

            reader = new NativeRtspPacketReader(_url, _options, cancellationToken);
            if (reader.HasAudio && _options.EnableAudio)
            {
                audioPlayback = new AudioPlaybackController(
                    Volatile.Read(ref _volume),
                    Volatile.Read(ref _isMuted));
                Volatile.Write(ref _audioPlayback, audioPlayback);
            }
            if (openSemaphoreEntered)
            {
                openSemaphore!.Release();
                openSemaphoreEntered = false;
            }

            var mimeType = GetMimeType(reader.Codec);
            var width = reader.Width >= 16 ? reader.Width : 320;
            var height = reader.Height >= 16 ? reader.Height : 240;
            var maxWidth = Math.Max(width, _options.MaxVideoWidth > 0 ? _options.MaxVideoWidth : 1920);
            var maxHeight = Math.Max(height, _options.MaxVideoHeight > 0 ? _options.MaxVideoHeight : 1080);
            _surfaceTexture.SetDefaultBufferSize(maxWidth, maxHeight);
            VideoSizeChanged?.Invoke(this, new AndroidVideoSizeChangedEventArgs(width, height));

            surface = new Surface(_surfaceTexture);
            using var format = MediaFormat.CreateVideoFormat(mimeType, width, height);
            format.SetInteger("max-width", maxWidth);
            format.SetInteger("max-height", maxHeight);
            format.SetInteger(MediaFormat.KeyMaxInputSize, Math.Max(1_048_576, maxWidth * maxHeight));
            var codecSpecificData = GetCodecSpecificData(format, reader.Codec, reader.CodecExtraData);
            var codecSelection = AndroidMediaCodecSelector.SelectHardwareDecoder(mimeType);
            codec = MediaCodec.CreateByCodecName(codecSelection.Name);
            codec.Configure(format, surface, null, MediaCodecConfigFlags.None);
            codec.Start();
            HardwareDiagnostics = codecSelection.Diagnostics;
            HardwareAccelerationChanged?.Invoke(this, codecSelection.IsHardwareAccelerated);
            RaiseConnectionStateChanged(RtspConnectionState.Connected);

            var bufferInfo = new MediaCodec.BufferInfo();
            if (codecSpecificData.Length > 0)
            {
                QueueInputData(
                    codec,
                    codecSpecificData,
                    codecSpecificData.Length,
                    0,
                    MediaCodecBufferFlags.CodecConfig,
                    cancellationToken);
            }

            while (_isRunning && !cancellationToken.IsCancellationRequested)
            {
                var readStart = Stopwatch.GetTimestamp();
                var hasPacket = reader.TryReadPacket(out var packet);
                DrainAudio(reader, audioPlayback);
                totalReadTicks += Stopwatch.GetTimestamp() - readStart;
                if (!hasPacket || packet == null)
                {
                    break;
                }

                using (packet)
                {
                    var codecStart = Stopwatch.GetTimestamp();
                    DrainOutput(
                        codec,
                        bufferInfo,
                        audioPlayback,
                        cancellationToken,
                        ref totalCodecTicks);
                    QueuePacket(codec, packet, reader, cancellationToken);
                    DrainOutput(
                        codec,
                        bufferInfo,
                        audioPlayback,
                        cancellationToken,
                        ref totalCodecTicks);
                    totalCodecTicks += Stopwatch.GetTimestamp() - codecStart;
                }

                performanceSamples++;
                PublishPerformance(ref totalReadTicks, ref totalCodecTicks, ref performanceSamples);
            }
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            terminalState = RtspConnectionState.Failed;
            RaiseError(exception, willRetry: _options.FallbackToSoftwareDecoding);
        }
        finally
        {
            if (openSemaphoreEntered)
            {
                TryCleanup(() => openSemaphore!.Release(), "release the Android RTSP open semaphore");
            }
            Interlocked.CompareExchange(ref _audioPlayback, null, audioPlayback);
            TryCleanup(() => reader?.Dispose(), "dispose the native packet reader");
            TryCleanup(() => audioPlayback?.Dispose(), "dispose the Android audio output");
            TryCleanup(() => codec?.Stop(), "stop the Android media codec");
            TryCleanup(() => codec?.Release(), "release the Android media codec");
            TryCleanup(() => codec?.Dispose(), "dispose the Android media codec");
            TryCleanup(() => surface?.Release(), "release the Android decoder surface");
            TryCleanup(() => surface?.Dispose(), "dispose the Android decoder surface");
            TryCleanup(() => RaiseConnectionStateChanged(terminalState), "publish the terminal state");

            _isRunning = false;
            if (ReferenceEquals(_cancellationTokenSource, threadCancellation))
            {
                _cancellationTokenSource = null;
            }
            if (ReferenceEquals(_thread, Thread.CurrentThread))
            {
                _thread = null;
            }
            threadCancellation.Dispose();
            completion.TrySetResult(null);
        }
    }

    private static void QueuePacket(
        MediaCodec codec,
        NativeEncodedPacket packet,
        NativeRtspPacketReader reader,
        CancellationToken cancellationToken)
    {
        var size = Math.Max(0, packet.Info.Size);
        var presentationTime = GetPresentationTimeMicroseconds(packet.Info, reader);
        if (size == 0)
        {
            QueueInputData(codec, [], 0, presentationTime, MediaCodecBufferFlags.None, cancellationToken);
            return;
        }

        var data = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            packet.CopyTo(data, size);
            QueueInputData(codec, data, size, presentationTime, MediaCodecBufferFlags.None, cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(data);
        }
    }

    private static void QueueInputData(
        MediaCodec codec,
        byte[] data,
        int dataLength,
        long presentationTimeMicroseconds,
        MediaCodecBufferFlags flags,
        CancellationToken cancellationToken)
    {
        int inputIndex;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            inputIndex = codec.DequeueInputBuffer(DequeueTimeoutMicroseconds);
        }
        while (inputIndex < 0);

        var inputBuffer = codec.GetInputBuffer(inputIndex);
        if (inputBuffer == null)
        {
            codec.QueueInputBuffer(inputIndex, 0, 0, presentationTimeMicroseconds, flags);
            return;
        }

        inputBuffer.Clear();
        var capacity = inputBuffer.Capacity();
        if (dataLength > capacity)
        {
            codec.QueueInputBuffer(inputIndex, 0, 0, presentationTimeMicroseconds, flags);
            throw new InvalidOperationException(
                $"Encoded video packet ({dataLength} bytes) exceeds the MediaCodec input buffer capacity ({capacity} bytes). Packet truncation is not allowed.");
        }

        inputBuffer.Put(data, 0, dataLength);
        codec.QueueInputBuffer(inputIndex, 0, dataLength, presentationTimeMicroseconds, flags);
    }

    private static void DrainOutput(
        MediaCodec codec,
        MediaCodec.BufferInfo bufferInfo,
        AudioPlaybackController? audioPlayback,
        CancellationToken cancellationToken,
        ref long totalCodecTicks)
    {
        while (true)
        {
            var startedAt = Stopwatch.GetTimestamp();
            var outputIndex = codec.DequeueOutputBuffer(bufferInfo, 0);
            totalCodecTicks += Stopwatch.GetTimestamp() - startedAt;
            if (outputIndex == InfoTryAgainLater)
            {
                return;
            }
            if (outputIndex == InfoOutputFormatChanged)
            {
                continue;
            }
            if (outputIndex >= 0)
            {
                var render = bufferInfo.Size > 0 &&
                    SynchronizeVideo(bufferInfo.PresentationTimeUs / 1_000_000d, audioPlayback, cancellationToken);
                codec.ReleaseOutputBuffer(outputIndex, render);
            }
        }
    }

    private static void DrainAudio(
        NativeRtspPacketReader reader,
        AudioPlaybackController? audioPlayback)
    {
        if (audioPlayback is null)
        {
            while (reader.TryDequeueAudioFrame(out _)) { }
            return;
        }

        while (reader.TryDequeueAudioFrame(out var frame) && frame is not null)
        {
            audioPlayback.Write(frame);
        }
    }

    private static bool SynchronizeVideo(
        double videoPosition,
        AudioPlaybackController? audioPlayback,
        CancellationToken cancellationToken)
    {
        if (audioPlayback?.PositionSeconds is not { } audioPosition)
        {
            return true;
        }

        var difference = videoPosition - audioPosition;
        if (difference < -0.100d)
        {
            return false;
        }

        if (difference > 0.005d &&
            cancellationToken.WaitHandle.WaitOne(
                TimeSpan.FromSeconds(Math.Min(difference, 0.5d))))
        {
            return false;
        }

        return true;
    }

    private void PublishPerformance(ref long readTicks, ref long codecTicks, ref int samples)
    {
        if (samples < 30)
        {
            return;
        }

        PerformanceUpdated?.Invoke(this, new RtspPerformanceSnapshot(
            readTicks * 1000d / Stopwatch.Frequency / samples,
            codecTicks * 1000d / Stopwatch.Frequency / samples,
            0, 0, 0, 0, samples));
        readTicks = 0;
        codecTicks = 0;
        samples = 0;
    }

    private static long GetPresentationTimeMicroseconds(
        NativePacketInfo packet,
        NativeRtspPacketReader reader)
    {
        var timestamp = packet.PresentationTimestamp != long.MinValue
            ? packet.PresentationTimestamp
            : packet.DecodeTimestamp;
        if (timestamp == long.MinValue || reader.TimeBaseDenominator <= 0)
        {
            return 0;
        }

        return (long)(timestamp * 1_000_000d * reader.TimeBaseNumerator / reader.TimeBaseDenominator);
    }

    private static string GetMimeType(NativeVideoCodec codec) => codec switch
    {
        NativeVideoCodec.H264 => MediaFormat.MimetypeVideoAvc,
        NativeVideoCodec.Hevc => MediaFormat.MimetypeVideoHevc,
        _ => throw new NotSupportedException($"Android SurfaceTexture does not support codec {codec}.")
    };

    private static byte[] GetCodecSpecificData(
        MediaFormat format,
        NativeVideoCodec codec,
        byte[] extraData)
    {
        if (extraData.Length == 0)
        {
            return [];
        }
        if (codec == NativeVideoCodec.H264 && TryParseAvcC(extraData, out var sps, out var pps))
        {
            format.SetByteBuffer("csd-0", ByteBuffer.Wrap(sps));
            format.SetByteBuffer("csd-1", ByteBuffer.Wrap(pps));
            return [];
        }
        if (codec == NativeVideoCodec.Hevc && TryParseHevcC(extraData, out var hevcData))
        {
            return hevcData;
        }

        format.SetByteBuffer("csd-0", ByteBuffer.Wrap(extraData));
        return [];
    }

    private static bool TryParseAvcC(byte[] source, out byte[] sps, out byte[] pps)
    {
        sps = [];
        pps = [];
        if (source.Length < 7 || source[0] != 1)
        {
            return false;
        }
        var offset = 5;
        var spsCount = source[offset++] & 0x1F;
        if (spsCount <= 0 || !TryReadNalUnits(source, ref offset, spsCount, out sps) || offset >= source.Length)
        {
            return false;
        }
        var ppsCount = source[offset++];
        return ppsCount > 0 && TryReadNalUnits(source, ref offset, ppsCount, out pps);
    }

    private static bool TryReadNalUnits(byte[] source, ref int offset, int count, out byte[] data)
    {
        using var stream = new MemoryStream();
        data = [];
        for (var index = 0; index < count; index++)
        {
            if (offset + 2 > source.Length)
            {
                return false;
            }
            var length = (source[offset] << 8) | source[offset + 1];
            offset += 2;
            if (offset + length > source.Length)
            {
                return false;
            }
            stream.Write(StartCode);
            stream.Write(source, offset, length);
            offset += length;
        }
        data = stream.ToArray();
        return data.Length > StartCode.Length;
    }

    private static bool TryParseHevcC(byte[] source, out byte[] data)
    {
        data = [];
        if (source.Length < 23 || source[0] != 1)
        {
            return false;
        }
        var offset = 23;
        var arrayCount = source[22];
        using var stream = new MemoryStream();
        for (var arrayIndex = 0; arrayIndex < arrayCount; arrayIndex++)
        {
            if (offset + 3 > source.Length)
            {
                return false;
            }
            var nalType = source[offset++] & 0x3F;
            var nalCount = (source[offset] << 8) | source[offset + 1];
            offset += 2;
            for (var nalIndex = 0; nalIndex < nalCount; nalIndex++)
            {
                if (offset + 2 > source.Length)
                {
                    return false;
                }
                var nalLength = (source[offset] << 8) | source[offset + 1];
                offset += 2;
                if (offset + nalLength > source.Length)
                {
                    return false;
                }
                if (nalType is 32 or 33 or 34)
                {
                    stream.Write(StartCode);
                    stream.Write(source, offset, nalLength);
                }
                offset += nalLength;
            }
        }
        data = stream.ToArray();
        return data.Length > StartCode.Length;
    }

    private static void TryCleanup(Action cleanup, string operation)
    {
        try
        {
            cleanup();
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Failed to {operation}: {exception}");
        }
    }

    private static TaskCompletionSource<object?> CreateCompletedCompletionSource()
    {
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        completion.SetResult(null);
        return completion;
    }

    private void RaiseConnectionStateChanged(RtspConnectionState state)
    {
        var previous = _connectionState;
        _connectionState = state;
        ConnectionStateChanged?.Invoke(this, new RtspConnectionStateChangedEventArgs(previous, state));
    }

    private void RaiseError(Exception exception, bool willRetry)
    {
        var error = new RtspStreamError(
            RtspStreamErrorKind.Unknown,
            exception.Message,
            exception,
            willRetry);
        StreamError?.Invoke(this, new RtspStreamErrorEventArgs(error));
    }
}

internal sealed class AndroidVideoSizeChangedEventArgs : EventArgs
{
    public AndroidVideoSizeChangedEventArgs(int width, int height)
    {
        Width = width;
        Height = height;
    }

    public int Width { get; }
    public int Height { get; }
}
#endif
