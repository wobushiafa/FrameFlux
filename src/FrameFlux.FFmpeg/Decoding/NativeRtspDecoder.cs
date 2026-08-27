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

internal sealed class RtspDecoder : IDisposable
{
    private readonly RtspStreamOptions _options;
    private readonly NativeRtspSessionHandle _session;
    private readonly CancellationTokenRegistration _cancellationRegistration;
    private bool _disposed;

    public RtspDecoder(string url, bool useHardwareAcceleration = true)
        : this(url, new RtspStreamOptions
        {
            UseHardwareAcceleration = useHardwareAcceleration,
            HardwareAccelerationMode = useHardwareAcceleration
                ? RtspHardwareAccelerationMode.Enabled
                : RtspHardwareAccelerationMode.Disabled
        })
    {
    }

    public RtspDecoder(
        string url,
        RtspStreamOptions options,
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
            UseHardwareAcceleration = options.UseHardwareAcceleration ? 1 : 0,
            FallbackToSoftware = options.FallbackToSoftwareDecoding ? 1 : 0,
            PreserveHardwareFrames =
                OperatingSystem.IsWindows() &&
                options.RenderMode == RtspRenderMode.NativeSurface
                    ? 1
                    : 0,
            EnableAudio = options.EnableAudio ? 1 : 0,
            MaxFramesPerSecond = options.MaxFramesPerSecond
        };

        var result = FrameFluxFFmpegNative.OpenDecoder(nativeOptions, out var session);
        _session = session;
        if (result < 0 || session.IsInvalid)
        {
            var message = session.IsInvalid
                ? $"Unable to open RTSP source (native error {result})."
                : FrameFluxFFmpegNative.GetError(session);
            session.Dispose();
            throw new ApplicationException(message);
        }

        _cancellationRegistration = cancellationToken.Register(
            static state => FrameFluxFFmpegNative.Cancel((NativeRtspSessionHandle)state!),
            session);
        HardwareDiagnostics = FrameFluxFFmpegNative.GetHardwareDiagnostics(session);
    }

    public bool IsHardwareAccelerationActive =>
        !_disposed && FrameFluxFFmpegNative.IsHardwareActive(_session) != 0;

    public bool IsLinuxVaapiActive => false;

    public string HardwareDiagnostics { get; }

    internal bool HasAudio => FrameFluxFFmpegNative.HasAudio(_session);

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
                throw new ApplicationException(FrameFluxFFmpegNative.GetError(_session));
            }

            return false;
        }

        if (FrameFluxFFmpegNative.GetFrameInfo(handle, out var info) < 0)
        {
            handle.Dispose();
            throw new ApplicationException("The native RTSP decoder returned an invalid video frame.");
        }

        frame = new NativeDecodedFrame(handle, info);
        return true;
    }

    internal void ConvertFrameToBgra(
        NativeDecodedFrame frame,
        IntPtr destination,
        int destinationWidth,
        int destinationHeight,
        int destinationStride)
    {
        var result = FrameFluxFFmpegNative.CopyFrameToBgra(
            frame.Handle,
            destination,
            destinationWidth,
            destinationHeight,
            destinationStride,
            (int)_options.ScaleQuality,
            _options.ForceOpaqueAlpha ? 1 : 0);
        if (result < 0)
        {
            throw new ApplicationException($"Unable to convert the decoded frame to BGRA (native error {result}).");
        }
    }

    internal bool TryGetNativePixelFormat(
        NativeDecodedFrame frame,
        out RtspNativePixelFormat pixelFormat)
    {
        pixelFormat = frame.Info.PixelFormat switch
        {
            NativePixelFormat.Bgra32 => RtspNativePixelFormat.Bgra32,
            NativePixelFormat.Yuv420P => RtspNativePixelFormat.Yuv420P,
            NativePixelFormat.Nv12 => RtspNativePixelFormat.Nv12,
            NativePixelFormat.Nv21 => RtspNativePixelFormat.Nv21,
            NativePixelFormat.D3D11Texture => RtspNativePixelFormat.D3D11Texture,
            _ => default
        };
        return frame.Info.PixelFormat is NativePixelFormat.Bgra32 or
            NativePixelFormat.Yuv420P or NativePixelFormat.Nv12 or NativePixelFormat.Nv21 or
            NativePixelFormat.D3D11Texture;
    }

    internal FfmpegMediaFrameLease CreateNativeFrameLease(
        NativeDecodedFrame frame,
        RtspNativePixelFormat pixelFormat)
    {
        var info = frame.Info;
        var handle = frame.DetachHandle();
        var lease = new FfmpegMediaFrameLease(IntPtr.Zero, 0, _ => handle.Dispose());
        if (pixelFormat == RtspNativePixelFormat.D3D11Texture)
        {
            lease.ResetD3D11(info.Width, info.Height, info.Plane0, checked((int)info.Plane1));
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
