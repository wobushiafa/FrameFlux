# FrameFlux.Avalonia.Linux

This package adds the Linux GPU presentation backend for
`FrameFlux.Avalonia.MediaView`. Register it while configuring Avalonia:

```csharp
AppBuilder.Configure<App>()
    .UsePlatformDetect()
    .UseFrameFluxLinux();
```

`GpuComposition` renders inside Avalonia's composition tree, so overlays remain
supported. `NativeSurface` uses a real X11 child window and independent EGL
window surface on X11 and XWayland. With `FrameFlux.FFmpeg.NativeAssets.Linux`,
`HardwarePreferred` and `Automatic` decoding keep VAAPI frames on the GPU,
export them as DRM PRIME DMA-BUF objects, and import NV12 or P010 planes through
EGLImage. Both presentation paths avoid hardware-frame readback, BGRA
conversion, and per-frame texture upload.

Avalonia 12 does not expose a `wl_subsurface` host through `NativeControlHost`.
Consequently, a pure Wayland backend can use `GpuComposition`, while
`NativeSurface` currently requires X11/XWayland and fails explicitly if its
parent is not an XID.

Zero-copy presentation requires `EGL_EXT_image_dma_buf_import`; non-linear
buffers also require `EGL_EXT_image_dma_buf_import_modifiers`. Both packed
NV12/P010 layers and separate luma/chroma DRM layers are supported. Unsupported
drivers and layouts trigger the existing presentation-failure policy and
restart playback through the software renderer.
