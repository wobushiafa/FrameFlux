using System.Runtime.InteropServices;

namespace FrameFlux.FFmpeg;

internal sealed class FFmpegHardwareApi
{
    internal FFmpegHardwareApi()
    {
        AvHardwareDeviceFindTypeByName = Load<HardwareDeviceFindTypeByNameDelegate>(
            "avutil",
            "av_hwdevice_find_type_by_name");
        AvHardwareDeviceContextCreate = Load<HardwareDeviceContextCreateDelegate>(
            "avutil",
            "av_hwdevice_ctx_create");
        AvHardwareFrameTransferData = Load<HardwareFrameTransferDataDelegate>(
            "avutil",
            "av_hwframe_transfer_data");
        AvBufferReference = Load<BufferReferenceDelegate>("avutil", "av_buffer_ref");
        AvBufferUnreference = Load<BufferUnreferenceDelegate>("avutil", "av_buffer_unref");
        AvCodecGetHardwareConfig = Load<CodecGetHardwareConfigDelegate>(
            "avcodec",
            "avcodec_get_hw_config");
    }

    internal HardwareDeviceFindTypeByNameDelegate AvHardwareDeviceFindTypeByName { get; }
    internal HardwareDeviceContextCreateDelegate AvHardwareDeviceContextCreate { get; }
    internal HardwareFrameTransferDataDelegate AvHardwareFrameTransferData { get; }
    internal BufferReferenceDelegate AvBufferReference { get; }
    internal BufferUnreferenceDelegate AvBufferUnreference { get; }
    internal CodecGetHardwareConfigDelegate AvCodecGetHardwareConfig { get; }

    private static T Load<T>(string component, string exportName) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(
            FFmpegLibraryLoader.GetExport(component, exportName));

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int HardwareDeviceFindTypeByNameDelegate(IntPtr name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int HardwareDeviceContextCreateDelegate(
        ref IntPtr deviceContext,
        int type,
        IntPtr device,
        IntPtr options,
        int flags);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int HardwareFrameTransferDataDelegate(
        IntPtr destination,
        IntPtr source,
        int flags);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate IntPtr BufferReferenceDelegate(IntPtr buffer);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void BufferUnreferenceDelegate(ref IntPtr buffer);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate IntPtr CodecGetHardwareConfigDelegate(IntPtr codec, int index);
}
