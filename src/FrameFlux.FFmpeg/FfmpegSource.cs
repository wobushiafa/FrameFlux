namespace FrameFlux.FFmpeg;

internal static class FfmpegSource
{
    internal static bool IsHls(Uri uri) =>
        uri.Scheme is "http" or "https" &&
        uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
}
