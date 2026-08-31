using System.Diagnostics;

namespace FrameFlux.FFmpeg;

internal sealed class NativeDecodedFrame : IDisposable
{
    private NativeVideoFrameHandle? _handle;

    internal NativeDecodedFrame(NativeVideoFrameHandle handle, NativeFrameInfo info)
    {
        _handle = handle;
        Info = info;
    }

    internal NativeFrameInfo Info { get; }

    internal NativeVideoFrameHandle Handle =>
        _handle ?? throw new ObjectDisposedException(nameof(NativeDecodedFrame));

    internal NativeVideoFrameHandle DetachHandle()
    {
        var handle = Handle;
        _handle = null;
        return handle;
    }

    public void Dispose()
    {
        _handle?.Dispose();
        _handle = null;
    }
}

internal sealed class FfmpegDecoder : IDisposable
{
    private readonly FfmpegPlaybackOptions _options;
    private readonly NativeRtspSessionHandle _session;
    private readonly CancellationTokenRegistration _cancellationRegistration;
    private readonly NativeStreamInfo _streamInfo;
    private bool _disposed;

    internal FfmpegDecoder(
        string url,
        FfmpegPlaybackOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        _options = options ?? throw new ArgumentNullException(nameof(options));

        using var nativeUrl = new NativeUtf8String(url);
        using var nativeTransport = new NativeUtf8String(
            string.IsNullOrWhiteSpace(options.Transport) ? "tcp" : options.Transport);
        var nativeOptions = new NativeRtspOptions
        {
            Url = nativeUrl.Pointer,
            Transport = nativeTransport.Pointer,
            OpenTimeoutMilliseconds = options.OpenTimeoutMilliseconds,
            ReadTimeoutMilliseconds = options.ReadTimeoutMilliseconds,
            LowLatency = options.LowLatency ? 1 : 0,
            UseHardwareAcceleration =
                FfmpegPlaybackConfiguration.UsesHardwareDecoding(options.VideoDecodingMode) ? 1 : 0,
            FallbackToSoftware =
                FfmpegPlaybackConfiguration.AllowsSoftwareFallback(options.VideoDecodingMode) ? 1 : 0,
            PreserveHardwareFrames =
                (OperatingSystem.IsWindows() &&
                 options.FrameDeliveryMode == FfmpegFrameDeliveryMode.D3D11Texture) ||
                (OperatingSystem.IsLinux() &&
                 options.FrameDeliveryMode == FfmpegFrameDeliveryMode.DmaBuf)
                    ? 1
                    : 0,
            EnableAudio = options.EnableAudio ? 1 : 0,
            MaxFramesPerSecond = options.MaxFramesPerSecond
        };

        var result = FrameFluxFFmpegNative.OpenDecoder(
            nativeOptions,
            cancellationToken,
            out var session);
        _session = session;
        if (cancellationToken.IsCancellationRequested)
        {
            session.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
        }
        if (result < 0 || session.IsInvalid)
        {
            var message = session.IsInvalid
                ? $"Unable to open media source (native error {result})."
                : FrameFluxFFmpegNative.GetError(session);
            session.Dispose();
            throw new ApplicationException(message);
        }

        _cancellationRegistration = cancellationToken.Register(
            static state => FrameFluxFFmpegNative.Cancel((NativeRtspSessionHandle)state!),
            session);
        VideoDecoderDiagnostics = FrameFluxFFmpegNative.GetVideoDecoderDiagnostics(session);
        if (FrameFluxFFmpegNative.GetStreamInfo(session, out _streamInfo) < 0)
        {
            session.Dispose();
            throw new ApplicationException("Unable to read media stream information.");
        }
        Duration = ToTimeSpan(_streamInfo.DurationTimestamp);
    }

    public bool IsHardwareVideoDecodingActive =>
        !_disposed && FrameFluxFFmpegNative.IsHardwareActive(_session) != 0;

    public bool IsLinuxVaapiActive =>
        OperatingSystem.IsLinux() && IsHardwareVideoDecodingActive;

    public string VideoDecoderDiagnostics { get; }

    internal bool HasAudio => FrameFluxFFmpegNative.HasAudio(_session);

    internal TimeSpan Position { get; private set; }

    internal TimeSpan? Duration { get; }

    internal bool TryDequeueAudioFrame(out NativeAudioFrame? frame) =>
        FrameFluxFFmpegNative.TryDequeueAudioFrame(_session, out frame);

    public long LastReadTicks { get; private set; }

    public long LastCodecTicks { get; private set; }

    public long LastHardwareTransferTicks =>
        _disposed ? 0 : FrameFluxFFmpegNative.GetLastHardwareTransferTicks(_session);

    internal bool TryDecodeNextFrame(out NativeDecodedFrame? frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var startedAt = Stopwatch.GetTimestamp();
        var result = FrameFluxFFmpegNative.ReadFrame(_session, out var handle);
        LastReadTicks = Stopwatch.GetTimestamp() - startedAt;
        LastCodecTicks = LastReadTicks;
        if (result != NativeReadResult.Ok || handle.IsInvalid)
        {
            handle.Dispose();
            frame = null;
            if (result == NativeReadResult.Error)
            {
                throw CreateRuntimeException(FrameFluxFFmpegNative.GetError(_session));
            }

            return false;
        }

        if (FrameFluxFFmpegNative.GetFrameInfo(handle, out var info) < 0)
        {
            handle.Dispose();
            throw CreateRuntimeException(
                "The native RTSP decoder returned an invalid video frame.");
        }

        if (info.PresentationTimestamp != long.MinValue)
        {
            Position = GetRelativePosition(info, _streamInfo) ?? Position;
        }

        frame = new NativeDecodedFrame(handle, info);
        return true;
    }

    internal void Seek(TimeSpan position)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (position < TimeSpan.Zero || Duration is { } duration && position > duration)
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }

        var timestamp = GetSeekTimestamp(position, _streamInfo);
        if (FrameFluxFFmpegNative.Seek(_session, timestamp) < 0)
        {
            throw CreateRuntimeException(FrameFluxFFmpegNative.GetError(_session));
        }

        Position = position;
    }

    internal void SetPlaybackRate(double playbackRate)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        FrameFluxFFmpegNative.SetPlaybackRate(_session, playbackRate);
    }

    internal static TimeSpan? GetRelativePosition(
        NativeFrameInfo frameInfo,
        NativeStreamInfo streamInfo)
    {
        if (frameInfo.PresentationTimestamp == long.MinValue ||
            frameInfo.TimeBaseDenominator <= 0)
        {
            return null;
        }

        var startTimestamp = streamInfo.StartTimestamp == long.MinValue
            ? 0L
            : streamInfo.StartTimestamp;
        var seconds = (frameInfo.PresentationTimestamp - (double)startTimestamp) *
            frameInfo.TimeBaseNumerator / frameInfo.TimeBaseDenominator;
        return TimeSpan.FromSeconds(Math.Max(0d, seconds));
    }

    internal static long GetSeekTimestamp(TimeSpan position, NativeStreamInfo streamInfo)
    {
        var numerator = Math.Max(1, streamInfo.TimeBaseNumerator);
        var relativeTimestamp = checked((long)Math.Round(
            position.TotalSeconds * streamInfo.TimeBaseDenominator / numerator));
        var startTimestamp = streamInfo.StartTimestamp == long.MinValue
            ? 0L
            : streamInfo.StartTimestamp;
        return checked(relativeTimestamp + startTimestamp);
    }

    private TimeSpan? ToTimeSpan(long timestamp) =>
        timestamp > 0
            ? TimeSpan.FromSeconds(timestamp * (double)_streamInfo.TimeBaseNumerator / _streamInfo.TimeBaseDenominator)
            : null;

    internal void ConvertFrameToBgra(
        NativeDecodedFrame frame,
        IntPtr destination,
        int destinationWidth,
        int destinationHeight,
        int destinationStride)
    {
        var result = FrameFluxFFmpegNative.CopyFrameToBgra(
            _session,
            frame.Handle,
            destination,
            destinationWidth,
            destinationHeight,
            destinationStride,
            (int)_options.ScaleQuality,
            _options.ForceOpaqueAlpha ? 1 : 0);
        if (result < 0)
        {
            throw CreateRuntimeException(
                $"Unable to convert the decoded frame to BGRA (native error {result}).");
        }
    }

    private FfmpegDecoderRuntimeException CreateRuntimeException(string message) =>
        new(message, null, IsHardwareVideoDecodingActive);

    internal bool TryGetNativePixelFormat(
        NativeDecodedFrame frame,
        out FfmpegNativePixelFormat pixelFormat)
    {
        pixelFormat = frame.Info.PixelFormat switch
        {
            NativePixelFormat.Bgra32 => FfmpegNativePixelFormat.Bgra32,
            NativePixelFormat.Yuv420P => FfmpegNativePixelFormat.Yuv420P,
            NativePixelFormat.Nv12 => FfmpegNativePixelFormat.Nv12,
            NativePixelFormat.Nv21 => FfmpegNativePixelFormat.Nv21,
            NativePixelFormat.D3D11Texture => FfmpegNativePixelFormat.D3D11Texture,
            NativePixelFormat.DmaBuf => FfmpegNativePixelFormat.DmaBuf,
            _ => default
        };
        return frame.Info.PixelFormat is NativePixelFormat.Bgra32 or
            NativePixelFormat.Yuv420P or NativePixelFormat.Nv12 or NativePixelFormat.Nv21 or
            NativePixelFormat.D3D11Texture or NativePixelFormat.DmaBuf;
    }

    internal FfmpegMediaFrameLease CreateNativeFrameLease(
        NativeDecodedFrame frame,
        FfmpegNativePixelFormat pixelFormat)
    {
        var info = frame.Info;
        var handle = frame.DetachHandle();
        var lease = new FfmpegMediaFrameLease(IntPtr.Zero, 0, _ => handle.Dispose());
        if (pixelFormat == FfmpegNativePixelFormat.D3D11Texture)
        {
            lease.ResetD3D11(info.Width, info.Height, info.Plane0, checked((int)info.Plane1));
            return lease;
        }
        if (pixelFormat == FfmpegNativePixelFormat.DmaBuf)
        {
            lease.ResetDmaBuf(
                info.Width,
                info.Height,
                DmaBufDescriptorReader.Read(info.DmaBufDescriptor));
            return lease;
        }

        lease.ResetNativeDirect(
            info.Width,
            info.Height,
            pixelFormat,
            info.Plane0,
            info.Plane1,
            info.Plane2,
            info.Stride0,
            info.Stride1,
            info.Stride2);
        return lease;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cancellationRegistration.Dispose();
        _session.Dispose();
        GC.SuppressFinalize(this);
    }
}

public static class FFmpegHelper
{
    public static void RegisterFFmpeg(string libraryPath = "")
    {
        FrameFluxFFmpegNative.ConfigureLibraryDirectory(libraryPath);
        _ = FrameFluxFFmpegNative.GetVersion();
    }
}
