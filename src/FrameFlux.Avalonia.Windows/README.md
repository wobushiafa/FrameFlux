# FrameFlux.Avalonia.Windows

This package adds Windows D3D11 presentation to
`FrameFlux.Avalonia.MediaView`. Register it while configuring Avalonia:

```csharp
AppBuilder.Configure<App>()
    .UsePlatformDetect()
    .UseFrameFluxWindows();
```

`NativeSurface` uses a child HWND and D3D11 swap chain for minimum latency.
`GpuComposition` imports a shared D3D11 texture through Avalonia's compositor
and supports Avalonia overlays. Both paths automatically fall back to the core
software output after repeated presentation failures.
