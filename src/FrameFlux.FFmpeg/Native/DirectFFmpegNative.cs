using System.Runtime.InteropServices;

namespace FrameFlux.FFmpeg;

internal static class FrameFluxFFmpegNative
{
    internal static void ConfigureLibraryDirectory(string? libraryDirectory) =>
        FFmpegLibraryLoader.Configure(libraryDirectory);

    internal static uint GetVersion() => FFmpegApi.Instance.AvCodecVersion();

    internal static int OpenDecoder(
        in NativeRtspOptions options,
        out NativeRtspSessionHandle session) =>
        Open(options, packetReader: false, out session);

    internal static int OpenPacketReader(
        in NativeRtspOptions options,
        out NativeRtspSessionHandle session) =>
        Open(options, packetReader: true, out session);

    private static int Open(
        in NativeRtspOptions options,
        bool packetReader,
        out NativeRtspSessionHandle session)
    {
        var state = new DirectRtspSession(FFmpegApi.Instance, packetReader);
        try
        {
            var result = state.Open(options);
            session = new NativeRtspSessionHandle(AllocateHandle(state));
            return result;
        }
        catch
        {
            state.Dispose();
            throw;
        }
    }

    internal static void Cancel(NativeRtspSessionHandle session)
    {
        if (!session.IsInvalid)
        {
            GetTarget<DirectRtspSession>(session.DangerousGetHandle()).Cancel();
        }
    }

    internal static void Close(IntPtr sessionHandle)
    {
        ReleaseHandle<DirectRtspSession>(sessionHandle, static session => session.Dispose());
    }

    internal static int GetStreamInfo(
        NativeRtspSessionHandle session,
        out NativeStreamInfo info)
    {
        if (session.IsInvalid)
        {
            info = default;
            return -1;
        }

        info = GetTarget<DirectRtspSession>(session.DangerousGetHandle()).GetStreamInfo();
        return 0;
    }

    internal static NativeReadResult ReadFrame(
        NativeRtspSessionHandle session,
        out NativeVideoFrameHandle frame)
    {
        var result = GetTarget<DirectRtspSession>(session.DangerousGetHandle())
            .ReadFrame(out var nativeFrame);
        frame = new NativeVideoFrameHandle(
            nativeFrame is null ? IntPtr.Zero : AllocateHandle(nativeFrame));
        return result;
    }

    internal static int GetFrameInfo(
        NativeVideoFrameHandle frame,
        out NativeFrameInfo info)
    {
        if (frame.IsInvalid)
        {
            info = default;
            return -1;
        }

        info = GetTarget<DirectVideoFrame>(frame.DangerousGetHandle()).GetInfo();
        return info.Width > 0 && info.Height > 0 ? 0 : -1;
    }

    internal static unsafe int CopyFrameToBgra(
        NativeVideoFrameHandle frame,
        IntPtr destination,
        int destinationWidth,
        int destinationHeight,
        int destinationStride,
        int scaleQuality,
        int forceOpaqueAlpha)
    {
        if (frame.IsInvalid || destination == IntPtr.Zero ||
            destinationWidth <= 0 || destinationHeight <= 0 ||
            destinationStride < destinationWidth * 4)
        {
            return -1;
        }

        var nativeFrame = GetTarget<DirectVideoFrame>(frame.DangerousGetHandle());
        var layout = FFmpegAbi.ReadFrame(nativeFrame.Pointer, FFmpegApi.Instance.UtilMajorVersion);
        var flags = scaleQuality == 0 ? 1 : scaleQuality == 2 ? 4 : 2;
        nativeFrame.ScaleContext = FFmpegApi.Instance.SwsGetCachedContext(
            nativeFrame.ScaleContext,
            layout.Width,
            layout.Height,
            layout.Format,
            destinationWidth,
            destinationHeight,
            FFmpegAbi.PixelFormatBgra,
            flags,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);
        if (nativeFrame.ScaleContext == IntPtr.Zero)
        {
            return -1;
        }

        IntPtr* sourceData = stackalloc IntPtr[4];
        int* sourceStride = stackalloc int[4];
        IntPtr* destinationData = stackalloc IntPtr[4];
        int* destinationStrides = stackalloc int[4];
        for (var index = 0; index < 4; index++)
        {
            sourceData[index] = layout.Data[index];
            sourceStride[index] = layout.LineSize[index];
            destinationData[index] = IntPtr.Zero;
            destinationStrides[index] = 0;
        }

        destinationData[0] = destination;
        destinationStrides[0] = destinationStride;
        var result = FFmpegApi.Instance.SwsScale(
            nativeFrame.ScaleContext,
            (IntPtr)sourceData,
            (IntPtr)sourceStride,
            0,
            layout.Height,
            (IntPtr)destinationData,
            (IntPtr)destinationStrides);
        if (result < 0)
        {
            return result;
        }

        if (forceOpaqueAlpha != 0)
        {
            var pixels = (byte*)destination;
            for (var y = 0; y < destinationHeight; y++)
            {
                var row = pixels + y * destinationStride;
                for (var x = 0; x < destinationWidth; x++)
                {
                    row[x * 4 + 3] = 255;
                }
            }
        }

        return 0;
    }

    internal static void ReleaseFrame(IntPtr frameHandle)
    {
        ReleaseHandle<DirectVideoFrame>(frameHandle, static frame => frame.Dispose());
    }

    internal static NativeReadResult ReadPacket(
        NativeRtspSessionHandle session,
        out NativeVideoPacketHandle packet)
    {
        var result = GetTarget<DirectRtspSession>(session.DangerousGetHandle())
            .ReadPacket(out var nativePacket);
        packet = new NativeVideoPacketHandle(
            nativePacket is null ? IntPtr.Zero : AllocateHandle(nativePacket));
        return result;
    }

    internal static int GetPacketInfo(
        NativeVideoPacketHandle packet,
        out NativePacketInfo info)
    {
        if (packet.IsInvalid)
        {
            info = default;
            return -1;
        }

        info = FFmpegAbi.ReadPacket(GetTarget<DirectVideoPacket>(packet.DangerousGetHandle()).Pointer);
        return info.Data != IntPtr.Zero && info.Size > 0 ? 0 : -1;
    }

    internal static void ReleasePacket(IntPtr packetHandle)
    {
        ReleaseHandle<DirectVideoPacket>(packetHandle, static packet => packet.Dispose());
    }

    internal static int IsHardwareActive(NativeRtspSessionHandle session) =>
        !session.IsInvalid && GetTarget<DirectRtspSession>(session.DangerousGetHandle()).IsHardwareVideoDecodingActive
            ? 1
            : 0;

    internal static long GetLastHardwareTransferTicks(NativeRtspSessionHandle session) =>
        session.IsInvalid
            ? 0
            : GetTarget<DirectRtspSession>(session.DangerousGetHandle()).LastHardwareTransferTicks;

    internal static bool HasAudio(NativeRtspSessionHandle session) =>
        !session.IsInvalid && GetTarget<DirectRtspSession>(session.DangerousGetHandle()).HasAudio;

    internal static bool TryDequeueAudioFrame(
        NativeRtspSessionHandle session,
        out NativeAudioFrame? frame)
    {
        if (session.IsInvalid)
        {
            frame = null;
            return false;
        }

        return GetTarget<DirectRtspSession>(session.DangerousGetHandle())
            .TryDequeueAudioFrame(out frame);
    }

    internal static string GetVideoDecoderDiagnostics(NativeRtspSessionHandle session) =>
        session.IsInvalid
            ? "Unavailable"
            : GetTarget<DirectRtspSession>(session.DangerousGetHandle()).VideoDecoderDiagnostics;

    internal static string GetError(NativeRtspSessionHandle session) =>
        session.IsInvalid
            ? "FFmpeg session is unavailable."
            : GetTarget<DirectRtspSession>(session.DangerousGetHandle()).Error;

    private static IntPtr AllocateHandle(object target) =>
        GCHandle.ToIntPtr(GCHandle.Alloc(target, GCHandleType.Normal));

    private static T GetTarget<T>(IntPtr handle) where T : class =>
        (T)(GCHandle.FromIntPtr(handle).Target ??
            throw new ObjectDisposedException(typeof(T).Name));

    private static void ReleaseHandle<T>(IntPtr handle, Action<T> release) where T : class
    {
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            return;
        }

        var gcHandle = GCHandle.FromIntPtr(handle);
        try
        {
            if (gcHandle.Target is T target)
            {
                release(target);
            }
        }
        finally
        {
            gcHandle.Free();
        }
    }
}
