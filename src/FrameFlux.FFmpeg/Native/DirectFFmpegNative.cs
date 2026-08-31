using System.Runtime.InteropServices;

namespace FrameFlux.FFmpeg;

internal static class FrameFluxFFmpegNative
{
    internal static void ConfigureLibraryDirectory(string? libraryDirectory) =>
        FFmpegLibraryLoader.Configure(libraryDirectory);

    internal static uint GetVersion() => FFmpegApi.Instance.AvCodecVersion();

    internal static int OpenDecoder(
        in NativeRtspOptions options,
        CancellationToken cancellationToken,
        out NativeRtspSessionHandle session) =>
        Open(options, packetReader: false, cancellationToken, out session);

    internal static int OpenPacketReader(
        in NativeRtspOptions options,
        CancellationToken cancellationToken,
        out NativeRtspSessionHandle session) =>
        Open(options, packetReader: true, cancellationToken, out session);

    private static int Open(
        in NativeRtspOptions options,
        bool packetReader,
        CancellationToken cancellationToken,
        out NativeRtspSessionHandle session)
    {
        var state = new DirectRtspSession(FFmpegApi.Instance, packetReader);
        try
        {
            using var cancellationRegistration = cancellationToken.Register(
                static target => ((DirectRtspSession)target!).Cancel(),
                state);
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

    internal static int Seek(NativeRtspSessionHandle session, long timestamp)
    {
        if (session.IsInvalid)
        {
            return -1;
        }

        return GetTarget<DirectRtspSession>(session.DangerousGetHandle())
            .Seek(timestamp);
    }

    internal static void SetPlaybackRate(
        NativeRtspSessionHandle session,
        double playbackRate)
    {
        if (!session.IsInvalid)
        {
            GetTarget<DirectRtspSession>(session.DangerousGetHandle())
                .SetPlaybackRate(playbackRate);
        }
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
        NativeRtspSessionHandle session,
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

        return GetTarget<DirectRtspSession>(session.DangerousGetHandle())
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
