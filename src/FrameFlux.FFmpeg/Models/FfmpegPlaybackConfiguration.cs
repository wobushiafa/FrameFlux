namespace FrameFlux.FFmpeg;

internal static class FfmpegPlaybackConfiguration
{
    public static FfmpegVideoDecodingMode ResolveVideoDecodingMode(
        MediaVideoDecodingPolicy policy)
    {
        return policy switch
        {
            MediaVideoDecodingPolicy.SoftwareOnly => FfmpegVideoDecodingMode.SoftwareOnly,
            MediaVideoDecodingPolicy.HardwareRequired => FfmpegVideoDecodingMode.HardwareRequired,
            MediaVideoDecodingPolicy.HardwarePreferred => FfmpegVideoDecodingMode.HardwarePreferred,
            _ => OperatingSystem.IsWindows() ||
                 OperatingSystem.IsLinux() ||
                 OperatingSystem.IsAndroid()
                ? FfmpegVideoDecodingMode.HardwarePreferred
                : FfmpegVideoDecodingMode.SoftwareOnly
        };
    }

    public static bool UsesHardwareDecoding(FfmpegVideoDecodingMode mode) =>
        mode is FfmpegVideoDecodingMode.HardwarePreferred or FfmpegVideoDecodingMode.HardwareRequired;

    public static bool AllowsSoftwareFallback(FfmpegVideoDecodingMode mode) =>
        mode == FfmpegVideoDecodingMode.HardwarePreferred;
}
