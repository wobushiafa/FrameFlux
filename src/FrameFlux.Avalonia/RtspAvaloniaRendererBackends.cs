using FrameFlux;

namespace FrameFlux.Avalonia;

public static class RtspAvaloniaRendererBackends
{
    public static RtspRendererBackendRegistry CreateDefaultRegistry()
    {
        var registry = new RtspRendererBackendRegistry();
        registry.Register(new SoftwareBitmapBackend());
        registry.Register(new NativeSurfaceCompatibilityBackend());
        return registry;
    }

    private sealed class SoftwareBitmapBackend : IRtspRendererBackend
    {
        public string Id => "avalonia-software-bitmap";

        public RtspRenderPreference Preference => RtspRenderPreference.Software;

        public int Priority => 100;

        public bool IsSupported(RtspPlatformCapabilities capabilities) => true;
    }

    private sealed class NativeSurfaceCompatibilityBackend : IRtspRendererBackend
    {
        public string Id => "frameflux-native-surface";

        public RtspRenderPreference Preference => RtspRenderPreference.NativeSurface;

        public int Priority => 200;

        public bool IsSupported(RtspPlatformCapabilities capabilities) =>
            capabilities.SupportedRenderPreferences.Contains(RtspRenderPreference.NativeSurface);
    }
}
