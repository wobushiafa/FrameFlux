using Avalonia;

namespace FrameFlux.Avalonia;

public static class FrameFluxLinuxAppBuilderExtensions
{
    private static readonly Func<AvaloniaPlatformMediaOutputs> OutputFactory =
        static () => new AvaloniaPlatformMediaOutputs(
            new LinuxOpenGlMediaOutput(),
            null);

    public static AppBuilder UseFrameFluxLinux(this AppBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        AvaloniaPlatformMediaOutputRegistry.RegisterLinuxFactory(OutputFactory);
        return builder;
    }
}
