namespace FrameFlux.FFmpeg;

internal static class FFmpegInputOptionPolicy
{
    // `fflags=nobuffer` can discard packets queued during stream probing, leaving
    // inter-frame codecs without the reference frames required to begin decoding.
    private static readonly KeyValuePair<string, string>[] SafeLowLatencyOptions =
    [
        new("flags", "low_delay"),
        new("max_delay", "500000")
    ];

    internal static IReadOnlyList<KeyValuePair<string, string>> GetLowLatencyOptions(
        bool enabled,
        bool isHls = false) =>
        enabled && !isHls ? SafeLowLatencyOptions : [];

    internal static bool ShouldPrefetchPackets(bool isHls, bool isPacketReader) =>
        isHls && !isPacketReader;
}
