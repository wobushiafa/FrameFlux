# FrameFlux.Avalonia.Android

Android hardware video presentation backends for `FrameFlux.Avalonia`.

Call `UseFrameFluxAndroid()` while building the Avalonia application. The
package registers two MediaCodec presentation paths:

- `GpuComposition` uses SurfaceTexture and `GL_TEXTURE_EXTERNAL_OES`, supports
  Avalonia overlays, and remains the `Automatic` default.
- `NativeSurface` sends MediaCodec output directly to a hosted Android
  `SurfaceView`, avoiding FrameFlux OpenGL composition.

```csharp
protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) =>
    base.CustomizeAppBuilder(builder).UseFrameFluxAndroid();
```

Select direct native presentation explicitly on the media view:

```csharp
Player.PresentationMode = MediaVideoPresentationMode.NativeSurface;
```

`NativeSurface` requires a dedicated session and hardware-capable decoding. It
does not support Avalonia overlay content, opacity, arbitrary transforms, or
rounded clipping because `SurfaceView` is composed by Android outside the
Avalonia visual tree. Popup-based controls such as ComboBox drop-downs, menus,
tooltips, and flyouts can therefore appear behind the video. Use
`GpuComposition` whenever Avalonia UI must overlap the video. `Fill`,
`Uniform`, `UniformToFill`, and `None` sizing are
supported by sizing and clipping the hosted native view.

If the native Surface is destroyed while MediaCodec is active, presentation
restarts with `GpuComposition`. A subsequent GPU presentation failure falls
back to `SoftwareBitmap`.

Install `FrameFlux.FFmpeg.NativeAssets.Android` in the final Android app to
package the supplied FFmpeg shared libraries for each ABI.
