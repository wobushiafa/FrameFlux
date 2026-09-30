#if ANDROID
using Android.Media;
using FrameFlux.FFmpeg.Android;
using Java.Nio;
using SIPSorceryMedia.Abstractions;

namespace FrameFlux.WebRtc;

internal sealed class AndroidWebRtcVideoDecoder : IWebRtcVideoDecoder
{
    private const long CodecTimeoutMicroseconds = 10_000;
    private readonly object _sync = new();
    private readonly FfmpegWebRtcVideoDecoder? _software =
        FfmpegWebRtcVideoDecoder.IsSupported ? new FfmpegWebRtcVideoDecoder() : null;
    private IAndroidVideoSurfaceOutput? _surfaceOutput;
    private MediaCodec? _codec;
    private readonly MediaCodec.BufferInfo _bufferInfo = new();
    private VideoCodecsEnum? _currentCodec;
    private byte[] _input = [];
    private long _nextTimestampMicroseconds;
    private VideoCodecsEnum? _failedCodec;
    private bool _disposed;
    private long _lastOutputTime;

    public MediaVideoDecodingPolicy DecodingPolicy { get; set; } = MediaVideoDecodingPolicy.HardwarePreferred;

    public bool IsHardwareAccelerated { get; private set; }

    public bool CanOutputD3D11Texture
    {
        get => _software?.CanOutputD3D11Texture ?? false;
        set
        {
            if (_software is not null) _software.CanOutputD3D11Texture = value;
        }
    }

    internal bool CanUseSurface => _surfaceOutput is not null;

    internal string DecoderDescription { get; private set; } = "FFmpeg CPU (SIMD)";

    internal void SetVideoOutput(IMediaVideoOutput? output)
    {
        var surface = output as IAndroidVideoSurfaceOutput ??
            (output as IMediaVideoOutputFeatureProvider)?.GetVideoOutputFeature(
                typeof(IAndroidVideoSurfaceOutput)) as IAndroidVideoSurfaceOutput;
        lock (_sync)
        {
            if (ReferenceEquals(_surfaceOutput, surface)) return;
            ReleaseCodec();
            _surfaceOutput = surface;
            _failedCodec = null;
        }
    }

    public bool CanDecode(VideoFormat format) =>
        (DecodingPolicy != MediaVideoDecodingPolicy.SoftwareOnly &&
        _surfaceOutput is not null &&
        format.Codec is VideoCodecsEnum.H264 or VideoCodecsEnum.H265) ||
        (DecodingPolicy != MediaVideoDecodingPolicy.HardwareRequired &&
         _software?.CanDecode(format) == true);

    public bool TryDecode(
        ReadOnlySpan<byte> encodedPayload,
        VideoFormat format,
        WebRtcFrameBufferPool pool,
        out WebRtcMediaFrameLease? decodedFrame)
    {
        decodedFrame = null;
        if (_disposed || encodedPayload.IsEmpty) return false;
        lock (_sync)
        {
            if (DecodingPolicy == MediaVideoDecodingPolicy.SoftwareOnly ||
                _surfaceOutput is null ||
                _failedCodec == format.Codec ||
                format.Codec is not (VideoCodecsEnum.H264 or VideoCodecsEnum.H265))
            {
                return DecodeSoftware(encodedPayload, format, pool, out decodedFrame);
            }

            try
            {
                EnsureCodec(format.Codec);
                DrainOutput();
                var inputWaitStart = System.Diagnostics.Stopwatch.GetTimestamp();
                var inputIndex = _codec!.DequeueInputBuffer(CodecTimeoutMicroseconds);
                if (inputIndex < 0)
                {
                    DrainOutput();
                    inputIndex = _codec.DequeueInputBuffer(CodecTimeoutMicroseconds);
                }
                var inputWait = System.Diagnostics.Stopwatch.GetElapsedTime(inputWaitStart).TotalMilliseconds;
                if (inputWait > 15)
                {
                    global::Android.Util.Log.Warn("FrameFluxTiming", $"MediaCodec input wait {inputWait:F0} ms, index {inputIndex}");
                }
                // A skipped compressed frame breaks the reference chain. Ask the
                // player to recover at a key frame instead of silently continuing.
                if (inputIndex < 0) throw new WebRtcDecoderInputDroppedException();

                var length = NormalizeAnnexB(encodedPayload, ref _input);
                using var inputBuffer = _codec.GetInputBuffer(inputIndex) ??
                    throw new InvalidOperationException("MediaCodec returned no input buffer.");
                if (inputBuffer.Capacity() < length)
                {
                    throw new InvalidOperationException("MediaCodec input buffer is too small.");
                }
                inputBuffer.Clear();
                inputBuffer.Put(_input, 0, length);
                _codec.QueueInputBuffer(inputIndex, 0, length,
                    _nextTimestampMicroseconds, MediaCodecBufferFlags.None);
                _nextTimestampMicroseconds += 33_333;
                DrainOutput();
                return true;
            }
            catch (Exception exception) when (exception is not WebRtcDecoderInputDroppedException &&
                DecodingPolicy == MediaVideoDecodingPolicy.HardwarePreferred)
            {
                System.Diagnostics.Trace.TraceWarning(
                    "Android WebRTC MediaCodec failed; using FFmpeg: {0}", exception);
                ReleaseCodec();
                _failedCodec = format.Codec;
                return DecodeSoftware(encodedPayload, format, pool, out decodedFrame);
            }
        }
    }

    internal void DrainPendingOutput()
    {
        lock (_sync)
        {
            if (!_disposed && _codec is not null) DrainOutput();
        }
    }

    internal void Flush()
    {
        lock (_sync)
        {
            if (_codec is not null)
            {
                // A new key frame must carry parameter sets after a flush. Recreating
                // the decoder also handles codec changes and lost Surface state.
                ReleaseCodec();
            }
            _failedCodec = null;
            _software?.Flush();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseCodec();
            _bufferInfo.Dispose();
            _software?.Dispose();
        }
    }

    private bool DecodeSoftware(
        ReadOnlySpan<byte> payload,
        VideoFormat format,
        WebRtcFrameBufferPool pool,
        out WebRtcMediaFrameLease? frame)
    {
        if (DecodingPolicy == MediaVideoDecodingPolicy.HardwareRequired)
        {
            throw new NotSupportedException("Android MediaCodec is unavailable for this WebRTC video format.");
        }
        IsHardwareAccelerated = false;
        DecoderDescription = "FFmpeg CPU (SIMD)";
        if (_software is null)
        {
            frame = null;
            return false;
        }
        _software.DecodingPolicy = MediaVideoDecodingPolicy.SoftwareOnly;
        return _software.TryDecode(payload, format, pool, out frame);
    }

    private void EnsureCodec(VideoCodecsEnum formatCodec)
    {
        if (_codec is not null && _currentCodec == formatCodec) return;
        ReleaseCodec();
        var mime = formatCodec == VideoCodecsEnum.H264
            ? MediaFormat.MimetypeVideoAvc
            : MediaFormat.MimetypeVideoHevc;
        using var format = MediaFormat.CreateVideoFormat(mime, 1280, 720) ??
            throw new InvalidOperationException("MediaCodec could not create a video format.");
        var surface = _surfaceOutput!.AcquireDecoderSurface(CancellationToken.None);
        var codec = CreateDecoder(mime);
        try
        {
            codec.Configure(format, surface, null, MediaCodecConfigFlags.None);
            codec.Start();
            _codec = codec;
            _currentCodec = formatCodec;
            IsHardwareAccelerated = IsHardwareCodec(codec.CodecInfo);
            DecoderDescription = $"Android MediaCodec ({codec.Name ?? mime})";
        }
        catch
        {
            codec.Release();
            codec.Dispose();
            throw;
        }
    }

    private MediaCodec CreateDecoder(string mime)
    {
        using (var codecList = new MediaCodecList(MediaCodecListKind.AllCodecs))
        {
            foreach (var info in codecList.GetCodecInfos() ?? [])
            {
                if (info.IsEncoder || !IsHardwareCodec(info) ||
                    !(info.GetSupportedTypes()?.Any(type => string.Equals(type, mime, StringComparison.OrdinalIgnoreCase)) ?? false))
                {
                    continue;
                }

                try
                {
                    return MediaCodec.CreateByCodecName(info.Name) ??
                        throw new PlatformNotSupportedException($"Could not create {info.Name}.");
                }
                catch (Exception) when (DecodingPolicy == MediaVideoDecodingPolicy.HardwarePreferred)
                {
                    // Try the next hardware codec before falling back to Android's default.
                }
            }
        }

        if (DecodingPolicy == MediaVideoDecodingPolicy.HardwareRequired)
        {
            throw new PlatformNotSupportedException($"No hardware Android decoder supports {mime}.");
        }

        return MediaCodec.CreateDecoderByType(mime) ??
            throw new PlatformNotSupportedException($"No Android decoder supports {mime}.");
    }

    private static bool IsHardwareCodec(MediaCodecInfo info) =>
        OperatingSystem.IsAndroidVersionAtLeast(29)
            ? info.IsHardwareAccelerated
            : !info.Name.StartsWith("OMX.google.", StringComparison.OrdinalIgnoreCase) &&
              !info.Name.StartsWith("c2.android.", StringComparison.OrdinalIgnoreCase);

    private void DrainOutput()
    {
        while (true)
        {
            var outputIndex = _codec!.DequeueOutputBuffer(_bufferInfo, 0);
            if (outputIndex >= 0)
            {
                var now = System.Diagnostics.Stopwatch.GetTimestamp();
                var previous = _lastOutputTime;
                _lastOutputTime = now;
                if (previous != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(previous, now).TotalMilliseconds > 500)
                {
                    global::Android.Util.Log.Warn("FrameFluxTiming",
                        $"MediaCodec output gap {System.Diagnostics.Stopwatch.GetElapsedTime(previous, now).TotalMilliseconds:F0} ms");
                }
                _codec.ReleaseOutputBuffer(outputIndex, render: true);
                continue;
            }
            if (outputIndex == -2)
            {
                using var format = _codec.OutputFormat;
                if (format is not null)
                {
                    var width = format.GetInteger(MediaFormat.KeyWidth);
                    var height = format.GetInteger(MediaFormat.KeyHeight);
                    _surfaceOutput?.SetDecodedVideoSize(width, height);
                }
                continue;
            }
            return;
        }
    }

    private void ReleaseCodec()
    {
        var codec = _codec;
        _codec = null;
        _currentCodec = null;
        IsHardwareAccelerated = false;
        DecoderDescription = "FFmpeg CPU (SIMD)";
        if (codec is null) return;
        try { codec.Stop(); }
        catch (Java.Lang.IllegalStateException) { }
        codec.Release();
        codec.Dispose();
    }

    private static int NormalizeAnnexB(ReadOnlySpan<byte> payload, ref byte[] buffer)
    {
        if (buffer.Length < payload.Length + 4)
        {
            buffer = GC.AllocateUninitializedArray<byte>(payload.Length + 4);
        }
        if (payload.Length >= 4 && payload[0] == 0 && payload[1] == 0 &&
            (payload[2] == 1 || payload[2] == 0 && payload[3] == 1))
        {
            payload.CopyTo(buffer);
            return payload.Length;
        }

        var offset = 0;
        var outputLength = 0;
        while (offset + 4 < payload.Length)
        {
            var nalLength = (payload[offset] << 24) | (payload[offset + 1] << 16) |
                (payload[offset + 2] << 8) | payload[offset + 3];
            if (nalLength <= 0 || nalLength > payload.Length - offset - 4) break;
            buffer[outputLength++] = 0;
            buffer[outputLength++] = 0;
            buffer[outputLength++] = 0;
            buffer[outputLength++] = 1;
            payload.Slice(offset + 4, nalLength).CopyTo(buffer.AsSpan(outputLength));
            outputLength += nalLength;
            offset += 4 + nalLength;
        }
        if (outputLength > 0 && offset == payload.Length) return outputLength;

        buffer[0] = 0;
        buffer[1] = 0;
        buffer[2] = 0;
        buffer[3] = 1;
        payload.CopyTo(buffer.AsSpan(4));
        return payload.Length + 4;
    }
}
#endif
