# FrameFlux.Avalonia.Linux

This package adds the Linux GPU presentation backend for
`FrameFlux.Avalonia.MediaView`. Register it while configuring Avalonia:

```csharp
AppBuilder.Configure<App>()
    .UsePlatformDetect()
    .UseFrameFluxLinux();
```

The backend uploads decoded BGRA frames into an OpenGL texture and renders
them inside Avalonia's composition tree, so overlays remain supported. With
`FrameFlux.FFmpeg.NativeAssets.Linux`, `HardwarePreferred` and `Automatic`
decoding use VAAPI when the host driver and codec support it, then fall back to
software without changing application code.

The initial Linux backend transfers VAAPI frames to system memory before the
OpenGL upload. This keeps the public media contracts and fallback path stable;
DMA-BUF zero-copy interop can be added inside this package without changing
the `MediaView` API.
