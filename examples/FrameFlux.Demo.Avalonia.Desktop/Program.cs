using Avalonia;

namespace FrameFlux.Demo.Avalonia.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    private static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<global::FrameFlux.Demo.Avalonia.App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
