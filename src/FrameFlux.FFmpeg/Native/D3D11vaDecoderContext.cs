using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FrameFlux.FFmpeg;

internal sealed class D3D11vaDecoderContext : IDisposable
{
    private const int HardwareDeviceContextMethod = 0x01;
    private readonly FFmpegApi _api;
    private readonly FFmpegApi.GetFormatDelegate _getFormat;
    private IntPtr _deviceContext;
    private IntPtr _transferFrame;
    private int _hardwarePixelFormat = -1;

    private D3D11vaDecoderContext(FFmpegApi api)
    {
        _api = api;
        _getFormat = SelectHardwareFormat;
    }

    internal long LastTransferTicks { get; private set; }

    internal static bool TryCreate(FFmpegApi api, IntPtr decoder, IntPtr codecContext,
        out D3D11vaDecoderContext? context, out string error)
    {
        context = null;
        if (!OperatingSystem.IsWindows())
        {
            error = "D3D11VA is only available on Windows";
            return false;
        }
        if (api.CodecMajorVersion != 61 || IntPtr.Size != 8)
        {
            error = $"D3D11VA ABI is currently validated for FFmpeg 7 x64; found avcodec {api.CodecMajorVersion}";
            return false;
        }

        var candidate = new D3D11vaDecoderContext(api);
        try
        {
            using var name = new NativeUtf8String("d3d11va");
            var deviceType = api.AvHardwareDeviceFindTypeByName(name.Pointer);
            if (deviceType < 0)
            {
                error = "the local FFmpeg build does not expose the D3D11VA device type";
                return false;
            }

            candidate._hardwarePixelFormat = FindHardwarePixelFormat(api, decoder, deviceType);
            if (candidate._hardwarePixelFormat < 0)
            {
                error = "the selected video decoder does not advertise D3D11VA support";
                return false;
            }

            var result = api.AvHardwareDeviceContextCreate(ref candidate._deviceContext,
                deviceType, IntPtr.Zero, IntPtr.Zero, 0);
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

            var codecDeviceReference = api.AvBufferReference(candidate._deviceContext);
            if (codecDeviceReference == IntPtr.Zero)
            {
                error = "av_buffer_ref: out of memory";
                return false;
            }

            FFmpegAbi.ConfigureD3D11vaCodecContext(codecContext, codecDeviceReference,
                Marshal.GetFunctionPointerForDelegate(candidate._getFormat), api.CodecMajorVersion);
            context = candidate;
            error = string.Empty;
            return true;
        }
        finally
        {
            if (context is null) candidate.Dispose();
        }
    }

    internal bool IsHardwareFrame(IntPtr frame) =>
        FFmpegAbi.ReadFrameFormat(frame) == _hardwarePixelFormat;

    internal int Transfer(IntPtr source, out IntPtr destination)
    {
        destination = IntPtr.Zero;
        _api.AvFrameUnref(_transferFrame);
        var startedAt = Stopwatch.GetTimestamp();
        var result = _api.AvHardwareFrameTransferData(_transferFrame, source, 0);
        LastTransferTicks = Stopwatch.GetTimestamp() - startedAt;
        if (result < 0) return result;

        result = _api.AvFrameCopyProperties(_transferFrame, source);
        if (result >= 0) destination = _transferFrame;
        return result;
    }

    public void Dispose()
    {
        if (_transferFrame != IntPtr.Zero) _api.AvFrameFree(ref _transferFrame);
        if (_deviceContext != IntPtr.Zero) _api.AvBufferUnreference(ref _deviceContext);
    }

    private int SelectHardwareFormat(IntPtr codecContext, IntPtr formats)
    {
        for (var offset = 0; ; offset += sizeof(int))
        {
            var format = Marshal.ReadInt32(formats, offset);
            if (format == -1) return -1;
            if (format == _hardwarePixelFormat) return format;
        }
    }

    private static int FindHardwarePixelFormat(FFmpegApi api, IntPtr decoder, int deviceType)
    {
        for (var index = 0; ; index++)
        {
            var config = api.AvCodecGetHardwareConfig(decoder, index);
            if (config == IntPtr.Zero) return -1;
            var pixelFormat = Marshal.ReadInt32(config);
            var methods = Marshal.ReadInt32(config, sizeof(int));
            var configuredDeviceType = Marshal.ReadInt32(config, sizeof(int) * 2);
            if (configuredDeviceType == deviceType && (methods & HardwareDeviceContextMethod) != 0)
                return pixelFormat;
        }
    }
}
