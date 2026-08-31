namespace FrameFlux.Presentation;

internal static class MediaPresentationFallbackPolicy
{
    internal static MediaVideoPresentationMode Resolve(
        bool failedNativeSurface,
        bool gpuCompositionAvailable) =>
        failedNativeSurface && gpuCompositionAvailable
            ? MediaVideoPresentationMode.GpuComposition
            : MediaVideoPresentationMode.SoftwareBitmap;
}
