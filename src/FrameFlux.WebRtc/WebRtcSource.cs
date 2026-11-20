namespace FrameFlux.WebRtc;

/// <summary>
/// Helper methods for constructing <see cref="MediaSource"/> instances suitable for WebRTC playback.
/// </summary>
public static class WebRtcSource
{
    /// <summary>
    /// Creates a <see cref="MediaSource"/> from a raw SDP string (encoded into a data URI).
    /// </summary>
    public static MediaSource FromSdp(string sdp)
    {
        RuntimeCompat.NotBlank(sdp, nameof(sdp));
        return MediaSource.FromUri(new Uri("data:application/sdp;utf-8," + Uri.EscapeDataString(sdp)));
    }

    /// <summary>
    /// Creates a <see cref="MediaSource"/> from a JSON string containing SDP and/or ICE servers.
    /// </summary>
    public static MediaSource FromJson(string json)
    {
        RuntimeCompat.NotBlank(json, nameof(json));
        return MediaSource.FromUri(new Uri("data:application/json;utf-8," + Uri.EscapeDataString(json)));
    }

    /// <summary>
    /// Creates a <see cref="MediaSource"/> from a WHEP endpoint URL.
    /// </summary>
    public static MediaSource FromWhep(string url)
    {
        RuntimeCompat.NotBlank(url, nameof(url));
        return MediaSource.Parse(url);
    }

    /// <summary>
    /// Creates a <see cref="MediaSource"/> from a WHEP endpoint URI.
    /// </summary>
    public static MediaSource FromWhep(Uri uri)
    {
        RuntimeCompat.NotNull(uri, nameof(uri));
        return MediaSource.FromUri(uri);
    }

    /// <summary>
    /// Creates a <see cref="MediaSource"/> from a local file containing SDP or JSON configuration.
    /// </summary>
    public static MediaSource FromFile(string filePath)
    {
        RuntimeCompat.NotBlank(filePath, nameof(filePath));
        return MediaSource.FromFile(filePath);
    }
}
