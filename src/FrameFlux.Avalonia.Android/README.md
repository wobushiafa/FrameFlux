# FrameFlux.Avalonia.Android

Android GPU presentation backend for `FrameFlux.Avalonia`.

Call `UseFrameFluxAndroid()` while building the Avalonia application. The
backend connects Android MediaCodec to a SurfaceTexture backed by an external
OpenGL ES texture, so decoded video remains on the GPU through presentation.

```csharp
protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) =>
    base.CustomizeAppBuilder(builder).UseFrameFluxAndroid();
```

Install `FrameFlux.FFmpeg.NativeAssets.Android` in the final Android app to
package the supplied FFmpeg shared libraries for each ABI.
