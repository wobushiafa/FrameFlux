using Avalonia;

namespace FrameFlux.Avalonia;

public static class FrameFluxLinuxAppBuilderExtensions
{
    private static readonly Func<AvaloniaPlatformMediaOutputs> OutputFactory =
        static () => new AvaloniaPlatformMediaOutputs(
            new LinuxOpenGlMediaOutput(),
            static () => new LinuxNativeSurfaceMediaOutput());

    public static AppBuilder UseFrameFluxLinux(this AppBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.With(new X11PlatformOptions
        {
            RenderingMode =
            [
                X11RenderingMode.Egl,
                X11RenderingMode.Glx,
                X11RenderingMode.Software
            ]
        });
        AvaloniaPlatformMediaOutputRegistry.RegisterLinuxFactory(OutputFactory);
        return builder;
    }
}
