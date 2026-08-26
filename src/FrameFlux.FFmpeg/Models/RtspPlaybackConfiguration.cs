namespace FrameFlux.FFmpeg;

internal static class RtspPlaybackConfiguration
{
    public static RtspHardwareAccelerationMode ResolveHardwareAccelerationMode(
        RtspHardwareAccelerationMode configuredMode,
        bool useHardwareAcceleration)
    {
        return !useHardwareAcceleration && configuredMode == RtspHardwareAccelerationMode.Auto
            ? RtspHardwareAccelerationMode.Disabled
            : configuredMode;
    }

    public static bool ResolveUseHardwareAcceleration(
        RtspHardwareAccelerationMode configuredMode,
        bool useHardwareAcceleration,
        RtspRenderMode effectiveRenderMode)
    {
        return configuredMode switch
        {
            RtspHardwareAccelerationMode.Disabled => false,
            RtspHardwareAccelerationMode.Enabled => true,
            _ => ResolveAutoHardwareAcceleration(useHardwareAcceleration, effectiveRenderMode)
        };
    }

    public static bool IsInefficientWindowsCombination(bool useHardwareAcceleration, RtspRenderMode effectiveRenderMode)
    {
        return OperatingSystem.IsWindows() &&
               useHardwareAcceleration &&
               effectiveRenderMode == RtspRenderMode.NativeSurface;
    }

    private static bool ResolveAutoHardwareAcceleration(bool useHardwareAcceleration, RtspRenderMode effectiveRenderMode)
    {
        if (!useHardwareAcceleration || effectiveRenderMode == RtspRenderMode.SoftwareBitmap)
        {
            return false;
        }

        if (IsInefficientWindowsCombination(true, effectiveRenderMode))
        {
            return false;
        }

        return true;
    }
}
