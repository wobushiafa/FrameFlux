namespace FrameFlux.Avalonia;

internal static class RtspFrameRendererFactory
{
    public static IRtspFrameRenderer Create(RtspRenderMode requestedMode, bool useHardwareAcceleration, out RtspRenderMode effectiveMode)
    {
        if (ShouldTryNativeSurface(requestedMode, useHardwareAcceleration))
        {
            var nativeRenderer = TryCreateNativeSurfaceRenderer();
            if (nativeRenderer != null)
            {
                effectiveMode = RtspRenderMode.NativeSurface;
                return nativeRenderer;
            }
        }

        effectiveMode = RtspRenderMode.SoftwareBitmap;
        return new SoftwareBitmapFrameRenderer();
    }

    private static bool ShouldTryNativeSurface(RtspRenderMode requestedMode, bool useHardwareAcceleration)
    {
        return requestedMode == RtspRenderMode.NativeSurface ||
               (requestedMode == RtspRenderMode.Auto && useHardwareAcceleration);
    }

    private static IRtspFrameRenderer? TryCreateNativeSurfaceRenderer()
    {
        return null;
    }
}
