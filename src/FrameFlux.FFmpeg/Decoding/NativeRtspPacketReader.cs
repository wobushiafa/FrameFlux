using System.Runtime.InteropServices;

namespace FrameFlux.FFmpeg;

internal sealed class NativeEncodedPacket : IDisposable
{
    private NativeVideoPacketHandle? _handle;

    internal NativeEncodedPacket(NativeVideoPacketHandle handle, NativePacketInfo info)
    {
        _handle = handle;
        Info = info;
    }

    internal NativePacketInfo Info { get; }

    internal void CopyTo(byte[] destination, int length) =>
        Marshal.Copy(Info.Data, destination, 0, Math.Min(length, Info.Size));

    public void Dispose()
    {
        _handle?.Dispose();
        _handle = null;
    }
}

internal sealed class NativeRtspPacketReader : IDisposable
{
    private readonly NativeRtspSessionHandle _session;
    private readonly CancellationTokenRegistration _cancellationRegistration;
    private bool _disposed;

    internal NativeRtspPacketReader(
        string url,
        RtspStreamOptions options,
        CancellationToken cancellationToken)
    {
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
            UseHardwareAcceleration = 0,
            FallbackToSoftware = 1,
            EnableAudio = options.EnableAudio ? 1 : 0,
            MaxFramesPerSecond = options.MaxFramesPerSecond
        };

        var result = FrameFluxFFmpegNative.OpenPacketReader(
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
                ? $"Unable to open RTSP packet source (native error {result})."
                : FrameFluxFFmpegNative.GetError(session);
            session.Dispose();
            throw new ApplicationException(message);
        }

        if (FrameFluxFFmpegNative.GetStreamInfo(session, out var streamInfo) < 0)
        {
            session.Dispose();
            throw new ApplicationException("The native RTSP packet reader returned invalid stream information.");
        }

        Width = streamInfo.Width;
        Height = streamInfo.Height;
        Codec = streamInfo.Codec;
        TimeBaseNumerator = streamInfo.TimeBaseNumerator;
        TimeBaseDenominator = streamInfo.TimeBaseDenominator;
        CodecExtraData = streamInfo.CodecExtraDataSize > 0
            ? new byte[streamInfo.CodecExtraDataSize]
            : [];
        if (CodecExtraData.Length > 0)
        {
            Marshal.Copy(streamInfo.CodecExtraData, CodecExtraData, 0, CodecExtraData.Length);
        }

        _cancellationRegistration = cancellationToken.Register(
            static state => FrameFluxFFmpegNative.Cancel((NativeRtspSessionHandle)state!),
            session);
    }

    internal int Width { get; }

    internal int Height { get; }

    internal NativeVideoCodec Codec { get; }

    internal byte[] CodecExtraData { get; }

    internal int TimeBaseNumerator { get; }

    internal int TimeBaseDenominator { get; }

    internal bool HasAudio => FrameFluxFFmpegNative.HasAudio(_session);

    internal bool TryDequeueAudioFrame(out NativeAudioFrame? frame) =>
        FrameFluxFFmpegNative.TryDequeueAudioFrame(_session, out frame);

    internal bool TryReadPacket(out NativeEncodedPacket? packet)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var result = FrameFluxFFmpegNative.ReadPacket(_session, out var handle);
        if (result != NativeReadResult.Ok || handle.IsInvalid)
        {
            handle.Dispose();
            packet = null;
            if (result == NativeReadResult.Error)
            {
                throw new ApplicationException(FrameFluxFFmpegNative.GetError(_session));
            }

            return false;
        }

        if (FrameFluxFFmpegNative.GetPacketInfo(handle, out var info) < 0)
        {
            handle.Dispose();
            throw new ApplicationException("The native RTSP packet reader returned an invalid packet.");
        }

        packet = new NativeEncodedPacket(handle, info);
        return true;
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
    }
}
