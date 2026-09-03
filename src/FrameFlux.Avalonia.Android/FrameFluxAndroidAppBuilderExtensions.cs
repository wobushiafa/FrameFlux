using Avalonia;
using FrameFlux.FFmpeg.Android;

namespace FrameFlux.Avalonia;

public static class FrameFluxAndroidAppBuilderExtensions
{
    private static readonly Func<AvaloniaPlatformMediaOutputs> OutputFactory =
        static () => new AvaloniaPlatformMediaOutputs(
            new AndroidSurfaceTextureMediaOutput(),
            static () => new AndroidNativeSurfaceMediaOutput());

    public static AppBuilder UseFrameFluxAndroid(this AppBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        FrameFluxAndroidMediaCodec.Register();
        AvaloniaPlatformMediaOutputRegistry.RegisterAndroidFactory(OutputFactory);
        return builder;
    }
}
