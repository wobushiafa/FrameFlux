using Android.Views;

namespace FrameFlux.FFmpeg.Android;

/// <summary>
/// Provides the Android Surface consumed by the MediaCodec decoder.
/// </summary>
public interface IAndroidVideoSurfaceOutput : IMediaVideoOutput
{
    /// <summary>
    /// Waits for and returns the current decoder Surface. The output retains ownership.
    /// </summary>
    Surface AcquireDecoderSurface(CancellationToken cancellationToken);

    /// <summary>
    /// Updates the encoded video dimensions used by the presentation layout.
    /// </summary>
    void SetDecodedVideoSize(int width, int height);
}
