namespace FrameFlux.FFmpeg;

internal static class FfmpegMediaAdapter
{
    internal static void Validate(MediaOpenOptions options)
    {
        options.Validate();
    }

    internal static bool Supports(MediaSource source) =>
        source.Uri.Scheme is "rtsp" or "rtsps";

    internal static RtspSource ToRtspSource(MediaSource source)
    {
        if (!Supports(source))
        {
            throw new NotSupportedException(
                $"The current FFmpeg backend does not support the '{source.Uri.Scheme}' media scheme yet.");
        }
        return RtspSource.Parse(source.Uri.ToString());
    }

    internal static RtspSessionOptions ToRtspOptions(
        MediaOpenOptions options,
        double volume,
        bool muted,
        bool supportsNativeSurface = false)
    {
        Validate(options);
        return new RtspSessionOptions
        {
            StreamSharing = options.StreamSharing switch
            {
                MediaStreamSharingMode.Shared => RtspStreamSharingMode.Shared,
                _ => RtspStreamSharingMode.Dedicated
            },
            Transport = options.Transport switch
            {
                MediaTransport.Udp => RtspTransport.Udp,
                MediaTransport.Http => RtspTransport.Http,
                MediaTransport.Https => RtspTransport.Https,
                _ => RtspTransport.Tcp
            },
            HardwareAcceleration = options.HardwareAcceleration switch
            {
                MediaHardwareAcceleration.Disabled => RtspHardwareAcceleration.Disabled,
                MediaHardwareAcceleration.Enabled => RtspHardwareAcceleration.Enabled,
                _ => RtspHardwareAcceleration.Auto
            },
            RenderPreference = options.RenderPreference switch
            {
                MediaRenderPreference.Software => RtspRenderPreference.Software,
                MediaRenderPreference.NativeSurface when supportsNativeSurface =>
                    RtspRenderPreference.NativeSurface,
                // IMediaPlayer exposes CPU frames and cannot consume native GPU surfaces.
                MediaRenderPreference.NativeSurface => RtspRenderPreference.Software,
                _ => RtspRenderPreference.Auto
            },
            OpenTimeout = options.OpenTimeout,
            EndpointProbeTimeout = options.EndpointProbeTimeout,
            ReadTimeout = options.ReadTimeout,
            ReconnectDelay = options.ReconnectDelay,
            MaxConcurrentOpenStreams = options.MaxConcurrentOpenStreams,
            MaxFramesPerSecond = options.MaxFramesPerSecond,
            MaxVideoWidth = options.MaxVideoWidth,
            MaxVideoHeight = options.MaxVideoHeight,
            LowLatency = options.LowLatency,
            FallbackToSoftwareDecoding = options.FallbackToSoftwareDecoding,
            CaptureSnapshots = options.CaptureSnapshots,
            EnableAudio = options.EnableAudio,
            Volume = volume,
            IsMuted = muted
        };
    }
}
