using System.Runtime.InteropServices;

namespace FrameFlux.FFmpeg;

internal static class FrameFluxFFmpegNative
{
    internal static void ConfigureLibraryDirectory(string? libraryDirectory) =>
        FFmpegLibraryLoader.Configure(libraryDirectory);

    internal static uint GetVersion() => FFmpegApi.Instance.AvCodecVersion();

    internal static int OpenDecoder(
        in NativeFfmpegOptions options,
        CancellationToken cancellationToken,
        out NativeFfmpegSessionHandle session) =>
        Open(options, packetReader: false, cancellationToken, out session);

    internal static int OpenPacketReader(
        in NativeFfmpegOptions options,
        CancellationToken cancellationToken,
        out NativeFfmpegSessionHandle session) =>
        Open(options, packetReader: true, cancellationToken, out session);

    private static int Open(
        in NativeFfmpegOptions options,
        bool packetReader,
        CancellationToken cancellationToken,
        out NativeFfmpegSessionHandle session)
    {
        var state = new DirectFfmpegSession(FFmpegApi.Instance, packetReader);
        try
        {
            using var cancellationRegistration = cancellationToken.Register(
                static target => ((DirectFfmpegSession)target!).Cancel(),
                state);
            var result = state.Open(options);
            session = new NativeFfmpegSessionHandle(AllocateHandle(state));
            return result;
        }
        catch
        {
            state.Dispose();
            throw;
        }
    }

    internal static void Cancel(NativeFfmpegSessionHandle session)
    {
        if (!session.IsInvalid)
        {
            GetTarget<DirectFfmpegSession>(session.DangerousGetHandle()).Cancel();
        }
    }

    internal static void Close(IntPtr sessionHandle)
    {
        ReleaseHandle<DirectFfmpegSession>(sessionHandle, static session => session.Dispose());
    }

    internal static int GetStreamInfo(
        NativeFfmpegSessionHandle session,
        out NativeStreamInfo info)
    {
        if (session.IsInvalid)
        {
            info = default;
            return -1;
        }

        info = GetTarget<DirectFfmpegSession>(session.DangerousGetHandle()).GetStreamInfo();
        return 0;
    }

    internal static int Seek(NativeFfmpegSessionHandle session, long timestamp)
    {
        if (session.IsInvalid)
        {
            return -1;
        }

        return GetTarget<DirectFfmpegSession>(session.DangerousGetHandle())
            .Seek(timestamp);
    }

    internal static void SetPlaybackRate(
        NativeFfmpegSessionHandle session,
        double playbackRate)
    {
        if (!session.IsInvalid)
        {
            GetTarget<DirectFfmpegSession>(session.DangerousGetHandle())
                .SetPlaybackRate(playbackRate);
        }
    }

    internal static NativeReadResult ReadFrame(
        NativeFfmpegSessionHandle session,
        out NativeVideoFrameHandle frame)
    {
        var result = GetTarget<DirectFfmpegSession>(session.DangerousGetHandle())
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
        NativeFfmpegSessionHandle session,
        NativeVideoFrameHandle frame,
        IntPtr destination,
        int destinationWidth,
        int destinationHeight,
        int destinationStride,
        int scaleQuality,
        int forceOpaqueAlpha)
    {
        if (session.IsInvalid ||
            frame.IsInvalid ||
            destination == IntPtr.Zero ||
            destinationWidth <= 0 || destinationHeight <= 0 ||
            destinationStride < destinationWidth * 4)
        {
            return -1;
        }

        return GetTarget<DirectFfmpegSession>(session.DangerousGetHandle())
            .CopyFrameToBgra(
                GetTarget<DirectVideoFrame>(frame.DangerousGetHandle()),
                destination,
                destinationWidth,
                destinationHeight,
                destinationStride,
                scaleQuality,
                forceOpaqueAlpha != 0);
    }

    internal static void ReleaseFrame(IntPtr frameHandle)
    {
        ReleaseHandle<DirectVideoFrame>(frameHandle, static frame => frame.Dispose());
    }

    internal static NativeReadResult ReadPacket(
        NativeFfmpegSessionHandle session,
        out NativeVideoPacketHandle packet)
    {
        var result = GetTarget<DirectFfmpegSession>(session.DangerousGetHandle())
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

    internal static int IsHardwareActive(NativeFfmpegSessionHandle session) =>
        !session.IsInvalid && GetTarget<DirectFfmpegSession>(session.DangerousGetHandle()).IsHardwareVideoDecodingActive
            ? 1
            : 0;

    internal static long GetLastHardwareTransferTicks(NativeFfmpegSessionHandle session) =>
        session.IsInvalid
            ? 0
            : GetTarget<DirectFfmpegSession>(session.DangerousGetHandle()).LastHardwareTransferTicks;

    internal static bool HasAudio(NativeFfmpegSessionHandle session) =>
        !session.IsInvalid && GetTarget<DirectFfmpegSession>(session.DangerousGetHandle()).HasAudio;

    internal static bool TryDequeueAudioFrame(
        NativeFfmpegSessionHandle session,
        out NativeAudioFrame? frame)
    {
        if (session.IsInvalid)
        {
            frame = null;
            return false;
        }

        return GetTarget<DirectFfmpegSession>(session.DangerousGetHandle())
            .TryDequeueAudioFrame(out frame);
    }

    internal static string GetVideoDecoderDiagnostics(NativeFfmpegSessionHandle session) =>
        session.IsInvalid
            ? "Unavailable"
            : GetTarget<DirectFfmpegSession>(session.DangerousGetHandle()).VideoDecoderDiagnostics;

    internal static string GetError(NativeFfmpegSessionHandle session) =>
        session.IsInvalid
            ? "FFmpeg session is unavailable."
            : GetTarget<DirectFfmpegSession>(session.DangerousGetHandle()).Error;

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
