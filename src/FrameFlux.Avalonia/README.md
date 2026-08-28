# FrameFlux.Avalonia

This package provides the protocol-neutral Avalonia `MediaView`. It creates an
`IMediaPlayer`, forwards the common playback lifecycle, and consumes software
and native frames through `IMediaVideoOutput`.

`MediaView` is a media surface rather than a general content host. It
intentionally does not consume a `Content` property. Use its explicit
`Overlay` property for controls that must be drawn above the video. Overlays
are supported by `SoftwareBitmap` and `GpuComposition` presentation.

The existing `MediaVideoPresentationMode.NativeSurface` path remains available for
minimum-latency presentation. It uses a real child window, so Avalonia controls
cannot be reliably composited above it due to native-window airspace rules.
Combining `Overlay` with explicit `NativeSurface` rendering is rejected.

`GpuComposition` is available on Windows for dedicated playback after
referencing `FrameFlux.Avalonia.Windows` and calling `UseFrameFluxWindows()`. It
converts the decoder's D3D11 texture into a keyed-mutex shared BGRA texture and
imports it through Avalonia's compositor without reading the frame back to the
CPU. The active Avalonia renderer and decoder must use compatible GPU adapters.
On Linux, reference `FrameFlux.Avalonia.Linux` and register it during startup:

```csharp
AppBuilder.Configure<App>()
    .UsePlatformDetect()
    .UseFrameFluxWindows()
    .UseFrameFluxLinux();
```

The Linux output renders inside Avalonia's composition tree and supports
overlays and all `Stretch` modes. On EGL-backed X11 and Wayland sessions it
imports VAAPI DRM PRIME DMA-BUF frames as EGLImages, avoiding system-memory
readback and BGRA texture upload. Unsupported EGL capabilities and frame layouts
fall back to the shared software renderer.

On Android, reference `FrameFlux.Avalonia.Android` and call
`UseFrameFluxAndroid()` from the Android `AppBuilder`. The backend exposes a
MediaCodec Surface backed by SurfaceTexture and renders its external OES
texture inside Avalonia's composition tree. It supports overlays and every
`Stretch` mode without copying decoded pixels to CPU memory. Surface creation
is tied to the Avalonia GL context; decoder shutdown completes before the
SurfaceTexture and OES texture are released.


The control does not select a playback backend. Applications must reference a
backend package and set `PlayerFactory` before playback starts:

```csharp
Player.PlayerFactory = new FfmpegMediaPlayerFactory();
```

Software rendering supports snapshots and frame subscriptions on every
target. On Windows, dedicated playback can select
`MediaVideoPresentationMode.NativeSurface` for direct D3D11 output or
`MediaVideoPresentationMode.GpuComposition` for overlay-capable GPU composition.
Android uses `GpuComposition` for MediaCodec/SurfaceTexture playback and falls
back to software when snapshots are requested or hardware initialization fails.
D3D11 GPU-texture playback does not advertise snapshot support because textures
are not read back to the CPU.

Configure decoding and presentation independently:

```csharp
Player.OpenOptions = new MediaOpenOptions
{
    Video = new MediaVideoOptions
    {
        DecodingPolicy = MediaVideoDecodingPolicy.HardwarePreferred
    }
};
Player.PresentationMode = MediaVideoPresentationMode.GpuComposition;
```

`Automatic` presentation chooses `GpuComposition` when it is available and
`SoftwareBitmap` otherwise. Explicit GPU modes throw when their requirements
are not met. A `HardwarePreferred` decoder may fall back to software at
runtime; in that case the adaptive output changes
`EffectivePresentationMode` to `SoftwareBitmap`. The read-only
`IsHardwareVideoDecodingActive` and `VideoDecoderDiagnostics` properties
report the active decoder.

Software bitmap delivery and platform GPU outputs are isolated implementations.
The Win32 window, D3D11 video processor, and swap chain are provided by the
opt-in `FrameFlux.Avalonia.Windows` package over
`FrameFlux.Rendering.Windows`. Linux OpenGL presentation is
provided by the opt-in `FrameFlux.Avalonia.Linux` package.
Android MediaCodec and OES presentation are split into the opt-in
`FrameFlux.FFmpeg.Android` and `FrameFlux.Avalonia.Android` packages.
