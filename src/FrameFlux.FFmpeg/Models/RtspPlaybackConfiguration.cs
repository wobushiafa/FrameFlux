namespace FrameFlux.FFmpeg;

internal static class RtspPlaybackConfiguration
{
    public static RtspVideoDecodingMode ResolveVideoDecodingMode(
        MediaVideoDecodingPolicy policy)
    {
        return policy switch
        {
            MediaVideoDecodingPolicy.SoftwareOnly => RtspVideoDecodingMode.SoftwareOnly,
            MediaVideoDecodingPolicy.HardwareRequired => RtspVideoDecodingMode.HardwareRequired,
            MediaVideoDecodingPolicy.HardwarePreferred => RtspVideoDecodingMode.HardwarePreferred,
            _ => OperatingSystem.IsWindows()
                ? RtspVideoDecodingMode.HardwarePreferred
                : RtspVideoDecodingMode.SoftwareOnly
        };
    }

    public static bool UsesHardwareDecoding(RtspVideoDecodingMode mode) =>
        mode is RtspVideoDecodingMode.HardwarePreferred or RtspVideoDecodingMode.HardwareRequired;

    public static bool AllowsSoftwareFallback(RtspVideoDecodingMode mode) =>
        mode == RtspVideoDecodingMode.HardwarePreferred;
}
