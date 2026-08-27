namespace FrameFlux.Presentation;

internal readonly record struct MediaPresentationPlan(
    MediaVideoPresentationMode EffectiveMode,
    bool UsesNativeSurface,
    bool UsesGpuComposition);

internal static class MediaPresentationPolicy
{
    internal static MediaPresentationPlan Resolve(
        MediaVideoPresentationMode requestedMode,
        MediaOpenOptions options,
        bool platformGpuPresentationAvailable,
        bool hasOverlay)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (!Enum.IsDefined(requestedMode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestedMode),
                requestedMode,
                "Unsupported presentation mode.");
        }

        if (hasOverlay && requestedMode == MediaVideoPresentationMode.NativeSurface)
        {
            throw new InvalidOperationException(
                "Overlay content requires SoftwareBitmap or GpuComposition presentation.");
        }

        var gpuPresentationAvailable =
            platformGpuPresentationAvailable &&
            options.SessionSharing == MediaSessionSharingMode.Dedicated &&
            options.Video.DecodingPolicy != MediaVideoDecodingPolicy.SoftwareOnly;
        if (!gpuPresentationAvailable &&
            requestedMode is MediaVideoPresentationMode.NativeSurface or
                MediaVideoPresentationMode.GpuComposition)
        {
            throw new InvalidOperationException(
                "The requested GPU presentation mode requires Windows, a dedicated session, and hardware-capable decoding.");
        }

        var effectiveMode = requestedMode == MediaVideoPresentationMode.Automatic
            ? gpuPresentationAvailable
                ? MediaVideoPresentationMode.GpuComposition
                : MediaVideoPresentationMode.SoftwareBitmap
            : requestedMode;
        return new MediaPresentationPlan(
            effectiveMode,
            effectiveMode == MediaVideoPresentationMode.NativeSurface,
            effectiveMode == MediaVideoPresentationMode.GpuComposition);
    }
}

internal sealed class AdaptiveMediaVideoOutput(
    IMediaVideoOutput primary,
    IMediaVideoOutput softwareFallback) : IMediaVideoOutput
{
    public MediaFrameStorageKind PreferredFrameStorage => primary.PreferredFrameStorage;

    public bool Supports(MediaFrameStorageKind storageKind, MediaPixelFormat pixelFormat) =>
        primary.Supports(storageKind, pixelFormat) ||
        softwareFallback.Supports(storageKind, pixelFormat);

    public bool TryPresent(IMediaFrameLease frame)
    {
        var output = primary.Supports(frame.StorageKind, frame.PixelFormat)
            ? primary
            : softwareFallback;
        return output.Supports(frame.StorageKind, frame.PixelFormat) &&
               output.TryPresent(frame);
    }
}
