using Avalonia.Controls;
using Avalonia.Media;
using FrameFlux.Presentation;

namespace FrameFlux.Avalonia;

internal interface IAvaloniaPlatformMediaOutput :
    IMediaVideoOutput,
    IAsyncDisposable
{
    Control Surface { get; }

    Stretch Stretch { get; set; }

    event EventHandler? FramePresented;

    event Action<object?, MediaPresentationFailure>? PresentationFailed;

    void Clear();

    ValueTask ReleaseResourcesAsync();
}

internal static class AvaloniaPlatformMediaOutputRegistry
{
    private static Func<AvaloniaPlatformMediaOutputs>? _androidFactory;
    private static Func<AvaloniaPlatformMediaOutputs>? _linuxFactory;
    private static Func<AvaloniaPlatformMediaOutputs>? _windowsFactory;

    internal static void RegisterAndroidFactory(
        Func<AvaloniaPlatformMediaOutputs> factory) =>
        RegisterFactory(ref _androidFactory, factory, "Android");

    internal static void RegisterLinuxFactory(
        Func<AvaloniaPlatformMediaOutputs> factory) =>
        RegisterFactory(ref _linuxFactory, factory, "Linux");

    internal static void RegisterWindowsFactory(
        Func<AvaloniaPlatformMediaOutputs> factory) =>
        RegisterFactory(ref _windowsFactory, factory, "Windows");

    internal static AvaloniaPlatformMediaOutputs TryCreate()
    {
        var factory = OperatingSystem.IsAndroid()
            ? Volatile.Read(ref _androidFactory)
            : OperatingSystem.IsWindows()
            ? Volatile.Read(ref _windowsFactory)
            : OperatingSystem.IsLinux()
                ? Volatile.Read(ref _linuxFactory)
                : null;
        return factory?.Invoke() ?? default;
    }

    private static void RegisterFactory(
        ref Func<AvaloniaPlatformMediaOutputs>? storage,
        Func<AvaloniaPlatformMediaOutputs> factory,
        string platformName)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var existing = Interlocked.CompareExchange(
            ref storage,
            factory,
            null);
        if (existing is not null && existing != factory)
        {
            throw new InvalidOperationException(
                $"A FrameFlux Avalonia {platformName} platform backend is already registered.");
        }
    }
}

internal readonly record struct AvaloniaPlatformMediaOutputs(
    IAvaloniaPlatformMediaOutput? GpuComposition,
    IAvaloniaPlatformMediaOutput? NativeSurface);
