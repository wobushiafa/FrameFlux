namespace FrameFlux.FFmpeg;

internal static class FfmpegSource
{
    internal static bool IsHls(Uri uri) =>
        uri.Scheme is "http" or "https" &&
        uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);

    internal static bool IsWebRtcSignalingEndpoint(Uri uri)
    {
        var path = uri.AbsolutePath;
        return path.EndsWith("/whep", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith("/whip", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith("/api/ws", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith("/stream.html", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith("/links.html", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsHttpMedia(Uri uri) =>
        uri.Scheme is "http" or "https" &&
        !IsHls(uri) &&
        !IsWebRtcSignalingEndpoint(uri);

    internal static bool IsSeekable(Uri uri) =>
        uri.IsFile || IsHttpMedia(uri);

    internal static bool IsSupported(Uri uri) =>
        uri.IsFile ||
        uri.Scheme is "rtsp" or "rtsps" ||
        IsHls(uri) ||
        IsHttpMedia(uri);
}
