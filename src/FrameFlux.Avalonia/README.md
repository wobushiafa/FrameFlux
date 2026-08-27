# FrameFlux.Avalonia

This package provides the protocol-neutral Avalonia `MediaView`. It creates an
`IMediaPlayer`, forwards the common playback lifecycle, and consumes software
and native frames through `IMediaVideoOutput`.

`MediaView` is a media surface rather than a general content host. It
intentionally does not expose a `Content` property. Use its explicit `Overlay`
property for controls that must be drawn above the video. Overlays are
supported by software rendering and `MediaRenderPreference.CompositedGpu`.

The existing `MediaRenderPreference.NativeSurface` path remains available for
minimum-latency presentation. It uses a real child window, so Avalonia controls
cannot be reliably composited above it due to native-window airspace rules.
Combining `Overlay` with explicit `NativeSurface` rendering is rejected.

`CompositedGpu` is currently available on Windows for dedicated playback. It
converts the decoder's D3D11 texture into a keyed-mutex shared BGRA texture and
imports it through Avalonia's compositor without reading the frame back to the
CPU. The active Avalonia renderer and decoder must use compatible GPU adapters.

The control does not select a playback backend. Applications must reference a
backend package and set `PlayerFactory` before playback starts:

```csharp
Player.PlayerFactory = new FfmpegMediaPlayerFactory();
```

Software rendering supports snapshots and frame subscriptions on every
target. On Windows, dedicated playback can select
`MediaRenderPreference.NativeSurface` for direct D3D11 output or
`MediaRenderPreference.CompositedGpu` for overlay-capable GPU composition.
Linux and Android currently use software output. GPU-texture playback does not
advertise snapshot support because D3D11 textures are not read back to the CPU.

Software bitmap delivery and the Avalonia native host are isolated outputs.
The Win32 window, D3D11 video processor, and swap chain are provided by the
shared `FrameFlux.Rendering.Windows` package.
