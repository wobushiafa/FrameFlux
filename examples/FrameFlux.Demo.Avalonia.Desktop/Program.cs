using Avalonia;
using FrameFlux.Avalonia;
using FrameFlux.FFmpeg;

namespace FrameFlux.Demo.Avalonia.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (OperatingSystem.IsLinux())
        {
            FFmpegHelper.RegisterFFmpeg();
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<global::FrameFlux.Demo.Avalonia.App>()
            .UsePlatformDetect()
            .UseFrameFluxWindows()
            .UseFrameFluxLinux()
            .WithInterFont()
            .LogToTrace();
}
