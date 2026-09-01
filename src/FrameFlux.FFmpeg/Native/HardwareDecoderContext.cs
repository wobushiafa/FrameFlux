using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FrameFlux.FFmpeg;

internal interface IHardwareDecoderContext : IDisposable
{
    string BackendName { get; }
    bool SupportsDirectFrameOutput { get; }
    long LastTransferTicks { get; }
    bool IsHardwareFrame(IntPtr frame);
    int Transfer(IntPtr source, out IntPtr destination);
    int Export(IntPtr source, out IntPtr destination);
}

internal static class HardwareDecoderContextFactory
{
    internal static string PlatformBackendName => GetBackend()?.DisplayName ??
        "hardware acceleration";

    internal static bool TryCreate(
        FFmpegApi api,
        IntPtr decoder,
        IntPtr codecContext,
        out IHardwareDecoderContext? context,
        out string error)
    {
        var backend = GetBackend();
        if (backend is null)
        {
            context = null;
            error = "no hardware decoder backend is available for this operating system";
            return false;
        }

        var created = FFmpegHardwareDecoderContext.TryCreate(
            api, decoder, codecContext, backend.Value,
            out var hardwareContext, out error);
        context = hardwareContext;
        return created;
    }

    private static HardwareDecoderBackend? GetBackend()
    {
        if (OperatingSystem.IsWindows())
            return new HardwareDecoderBackend("D3D11VA", "d3d11va", true);
        if (OperatingSystem.IsLinux())
            return new HardwareDecoderBackend("VAAPI", "vaapi", true);
        return null;
    }
}

internal sealed class FFmpegHardwareDecoderContext : IHardwareDecoderContext
{
    private const int HardwareDeviceContextMethod = 0x01;
    private readonly FFmpegApi _api;
    private readonly FFmpegHardwareApi _hardwareApi;
    private readonly HardwareDecoderBackend _backend;
    private readonly FFmpegApi.GetFormatDelegate _getFormat;
    private IntPtr _deviceContext;
    private IntPtr _transferFrame;
    private int _hardwarePixelFormat = -1;

    private FFmpegHardwareDecoderContext(
        FFmpegApi api,
        FFmpegHardwareApi hardwareApi,
        HardwareDecoderBackend backend)
    {
        _api = api;
        _hardwareApi = hardwareApi;
        _backend = backend;
        _getFormat = SelectHardwareFormat;
    }

    public string BackendName => _backend.DisplayName;
    public bool SupportsDirectFrameOutput => _backend.SupportsDirectFrameOutput;
    public long LastTransferTicks { get; private set; }

    internal static bool TryCreate(
        FFmpegApi api,
        IntPtr decoder,
        IntPtr codecContext,
        HardwareDecoderBackend backend,
        out FFmpegHardwareDecoderContext? context,
        out string error)
    {
        context = null;
        if (!FFmpegAbi.SupportsHardwareDecoderLayout(api.CodecMajorVersion))
        {
            error = $"{backend.DisplayName} ABI is validated for FFmpeg 7 and 8 x64; " +
                $"found avcodec {api.CodecMajorVersion}";
            return false;
        }

        FFmpegHardwareApi hardwareApi;
        try
        {
            hardwareApi = api.Hardware;
        }
        catch (Exception exception) when (IsHardwareApiUnavailable(exception))
        {
            error = $"the local FFmpeg build cannot initialize {backend.DisplayName}: " +
                exception.GetBaseException().Message;
            return false;
        }

        var candidate = new FFmpegHardwareDecoderContext(api, hardwareApi, backend);
        try
        {
            using var deviceTypeName = new NativeUtf8String(backend.DeviceTypeName);
            var deviceType = hardwareApi.AvHardwareDeviceFindTypeByName(deviceTypeName.Pointer);
            if (deviceType < 0)
            {
                error = $"the local FFmpeg build does not expose the {backend.DisplayName} device type";
                return false;
            }

            candidate._hardwarePixelFormat = FindHardwarePixelFormat(hardwareApi, decoder, deviceType);
            if (candidate._hardwarePixelFormat < 0)
            {
                error = $"the selected video decoder does not advertise {backend.DisplayName} support";
                return false;
            }

            var result = hardwareApi.AvHardwareDeviceContextCreate(
                ref candidate._deviceContext, deviceType,
                IntPtr.Zero, IntPtr.Zero, 0);
            if (result < 0)
            {
                error = $"av_hwdevice_ctx_create: {api.FormatError(result)} ({result})";
                return false;
            }

            candidate._transferFrame = api.AvFrameAlloc();
            if (candidate._transferFrame == IntPtr.Zero)
            {
                error = "av_frame_alloc: out of memory";
                return false;
            }

            var codecDeviceReference = hardwareApi.AvBufferReference(candidate._deviceContext);
            if (codecDeviceReference == IntPtr.Zero)
            {
                error = "av_buffer_ref: out of memory";
                return false;
            }

            FFmpegAbi.ConfigureHardwareDecoderCodecContext(
                codecContext,
                codecDeviceReference,
                Marshal.GetFunctionPointerForDelegate(candidate._getFormat),
                api.CodecMajorVersion);
            context = candidate;
            error = string.Empty;
            return true;
        }
        finally
        {
            if (context is null) candidate.Dispose();
        }
    }

    public bool IsHardwareFrame(IntPtr frame) =>
        FFmpegAbi.ReadFrameFormat(frame, _api.CodecMajorVersion) == _hardwarePixelFormat;

    public int Transfer(IntPtr source, out IntPtr destination)
    {
        destination = IntPtr.Zero;
        _api.AvFrameUnref(_transferFrame);
        var startedAt = Stopwatch.GetTimestamp();
        var result = _hardwareApi.AvHardwareFrameTransferData(_transferFrame, source, 0);
        LastTransferTicks = Stopwatch.GetTimestamp() - startedAt;
        if (result < 0) return result;

        result = _api.AvFrameCopyProperties(_transferFrame, source);
        if (result >= 0) destination = _transferFrame;
        return result;
    }

    public int Export(IntPtr source, out IntPtr destination)
    {
        destination = _api.AvFrameAlloc();
        if (destination == IntPtr.Zero)
        {
            return -12;
        }

        using var name = new NativeUtf8String("drm_prime");
        var drmPrimeFormat = _hardwareApi.AvGetPixelFormat(name.Pointer);
        if (drmPrimeFormat < 0)
        {
            _api.AvFrameFree(ref destination);
            return -22;
        }

        FFmpegAbi.WriteFrameFormat(destination, _api.UtilMajorVersion, drmPrimeFormat);
        var result = _hardwareApi.AvHardwareFrameMap(destination, source, 1);
        if (result >= 0)
        {
            result = _api.AvFrameCopyProperties(destination, source);
            if (result >= 0)
            {
                return result;
            }
        }

        _api.AvFrameFree(ref destination);
        return result;
    }

    public void Dispose()
    {
        if (_transferFrame != IntPtr.Zero) _api.AvFrameFree(ref _transferFrame);
        if (_deviceContext != IntPtr.Zero) _hardwareApi.AvBufferUnreference(ref _deviceContext);
    }

    private int SelectHardwareFormat(IntPtr codecContext, IntPtr formats)
    {
        for (var offset = 0; ; offset += sizeof(int))
        {
            var format = Marshal.ReadInt32(formats, offset);
            if (format == -1 || format == _hardwarePixelFormat) return format;
        }
    }

    private static int FindHardwarePixelFormat(
        FFmpegHardwareApi hardwareApi,
        IntPtr decoder,
        int deviceType)
    {
        for (var index = 0; ; index++)
        {
            var config = hardwareApi.AvCodecGetHardwareConfig(decoder, index);
            if (config == IntPtr.Zero) return -1;

            var descriptor = FFmpegAbi.ReadHardwareConfig(config);
            if (descriptor.DeviceType == deviceType &&
                (descriptor.Methods & HardwareDeviceContextMethod) != 0)
                return descriptor.PixelFormat;
        }
    }

    private static bool IsHardwareApiUnavailable(Exception exception) =>
        exception is DllNotFoundException or
            EntryPointNotFoundException or
            BadImageFormatException or
            TypeInitializationException ||
        exception.InnerException is not null && IsHardwareApiUnavailable(exception.InnerException);
}

internal readonly record struct HardwareDecoderBackend(
    string DisplayName,
    string DeviceTypeName,
    bool SupportsDirectFrameOutput);

internal readonly record struct HardwareDecoderConfig(
    int PixelFormat,
    int Methods,
    int DeviceType);
