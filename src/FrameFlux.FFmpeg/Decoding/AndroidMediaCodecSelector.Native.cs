#if ANDROID
using Android.Media;

namespace FrameFlux.FFmpeg;

internal static class AndroidMediaCodecSelector
{
    internal static AndroidMediaCodecSelection SelectHardwareDecoder(string mimeType)
    {
        using var codecList = new MediaCodecList(MediaCodecListKind.AllCodecs);
        AndroidMediaCodecSelection? fallbackCandidate = null;

        foreach (var codecInfo in codecList.GetCodecInfos() ?? [])
        {
            if (codecInfo.IsEncoder || !SupportsType(codecInfo, mimeType))
            {
                continue;
            }

            var isHardwareAccelerated = IsHardwareAccelerated(codecInfo);
            var selection = new AndroidMediaCodecSelection(codecInfo.Name, isHardwareAccelerated);
            fallbackCandidate ??= selection;
            if (isHardwareAccelerated)
            {
                return selection;
            }
        }

        var availableDecoder = fallbackCandidate is { } candidate
            ? $" Only software decoder '{candidate.Name}' is available."
            : string.Empty;
        throw new NotSupportedException(
            $"No hardware MediaCodec decoder is available for '{mimeType}'.{availableDecoder}");
    }

    private static bool SupportsType(MediaCodecInfo codecInfo, string mimeType)
    {
        foreach (var supportedType in codecInfo.GetSupportedTypes() ?? [])
        {
            if (string.Equals(supportedType, mimeType, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsHardwareAccelerated(MediaCodecInfo codecInfo)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            return codecInfo.IsHardwareAccelerated && !codecInfo.IsSoftwareOnly;
        }

        return !MediaCodecNamePolicy.IsKnownSoftwareCodec(codecInfo.Name);
    }
}

internal readonly record struct AndroidMediaCodecSelection(string Name, bool IsHardwareAccelerated)
{
    internal string Diagnostics =>
        $"MediaCodec active: {Name}, hardware decode, zero-copy SurfaceTexture";
}
#endif
