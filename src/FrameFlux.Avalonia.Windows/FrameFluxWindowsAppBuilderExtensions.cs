using Avalonia;

namespace FrameFlux.Avalonia;

public static class FrameFluxWindowsAppBuilderExtensions
{
    private static readonly Func<AvaloniaPlatformMediaOutputs> OutputFactory =
        static () => new AvaloniaPlatformMediaOutputs(
            new WindowsD3D11CompositionMediaOutput(),
            static () => new WindowsD3D11MediaOutput());

    public static AppBuilder UseFrameFluxWindows(this AppBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        AvaloniaPlatformMediaOutputRegistry.RegisterWindowsFactory(OutputFactory);
        return builder;
    }
}
