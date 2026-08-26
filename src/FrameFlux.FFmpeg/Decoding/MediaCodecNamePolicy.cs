namespace FrameFlux.FFmpeg;

internal static class MediaCodecNamePolicy
{
    private static readonly string[] SoftwarePrefixes =
    [
        "omx.google.",
        "omx.ffmpeg.",
        "omx.pv.",
        "c2.android.",
        "c2.google."
    ];

    internal static bool IsKnownSoftwareCodec(string? codecName)
    {
        if (string.IsNullOrWhiteSpace(codecName))
        {
            return true;
        }

        foreach (var prefix in SoftwarePrefixes)
        {
            if (codecName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return codecName.Contains(".sw.", StringComparison.OrdinalIgnoreCase) ||
               codecName.Contains("software", StringComparison.OrdinalIgnoreCase);
    }
}
