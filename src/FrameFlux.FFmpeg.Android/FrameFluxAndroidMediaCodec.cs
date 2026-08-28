namespace FrameFlux.FFmpeg.Android;

/// <summary>
/// Registers the Android MediaCodec decoder with the FrameFlux FFmpeg engine.
/// </summary>
public static class FrameFluxAndroidMediaCodec
{
    private static readonly AndroidMediaCodecDecoderFactory Factory = new();

    /// <summary>
    /// Registers the process-wide Android decoder backend. Repeated calls are safe.
    /// </summary>
    public static void Register() =>
        PlatformRtspDecoderRegistry.RegisterAndroidFactory(Factory);
}
