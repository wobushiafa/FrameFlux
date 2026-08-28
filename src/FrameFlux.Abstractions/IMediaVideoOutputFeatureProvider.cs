namespace FrameFlux;

/// <summary>
/// Exposes optional platform capabilities without expanding the base video output contract.
/// </summary>
public interface IMediaVideoOutputFeatureProvider
{
    /// <summary>
    /// Returns a platform feature assignable to <paramref name="featureType" />, or null.
    /// </summary>
    object? GetVideoOutputFeature(Type featureType);
}
