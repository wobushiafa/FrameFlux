using System.Diagnostics;
using Android.Media;
using Android.Views;
using Java.Nio;

namespace FrameFlux.FFmpeg.Android;

internal sealed class AndroidMediaCodecDecoderFactory : IPlatformRtspDecoderFactory
{
    public bool CanCreate(IMediaVideoOutput? output, RtspStreamOptions options) =>
        !options.CreateSnapshotFrames && ResolveSurfaceOutput(output) is not null;

    public IPlatformRtspDecoder Create(
        string url,
        RtspStreamOptions options,
        IMediaVideoOutput output,
        CancellationToken cancellationToken)
    {
        var surfaceOutput = ResolveSurfaceOutput(output) ??
            throw new InvalidOperationException(
                "The Android MediaCodec backend requires an IAndroidVideoSurfaceOutput.");
        try
        {
            return new AndroidMediaCodecDecoder(
                url,
                options,
                surfaceOutput,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new RtspDecoderRuntimeException(
                $"Android MediaCodec initialization failed: {exception.Message}",
                exception,
                isHardwareVideoDecodingActive: true);
        }
    }

    private static IAndroidVideoSurfaceOutput? ResolveSurfaceOutput(IMediaVideoOutput? output)
    {
        if (output is IAndroidVideoSurfaceOutput direct) return direct;
        return (output as IMediaVideoOutputFeatureProvider)?.GetVideoOutputFeature(
            typeof(IAndroidVideoSurfaceOutput)) as IAndroidVideoSurfaceOutput;
    }
}

internal sealed class AndroidMediaCodecDecoder : IPlatformRtspDecoder
{
    private const int OutputFormatChanged = -2;
    private const long CodecTimeoutMicroseconds = 10_000;
    private readonly IAndroidVideoSurfaceOutput _output;
    private readonly NativeRtspPacketReader _reader;
    private readonly MediaCodec _codec;
    private readonly MediaCodec.BufferInfo _bufferInfo = new();
    private readonly MediaCodecInitializationData _initializationData;
    private byte[] _packetBuffer = [];
    private byte[] _normalizedPacketBuffer = [];
    private long _nextFallbackTimestampMicroseconds;
    private bool _disposed;
    private int _outputWidth;
    private int _outputHeight;

    internal AndroidMediaCodecDecoder(
        string url,
        RtspStreamOptions options,
        IAndroidVideoSurfaceOutput output,
        CancellationToken cancellationToken)
    {
        NativeRtspPacketReader? reader = null;
        MediaCodec? codec = null;
        try
        {
            reader = new NativeRtspPacketReader(url, options, cancellationToken);
            var mimeType = reader.Codec switch
            {
                NativeVideoCodec.H264 => MediaFormat.MimetypeVideoAvc,
                NativeVideoCodec.Hevc => MediaFormat.MimetypeVideoHevc,
                _ => throw new PlatformNotSupportedException(
                    $"Android MediaCodec does not support the native codec id {reader.Codec}.")
            };

            _initializationData = MediaCodecBitstreamAdapter.Parse(
                reader.Codec,
                reader.CodecExtraData);
            using var format = MediaFormat.CreateVideoFormat(
                mimeType,
                reader.Width,
                reader.Height) ?? throw new InvalidOperationException(
                    "Android failed to create a MediaCodec video format.");
            SetCodecSpecificData(format, "csd-0", _initializationData.CodecSpecificData0);
            SetCodecSpecificData(format, "csd-1", _initializationData.CodecSpecificData1);
            if (options.LowLatency)
            {
                format.SetInteger(MediaFormat.KeyPriority, 0);
                format.SetInteger(MediaFormat.KeyOperatingRate, short.MaxValue);
            }

            var surface = output.AcquireDecoderSurface(cancellationToken);
            output.SetDecodedVideoSize(reader.Width, reader.Height);
            codec = MediaCodec.CreateDecoderByType(mimeType) ??
                throw new PlatformNotSupportedException(
                    $"No Android MediaCodec decoder is available for {mimeType}.");
            codec.Configure(format, surface, null, MediaCodecConfigFlags.None);
            codec.Start();

            _output = output;
            _outputWidth = reader.Width;
            _outputHeight = reader.Height;
            _reader = reader;
            _codec = codec;
            VideoDecoderDiagnostics = $"Android MediaCodec ({codec.Name ?? mimeType})";
        }
        catch
        {
            reader?.Dispose();
            if (codec is not null)
            {
                TryStopAndRelease(codec);
            }
            throw;
        }
    }

    public bool HasAudio => _reader.HasAudio;

    public bool IsHardwareVideoDecodingActive => true;

    public string VideoDecoderDiagnostics { get; }

    public long LastReadTicks { get; private set; }

    public long LastCodecTicks { get; private set; }

    public bool TryDecodeNextFrame(out IPlatformDecodedVideoFrame? frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        frame = null;
        try
        {
            while (true)
            {
                var codecStartedAt = Stopwatch.GetTimestamp();
                var outputIndex = _codec.DequeueOutputBuffer(
                    _bufferInfo,
                    CodecTimeoutMicroseconds);
                LastCodecTicks = Stopwatch.GetTimestamp() - codecStartedAt;
                if (outputIndex >= 0)
                {
                    frame = new AndroidMediaCodecFrame(
                        _codec,
                        outputIndex,
                        _bufferInfo.PresentationTimeUs / 1_000_000d,
                        _outputWidth,
                        _outputHeight);
                    return true;
                }

                if (outputIndex == OutputFormatChanged)
                {
                    UpdateOutputFormat();
                    continue;
                }

                var inputIndex = _codec.DequeueInputBuffer(CodecTimeoutMicroseconds);
                if (inputIndex < 0)
                {
                    continue;
                }

                var readStartedAt = Stopwatch.GetTimestamp();
                var hasPacket = _reader.TryReadPacket(out var packet);
                LastReadTicks = Stopwatch.GetTimestamp() - readStartedAt;
                if (!hasPacket || packet is null)
                {
                    return false;
                }

                using (packet)
                {
                    QueuePacket(inputIndex, packet);
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new RtspDecoderRuntimeException(
                $"Android MediaCodec decoding failed: {exception.Message}",
                exception,
                isHardwareVideoDecodingActive: true);
        }
    }

    public bool TryDequeueAudioFrame(out NativeAudioFrame? frame) =>
        _reader.TryDequeueAudioFrame(out frame);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Cancelling/closing packet input precedes codec shutdown. The Surface is output-owned.
        _reader.Dispose();
        TryStopAndRelease(_codec);
        _bufferInfo.Dispose();
    }

    private void QueuePacket(int inputIndex, NativeEncodedPacket packet)
    {
        var packetSize = packet.Info.Size;
        if (_packetBuffer.Length < packetSize)
        {
            _packetBuffer = GC.AllocateUninitializedArray<byte>(packetSize);
        }
        packet.CopyTo(_packetBuffer, packetSize);
        var normalizedLength = MediaCodecBitstreamAdapter.NormalizePacket(
            _packetBuffer.AsSpan(0, packetSize),
            _initializationData.NalLengthSize,
            ref _normalizedPacketBuffer);

        using var inputBuffer = _codec.GetInputBuffer(inputIndex) ??
            throw new InvalidOperationException("MediaCodec returned no input buffer.");
        if (inputBuffer.Capacity() < normalizedLength)
        {
            throw new InvalidOperationException(
                $"MediaCodec input capacity {inputBuffer.Capacity()} is smaller than packet {normalizedLength}.");
        }

        inputBuffer.Clear();
        inputBuffer.Put(_normalizedPacketBuffer, 0, normalizedLength);
        var timestamp = ToMicroseconds(packet.Info.PresentationTimestamp);
        _codec.QueueInputBuffer(
            inputIndex,
            0,
            normalizedLength,
            timestamp,
            MediaCodecBufferFlags.None);
    }

    private long ToMicroseconds(long timestamp)
    {
        if (timestamp != long.MinValue && _reader.TimeBaseDenominator > 0)
        {
            var converted = timestamp *
                (double)_reader.TimeBaseNumerator /
                _reader.TimeBaseDenominator * 1_000_000d;
            var value = (long)Math.Clamp(converted, 0d, long.MaxValue);
            _nextFallbackTimestampMicroseconds = Math.Max(
                _nextFallbackTimestampMicroseconds,
                value + 1);
            return value;
        }

        return _nextFallbackTimestampMicroseconds += 33_333;
    }

    private void UpdateOutputFormat()
    {
        using var format = _codec.OutputFormat;
        if (format is null) return;
        var width = format.GetInteger(MediaFormat.KeyWidth);
        var height = format.GetInteger(MediaFormat.KeyHeight);
        if (width <= 0 || height <= 0) return;
        _outputWidth = width;
        _outputHeight = height;
        _output.SetDecodedVideoSize(width, height);
    }

    private static void SetCodecSpecificData(
        MediaFormat format,
        string key,
        byte[]? data)
    {
        if (data is null || data.Length == 0) return;
        using var buffer = ByteBuffer.Wrap(data);
        format.SetByteBuffer(key, buffer);
    }

    private static void TryStopAndRelease(MediaCodec codec)
    {
        try
        {
            codec.Stop();
        }
        catch
        {
        }
        finally
        {
            codec.Release();
            codec.Dispose();
        }
    }
}

internal sealed class AndroidMediaCodecFrame(
    MediaCodec codec,
    int outputIndex,
    double presentationSeconds,
    int width,
    int height) : IPlatformDecodedVideoFrame
{
    private int _outputIndex = outputIndex;

    public int Width { get; } = width;

    public int Height { get; } = height;

    public double? PresentationSeconds { get; } = presentationSeconds;

    public void Present()
    {
        var index = Interlocked.Exchange(ref _outputIndex, -1);
        if (index >= 0) codec.ReleaseOutputBuffer(index, render: true);
    }

    public void Dispose()
    {
        var index = Interlocked.Exchange(ref _outputIndex, -1);
        if (index >= 0) codec.ReleaseOutputBuffer(index, render: false);
    }
}
